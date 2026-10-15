using System.Diagnostics;
using System.Runtime.InteropServices;
using PerAppAudio.RoutingEngine;
using PerAppAudio.RoutingEngine.Backends;
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

            示例:
             devices
             route --pid 12345 --to "Realtek HD Audio 2nd output" --ms 30000
            """);
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        return Usage();
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
