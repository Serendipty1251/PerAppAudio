using System.Diagnostics;
using PerAppAudio.RoutingEngine;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RuleEngine;

/// <summary>一次 Profile 应用的结果报告。</summary>
/// <param name="ProfileName">Profile 名。</param>
/// <param name="MatchedProcesses">命中的进程数（已按优先级裁决，一进程只计一次）。</param>
/// <param name="AppliedRoutes">实际应用的路由数（含每条说明）。</param>
/// <param name="Elapsed">总耗时（验收指标：一键切换 200ms 内生效）。</param>
public sealed record ProfileApplyReport(
    string ProfileName,
    int MatchedProcesses,
    IReadOnlyList<string> AppliedRoutes,
    TimeSpan Elapsed)
{
    public bool Success => AppliedRoutes.Count > 0;

    /// <summary>非致命告警（设备别名解析失败、规则字段缺失等）；不影响已成功的路由。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Profile 应用服务（P2）：把 rules.yaml 中的某个场景实例化为当前路由。
/// 语义：profile 是「模板库」，RouteStore 是「当前启用的路由集合」（单一真相源）——
/// apply 时把 Profile 的规则写入 RouteStore 并立即经 RoutingCoordinator 应用；
/// --replace 模式先拆除旧集合中不在新 Profile 里的路由（切换场景的完整语义）。
///
/// 匹配裁决（v2.0 主功能强化）：同一进程命中多条规则时按
/// **精确路径 &gt; 进程名 &gt; 通配符** 取最优，后者让位；同级取文件中靠前的一条。
/// 规则的 device 支持 devices: 段别名；mode 省略时用全局 default_mode。
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
    /// 应用一个 Profile：解析设备（含别名）→ 匹配运行中进程（含通配符）→ 按优先级裁决
    /// → 写 RouteStore → 立即路由。replace=true 时先拆除当前集合中不被新 Profile 覆盖的路由。
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
        var warnings = new List<string>();

        // 1. 逐规则匹配进程（通配符在此展开），得到候选集
        var plans = new List<RulePlan>();
        for (int index = 0; index < profile.Rules.Count; index++)
        {
            var rule = profile.Rules[index];
            if (string.IsNullOrWhiteSpace(rule.Process) || string.IsNullOrWhiteSpace(rule.Device))
            {
                warnings.Add($"规则 #{index + 1} 缺少 process/device，已跳过");
                continue;
            }

            plans.Add(new RulePlan(
                Rule: rule,
                Index: index,
                Priority: ProcessFinder.Specificity(rule.Process),
                DeviceRef: _rules.ResolveDeviceName(rule.Device),
                Matches: ProcessFinder.FindMatches(rule.Process)));
        }

        // 2. 每进程取最优规则（优先级数值越小越优；同级取文件中靠前者）
        var winners = new Dictionary<int, (RulePlan Plan, string Path)>();
        foreach (var plan in plans)
        {
            foreach (var (pid, path) in plan.Matches)
            {
                if (!winners.TryGetValue(pid, out var current)
                    || plan.Priority < current.Plan.Priority
                    || (plan.Priority == current.Plan.Priority && plan.Index < current.Plan.Index))
                {
                    winners[pid] = (plan, path);
                }
            }
        }

        // 3. 场景切换：拆除旧集合中不被新 Profile 覆盖的路由（按文件名覆盖判断）
        if (replace && !dryRun)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                covered.Add(Path.GetFileName(plan.Rule.Process));
                foreach (var (_, path) in plan.Matches)
                {
                    covered.Add(Path.GetFileName(path));
                }
            }

            foreach (var old in _store.Routes)
            {
                if (covered.Contains(Path.GetFileName(old.ProcessPath)))
                {
                    continue;
                }
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

        // 4. 规则意图无条件写入 RouteStore（单一真相源；进程未运行时由 ctl watch 接管）
        if (!dryRun)
        {
            foreach (var plan in plans)
            {
                // store 键规范化：有运行中匹配进程 → 用其完整路径（强绑定）；
                // 否则按规则字面（完整路径或弱名）。避免同程序出现多个键。
                string storeKey = plan.Matches.Count > 0
                    ? plan.Matches[0].Path
                    : (Path.IsPathRooted(plan.Rule.Process) ? plan.Rule.Process : Path.GetFileName(plan.Rule.Process));

                _store.Upsert(new StoredRoute
                {
                    ProcessPath = storeKey,
                    DeviceId = string.Empty,   // 设备解析放后面（避免为未命中的规则白解析）
                    DeviceName = plan.Rule.Device,
                    Mode = ParseMode(plan.Rule.Mode, _rules.DefaultMode),
                    Volume = plan.Rule.Volume,
                    Mute = plan.Rule.Mute,
                });
            }
        }

        // 5. 对裁决后的进程立即路由
        foreach (var (pid, (plan, path)) in winners.OrderBy(w => w.Key))
        {
            var mode = ParseMode(plan.Rule.Mode, _rules.DefaultMode);

            if (dryRun)
            {
                applied.Add($"[dry-run] {Path.GetFileName(path)}（PID {pid}）→ {plan.DeviceRef} [{Describe(mode)}] vol {plan.Rule.Volume:F2}");
                continue;
            }

            WasapiDevice device;
            try
            {
                device = DeviceManager.ResolveDevice(plan.DeviceRef);
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(path)}：设备「{plan.DeviceRef}」解析失败（{ex.Message}），该进程未路由");
                continue;
            }

            // 回填真实设备信息到 RouteStore（步骤 4 写的是规则字面）
            _store.Upsert(new StoredRoute
            {
                ProcessPath = path,
                DeviceId = device.Id,
                DeviceName = device.FriendlyName,
                Mode = mode,
                Volume = plan.Rule.Volume,
                Mute = plan.Rule.Mute,
            });

            var command = new RoutingCommand(
                pid, path,
                new AudioDeviceRef(device.FriendlyName, device.Id),
                plan.Rule.Volume, plan.Rule.Mute);

            var report = await coordinator.ApplyRouteAsync(command, mode, ct).ConfigureAwait(false);
            applied.Add($"{Path.GetFileName(path)}（PID {pid}）→ {device.FriendlyName}" +
                $" [B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}] vol {plan.Rule.Volume:F2}");
        }

        sw.Stop();
        return new ProfileApplyReport(profile.Name, winners.Count, applied, sw.Elapsed)
        {
            Warnings = warnings,
        };
    }

    private static RoutingMode ParseMode(string text, string globalDefault)
    {
        string t = string.IsNullOrWhiteSpace(text) ? globalDefault : text;
        return t.ToLowerInvariant() switch
        {
            "a" or "loopback" => RoutingMode.Loopback,
            "b" or "endpoint" => RoutingMode.Endpoint,
            _ => RoutingMode.Auto,
        };
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Loopback => "A",
        RoutingMode.Endpoint => "B",
        RoutingMode.Auto => "auto",
        _ => mode.ToString(),
    };

    /// <summary>一条规则的匹配计划（含裁决优先级）。</summary>
    private sealed record RulePlan(
        ProfileRule Rule,
        int Index,
        int Priority,
        string DeviceRef,
        List<(int Pid, string Path)> Matches);
}

/// <summary>按进程名 / 完整路径 / 通配符查找运行中的进程（Profile 匹配与 RouteStore 扫描共用）。</summary>
public static class ProcessFinder
{
    /// <summary>查找运行中进程（旧接口：进程名或完整路径，不支持通配符）。</summary>
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

    /// <summary>
    /// 按模式匹配运行中进程，返回 (PID, 可执行完整路径)。
    /// 支持：精确完整路径 / 进程名（带或不带 .exe）/ 通配符（* 多字符，? 单字符）。
    /// 提权或系统进程 MainModule 不可读时跳过（无法可靠匹配）。
    /// </summary>
    public static List<(int Pid, string Path)> FindMatches(string pattern)
    {
        var result = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return result;
        }

        int priority = Specificity(pattern);
        string patternName = Path.GetFileName(pattern);
        string patternStem = Path.GetFileNameWithoutExtension(patternName);

        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // 不可读：跳过
            }

            if (!string.IsNullOrEmpty(path))
            {
                string name = Path.GetFileName(path);
                bool hit = priority switch
                {
                    0 => string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase),
                    1 => string.Equals(name, patternName, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetFileNameWithoutExtension(name), patternStem, StringComparison.OrdinalIgnoreCase),
                    _ => WildcardMatch(pattern, name) || WildcardMatch(pattern, path),
                };

                if (hit)
                {
                    result.Add((process.Id, path));
                }
            }

            process.Dispose();
        }

        return result;
    }

    /// <summary>
    /// 列出运行中可识别进程（PID / 名称 / 路径），供「按进程添加路由」与诊断使用。
    /// 仅返回 MainModule 可读的进程（提权/系统进程跳过）。
    /// </summary>
    public static List<(int Pid, string Name, string Path)> ListRunning()
    {
        var result = new List<(int Pid, string Name, string Path)>();
        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(path))
            {
                result.Add((process.Id, process.ProcessName, path));
            }

            process.Dispose();
        }

        return result
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Pid)
            .ToList();
    }

    /// <summary>模式是否含通配符。</summary>
    public static bool IsWildcard(string pattern) =>
        !string.IsNullOrEmpty(pattern) && pattern.IndexOfAny(new[] { '*', '?' }) >= 0;

    /// <summary>
    /// 匹配优先级（数值越小越具体，裁决时越优先）：
    /// 0 = 精确完整路径；1 = 进程名；2 = 通配符。
    /// </summary>
    public static int Specificity(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return 3;
        }
        if (IsWildcard(pattern))
        {
            return 2;
        }
        return Path.IsPathRooted(pattern) ? 0 : 1;
    }

    /// <summary>通配符匹配（* 任意多字符，? 单字符；不区分大小写）。经典双指针实现，无正则。</summary>
    public static bool WildcardMatch(string pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return string.IsNullOrEmpty(value);
        }

        int p = 0;
        int v = 0;
        int star = -1;
        int mark = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
