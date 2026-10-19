using PerAppAudio.RoutingEngine;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RuleEngine;

/// <summary>
/// 全量路由重建（睡眠唤醒 / 手动 `ctl rebuild` 共用）。
/// 先显式拆除所有 A 链路（保留 B2 注册表持久化），再按路由表重新应用。
/// 为什么不能只调 ApplyRouteAsync：A 链路可能已死（睡眠后 WASAPI 客户端失效），
/// 而 _live 仍标记 LoopbackActive=true，ApplyRouteAsync 会认为「已有活动 A 路由」而复用死链。
/// </summary>
public static class RouteRebuilder
{
    public static async Task<int> RebuildAllAsync(
        RoutingCoordinator coordinator,
        RouteStore store,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var live = coordinator.GetLiveRoutes();
        foreach (var l in live)
        {
            try
            {
                await coordinator.RemoveRouteAsync(l.ProcessId, clearEndpoint: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[重建] 拆除旧链路 PID {l.ProcessId} 失败: {ex.Message}");
            }
        }

        store.Load();
        int rebuilt = 0;
        foreach (var route in store.Routes)
        {
            foreach (var process in ProcessFinder.Find(route.ProcessPath))
            {
                using (process)
                {
                    try
                    {
                        var device = DeviceManager.ResolveDevice(route.DeviceName);
                        var command = new RoutingCommand(
                            process.Id, route.ProcessPath,
                            new AudioDeviceRef(device.FriendlyName, device.Id),
                            route.Volume, route.Mute);
                        await coordinator.ApplyRouteAsync(command, route.Mode, ct).ConfigureAwait(false);
                        rebuilt++;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[重建] {Path.GetFileName(route.ProcessPath)} 失败: {ex.Message}");
                    }
                }
            }
        }

        log?.Invoke($"[重建] 已拆除 {live.Count} 条旧链路，重新应用 {rebuilt} 条路由");
        return rebuilt;
    }
}
