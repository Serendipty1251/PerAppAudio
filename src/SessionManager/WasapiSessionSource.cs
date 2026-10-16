using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace PerAppAudio.SessionManager;

/// <summary>
/// WASAPI 音频会话感知源（基于 NAudio.Core，MIT）：
/// 枚举渲染设备上的音频会话（PID / 路径 / 状态），并提供按进程音量 / 静音控制。
/// 注：自研 IAudioSessionControl2 COM 声明在本机（Win11 26200）实测不可靠
/// （GetProcessId 恒 0），改用被广泛验证的 NAudio 实现后会话枚举完全正常。
/// </summary>
public class WasapiSessionSource : IAudioSessionSource
{
    // 进程级共享 NAudio 枚举器：与自研 COM 共享 CLSID_MMDeviceEnumerator，
    // 实测只有 NAudio 的 ComImport 类 RCW 先创建时两者才能共存（反向实验通过）。
    // 因此进程启动必须调用 Warmup()（AppHost.StartAsync 首行），此后永久持有不释放。
    private static readonly object EnumeratorGate = new();
    private static MMDeviceEnumerator? _sharedEnumerator;

    /// <summary>预热共享枚举器（必须先于任何自研 COM 设备枚举调用）。</summary>
    public static void Warmup()
    {
        lock (EnumeratorGate)
        {
            _sharedEnumerator ??= new MMDeviceEnumerator();
        }
    }

    /// <summary>枚举所有活动渲染设备上的会话；deviceId 非空时只查该设备。</summary>
    public static IReadOnlyList<AudioSessionInfo> EnumerateSessions(string? deviceId = null)
    {
        var result = new List<AudioSessionInfo>();
        foreach (var device in EnumerateRenderDevices(deviceId))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var s = sessions[i];
                        int pid = (int)s.GetProcessID;
                        bool systemSounds = false;
                        try { systemSounds = s.IsSystemSoundsSession; } catch { }
                        string display = s.DisplayName ?? string.Empty;

                        result.Add(new AudioSessionInfo(
                            ProcessId: pid,
                            ProcessPath: ResolveProcessPath(pid),
                            SessionId: TryGetSessionInstanceId(s),
                            DisplayName: systemSounds ? "系统声音" : (string.IsNullOrEmpty(display) ? ResolveProcessName(pid) : display),
                            State: s.State.ToString() switch
                            {
                                "AudioSessionStateActive" => SessionState.Active,
                                "AudioSessionStateExpired" => SessionState.Expired,
                                _ => SessionState.Inactive,
                            }));
                    }
                    catch
                    {
                        // 单个会话枚举失败（已断开等）跳过
                    }
                }
            }
            catch
            {
                // 设备级失败跳过
            }
            finally
            {
                device.Dispose();
            }
        }

        return result;
    }

    private static string TryGetSessionInstanceId(AudioSessionControl s)
    {
        try
        {
            string id = s.GetSessionInstanceIdentifier;
            return string.IsNullOrEmpty(id) ? "<unknown>" : id;
        }
        catch
        {
            return "<unknown>";
        }
    }

    IReadOnlyList<AudioSessionInfo> IAudioSessionSource.EnumerateSessions() => EnumerateSessions();

    /// <summary>
    /// 建立「进程 → 其音频会话所在设备」映射（UI 显示"当前设备"用）。
    /// 同一进程可能在多个设备上有会话（多流应用），取第一个命中。
    /// </summary>
    public static Dictionary<int, string> GetDeviceOfProcesses()
    {
        var map = new Dictionary<int, string>();
        foreach (var device in EnumerateRenderDevices(null))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var s = sessions[i];
                        int pid = (int)s.GetProcessID;
                        if (pid > 0 && !map.ContainsKey(pid))
                        {
                            map[pid] = device.FriendlyName;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                device.Dispose();
            }
        }
        return map;
    }

    /// <summary>一个进程会话的音量控制状态快照（诊断用）。</summary>
    public sealed record SessionVolumeInfo(string Device, int ProcessId, string DisplayName, float Volume, bool Muted);

    /// <summary>
    /// 读回指定进程在所有渲染设备上的会话音量 / 静音状态（P2 诊断）。
    /// </summary>
    public static List<SessionVolumeInfo> GetSessionVolumeStates(int processId)
    {
        var result = new List<SessionVolumeInfo>();
        foreach (var device in EnumerateRenderDevices(null))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var s = sessions[i];
                        if ((int)s.GetProcessID != processId) continue;
                        result.Add(new SessionVolumeInfo(
                            Device: device.ID,
                            ProcessId: processId,
                            DisplayName: string.IsNullOrEmpty(s.DisplayName) ? "<unnamed>" : s.DisplayName,
                            Volume: s.SimpleAudioVolume.Volume,
                            Muted: s.SimpleAudioVolume.Mute));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                device.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// 把指定进程在所有渲染设备上的音频会话静音 / 恢复。
    /// 返回被操作的会话数量。供手段A 的双重播放守护调用。
    /// </summary>
    public static int MuteProcessSessions(int processId, bool mute)
        => ApplySessionControl(processId, mute: mute);

    /// <summary>
    /// 对指定进程的音频会话统一施加音量 / 静音控制（P2 音量守护）。
    /// deviceId 为空时作用于所有渲染设备上的该进程会话（B2 场景：应用直接在目标设备出声）。
    /// 返回被操作的会话数量。
    /// </summary>
    public static int ApplySessionControl(int processId, float? volume = null, bool? mute = null, string? deviceId = null)
    {
        int affected = 0;
        foreach (var device in EnumerateRenderDevices(deviceId))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var s = sessions[i];
                        if ((int)s.GetProcessID != processId) continue;

                        if (volume.HasValue)
                        {
                            s.SimpleAudioVolume.Volume = Math.Clamp(volume.Value, 0f, 1f);
                        }
                        if (mute.HasValue)
                        {
                            s.SimpleAudioVolume.Mute = mute.Value;
                        }
                        affected++;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                device.Dispose();
            }
        }

        return affected;
    }

    /// <summary>按 ID 或名称解析渲染设备（走共享 NAudio 枚举器）。</summary>
    public static MMDevice ResolveDevice(string idOrName)
    {
        lock (EnumeratorGate)
        {
            var enumerator = _sharedEnumerator ?? new MMDeviceEnumerator();
            try
            {
                return enumerator.GetDevice(idOrName);
            }
            catch
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (d.FriendlyName.Contains(idOrName, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }
                }
                throw new InvalidOperationException($"未找到设备「{idOrName}」");
            }
        }
    }

    private static List<MMDevice> EnumerateRenderDevices(string? deviceId)
    {
        // 复用进程级共享枚举器（Warmup 已创建；调用需串行化）
        lock (EnumeratorGate)
        {
            var enumerator = _sharedEnumerator ?? new MMDeviceEnumerator();
            var devices = new List<MMDevice>();
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    devices.Add(enumerator.GetDevice(deviceId));
                }
                catch
                {
                }
                return devices;
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(device);
            }
            return devices;
        }
    }

    private static string ResolveProcessPath(int pid)
    {
        if (pid <= 0) return "(系统)";
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName ?? process.ProcessName;
        }
        catch
        {
            return "(已退出或无权限)";
        }
    }

    private static string ResolveProcessName(int pid)
    {
        if (pid <= 0) return "系统声音";
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid:{pid}";
        }
    }
}
