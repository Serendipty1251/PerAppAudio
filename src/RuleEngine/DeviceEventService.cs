using System.Diagnostics;
using PerAppAudio.RoutingEngine;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RuleEngine;

/// <summary>
/// 设备事件触发器（P2 #21）：
/// 订阅 IMMNotificationClient，端点插拔 / 激活状态变化 / 默认设备切换经 800ms 防抖合并后，
/// 按 rules.yaml 的 device_events 规则匹配（事件类型 ∩ 设备名片段），命中即应用对应场景 Profile。
/// 同时外发 DevicesChanged——UI 借此在设备热插拔时立即刷新下拉列表（不必等刷新定时器）。
/// </summary>
public sealed class DeviceEventService : IDisposable
{
    private const int DebounceMs = 800;

    private readonly RoutingCoordinator _coordinator;
    private readonly RouteStore _store;
    private readonly DeviceNotifier _notifier = new();
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private readonly List<(string Kind, string DeviceId)> _pending = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private IMMDeviceEnumerator? _enumerator;
    private bool _processing;
    private bool _disposed;

    /// <summary>规则应用 / 订阅状态日志（并入宿主 Log）。</summary>
    public event Action<string>? Log;

    /// <summary>设备集合变化（UI 刷新设备列表）。</summary>
    public event Action? DevicesChanged;

    /// <summary>规则命中介入导致路由变化（UI 刷新路由表）。</summary>
    public event Action? RoutesChanged;

    public DeviceEventService(RoutingCoordinator coordinator, RouteStore store)
    {
        _coordinator = coordinator;
        _store = store;
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        try
        {
            (_enumerator, _) = DeviceNotifications.Subscribe(_notifier);

            _notifier.StateChanged += (id, state) => Record(IsActive(state) ? "activated" : "deactivated", id);
            _notifier.Added += id => Record("added", id);
            _notifier.Removed += id => Record("removed", id);
            _notifier.DefaultChanged += (flow, _, id) =>
            {
                if (flow == EDataFlow.Render)
                {
                    Record("default_changed", id);
                }
            };

            RefreshNames();
            Log?.Invoke("[设备事件] 已订阅端点通知（插拔/状态/默认设备变更触发 rules.yaml 规则）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[设备事件] 订阅失败（不影响主流程）: {ex.Message}");
        }
    }

    private static bool IsActive(DeviceState state) => (state & DeviceState.Active) == DeviceState.Active;

    /// <summary>记录一条待处理事件并重启防抖（COM 回调线程不确定，只做最轻的事）。</summary>
    private void Record(string kind, string deviceId)
    {
        lock (_gate)
        {
            _pending.Add((kind, deviceId));
        }

        try
        {
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 已释放：忽略
        }
    }

    private void RefreshNames()
    {
        try
        {
            foreach (var device in DeviceManager.EnumerateRenderDevices())
            {
                _names[device.Id] = device.FriendlyName;
            }
        }
        catch
        {
            // 枚举失败沿用旧缓存（已消失设备也能靠缓存名匹配 removed 事件）
        }
    }

    /// <summary>防抖到点：合并处理窗口内的全部事件，按规则匹配并应用场景。</summary>
    private async void Flush()
    {
        if (_disposed)
        {
            return;
        }

        if (_processing)
        {
            // 上一轮未结束：顺延一个周期，避免重入
            try
            {
                _debounce.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }

        List<(string Kind, string DeviceId)> events;
        lock (_gate)
        {
            events = _pending.ToList();
            _pending.Clear();
        }

        if (events.Count == 0)
        {
            DevicesChanged?.Invoke();
            return;
        }

        _processing = true;
        try
        {
            RefreshNames();

            var ruleSet = RuleSet.Load();
            foreach (var rule in ruleSet.DeviceEvents)
            {
                string kind = rule.On.Trim().ToLowerInvariant();
                string fragment = rule.Device.Trim();
                bool matched = events.Any(e =>
                    (kind is "changed" or "" || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    && (fragment.Length == 0
                        || (_names.TryGetValue(e.DeviceId, out var name)
                            && name.Contains(fragment, StringComparison.OrdinalIgnoreCase))));

                if (!matched || string.IsNullOrEmpty(rule.Apply))
                {
                    continue;
                }

                if (ruleSet.Get(rule.Apply) is null)
                {
                    Log?.Invoke($"[设备事件] 规则命中但 Profile「{rule.Apply}」不存在，跳过");
                    continue;
                }

                try
                {
                    var report = await new ProfileService(_store, ruleSet)
                        .ApplyAsync(_coordinator, rule.Apply, replace: true).ConfigureAwait(false);
                    Log?.Invoke($"[设备事件] {rule.On}{(fragment.Length > 0 ? $"/{fragment}" : "")} → " +
                        $"场景 {report.ProfileName}：命中 {report.MatchedProcesses} 进程，" +
                        $"应用 {report.AppliedRoutes.Count} 条，{report.Elapsed.TotalMilliseconds:F0} ms");
                    RoutesChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[设备事件] 应用场景 {rule.Apply} 失败: {ex.Message}");
                }
            }

            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[设备事件] 处理异常（忽略，下轮继续）: {ex.Message}");
        }
        finally
        {
            _processing = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _debounce.Dispose();
        }
        catch
        {
        }

        DeviceNotifications.Unsubscribe(_enumerator, _notifier);
    }
}
