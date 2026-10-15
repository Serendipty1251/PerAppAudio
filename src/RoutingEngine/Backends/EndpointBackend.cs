using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine.Backends;

/// <summary>
/// 手段B2：默认端点引导（免注入，&lt;10ms）。
/// 通过 WinRT 内部接口 IAudioPolicyConfigFactory 设置进程的持久化默认渲染端点
/// （EarTrumpet / Windows 现代声音设置同款机制，需 Win10 21H2+ / Win11）。
/// 与手段A 的本质区别：不改写音频流，让应用直接播放到目标设备——零转换、零 CPU 拷贝。
/// </summary>
public sealed class EndpointBackend : IRoutingBackend
{
    private IAudioPolicyConfigFactory? _factory;
    private readonly HashSet<int> _routed = new();
    private readonly object _gate = new();

    public RoutingMode Mode => RoutingMode.Endpoint;

    public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var factory = AudioPolicyConfigFactory.TryCreate();
        if (factory is null)
        {
            return ValueTask.FromResult(new BackendProbeResult(
                false,
                "IAudioPolicyConfigFactory 不可用（手段B2 需要 Windows 10 21H2+ / Windows 11；更早系统请用手段A）"));
        }

        _factory = factory;
        return ValueTask.FromResult(new BackendProbeResult(true, "IAudioPolicyConfigFactory 可用"));
    }

    public ValueTask<RoutingResult> ApplyAsync(RoutingCommand command, CancellationToken ct = default)
    {
        if (_factory is null)
        {
            var probed = AudioPolicyConfigFactory.TryCreate();
            if (probed is null)
            {
                return ValueTask.FromResult(RoutingResult.Fail("手段B2 不可用：IAudioPolicyConfigFactory 绑定失败"));
            }
            _factory = probed;
        }

        int hr = AudioPolicyConfigFactory.SetPersistedDefaultEndpoint(_factory, (uint)command.ProcessId, command.TargetDevice.DeviceId);
        if (hr < 0)
        {
            return ValueTask.FromResult(RoutingResult.Fail($"SetPersistedDefaultAudioEndpoint 失败: 0x{hr:X8}"));
        }

        lock (_gate)
        {
            _routed.Add(command.ProcessId);
        }

        return ValueTask.FromResult(RoutingResult.Ok(RoutingMode.Endpoint) with
        {
            Message = $"已设置进程 {command.ProcessId} 的持久化默认端点 → {command.TargetDevice.FriendlyName}（Multimedia+Console）",
        });
    }

    public ValueTask<RoutingResult> RemoveAsync(RoutingCommand command, CancellationToken ct = default)
    {
        if (_factory is null)
        {
            var probed = AudioPolicyConfigFactory.TryCreate();
            if (probed is null)
            {
                return ValueTask.FromResult(RoutingResult.Fail("手段B2 未初始化"));
            }
            _factory = probed;
        }

        int hr = AudioPolicyConfigFactory.SetPersistedDefaultEndpoint(_factory, (uint)command.ProcessId, string.Empty);
        lock (_gate)
        {
            _routed.Remove(command.ProcessId);
        }

        return ValueTask.FromResult(hr >= 0
            ? RoutingResult.Ok(RoutingMode.Endpoint) with { Message = "已清除端点覆盖，恢复跟随系统默认" }
            : RoutingResult.Fail($"清除端点覆盖失败: 0x{hr:X8}"));
    }

    /// <summary>读回进程当前的持久化默认端点（纯端点 ID）。null = 读取失败；string.Empty = 跟随系统默认。</summary>
    public string? GetPersistedEndpoint(int processId)
    {
        if (_factory is null)
        {
            var probed = AudioPolicyConfigFactory.TryCreate();
            if (probed is null) return null;
            _factory = probed;
        }

        return AudioPolicyConfigFactory.GetPersistedDefaultEndpoint(_factory, (uint)processId);
    }

    public bool IsRouting(int processId)
    {
        lock (_gate)
        {
            return _routed.Contains(processId);
        }
    }
}
