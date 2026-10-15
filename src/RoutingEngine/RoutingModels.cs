namespace PerAppAudio.RoutingEngine;

/// <summary>控制手段标识。</summary>
public enum RoutingMode
{
    /// <summary>手段A：进程级 Loopback 重定向（30–60ms，无驱动，通用兜底）。</summary>
    Loopback = 0,

    /// <summary>手段B：默认端点引导（&lt;10ms，B2 IPolicyConfig 主推）。</summary>
    Endpoint = 1,

    /// <summary>手段C：虚拟声卡混音（远期，需驱动）。</summary>
    VirtualDevice = 2,

    /// <summary>策略调度自动选择：优先 B，失败回退 A。</summary>
    Auto = 99,
}

/// <summary>设备引用：友好名 + 设备 ID（ID 防接口编号漂移）。</summary>
public sealed record AudioDeviceRef(string FriendlyName, string DeviceId);

/// <summary>一条路由命令：把哪个进程的音频引到哪个设备。</summary>
public sealed record RoutingCommand(
    int ProcessId,
    string ProcessPath,
    AudioDeviceRef TargetDevice,
    float Volume,
    bool Mute);

/// <summary>手段探测结果。</summary>
public sealed record BackendProbeResult(bool Available, string? Reason = null);

/// <summary>路由执行结果。<see cref="ActualMode"/> 记录实际生效的手段（Auto 会被解析为具体手段）。</summary>
public sealed record RoutingResult(bool Success, RoutingMode ActualMode, string? Message = null)
{
    public static RoutingResult Ok(RoutingMode mode) => new(true, mode);

    public static RoutingResult Fail(string message) => new(false, RoutingMode.Auto, message);
}
