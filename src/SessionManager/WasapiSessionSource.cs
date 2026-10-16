using System.Diagnostics;
using System.Runtime.InteropServices;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.SessionManager;

/// <summary>
/// WASAPI 音频会话感知源：枚举渲染设备上的音频会话（PID / 路径 / 状态），
/// 并提供按进程静音 / 恢复的能力（手段A 的双重播放守护基础）。
/// </summary>
public class WasapiSessionSource : IAudioSessionSource
{
    private const int ClsctxAll = 0x17;

    /// <summary>枚举所有活动渲染设备上的会话；deviceId 非空时只查该设备。</summary>
    public static IReadOnlyList<AudioSessionInfo> EnumerateSessions(string? deviceId = null)
    {
        var result = new List<AudioSessionInfo>();
        foreach (var device in EnumerateRenderDevices(deviceId))
        {
            IAudioSessionManager2? manager = null;
            IAudioSessionEnumerator? enumerator = null;
            try
            {
                var iid = Guid.Parse(Guids.IID_IAudioSessionManager2);
                device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object mgr);
                manager = (IAudioSessionManager2)mgr;
                manager.GetSessionEnumerator(out enumerator);

                int count = enumerator.GetCount();
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var control = enumerator.GetSession(i);
                        var control2 = (IAudioSessionControl2)control;
                        control2.GetProcessId(out int pid);
                        string display = control.GetDisplayName() ?? string.Empty;
                        var state = control.GetState();
                        // IsSystemSoundsSession：BOOL* 出参形态（Win11 26200 实测），true = 系统声音会话
                        bool systemSounds = false;
                        try { systemSounds = control2.IsSystemSoundsSession(); } catch { }
                        string instanceId = "<unknown>";
                        try
                        {
                            if (control2.GetSessionInstanceIdentifier(out IntPtr idPtr) >= 0 && idPtr != IntPtr.Zero)
                            {
                                instanceId = Marshal.PtrToStringUni(idPtr) ?? "<unknown>";
                                Marshal.FreeCoTaskMem(idPtr);
                            }
                        }
                        catch { }

                        result.Add(new AudioSessionInfo(
                            ProcessId: pid,
                            ProcessPath: ResolveProcessPath(pid),
                            SessionId: instanceId,
                            DisplayName: systemSounds ? "系统声音" : (string.IsNullOrEmpty(display) ? ResolveProcessName(pid) : display),
                            State: state switch
                            {
                                AudioSessionState.Active => SessionState.Active,
                                AudioSessionState.Expired => SessionState.Expired,
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
        }

        return result;
    }

    IReadOnlyList<AudioSessionInfo> IAudioSessionSource.EnumerateSessions() => EnumerateSessions();

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
                var iid = Guid.Parse(Guids.IID_IAudioSessionManager2);
                device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object mgr);
                var manager = (IAudioSessionManager2)mgr;
                manager.GetSessionEnumerator(out var enumerator);

                int count = enumerator.GetCount();
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var control = enumerator.GetSession(i);
                        var control2 = (IAudioSessionControl2)control;
                        control2.GetProcessId(out int pid);
                        if (pid != processId) continue;

                        var simple = (ISimpleAudioVolume)control;
                        string display = control.GetDisplayName();
                        if (string.IsNullOrEmpty(display))
                        {
                            display = "<unnamed>";
                        }
                        result.Add(new SessionVolumeInfo(
                            Device: device.GetId(),
                            ProcessId: pid,
                            DisplayName: display,
                            Volume: simple.GetMasterVolume(),
                            Muted: simple.GetMute()));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
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
                var iid = Guid.Parse(Guids.IID_IAudioSessionManager2);
                device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object mgr);
                var manager = (IAudioSessionManager2)mgr;
                manager.GetSessionEnumerator(out var enumerator);

                int count = enumerator.GetCount();
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var control = enumerator.GetSession(i);
                        var control2 = (IAudioSessionControl2)control;
                        control2.GetProcessId(out int pid);
                        if (pid != processId) continue;

                        var simple = (ISimpleAudioVolume)control;
                        var eventContext = Guid.Empty;
                        if (volume.HasValue)
                        {
                            simple.SetMasterVolume(Math.Clamp(volume.Value, 0f, 1f), ref eventContext);
                        }
                        if (mute.HasValue)
                        {
                            simple.SetMute(mute.Value, ref eventContext);
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
        }

        return affected;
    }

    private static List<IMMDevice> EnumerateRenderDevices(string? deviceId)
    {
        var devices = new List<IMMDevice>();
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                devices.Add(DeviceManager.GetDevice(deviceId));
            }
            catch
            {
            }
            return devices;
        }

        var enumerator = DeviceManager.CreateEnumerator();
        var collection = enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active);
        int count = collection.GetCount();
        for (int i = 0; i < count; i++)
        {
            try
            {
                devices.Add(collection.Item(i));
            }
            catch
            {
            }
        }

        return devices;
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
