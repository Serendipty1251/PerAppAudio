using System.Diagnostics;
using System.Runtime.InteropServices;
using PerAppAudio.RoutingEngine;
using PerAppAudio.RoutingEngine.Audio;
using PerAppAudio.RoutingEngine.Backends;
using PerAppAudio.RuleEngine;
using PerAppAudio.SessionManager;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.App.Cli;

/// <summary>
/// P0-1 诊断 CLI：设备枚举 / 会话枚举 / 手段A 路由试验与量测。
/// 正式 CLI（调用层）在 P2 建设。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (args.Length == 0) return Usage();
            return args[0].ToLowerInvariant() switch
            {
                "devices" => ListDevices(),
                "sessions" => ListSessions(args),
                "route" => RunRoute(args),
                "endpoint" => RunEndpoint(args),
                "ctl" => RunCtl(args),
                "devlisten" => RunDevListen(args),
                "test" => RunInteropTest(),
                "help" or "--help" or "-h" => Usage(),
                var cmd => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            PerAppAudio P0 诊断 CLI

             devices                      列出渲染设备
             sessions [--device <ID|名称>] 列出音频会话（默认默认设备）
             route --pid <PID> --to <ID|名称> [--ms <时长>] [--buffer <缓冲ms>] [--keep]
                                         手段A 路由试验（默认 10 秒后自动拆除）
             endpoint probe               探测手段B2（IAudioPolicyConfigFactory）可用性
             endpoint set --pid <PID> --to <ID|名称>   手段B2：设置每进程默认端点
             endpoint get --pid <PID>     手段B2：读回每进程默认端点
             endpoint clear --pid <PID>   手段B2：清除端点覆盖
             devlisten --device <ID|名称> [--ms <时长>]   监听设备播放能量（验证迁移用）

             ctl add --pid <PID> --to <ID|名称> [--mode auto|a|b] [--volume 0-1]
                                         P1 控制闭环：立即改道 + 写入持久路由表
             ctl remove --pid <PID>       拆除路由 + 清 B2 覆盖 + 删持久记录
             ctl list                     查看持久路由表与运行中匹配进程
             ctl watch [--interval ms]    常驻协调：看门狗 + 新进程自动应用路由（Ctrl+C 退出）

            示例:
             devices
             route --pid 12345 --to "Realtek HD Audio 2nd output" --ms 30000
             endpoint set --pid 12345 --to "VoiceMeeter VAIO3"
             ctl add --pid 12345 --to "VoiceMeeter VAIO3" --mode auto
             ctl watch
            """);
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        return Usage();
    }

    /// <summary>设备级 Loopback 探针：监听指定设备正在播放的音频能量（验证音频迁移用）。</summary>
    private static int RunDevListen(string[] args)
    {
        var to = GetOption(args, "--device") ?? GetOption(args, "--to") ?? throw new InvalidOperationException("缺少 --device");
        int ms = int.TryParse(GetOption(args, "--ms"), out int m) ? m : 3000;
        var device = DeviceManager.ResolveDevice(to);

        Console.WriteLine($"监听设备: {device.FriendlyName}（{ms} ms）...");
        var probe = DeviceLoopbackProbe.Listen(device.Id, ms);
        double rms = probe.FinalRms();
        string verdict = rms > 0.001 ? "有信号" : "静默";
        Console.WriteLine($"[结果] RMS {rms:F6} / Peak {probe.Peak:F6} / 采样 {probe.SampleCount} → {verdict}");
        return 0;
    }

    /// <summary>
    /// 手段B2 诊断：每进程默认端点设置（IAudioPolicyConfigFactory，Win10 21H2+）。
    /// endpoint probe | set --pid N --to <ID|名称> | get --pid N | clear --pid N
    /// </summary>
    private static int RunEndpoint(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "probe";
        var backend = new EndpointBackend();

        switch (sub)
        {
            case "probe":
            {
                var probe = backend.ProbeAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"[探测] 手段B2 可用性: {probe.Available} {probe.Reason}");
                return probe.Available ? 0 : 1;
            }

            case "set":
            {
                if (!int.TryParse(GetOption(args, "--pid"), out int pid) || pid <= 0)
                {
                    Console.Error.WriteLine("缺少或非法的 --pid");
                    return 2;
                }
                var to = GetOption(args, "--to") ?? throw new InvalidOperationException("缺少 --to");
                var device = DeviceManager.ResolveDevice(to);
                var command = new RoutingCommand(pid, $"pid:{pid}", new AudioDeviceRef(device.FriendlyName, device.Id), 1.0f, false);
                var result = backend.ApplyAsync(command).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"[设置] {(result.Success ? "成功" : "失败")} {result.Message}");
                if (result.Success)
                {
                    var readback = backend.GetPersistedEndpoint(pid);
                    Console.WriteLine($"[读回] {(readback is null ? "读取失败" : readback.Length == 0 ? "(跟随系统默认)" : readback)}");
                }
                return result.Success ? 0 : 1;
            }

            case "get":
            {
                if (!int.TryParse(GetOption(args, "--pid"), out int pid) || pid <= 0)
                {
                    Console.Error.WriteLine("缺少或非法的 --pid");
                    return 2;
                }
                var route = backend.GetPersistedEndpoint(pid);
                if (route is null)
                {
                    Console.WriteLine("[读回] 失败（手段B2 不可用）");
                    return 1;
                }
                Console.WriteLine($"[读回] {(route.Length == 0 ? "(跟随系统默认)" : route)}");
                return 0;
            }

            case "clear":
            {
                if (!int.TryParse(GetOption(args, "--pid"), out int pid) || pid <= 0)
                {
                    Console.Error.WriteLine("缺少或非法的 --pid");
                    return 2;
                }
                var command = new RoutingCommand(pid, $"pid:{pid}", new AudioDeviceRef("(clear)", "(clear)"), 1.0f, false);
                var result = backend.RemoveAsync(command).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"[清除] {(result.Success ? "成功" : "失败")} {result.Message}");
                return result.Success ? 0 : 1;
            }

            default:
                Console.Error.WriteLine($"未知子命令: {sub}（probe / set / get / clear）");
                return 2;
        }
    }

    private static int RunInteropTest()
    {
        void Step(string name, Action action)
        {
            Console.WriteLine($"--- {name}");
            Console.Out.Flush();
            action();
            Console.WriteLine($"    OK");
            Console.Out.Flush();
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        Step("创建 MMDeviceEnumerator", () =>
        {
            enumerator = WasapiInterop.DeviceManager.CreateEnumerator();
        });

        Step("EnumAudioEndpoints(Render, All)", () =>
        {
            var collection = enumerator!.EnumAudioEndpoints(EDataFlow.Render, DeviceState.All);
            Console.WriteLine($"    GetCount = {collection.GetCount()}");
        });

        Step("GetDefaultAudioEndpoint(Render, Console)", () =>
        {
            device = enumerator!.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console);
        });

        Step("device.GetState() [retval 风格]", () =>
        {
            Console.WriteLine($"    State = {device!.GetState()}");
        });

        Step("device.GetState(out) [out 参数风格]", () =>
        {
            var alt = (IMMDeviceAlt)device!;
            alt.GetState(out DeviceState state);
            Console.WriteLine($"    State = {state}");
        });

        Step("alt.GetId(out string) [out 参数风格]", () =>
        {
            var alt = (IMMDeviceAlt)device!;
            alt.GetId(out string id);
            Console.WriteLine($"    Id = {id}");
        });

        Step("collection.Item(0).GetState()", () =>
        {
            var collection = enumerator!.EnumAudioEndpoints(EDataFlow.Render, DeviceState.All);
            var item = collection.Item(0);
            Console.WriteLine($"    State = {item.GetState()}");
            var itemAlt = (IMMDeviceAlt)item;
            itemAlt.GetState(out DeviceState s2);
            itemAlt.GetId(out string id2);
            Console.WriteLine($"    [alt] State = {s2}, Id = {id2}");
        });

        return 0;
    }

    // 同 GUID 的第二声明：命中同一 RCW，用于对照 retval / out 两种封送风格
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceAlt
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        void OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        void GetState(out DeviceState state);
    }

    private static int ListDevices()
    {
        var devices = DeviceManager.EnumerateRenderDevices();
        Console.WriteLine($"{"#",-3} {"友好名",-40} {"状态",-10} 默认角色");
        Console.WriteLine(new string('-', 90));
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var roles = string.Join(",",
                d.IsDefaultConsole ? "控制台" : "",
                d.IsDefaultMultimedia ? "多媒体" : "",
                d.IsDefaultCommunications ? "通信" : "").Trim(',');
            Console.WriteLine($"{i,-3} {Truncate(d.FriendlyName, 38),-40} {d.State,-10} {roles}");
            Console.WriteLine($"    ID: {d.Id}");
            Console.WriteLine($"    Mix: {DeviceManager.DescribeDeviceMixFormat(d.Id)}");
        }
        return 0;
    }

    private static int ListSessions(string[] args)
    {
        string? deviceArg = GetOption(args, "--device");
        var device = deviceArg is null
            ? DeviceManager.ResolveDevice(GetDefaultDeviceId())
            : DeviceManager.ResolveDevice(deviceArg);

        Console.WriteLine($"设备: {device.FriendlyName}");
        Console.WriteLine($"{"PID",-8} {"进程",-28} {"会话",-24} 状态");
        Console.WriteLine(new string('-', 78));

        var sessions = WasapiSessionSource.EnumerateSessions(device.Id);
        foreach (var s in sessions)
        {
            var name = Path.GetFileName(s.ProcessPath);
            if (string.IsNullOrEmpty(name)) name = s.ProcessPath;
            Console.WriteLine($"{s.ProcessId,-8} {Truncate(name, 26),-28} {Truncate(s.DisplayName, 22),-24} {s.State}");
        }

        Console.WriteLine($"共 {sessions.Count} 个会话");
        return 0;
    }

    private static int RunRoute(string[] args)
    {
        if (!int.TryParse(GetOption(args, "--pid"), out int pid) || pid <= 0)
        {
            Console.Error.WriteLine("缺少或非法的 --pid");
            return 2;
        }

        var toArg = GetOption(args, "--to") ?? throw new InvalidOperationException("缺少 --to");
        var device = DeviceManager.ResolveDevice(toArg);
        int durationMs = int.TryParse(GetOption(args, "--ms"), out int ms) ? ms : 10_000;
        int bufferMs = int.TryParse(GetOption(args, "--buffer"), out int buf) ? buf : 10;
        bool keep = args.Contains("--keep", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"目标进程 : PID {pid}");
        Console.WriteLine($"目标设备 : {device.FriendlyName}");
        Console.WriteLine($"试验时长 : {durationMs} ms（缓冲 {bufferMs} ms）");
        Console.WriteLine();

        var backend = new LoopbackBackend();
        var probe = backend.ProbeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"[探测] 手段A 可用性: {probe.Available} {probe.Reason}");
        if (!probe.Available) return 1;

        var command = new RoutingCommand(pid, $"pid:{pid}", new AudioDeviceRef(device.FriendlyName, device.Id), 1.0f, false);
        var result = backend.ApplyAsync(command).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"[启动] {(result.Success ? "成功" : "失败")} {result.Message}");
        if (!result.Success) return 1;
        Console.WriteLine("请在目标进程中播放音频，观察下面的统计。");
        Console.WriteLine();

        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        try
        {
            while (wall.ElapsedMilliseconds < durationMs)
            {
                Thread.Sleep(1000);
                PrintLive(backend, pid, wall.ElapsedMilliseconds);
            }
        }
        finally
        {
            wall.Stop();
            var stats = backend.GetStats(pid);
            double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            double cpuPercent = cpuMs / (wall.ElapsedMilliseconds * Environment.ProcessorCount) * 100.0;

            if (!keep)
            {
                var remove = backend.RemoveAsync(command).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"[拆除] {(remove.Success ? "成功" : "失败")} {remove.Message}");
            }
            else
            {
                Console.WriteLine("[拆除] --keep 指定，路由保持运行");
            }

            Console.WriteLine();
            Console.WriteLine("==== PoC-1 手段A 报告 ====");
            Console.WriteLine($"运行时长        : {wall.ElapsedMilliseconds} ms");
            Console.WriteLine($"进程 CPU 占用   : {cpuPercent:F2}%（单核折算，{Environment.ProcessorCount} 逻辑核）");
            if (stats is null)
            {
                Console.WriteLine("统计           : 无数据");
            }
            else
            {
                var (packets, framesCaptured, framesWritten, framesDropped, disc, silent, avgMs, maxMs) = stats.Value;
                Console.WriteLine($"数据包          : {packets}");
                Console.WriteLine($"采集帧/写入帧   : {framesCaptured} / {framesWritten}");
                Console.WriteLine($"丢弃帧          : {framesDropped}");
                Console.WriteLine($"不连续(爆音)    : {disc}");
                Console.WriteLine($"静音包          : {silent}");
                Console.WriteLine($"单包处理耗时    : 平均 {avgMs:F3} ms / 最大 {maxMs:F3} ms");
                double seconds = wall.ElapsedMilliseconds / 1000.0;
                double audioSeconds = framesWritten / 48000.0;
                Console.WriteLine($"写入音频时长    : {audioSeconds:F1} s（实时率 {(seconds > 0 ? audioSeconds / seconds * 100 : 0):F1}%）");
            }
            Console.WriteLine("==========================");
        }

        return 0;
    }

    private static void PrintLive(LoopbackBackend backend, int pid, long elapsedMs)
    {
        var stats = backend.GetStats(pid);
        if (stats is null) return;
        var (packets, framesCaptured, framesWritten, framesDropped, disc, silent, avgMs, maxMs) = stats.Value;
        Console.WriteLine($"[{elapsedMs / 1000,4}s] 包 {packets,6} | 采集 {framesCaptured,9} | 写入 {framesWritten,9} | 丢弃 {framesDropped,7} | 不连续 {disc,5} | 静音 {silent,5} | 处理 avg {avgMs:F3} max {maxMs:F3} ms");
    }

    // ======================= P1 控制闭环（ctl） =======================

    /// <summary>
    /// P1 控制闭环 CLI：route add / remove / list / watch。
    /// add = 协调器立即改道（Auto 双管齐下）+ 持久路由表；
    /// watch = 常驻协调器（看门狗 + 对路由表中运行中的进程自动应用）。
    /// </summary>
    private static int RunCtl(string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        var store = new RouteStore().Load();

        switch (sub)
        {
            case "add":
                return CtlAdd(store, args);

            case "remove":
                return CtlRemove(store, args);

            case "list":
                return CtlList(store);

            case "watch":
                return CtlWatch(store, args);

            default:
                Console.Error.WriteLine($"未知子命令: {sub}（add / remove / list / watch）");
                return 2;
        }
    }

    private static int CtlAdd(RouteStore store, string[] args)
    {
        if (!int.TryParse(GetOption(args, "--pid"), out int pid) || pid <= 0)
        {
            Console.Error.WriteLine("缺少或非法的 --pid");
            return 2;
        }

        var toArg = GetOption(args, "--to") ?? throw new InvalidOperationException("缺少 --to");
        var device = DeviceManager.ResolveDevice(toArg);
        var mode = ParseMode(GetOption(args, "--mode") ?? "auto");
        float volume = float.TryParse(GetOption(args, "--volume"), out float v) ? Math.Clamp(v, 0f, 1f) : 1f;

        string processPath = RouteStore.ResolveProcessPath(pid);
        Console.WriteLine($"目标进程 : PID {pid}（{processPath}）");
        Console.WriteLine($"目标设备 : {device.FriendlyName}");
        Console.WriteLine($"手段     : {DescribeMode(mode)}");
        Console.WriteLine();

        using var coordinator = new RoutingCoordinator();
        var (loopbackProbe, endpointProbe) = coordinator.ProbeAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[探测] 手段A: {(loopbackProbe.Available ? "可用" : "不可用")}（{loopbackProbe.Reason}）");
        Console.WriteLine($"[探测] 手段B2: {(endpointProbe.Available ? "可用" : "不可用")}（{endpointProbe.Reason}）");

        var command = new RoutingCommand(pid, processPath, new AudioDeviceRef(device.FriendlyName, device.Id), volume, false);
        var report = coordinator.ApplyRouteAsync(command, mode).GetAwaiter().GetResult();

        Console.WriteLine($"[B2 持久化] {(report.EndpointApplied ? "✅" : "❌")} {report.EndpointMessage}");
        Console.WriteLine($"[A 实时迁移] {(report.LoopbackActive ? "✅" : "❌")} {report.LoopbackMessage}");
        Console.WriteLine($"[总评] {(report.Success ? "成功" : "失败")}，耗时 {report.Elapsed.TotalMilliseconds:F0} ms");

        store.Upsert(new StoredRoute
        {
            ProcessPath = processPath,
            DeviceId = device.Id,
            DeviceName = device.FriendlyName,
            Mode = mode,
            Volume = volume,
        });
        Console.WriteLine($"[持久化] 已写入路由表: {RouteStore.DefaultPath}");
        Console.WriteLine("[说明] A 实时迁移链路随本命令退出而拆除（B2 注册表持久化保留）；常驻迁移请运行 ctl watch");

        // add 是一次性命令：进程退出即拆（A 链路随本进程终止，B2 注册表持久化保留）
        return report.Success ? 0 : 1;
    }

    private static int CtlRemove(RouteStore store, string[] args)
    {
        if (!int.TryParse(GetOption(args, "--pid"), out int pid) || pid <= 0)
        {
            Console.Error.WriteLine("缺少或非法的 --pid");
            return 2;
        }

        string processPath = RouteStore.ResolveProcessPath(pid);
        var stored = store.Routes.FirstOrDefault(r =>
            string.Equals(r.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase));

        using var coordinator = new RoutingCoordinator();
        var result = coordinator.RemoveRouteAsync(pid, clearEndpoint: true).GetAwaiter().GetResult();
        Console.WriteLine($"[拆除] {(result.Success ? "成功" : "未命中")} {result.Message}");

        bool removed = store.Remove(processPath);
        if (stored is not null)
        {
            // 弱绑定（name.exe / pid:N）可能存的是别的键，兜底再删一次存储键
            removed |= store.Remove(stored.ProcessPath);
        }
        Console.WriteLine($"[持久化] {(removed ? "已从路由表删除" : "路由表中无记录")}");
        return 0;
    }

    private static int CtlList(RouteStore store)
    {
        var routes = store.Routes;
        Console.WriteLine($"持久路由表: {RouteStore.DefaultPath}");
        if (routes.Count == 0)
        {
            Console.WriteLine("  （空）");
        }
        else
        {
            Console.WriteLine($"{"进程",-32} {"目标设备",-28} {"手段",-8} {"音量",-5} 更新时间");
            Console.WriteLine(new string('-', 108));
            foreach (var r in routes)
            {
                Console.WriteLine(
                    $"{Truncate(Path.GetFileName(r.ProcessPath), 30),-32} {Truncate(r.DeviceName, 26),-28} {DescribeMode(r.Mode),-8} {r.Volume,-5:F2} {r.UpdatedAt:yyyy-MM-dd HH:mm}");
                Console.WriteLine($"    路径: {r.ProcessPath}");
                Console.WriteLine($"    设备ID: {r.DeviceId}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("运行中匹配进程:");
        int matched = 0;
        foreach (var r in routes)
        {
            foreach (var p in FindProcessesByPath(r.ProcessPath))
            {
                matched++;
                Console.WriteLine($"  PID {p.Id,-6} ← {Path.GetFileName(r.ProcessPath)} → {r.DeviceName}");
                p.Dispose();
            }
        }

        if (matched == 0)
        {
            Console.WriteLine("  （无）");
        }

        return 0;
    }

    private static int CtlWatch(RouteStore store, string[] args)
    {
        int intervalMs = int.TryParse(GetOption(args, "--interval"), out int iv) ? Math.Clamp(iv, 500, 30_000) : 2000;

        using var coordinator = new RoutingCoordinator();
        var (loopbackProbe, endpointProbe) = coordinator.ProbeAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[探测] 手段A: {(loopbackProbe.Available ? "可用" : "不可用")} | 手段B2: {(endpointProbe.Available ? "可用" : "不可用")}");

        coordinator.Log += msg => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");
        coordinator.StartWatchdog(intervalMs);
        Console.WriteLine($"[看门狗] 已启动（{intervalMs} ms）：进程退出拆 A / 设备消失暂停 / 设备回归重挂");
        Console.WriteLine("[常驻] Ctrl+C 退出（A 链路拆除恢复原状，B2 注册表持久化保留）");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 启动即扫一轮：路由表中正在运行的进程立即应用
        ScanAndApply(coordinator, store);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                Task.Delay(intervalMs, cts.Token).Wait();
            }
            catch (AggregateException)
            {
                break;
            }

            if (cts.IsCancellationRequested)
            {
                break;
            }

            // 路由表可能被别的 ctl add/remove 改过，重载
            store.Load();
            ScanAndApply(coordinator, store);
            PrintLiveRoutes(coordinator);
        }

        Console.WriteLine("[退出] 拆除全部 A 路由，恢复原会话…");
        return 0;
    }

    /// <summary>对路由表中每个运行中的匹配进程应用路由（已应用的跳过）。</summary>
    private static void ScanAndApply(RoutingCoordinator coordinator, RouteStore store)
    {
        var live = coordinator.GetLiveRoutes();
        foreach (var route in store.Routes)
        {
            foreach (var process in FindProcessesByPath(route.ProcessPath))
            {
                using (process)
                {
                    if (live.Any(l => l.ProcessId == process.Id))
                    {
                        continue;
                    }

                    var device = new AudioDeviceRef(route.DeviceName, route.DeviceId);
                    var command = new RoutingCommand(process.Id, route.ProcessPath, device, route.Volume, route.Mute);
                    var report = coordinator.ApplyRouteAsync(command, route.Mode).GetAwaiter().GetResult();
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} [应用] {Path.GetFileName(route.ProcessPath)}（PID {process.Id}）→ {route.DeviceName}" +
                        $" | B2 {(report.EndpointApplied ? "✅" : "❌")} A {(report.LoopbackActive ? "✅" : "❌")}" +
                        $" | {report.Elapsed.TotalMilliseconds:F0} ms");
                    if (!report.Success)
                    {
                        Console.WriteLine($"    B2: {report.EndpointMessage}");
                        Console.WriteLine($"    A : {report.LoopbackMessage}");
                    }
                }
            }
        }
    }

    private static void PrintLiveRoutes(RoutingCoordinator coordinator)
    {
        var routes = coordinator.GetLiveRoutes();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} [状态] 活动路由 {routes.Count} 条");
        foreach (var r in routes)
        {
            string flags = (r.LoopbackActive ? "A✅" : "A—") + " " + (r.EndpointApplied ? "B2✅" : "B2—") + (r.AwaitingDevice ? " ⏸设备" : "");
            Console.WriteLine($"    PID {r.ProcessId,-6} {Truncate(Path.GetFileName(r.ProcessPath), 24),-24} → {Truncate(r.TargetDevice, 24),-24} [{flags}] {r.Mode}");
        }
    }

    /// <summary>按存储的进程路径找运行中的进程（强绑定比对 MainModule；弱绑定按进程名）。调用方负责 Dispose。</summary>
    private static List<Process> FindProcessesByPath(string processPath)
    {
        var matched = new List<Process>();
        string fileName = Path.GetFileName(processPath);
        if (string.IsNullOrEmpty(fileName))
        {
            return matched;
        }

        // 弱绑定（读不到 MainModule 时存的 "name.exe"）：按进程名匹配
        bool weak = !Path.IsPathRooted(processPath);
        string name = Path.GetFileNameWithoutExtension(fileName);

        foreach (var process in Process.GetProcessesByName(name))
        {
            bool hit = weak;
            if (!hit)
            {
                try
                {
                    hit = string.Equals(process.MainModule?.FileName, processPath, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // MainModule 访问受限（提权/系统进程）→ 跳过，不误伤
                }
            }

            if (hit)
            {
                matched.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return matched;
    }

    private static RoutingMode ParseMode(string text) => text.ToLowerInvariant() switch
    {
        "a" or "loopback" => RoutingMode.Loopback,
        "b" or "endpoint" => RoutingMode.Endpoint,
        _ => RoutingMode.Auto,
    };

    private static string DescribeMode(RoutingMode mode) => mode switch
    {
        RoutingMode.Loopback => "A",
        RoutingMode.Endpoint => "B",
        RoutingMode.Auto => "auto",
        _ => mode.ToString(),
    };

    private static string GetDefaultDeviceId()
    {
        var devices = DeviceManager.EnumerateRenderDevices();
        var d = devices.FirstOrDefault(x => x.IsDefaultConsole) ?? devices[0];
        return d.Id;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
