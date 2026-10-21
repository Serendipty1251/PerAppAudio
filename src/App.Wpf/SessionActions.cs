using PerAppAudio.RoutingEngine;
using PerAppAudio.RuleEngine;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 会话级音频动作（全局热键 / 托盘弹出面板共用，保证两条入口行为一致）。
/// 每个动作都同时下发 Coordinator（实时改道）与 Store（持久路由）。
/// </summary>
public static class SessionActions
{
    /// <summary>动作结果：Message=简短结果（OSD 用），Detail=完整结果（日志用），Success=是否成功执行。</summary>
    public sealed record Result(string Message, string Detail, bool Success);

    /// <summary>
    /// 把进程音频循环切到下一个在线设备（仅渲染会话；无会话/无设备时返回失败结果）。
    /// </summary>
    public static async Task<Result> CycleToNextDeviceAsync(AppHost host, int pid, string appName)
    {
        bool hasSession = false;
        try
        {
            hasSession = WasapiSessionSource.EnumerateSessions().Any(s => s.ProcessId == pid);
        }
        catch
        {
            // 枚举失败按无会话处理，走提示分支
        }

        if (!hasSession)
        {
            string msg = string.Format(L10n.Get("OsdNoSessionApp"), appName);
            return new Result(msg, msg + L10n.Get("OsdNoSessionHint"), false);
        }

        var online = DeviceManager.EnumerateRenderDevices(DeviceState.Active);
        if (online.Count == 0)
        {
            return new Result(L10n.Get("OsdNoOnlineDevice"), L10n.Get("OsdNoOnlineDevice"), false);
        }

        // H2：current 优先取活动路由的目标设备——A 路由不移动应用会话（只采集端抑制），
        // 应用会话仍留在原设备；取会话所在设备会让热键循环永远停在「原设备→第一个目标」。
        string? routedTarget = host.Coordinator.GetLiveRoutes()
            .FirstOrDefault(l => l.ProcessId == pid && l.LoopbackActive).TargetDevice;
        string current = routedTarget
            ?? (WasapiSessionSource.GetDeviceOfProcesses().TryGetValue(pid, out var cur) ? cur : string.Empty);
        int idx = -1;
        for (int i = 0; i < online.Count; i++)
        {
            if (string.Equals(online[i].FriendlyName, current, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        var next = online[(idx + 1) % online.Count];
        float vol = WasapiSessionSource.GetSessionVolumeStates(pid).FirstOrDefault()?.Volume ?? 1f;
        string processPath = RouteStore.ResolveProcessPath(pid);

        var command = new RoutingCommand(pid, processPath,
            new AudioDeviceRef(next.FriendlyName, next.Id), vol, false);
        var report = await host.Coordinator.ApplyRouteAsync(command, RoutingMode.Auto).ConfigureAwait(false);

        host.Store.Upsert(new StoredRoute
        {
            ProcessPath = processPath,
            DeviceId = next.Id,
            DeviceName = next.FriendlyName,
            Mode = RoutingMode.Auto,
            Volume = vol,
        });

        string detail = $"{appName}（PID {pid}）→ {DeviceNaming.Display(next.FriendlyName)} | " +
            $"B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}";
        return new Result($"{appName} → {DeviceNaming.Display(next.FriendlyName)}", detail, report.Success);
    }

    /// <summary>
    /// 静音切换。返回 null 表示进程没有音频会话。
    /// </summary>
    public static (bool Muted, int Affected)? ToggleMute(int pid)
    {
        var states = WasapiSessionSource.GetSessionVolumeStates(pid);
        if (states.Count == 0)
        {
            return null;
        }

        bool nowMuted = !states[0].Muted;
        int affected = WasapiSessionSource.ApplySessionControl(pid, mute: nowMuted);
        return (nowMuted, affected);
    }

    /// <summary>前台应用会话音量增减（delta 有符号，调用方负责 clamp 前的步进）。</summary>
    public static (float Before, float After, int Affected)? NudgeVolume(int pid, float delta)
    {
        var states = WasapiSessionSource.GetSessionVolumeStates(pid);
        if (states.Count == 0)
        {
            return null;
        }

        float next = Math.Clamp((float)Math.Round(states[0].Volume + delta, 2), 0f, 1f);
        int affected = WasapiSessionSource.ApplySessionControl(pid, volume: next);
        return (states[0].Volume, next, affected);
    }
}
