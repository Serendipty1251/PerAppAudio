using PerAppAudio.RoutingEngine;

namespace PerAppAudio.RuleEngine;

/// <summary>规则匹配条件（对应 YAML <c>match</c> 段）。</summary>
public sealed record ProcessMatch(
    string? ProcessPath,
    string? ProcessName,
    string? WindowTitle);

/// <summary>规则动作（对应 YAML <c>action</c> 段）。</summary>
public sealed record RoutingAction(
    string Device,
    RoutingMode Mode,
    float Volume,
    bool Mute);

/// <summary>一条路由规则。</summary>
public sealed record RoutingRule(string Name, ProcessMatch Match, RoutingAction Action);
