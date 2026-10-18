using System.Runtime.InteropServices;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine.Backends;

/// <summary>
/// 手段B2：默认端点引导（免注入，&lt;10ms）。
/// 通过 WinRT 内部接口 IAudioPolicyConfigFactory 设置进程的持久化默认渲染端点
/// （EarTrumpet / Windows 现代声音设置同款机制，需 Win10 21H2+ / Win11）。
/// 与手段A 的本质区别：不改写音频流，让应用直接播放到目标设备——零转换、零 CPU 拷贝。
/// P0-2 实测发现同一工厂连续调用有间歇性 E_INVALIDARG，故所有调用走「失败重建工厂 + 退避重试」。
/// </summary>
public sealed class EndpointBackend : IRoutingBackend
{
    /// <summary>重试退避间隔（ms）：首次立即，之后 50 / 150 / 400。</summary>
    private static readonly int[] BackoffMs = { 0, 50, 150, 400 };

    private IAudioPolicyConfigFactory? _factory;
    private readonly HashSet<int> _routed = new();
    private readonly object _gate = new();
    /// <summary>工厂串行化：_factory 是共享 COM 对象，并发 Set/Recycle 会 use-after-release。</summary>
    private readonly object _factoryGate = new();

    public RoutingMode Mode => RoutingMode.Endpoint;

    public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        IAudioPolicyConfigFactory? factory;
        lock (_factoryGate)
        {
            factory = AudioPolicyConfigFactory.TryCreate();
            if (factory is not null)
            {
                _factory = factory;
            }
        }

        if (factory is null)
        {
            return ValueTask.FromResult(new BackendProbeResult(
                false,
                "IAudioPolicyConfigFactory 不可用（手段B2 需要 Windows 10 21H2+ / Windows 11；更早系统请用手段A）"));
        }

        return ValueTask.FromResult(new BackendProbeResult(true, "IAudioPolicyConfigFactory 可用"));
    }

    public ValueTask<RoutingResult> ApplyAsync(RoutingCommand command, CancellationToken ct = default)
    {
        int hr = SetWithRetry((uint)command.ProcessId, command.TargetDevice.DeviceId);
        if (hr < 0)
        {
            return ValueTask.FromResult(RoutingResult.Fail(
                $"SetPersistedDefaultAudioEndpoint 失败（含 {BackoffMs.Length} 次重试）: 0x{hr:X8}"));
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
        int hr = SetWithRetry((uint)command.ProcessId, string.Empty);
        lock (_gate)
        {
            _routed.Remove(command.ProcessId);
        }

        return ValueTask.FromResult(hr >= 0
            ? RoutingResult.Ok(RoutingMode.Endpoint) with { Message = "已清除端点覆盖，恢复跟随系统默认" }
            : RoutingResult.Fail($"清除端点覆盖失败（含重试）: 0x{hr:X8}"));
    }

    /// <summary>读回进程当前的持久化默认端点（纯端点 ID）。null = 读取失败；string.Empty = 跟随系统默认。</summary>
    public string? GetPersistedEndpoint(int processId)
    {
        lock (_factoryGate)
        {
            for (int attempt = 0; attempt < BackoffMs.Length; attempt++)
            {
                if (BackoffMs[attempt] > 0)
                {
                    Thread.Sleep(BackoffMs[attempt]);
                }

                var factory = EnsureFactory();
                if (factory is null)
                {
                    return null;
                }

                string? result = AudioPolicyConfigFactory.GetPersistedDefaultEndpoint(factory, (uint)processId);
                if (result is not null)
                {
                    return result;
                }

                // 读失败：丢弃工厂，下一轮重建后重试
                RecycleFactory(factory);
            }

            return null;
        }
    }

    public bool IsRouting(int processId)
    {
        lock (_gate)
        {
            return _routed.Contains(processId);
        }
    }

    /// <summary>
    /// 带重试的端点写入。P0-2 结论：同一工厂对象连续调用会间歇性 E_INVALIDARG，
    /// 因此每次失败都丢弃工厂、重新 RoGetActivationFactory，再按退避间隔重试。
    /// </summary>
    private int SetWithRetry(uint processId, string endpointId)
    {
        int hr = 0;
        lock (_factoryGate)
        {
            for (int attempt = 0; attempt < BackoffMs.Length; attempt++)
            {
                if (BackoffMs[attempt] > 0)
                {
                    Thread.Sleep(BackoffMs[attempt]);
                }

                var factory = EnsureFactory();
                if (factory is null)
                {
                    hr = unchecked((int)0x80004005); // E_FAIL：工厂绑定不上
                    continue;
                }

                hr = AudioPolicyConfigFactory.SetPersistedDefaultEndpoint(factory, processId, endpointId);
                if (hr >= 0)
                {
                    return hr;
                }

                RecycleFactory(factory);
            }
        }

        return hr;
    }

    private IAudioPolicyConfigFactory? EnsureFactory()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        _factory = AudioPolicyConfigFactory.TryCreate();
        return _factory;
    }

    private void RecycleFactory(IAudioPolicyConfigFactory factory)
    {
        _factory = null;
        try
        {
            if (Marshal.IsComObject(factory))
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            // 工厂释放失败不影响下一次重新激活
        }
    }
}
