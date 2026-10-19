using PerAppAudio.RoutingEngine;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RuleEngine;

/// <summary>
/// 常驻应用宿主（CLI ctl watch 与 WPF 托盘壳共用）。
/// 职责：单实例互斥 → 探测手段 → 载入路由表与规则 → 启动看门狗 → 周期扫描
/// （对路由表匹配的进程自动应用路由）。UI/CLI 只订阅事件与查询状态。
/// </summary>
public sealed class AppHost : IDisposable
{
    public const string SingleInstanceName = "PerAppAudio_SingleInstance";

    private readonly RoutingCoordinator _coordinator = new();
    private readonly RouteStore _store;
    private readonly Mutex? _instanceMutex;
    private CancellationTokenSource? _cts;
    private Task? _scanLoop;
    private DeviceEventService? _deviceEvents;
    private volatile bool _disposed;
    private string _lastRoutesSignature = string.Empty;

    private BackendProbeResult _probeA = new(false, "未探测");
    private BackendProbeResult _probeB2 = new(false, "未探测");

    public RoutingCoordinator Coordinator => _coordinator;
    public RouteStore Store => _store;
    public RuleSet Rules { get; private set; } = RuleSet.Load();

    public (BackendProbeResult A, BackendProbeResult B2) Probes => (_probeA, _probeB2);

    /// <summary>看门狗 / 扫描 / 应用日志（UI 显示与文件落盘共用）。</summary>
    public event Action<string>? Log;

    /// <summary>路由集合变化（扫描应用了新路由 / 看门狗拆除）。</summary>
    public event Action? RoutesChanged;

    /// <summary>设备集合变化（插拔/状态变化；UI 立即刷新设备下拉用）。</summary>
    public event Action? DeviceListChanged;

    /// <summary>设备事件触发器（StartAsync 后可用；设备事件 → rules.yaml 场景联动）。</summary>
    public DeviceEventService? DeviceEvents => _deviceEvents;

    private AppHost(RouteStore store, Mutex mutex)
    {
        _store = store;
        _instanceMutex = mutex;
        _coordinator.Log += msg => Log?.Invoke(msg);
    }

    /// <summary>尝试取得单实例锁；已被其他实例占用时返回 null。</summary>
    public static AppHost? TryAcquireSingleInstance(string? storePath = null)
    {
        var mutex = new Mutex(true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var store = new RouteStore(storePath).Load();
        return new AppHost(store, mutex);
    }

    /// <summary>启动常驻核心：预热 → 探测 → 立即扫一轮 → 看门狗 → 周期扫描。</summary>
    public async Task StartAsync(int scanIntervalMs = 2000, CancellationToken ct = default)
    {
        // 进程级 COM RCW 预热：NAudio 枚举器必须先于自研 COM 设备枚举创建
        // （两者共享 CLSID_MMDeviceEnumerator，顺序颠倒会 InvalidCastException）
        WasapiSessionSource.Warmup();

        var (probeA, probeB2) = await _coordinator.ProbeAsync(ct).ConfigureAwait(false);
        _probeA = probeA;
        _probeB2 = probeB2;
        Log?.Invoke($"[探测] 手段A: {(probeA.Available ? "可用" : "不可用")}（{probeA.Reason}）");
        Log?.Invoke($"[探测] 手段B2: {(probeB2.Available ? "可用" : "不可用")}（{probeB2.Reason}）");

        _coordinator.StartWatchdog(scanIntervalMs);
        Log?.Invoke($"[看门狗] 已启动（{scanIntervalMs} ms）：进程退出拆 A / 设备消失暂停 / 设备回归重挂");

        // 设备事件触发器：插拔/状态/默认设备变更 → rules.yaml 的 device_events 规则
        _deviceEvents = new DeviceEventService(_coordinator, _store);
        _deviceEvents.Log += msg => Log?.Invoke(msg);
        _deviceEvents.RoutesChanged += () => RoutesChanged?.Invoke();
        _deviceEvents.DevicesChanged += () => DeviceListChanged?.Invoke();
        _deviceEvents.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scanLoop = Task.Run(() => ScanLoopAsync(_cts.Token));
    }

    /// <summary>立即扫描一轮（路由表匹配的进程 → 应用路由）。</summary>
    public void ScanNow() => ScanAndApply();

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ScanAndApply();

                // 变化才通知：UI 每次全量刷新约 300ms（会话枚举），
                // 无条件每 2s 触发会把 UI 线程长期占掉 ~15%（表现为持续卡顿）。
                // 签名 = 持久路由 + 活动链路；看门狗拆链后下一轮扫描即可感知（≤2s）。
                string signature = BuildRoutesSignature();
                if (!string.Equals(signature, _lastRoutesSignature, StringComparison.Ordinal))
                {
                    _lastRoutesSignature = signature;
                    RoutesChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[扫描] 异常（忽略）: {ex.Message}");
            }

            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>重建全部路由（睡眠唤醒后调用）；实现见 RouteRebuilder。</summary>
    public Task<int> RebuildAllRoutesAsync(CancellationToken ct = default)
        => RouteRebuilder.RebuildAllAsync(_coordinator, _store, Log, ct);

    /// <summary>路由集合签名（持久路由 + 活动链路的关键字段拼接）。</summary>
    private string BuildRoutesSignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in _store.Routes)
        {
            sb.Append(r.ProcessPath).Append('|').Append(r.DeviceName).Append('|')
                .Append((int)r.Mode).Append('|').Append(r.Volume.ToString("F2")).Append('|')
                .Append(r.Mute).Append(';');
        }
        sb.Append('#');
        foreach (var l in _coordinator.GetLiveRoutes())
        {
            sb.Append(l.ProcessId).Append('|').Append(l.TargetDevice).Append('|')
                .Append(l.LoopbackActive).Append('|').Append(l.EndpointApplied).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>对路由表中每个运行中的匹配进程应用路由（已应用同一 PID 的跳过）。</summary>
    private void ScanAndApply()
    {
        _store.Load();
        Rules = RuleSet.Load();

        var live = _coordinator.GetLiveRoutes();
        foreach (var route in _store.Routes)
        {
            foreach (var process in ProcessFinder.Find(route.ProcessPath))
            {
                using (process)
                {
                    if (live.Any(l => l.ProcessId == process.Id))
                    {
                        continue;
                    }

                    var device = new AudioDeviceRef(route.DeviceName, route.DeviceId);
                    var command = new RoutingCommand(process.Id, route.ProcessPath, device, route.Volume, route.Mute);
                    var report = _coordinator.ApplyRouteAsync(command, route.Mode).GetAwaiter().GetResult();
                    Log?.Invoke($"[应用] {Path.GetFileName(route.ProcessPath)}（PID {process.Id}）→ {route.DeviceName}" +
                        $" | B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}" +
                        $" | {report.Elapsed.TotalMilliseconds:F0} ms");
                    if (!report.Success)
                    {
                        Log?.Invoke($"    B2: {report.EndpointMessage}");
                        Log?.Invoke($"    A : {report.LoopbackMessage}");
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        try
        {
            _scanLoop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }

        _coordinator.Dispose();
        _deviceEvents?.Dispose();
        _cts?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }
}
