namespace PerAppAudio.RoutingEngine;

/// <summary>
/// 控制手段抽象（v1.0 决策 D2）。
/// 每种「控制音频走向的手段」实现同一接口，由策略调度按能力探测结果与用户偏好选用。
/// </summary>
public interface IRoutingBackend
{
    /// <summary>手段标识。</summary>
    RoutingMode Mode { get; }

    /// <summary>
    /// 本机可用性探测。策略调度启动时对每个后端调用一次，失败的后端不参与自动选择。
    /// </summary>
    ValueTask<BackendProbeResult> ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// 把指定音频会话的走向改为目标设备（应用可直接播放到目标设备，或经本后端重定向）。
    /// </summary>
    ValueTask<RoutingResult> ApplyAsync(RoutingCommand command, CancellationToken ct = default);

    /// <summary>拆除路由，恢复该会话的原走向。</summary>
    ValueTask<RoutingResult> RemoveAsync(RoutingCommand command, CancellationToken ct = default);
}
