using System.Diagnostics;
using System.Runtime.InteropServices;
using PerAppAudio.RoutingEngine.Audio;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine.Backends;

/// <summary>
/// 手段A：进程 Loopback 重定向后端（PoC-1 实现）。
/// 链路：目标进程音频 → 进程级 Loopback 捕获 →（零转换直通）→ 目标设备渲染。
/// 无霁静音守护：进程 loopback Include 模式激活即抑制目标进程的原渲染（PoC-1 实测）。
/// 旧版的 500ms 静音守护已删除：它不防任何东西，只会在混合器里留下静音图标、
/// 并在退出拆链不完整时残留永久静音（2026-10-20 实测发现）；且它静音所有设备上的会话，
/// Auto 模式下 B2 直播的音也会被自家守护误杀。
/// </summary>
public sealed class LoopbackBackend : IRoutingBackend
{
    private sealed class ActiveRoute
    {
        public required ProcessLoopbackCapture Capture { get; init; }
        public required WasapiRenderer Renderer { get; init; }
        public required RedirectStats Stats { get; init; }
        public required int ProcessId { get; init; }
    }

    private readonly Dictionary<int, ActiveRoute> _routes = new();
    private readonly object _gate = new();

    public RoutingMode Mode => RoutingMode.Loopback;

    public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        // 进程级 Loopback 要求 Windows 10 2004 (build 19041)+
        int build = Environment.OSVersion.Version.Build;
        if (build >= 19041)
        {
            return ValueTask.FromResult(new BackendProbeResult(true, $"Windows build {build}"));
        }
        return ValueTask.FromResult(
            new BackendProbeResult(false, $"手段A 需要 Windows 10 2004+（build 19041），当前 build {build}"));
    }

    public async ValueTask<RoutingResult> ApplyAsync(RoutingCommand command, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_routes.ContainsKey(command.ProcessId))
            {
                return new RoutingResult(false, RoutingMode.Loopback, $"进程 {command.ProcessId} 已有活动路由");
            }
        }

        IntPtr mixFormatPtr = IntPtr.Zero;
        ProcessLoopbackCapture? capture = null;
        WasapiRenderer? renderer = null;
        string stage = "初始化";

        try
        {
            // 1. 目标设备 Mix 格式：采集端请求同一格式 → 常见情况零转换直通
            stage = "获取目标设备 Mix 格式";
            mixFormatPtr = DeviceManager.GetDeviceMixFormat(command.TargetDevice.DeviceId, out int formatSize);
            var format = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormatPtr);
            string formatDesc = DeviceManager.DescribeFormat(format);

            var stats = new RedirectStats();

            // 2. 启动进程级 Loopback 捕获
            stage = $"启动 Loopback 采集（请求格式 {formatDesc}）";
            capture = await ProcessLoopbackCapture.StartAsync(
                (uint)command.ProcessId,
                mixFormatPtr,
                formatSize,
                bufferMilliseconds: 10,
                onPacket: (data, frames, flags) => OnPacket(command.ProcessId, data, frames, flags, stats),
                onError: ex => Console.Error.WriteLine($"[LoopbackBackend] 采集错误: {ex.Message}"),
                ct: ct).ConfigureAwait(false);

            // 3. 启动目标设备渲染
            stage = $"启动渲染器（设备 {command.TargetDevice.FriendlyName}，格式 {formatDesc}）";
            renderer = WasapiRenderer.Start(command.TargetDevice.DeviceId, bufferMilliseconds: 10);

            var route = new ActiveRoute
            {
                Capture = capture,
                Renderer = renderer,
                Stats = stats,
                ProcessId = command.ProcessId,
            };

            lock (_gate)
            {
                _routes[command.ProcessId] = route;
            }

            // P2 音量守护：渲染会话音量（手段A 的目标设备独立音量）。
            // 必须无条件设置（含 1.0）：旧实现 Volume<1 作门槛，
            // 用户把音量从 0.3 拉回 100% 时渲染会话音量停在旧值。
            float? appliedVolume = null;
            if (command.Volume >= 0f)
            {
                try
                {
                    renderer.SetSessionVolume(command.Volume);
                    appliedVolume = renderer.GetSessionVolume(); // 读回闭环验证
                }
                catch (Exception ex)
                {
                    // 音量设置失败不阻断主链路，但留诊断痕迹
                    Console.Error.WriteLine($"[LoopbackBackend] 渲染会话音量设置失败: 0x{ex.HResult:X8} {ex.Message}");
                }
            }

            return RoutingResult.Ok(RoutingMode.Loopback) with
            {
                Message = $"格式 {formatDesc}；采集缓冲 {capture.BufferFrames} 帧；渲染缓冲 {renderer.BufferFrames} 帧" +
                    (appliedVolume.HasValue ? $"；渲染会话音量 {appliedVolume.Value:F2}" : string.Empty),
            };
        }
        catch (Exception ex)
        {
            renderer?.Dispose();
            capture?.Dispose();
            return RoutingResult.Fail($"手段A 启动失败于「{stage}」: 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            if (mixFormatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormatPtr);
            }
        }
    }

    private void OnPacket(int processId, IntPtr data, int frames, AudioClientBufferFlags flags, RedirectStats stats)
    {
        var sw = Stopwatch.StartNew();
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out var route))
            {
                return;
            }

            bool silent = flags.HasFlag(AudioClientBufferFlags.Silent);
            bool discontinuity = flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity);

            int written = silent ? 0 : route.Renderer.Write(data, frames);
            int dropped = silent ? 0 : frames - written;

            // 目标进程无声时也要保持渲染流连续：把静音填充到半缓冲水位
            int filled = route.Renderer.FillSilenceUpTo(route.Renderer.BufferFrames / 2);

            sw.Stop();
            stats.RecordPacket(frames, written + filled, dropped, discontinuity, silent, sw.Elapsed.TotalMilliseconds);
        }
    }

    public ValueTask<RoutingResult> RemoveAsync(RoutingCommand command, CancellationToken ct = default)
    {
        ActiveRoute? route;
        lock (_gate)
        {
            if (!_routes.Remove(command.ProcessId, out route))
            {
                return ValueTask.FromResult(new RoutingResult(false, RoutingMode.Loopback, "没有找到活动路由"));
            }
        }

        // 锁外释放：Capture.Dispose() 会 Join 采集线程，而该线程的 OnPacket 回调需要拿 _gate，
        // 持锁 Dispose 会让两者互相等待（Join 超时最多卡 2 秒，表现为拆除路由时界面卡顿）。
        route.Capture.Dispose(); // 先停采集：返回后不会再有 OnPacket 进入
        route.Renderer.Dispose();

        // 拆除后 loopback 抑制自动解除，原渲染恢复（无需手工恢复静音）
        return ValueTask.FromResult(RoutingResult.Ok(RoutingMode.Loopback) with { Message = "路由已拆除，原渲染自动恢复" });
    }

    /// <summary>PoC-1 报告用：取某进程的统计快照。</summary>
    public (long Packets, long FramesCaptured, long FramesWritten, long FramesDropped,
        long Discontinuities, long SilentPackets, double AvgProcessingMs, double MaxProcessingMs)?
        GetStats(int processId)
    {
        lock (_gate)
        {
            return _routes.TryGetValue(processId, out var route) ? route.Stats.Snapshot() : null;
        }
    }

    public bool IsRouting(int processId)
    {
        lock (_gate)
        {
            return _routes.ContainsKey(processId);
        }
    }

    /// <summary>
    /// 更新活动路由的渲染会话音量（UI 音量滑条对已激活的 A 路由生效）。
    /// 返回读回值；路由不存在返回 null。
    /// </summary>
    public float? UpdateVolume(int processId, float volume)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out var route))
            {
                return null;
            }

            try
            {
                route.Renderer.SetSessionVolume(Math.Clamp(volume, 0f, 1f));
                return route.Renderer.GetSessionVolume();
            }
            catch
            {
                return null;
            }
        }
    }
}
