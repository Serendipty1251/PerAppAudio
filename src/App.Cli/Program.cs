using PerAppAudio.RoutingEngine;

namespace PerAppAudio.App.Cli;

/// <summary>
/// P0 阶段诊断入口：设备与会话枚举、手段探测的临时载体。
/// 正式 CLI（调用层）在 P2 建设，此处仅服务 P0 技术验证。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("PerAppAudio P0 diagnostics");
        Console.WriteLine("TODO(P0-1): 枚举音频设备（IMMDeviceEnumerator）");
        Console.WriteLine("TODO(P0-1): 枚举音频会话（IAudioSessionManager2）");
        Console.WriteLine("TODO(P0-2): 手段A 进程级 Loopback 捕获 -> 渲染到第二设备");
        Console.WriteLine("TODO(P0-3): 手段B IPolicyConfig 端点引导试验");
        return 0;
    }
}
