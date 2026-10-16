using System.Diagnostics;
using PerAppAudio.RoutingEngine;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RuleEngine;

/// <summary>一次 Profile 应用的结果报告。</summary>
/// <param name="ProfileName">Profile 名。</param>
/// <param name="MatchedProcesses">命中的进程数。</param>
/// <param name="AppliedRoutes">实际应用的路由数（含每条说明）。</param>
/// <param name="Elapsed">总耗时（验收指标：一键切换 200ms 内生效）。</param>
public sealed record ProfileApplyReport(
    string ProfileName,
    int MatchedProcesses,
    IReadOnlyList<string> AppliedRoutes,
    TimeSpan Elapsed)
{
    public bool Success => AppliedRoutes.Count > 0;
}

/// <summary>
/// Profile 应用服务（P2）：把 rules.yaml 中的某个场景实例化为当前路由。
/// 语义：profile 是「模板库」，RouteStore 是「当前启用的路由集合」（单一真相源）——
/// apply 时把 Profile 的规则写入 RouteStore 并立即经 RoutingCoordinator 应用；
/// --replace 模式先拆除旧集合中不在新 Profile 里的路由（切换场景的完整语义）。
/// </summary>
public sealed class ProfileService
{
    private readonly RouteStore _store;
    private readonly RuleSet _rules;

    public ProfileService(RouteStore store, RuleSet rules)
    {
        _store = store;
        _rules = rules;
    }

    public IReadOnlyList<Profile> Profiles => _rules.Profiles;

    /// <summary>列出所有 Profile 名与规则数。</summary>
    public IEnumerable<(string Name, string Description, int Rules)> List() =>
        _rules.Profiles.Select(p => (p.Name, p.Description, p.Rules.Count));

    /// <summary>
    /// 应用一个 Profile：解析设备 → 匹配运行中进程 → 写 RouteStore → 立即路由。
    /// replace=true 时先拆除当前集合中不被新 Profile 覆盖的路由（场景切换语义）。
    /// </summary>
    public async Task<ProfileApplyReport> ApplyAsync(
        RoutingCoordinator coordinator,
        string profileName,
        bool replace = false,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var profile = _rules.Get(profileName)
            ?? throw new InvalidOperationException($"Profile「{profileName}」不存在（rules.yaml 中共 {Profiles.Count} 个）");

        var sw = Stopwatch.StartNew();
        var applied = new List<string>();

        // 0. 场景切换：拆除旧集合中不在新 Profile 进程集合里的路由
        if (replace && !dryRun)
        {
            var newProcesses = new HashSet<string>(
                profile.Rules.Select(r => Path.GetFileName(r.Process)), StringComparer.OrdinalIgnoreCase);
            foreach (var old in _store.Routes)
            {
                if (!newProcesses.Contains(Path.GetFileName(old.ProcessPath)))
                {
                    // 拆 A + 清 B2 + 删记录（目标进程可能已不在，Remove 内部容错）
                    foreach (var proc in ProcessFinder.Find(old.ProcessPath))
                    {
                        using (proc)
                        {
                            await coordinator.RemoveRouteAsync(proc.Id, clearEndpoint: true, ct).ConfigureAwait(false);
                        }
                    }
                    _store.Remove(old.ProcessPath);
                }
            }
        }

        // 1. 逐规则解析设备 + 匹配进程 + 应用
        int matched = 0;
        foreach (var rule in profile.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Process) || string.IsNullOrWhiteSpace(rule.Device))
            {
                continue;
            }

            var device = DeviceManager.ResolveDevice(rule.Device);
            var mode = ParseMode(rule.Mode);

            var processes = ProcessFinder.Find(rule.Process);

            // store 键规范化：有运行中匹配进程 → 用其完整路径（强绑定）；
            // 否则按规则字面（完整路径或弱名）。避免同程序出现多个键。
            string storeKey = processes.Count > 0
                ? RouteStore.ResolveProcessPath(processes[0].Id)
                : (Path.IsPathRooted(rule.Process) ? rule.Process : Path.GetFileName(rule.Process));

            // 规则意图无条件写入 RouteStore（单一真相源；进程未运行时由 ctl watch 接管）
            if (!dryRun)
            {
                _store.Upsert(new StoredRoute
                {
                    ProcessPath = storeKey,
                    DeviceId = device.Id,
                    DeviceName = device.FriendlyName,
                    Mode = mode,
                    Volume = rule.Volume,
                    Mute = rule.Mute,
                });
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    matched++;
                    string processPath = RouteStore.ResolveProcessPath(process.Id);

                    if (dryRun)
                    {
                        applied.Add($"[dry-run] {Path.GetFileName(processPath)}（PID {process.Id}）→ {device.FriendlyName} [{Describe(mode)}] vol {rule.Volume:F2}");
                        continue;
                    }

                    var command = new RoutingCommand(
                        process.Id, processPath,
                        new AudioDeviceRef(device.FriendlyName, device.Id),
                        rule.Volume, rule.Mute);

                    var report = await coordinator.ApplyRouteAsync(command, mode, ct).ConfigureAwait(false);
                    applied.Add($"{Path.GetFileName(processPath)}（PID {process.Id}）→ {device.FriendlyName}" +
                        $" [B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}] vol {rule.Volume:F2}");
                }
            }
        }

        sw.Stop();
        return new ProfileApplyReport(profile.Name, matched, applied, sw.Elapsed);
    }

    private static RoutingMode ParseMode(string text) => text.ToLowerInvariant() switch
    {
        "a" or "loopback" => RoutingMode.Loopback,
        "b" or "endpoint" => RoutingMode.Endpoint,
        _ => RoutingMode.Auto,
    };

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Loopback => "A",
        RoutingMode.Endpoint => "B",
        RoutingMode.Auto => "auto",
        _ => mode.ToString(),
    };
}

/// <summary>按进程名 / 完整路径查找运行中的进程（Profile 匹配与 RouteStore 扫描共用）。</summary>
public static class ProcessFinder
{
    public static List<Process> Find(string process)
    {
        var matched = new List<Process>();
        string fileName = Path.GetFileName(process);
        if (string.IsNullOrEmpty(fileName))
        {
            return matched;
        }

        bool weak = !Path.IsPathRooted(process); // 弱绑定：只有文件名
        string name = Path.GetFileNameWithoutExtension(fileName);

        foreach (var processItem in Process.GetProcessesByName(name))
        {
            bool hit = weak;
            if (!hit)
            {
                try
                {
                    hit = string.Equals(processItem.MainModule?.FileName, process, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // 提权 / 系统进程 MainModule 不可读 → 跳过
                }
            }

            if (hit)
            {
                matched.Add(processItem);
            }
            else
            {
                processItem.Dispose();
            }
        }

        return matched;
    }
}
