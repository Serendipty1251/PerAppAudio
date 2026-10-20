using System.Diagnostics;
using PerAppAudio.RoutingEngine.Backends;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine;

/// <summary>一次 ApplyRoute 的结果报告：两个手段各自成败 + 实际在跑的链路。</summary>
/// <param name="RequestedMode">调用方请求的手段（Auto 会被解析为具体动作组合）。</param>
/// <param name="EndpointApplied">手段B2 持久化端点是否写入成功（管「未来」）。</param>
/// <param name="LoopbackActive">手段A 实时改道链路是否在跑（管「现在」）。</param>
/// <param name="EndpointMessage">手段B2 的结果说明。</param>
/// <param name="LoopbackMessage">手段A 的结果说明。</param>
/// <param name="Elapsed">总耗时（验收指标：回退/组合应在 2s 内完成）。</param>
public sealed record RouteReport(
    RoutingMode RequestedMode,
    bool EndpointApplied,
    bool LoopbackActive,
    string? EndpointMessage,
    string? LoopbackMessage,
    TimeSpan Elapsed)
{
    /// <summary>Auto 语义：至少一个手段成功即成功（双保险）；全失败才算失败。</summary>
    public bool Success => EndpointApplied || LoopbackActive;
}

/// <summary>
/// 策略调度器（P1 控制闭环核心）。
/// 组合手段A（LoopbackBackend，实时迁移正在播放的流）与手段B2（EndpointBackend，持久化每程序默认端点），
/// 按 RoutingMode 分派：
/// - Auto（默认）：B2 写「未来」+ A 迁移「现在」，双管齐下；单侧失败不拖垮另一侧；
/// - Endpoint：只写持久化端点（应用新建音频会话时生效）；
/// - Loopback：只做实时改道。
/// 内置看门狗：进程退出自动拆 A 路由；目标设备消失拆 A、回归自动重挂。
/// </summary>
public sealed class RoutingCoordinator : IDisposable
{
    private sealed class LiveRoute
    {
        public required RoutingCommand Command { get; init; }
        public required RoutingMode RequestedMode { get; init; }
        public required bool EndpointApplied { get; init; }
        public required DateTime AppliedAt { get; init; }
        public bool LoopbackActive { get; set; }
        public bool AwaitingDevice { get; set; }
    }

    private readonly LoopbackBackend _loopback = new();
    private readonly EndpointBackend _endpoint = new();
    private readonly Dictionary<int, LiveRoute> _live = new();
    private readonly object _gate = new();
    private Timer? _watchdog;
    private volatile bool _disposed;
    private int _watchdogBusy;

    private BackendProbeResult _loopbackProbe = new(false, "未探测");
    private BackendProbeResult _endpointProbe = new(false, "未探测");

    /// <summary>看门狗与重挂事件日志（CLI/UI 订阅）。</summary>
    public event Action<string>? Log;

    /// <summary>探测两个手段的可用性（启动时调用一次，结果决定 Auto 的分派范围）。</summary>
    public async Task<(BackendProbeResult Loopback, BackendProbeResult Endpoint)> ProbeAsync(CancellationToken ct = default)
    {
        _loopbackProbe = await _loopback.ProbeAsync(ct).ConfigureAwait(false);
        _endpointProbe = await _endpoint.ProbeAsync(ct).ConfigureAwait(false);
        return (_loopbackProbe, _endpointProbe);
    }

    /// <summary>应用一条路由。Auto = B2 持久化 + A 实时迁移；单侧失败不影响另一侧。</summary>
    public async Task<RouteReport> ApplyRouteAsync(RoutingCommand command, RoutingMode mode, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        bool useEndpoint = mode is RoutingMode.Endpoint or RoutingMode.Auto;
        bool useLoopback = mode is RoutingMode.Loopback or RoutingMode.Auto;

        // 1. 手段B2：写持久化默认端点（管未来：应用新建音频会话即走目标设备）
        bool endpointApplied = false;
        string? endpointMessage = null;
        if (useEndpoint)
        {
            if (!_endpointProbe.Available)
            {
                endpointMessage = $"手段B2 跳过（探测不可用：{_endpointProbe.Reason}）";
            }
            else
            {
                var r = await _endpoint.ApplyAsync(command, ct).ConfigureAwait(false);
                endpointApplied = r.Success;
                endpointMessage = r.Message;
            }
        }

        // 2. 手段A：实时迁移正在播放的流（管现在：0 丢帧改道）
        bool loopbackActive = false;
        string? loopbackMessage = null;
        if (useLoopback)
        {
            if (!_loopbackProbe.Available)
            {
                loopbackMessage = $"手段A 跳过（探测不可用：{_loopbackProbe.Reason}）";
            }
            else if (_loopback.IsRouting(command.ProcessId))
            {
                loopbackActive = true;
                // 复用不等于不更新：A 路径的听感音量 = 我们的渲染会话音量，
                // 路由创建后 UI 改音量靠这里下发（应用自身会话已被静音，走 ApplySessionControl 无效）。
                float? readBack = _loopback.UpdateVolume(command.ProcessId, command.Volume);
                loopbackMessage = readBack.HasValue
                    ? $"已有活动 A 路由，已同步渲染会话音量→ {readBack.Value:F2}"
                    : "已有活动 A 路由（音量同步失败）";
            }
            else
            {
                var r = await _loopback.ApplyAsync(command, ct).ConfigureAwait(false);
                loopbackActive = r.Success;
                loopbackMessage = r.Message;
            }
        }

        sw.Stop();

        var report = new RouteReport(
            mode, endpointApplied, loopbackActive, endpointMessage, loopbackMessage, sw.Elapsed);

        lock (_gate)
        {
            // 双失败不登记（旧实现无条件写入）：
            // ScanAndApply 见 PID 在 _live 即跳过、看门狗对 !LoopbackActive 又 continue——
            // 失败路由永不重试（且与日志「看门狗每 2s 重试」的承诺矛盾）。
            // 不登记后下一轮扫描会重新尝试（进程仍匹配时）。
            if (endpointApplied || loopbackActive)
            {
                _live[command.ProcessId] = new LiveRoute
                {
                    Command = command,
                    RequestedMode = mode,
                    EndpointApplied = endpointApplied,
                    LoopbackActive = loopbackActive,
                    AppliedAt = DateTime.Now,
                };
            }
            else if (_live.Remove(command.ProcessId))
            {
                Log?.Invoke($"[调度] PID {command.ProcessId} 双失败不登记，下一轮扫描重试");
            }
        }

        // P2 音量守护：把路由的音量 / 静音意图施加到目标设备上的应用会话。
        // （A 路径另由 LoopbackBackend 设置渲染会话音量；此处作用于应用自身会话。）
        // 必须无条件施加（含 volume=1.0 / mute=false）：旧实现用 Volume<1 作门槛，
        // 用户把音量从 0.3 拉回 100% 时会话音量停在旧值——界面显示 100% 实际播 30%。
        if (endpointApplied || loopbackActive)
        {
            try
            {
                int affected = WasapiSessionSource.ApplySessionControl(
                    command.ProcessId,
                    volume: command.Volume,
                    mute: command.Mute,
                    deviceId: command.TargetDevice.DeviceId);
                Log?.Invoke($"[音量守护] 施加：PID {command.ProcessId} vol {command.Volume:F2} mute {command.Mute} → 命中 {affected} 个会话（目标设备 {command.TargetDevice.FriendlyName}）");
                if (affected == 0)
                {
                    Log?.Invoke($"[音量守护] PID {command.ProcessId} 未命中会话——应用尚未在目标设备创建音频会话（或属 winmm 等不可枚举类型）。看门狗每 2s 重试；需立即生效可把该应用手段改为 A（音量由我们的渲染会话控制，不依赖应用会话）。");
                }
            }
            catch
            {
                // 音量守护失败不阻断路由主链路
            }
        }

        return report;
    }

    /// <summary>
    /// 拆除一条路由：停 A 链路（恢复原会话声音）+ 清 B2 持久化覆盖（端点跟随系统默认）。
    /// B2 状态存于注册表（跨进程共享），因此 clearEndpoint 时无条件尝试清除——
    /// 即使本协调器实例没有该进程的活动记录（例如路由由别的 ctl watch 进程持有）。
    /// </summary>
    public async Task<RoutingResult> RemoveRouteAsync(int processId, bool clearEndpoint = true, CancellationToken ct = default)
    {
        bool hadLive;
        lock (_gate)
        {
            hadLive = _live.Remove(processId, out _);
        }

        var empty = new RoutingCommand(processId, string.Empty, new AudioDeviceRef(string.Empty, string.Empty), 1f, false);

        var loopbackResult = hadLive
            ? await _loopback.RemoveAsync(empty, ct).ConfigureAwait(false)
            : new RoutingResult(false, RoutingMode.Loopback, "本进程未持有该 A 路由（可能由其他常驻实例持有）");

        RoutingResult? endpointResult = null;
        if (clearEndpoint)
        {
            endpointResult = await _endpoint.RemoveAsync(empty, ct).ConfigureAwait(false);
        }

        bool success = loopbackResult.Success || endpointResult?.Success == true;
        string message = $"A: {loopbackResult.Message ?? "无"}";
        if (endpointResult is not null)
        {
            message += $"；B2: {endpointResult.Message}";
        }

        return new RoutingResult(success, RoutingMode.Auto, message);
    }

    /// <summary>
    /// 按进程路径拆除所有活动路由（路由表删除时的兜底）：
    /// UI 删除记录原先只对 ProcessFinder 能找到的进程拆链，路径解析不一致时会残留
    /// 活动 A 链路。此处按「完整路径或文件名」匹配 _live，无论进程是否还在跑都拆。
    /// </summary>
    public async Task<int> RemoveRoutesByPathAsync(string processPath, bool clearEndpoint = true, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return 0;
        }

        string fileName = Path.GetFileName(processPath);
        List<int> pids;
        lock (_gate)
        {
            pids = _live.Values
                .Where(r => string.Equals(r.Command.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(fileName)
                        && string.Equals(Path.GetFileName(r.Command.ProcessPath), fileName, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Command.ProcessId)
                .ToList();
        }

        foreach (int pid in pids)
        {
            await RemoveRouteAsync(pid, clearEndpoint, ct).ConfigureAwait(false);
        }

        return pids.Count;
    }

    /// <summary>启动看门狗（进程退出拆除 / 设备消失暂停 / 设备回归重挂）。</summary>
    public void StartWatchdog(int intervalMs = 2000)
    {
        _watchdog ??= new Timer(_ => WatchdogTick(), null, intervalMs, intervalMs);
    }

    /// <summary>当前活动路由快照（CLI/UI 展示用）。</summary>
    public IReadOnlyList<(int ProcessId, string ProcessPath, string TargetDevice, RoutingMode Mode, bool LoopbackActive, bool EndpointApplied, bool AwaitingDevice, DateTime AppliedAt)>
        GetLiveRoutes()
    {
        lock (_gate)
        {
            return _live.Values
                .Select(r => (r.Command.ProcessId, r.Command.ProcessPath, r.Command.TargetDevice.FriendlyName,
                    r.RequestedMode, r.LoopbackActive, r.EndpointApplied, r.AwaitingDevice, r.AppliedAt))
                .ToList();
        }
    }

    /// <summary>两个后端的探测结果快照。</summary>
    public (BackendProbeResult Loopback, BackendProbeResult Endpoint) GetProbeResults()
        => (_loopbackProbe, _endpointProbe);

    private void WatchdogTick()
    {
        if (_disposed)
        {
            return;
        }

        // Timer 不保证串行：上一轮未结束时直接跳过，避免同一路由被并发重挂/拆除。
        if (Interlocked.Exchange(ref _watchdogBusy, 1) != 0)
        {
            return;
        }

        try
        {
            List<LiveRoute> routes;
            lock (_gate)
            {
                routes = _live.Values.ToList();
            }

            // 只把「Active」端点视为存在：设备拔出后 Windows 通常保留端点并标记为
            // Unplugged/NotPresent（幽灵端点），用 DeviceState.All 会让「设备消失」分支永不触发。
            var deviceIds = new HashSet<string>(
                DeviceManager.EnumerateRenderDevices(DeviceState.Active).Select(d => d.Id),
                StringComparer.OrdinalIgnoreCase);

            foreach (var route in routes)
            {
                int pid = route.Command.ProcessId;

                // 1. 进程退出 → 拆 A（恢复静音的会话已随进程消失）；B2 是每程序持久化，保留管未来
                if (!IsProcessAlive(pid))
                {
                    Log?.Invoke($"[看门狗] 进程 {pid}（{Path.GetFileName(route.Command.ProcessPath)}）已退出 → 拆除 A 路由（B2 持久化保留）");
                    RemoveRouteAsync(pid, clearEndpoint: false).Wait(TimeSpan.FromSeconds(3));
                    continue;
                }

                // 2. 目标设备消失 → 拆 A，标记等待回归（不论 LoopbackActive 当前值）
                if (deviceIds.Contains(route.Command.TargetDevice.DeviceId))
                {
                    // 3. 设备在线且此前被暂停 → 重挂 A。
                    // 必须早于下面的 !LoopbackActive continue：旧顺序把重挂分支放在 continue 之后，
                    // 而暂停时 LoopbackActive 已置 false，第一个 continue 就把它挡掉——重挂永远不可达（死代码）。
                    if (route.AwaitingDevice)
                    {
                        Log?.Invoke($"[看门狗] 目标设备「{route.Command.TargetDevice.FriendlyName}」回归 → 重挂 A 路由（进程 {pid}）");
                        var r = _loopback.ApplyAsync(route.Command).AsTask().GetAwaiter().GetResult();
                        MarkLoopback(pid, active: r.Success, awaitingDevice: !r.Success);
                        if (!r.Success)
                        {
                            Log?.Invoke($"[看门狗] 重挂失败: {r.Message}");
                        }
                    }
                }
                else
                {
                    Log?.Invoke($"[看门狗] 目标设备「{route.Command.TargetDevice.FriendlyName}」消失 → 暂停 A 路由（进程 {pid}）");
                    _loopback.RemoveAsync(route.Command).AsTask().Wait(TimeSpan.FromSeconds(3));
                    MarkLoopback(pid, active: false, awaitingDevice: true);
                    continue;
                }

                if (!route.LoopbackActive)
                {
                    continue;
                }

                // 4. P2 音量守护：应用可能重建音频会话，周期性重设音量 / 静音
                if (route.Command.Volume < 1f || route.Command.Mute)
                {
                    try
                    {
                        WasapiSessionSource.ApplySessionControl(
                            pid,
                            volume: route.Command.Volume < 1f ? route.Command.Volume : null,
                            mute: route.Command.Mute ? true : null,
                            deviceId: route.Command.TargetDevice.DeviceId);
                    }
                    catch
                    {
                        // 守护失败下一轮再来
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[看门狗] 异常（忽略，下一轮继续）: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    private void MarkLoopback(int processId, bool active, bool awaitingDevice)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(processId, out var route))
            {
                route.LoopbackActive = active;
                route.AwaitingDevice = awaitingDevice;
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // 拿不到信息按存活处理，避免误拆
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog?.Dispose();
        _watchdog = null;

        List<int> pids;
        lock (_gate)
        {
            pids = _live.Keys.ToList();
        }

        foreach (int pid in pids)
        {
            try
            {
                RemoveRouteAsync(pid, clearEndpoint: false).Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // 退出路径尽力而为
            }
        }
    }
}
