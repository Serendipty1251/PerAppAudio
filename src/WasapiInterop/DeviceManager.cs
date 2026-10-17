using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

/// <summary>渲染设备信息。</summary>
public sealed record WasapiDevice(
    string Id,
    string FriendlyName,
    DeviceState State,
    bool IsDefaultConsole,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications);

/// <summary>设备枚举与访问（MMDevice API 薄封装，供控制手段层与会话管理层共用）。</summary>
public static class DeviceManager
{
    private const int ClsctxAll = 0x17; // CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER
    private const int StgmRead = 0;

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(Guid.Parse(Guids.CLSID_MMDeviceEnumerator))
            ?? throw new InvalidOperationException("找不到 MMDeviceEnumerator COM 组件");
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// 枚举渲染端点。默认全量（含 NotPresent/Unplugged 幽灵端点）——设备解析、
    /// 看门狗设备存在性判断需要全量视野；UI 下拉框应传 DeviceState.Active。
    /// </summary>
    public static IReadOnlyList<WasapiDevice> EnumerateRenderDevices(DeviceState states = DeviceState.All)
    {
        var enumerator = CreateEnumerator();
        var collection = enumerator.EnumAudioEndpoints(EDataFlow.Render, states);
        int count = collection.GetCount();

        string? defaultConsole = TryGetDefaultId(enumerator, ERole.Console);
        string? defaultMultimedia = TryGetDefaultId(enumerator, ERole.Multimedia);
        string? defaultComms = TryGetDefaultId(enumerator, ERole.Communications);

        var list = new List<WasapiDevice>(count);
        for (int i = 0; i < count; i++)
        {
            var device = collection.Item(i);
            string id;
            DeviceState state;
            try
            {
                id = device.GetId();
                state = device.GetState();
            }
            catch
            {
                continue;
            }

            var friendly = AudioClientActivation.ReadDeviceFriendlyName(device) ?? id;
            list.Add(new WasapiDevice(
                id, friendly, state,
                string.Equals(id, defaultConsole, StringComparison.OrdinalIgnoreCase),
                string.Equals(id, defaultMultimedia, StringComparison.OrdinalIgnoreCase),
                string.Equals(id, defaultComms, StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }

    private static string? TryGetDefaultId(IMMDeviceEnumerator enumerator, ERole role)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role).GetId();
        }
        catch
        {
            return null;
        }
    }

    public static IMMDevice GetDevice(string deviceId)
    {
        return CreateEnumerator().GetDevice(deviceId);
    }

    /// <summary>按 ID 或友好名（精确 → 不区分大小写子串）解析设备。</summary>
    public static WasapiDevice ResolveDevice(string idOrName)
    {
        var devices = EnumerateRenderDevices();
        var exact = devices.FirstOrDefault(d => string.Equals(d.Id, idOrName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var byFriendly = devices.FirstOrDefault(d => string.Equals(d.FriendlyName, idOrName, StringComparison.OrdinalIgnoreCase));
        if (byFriendly is not null) return byFriendly;

        var byContains = devices.FirstOrDefault(d => d.FriendlyName.Contains(idOrName, StringComparison.OrdinalIgnoreCase));
        if (byContains is not null) return byContains;

        throw new InvalidOperationException($"找不到设备：{idOrName}");
    }

    /// <summary>
    /// 取设备 Mix 格式（CoTaskMem 分配，调用方负责 FreeCoTaskMem）。
    /// 进程 Loopback 虚拟客户端不暴露 GetMixFormat，故捕获端直接请求目标设备的此格式。
    /// </summary>
    public static IntPtr GetDeviceMixFormat(string deviceId, out int formatSizeBytes)
    {
        var device = GetDevice(deviceId);
        var client = ActivateAudioClient(device);
        client.GetMixFormat(out IntPtr mixPtr);
        var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
        formatSizeBytes = WAVEFORMATEX.SizeOf + wfx.cbSize;
        return mixPtr;
    }

    public static IAudioClient ActivateAudioClient(IMMDevice device)
    {
        var iid = Guid.Parse(Guids.IID_IAudioClient);
        device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object client);
        return (IAudioClient)client;
    }

    public static string DescribeFormat(WAVEFORMATEX wfx)
    {
        string tag = wfx.wFormatTag switch
        {
            1 => "PCM",
            3 => "IEEE Float",
            0xFFFE => "Extensible",
            _ => $"tag={wfx.wFormatTag}",
        };
        return $"{wfx.nSamplesPerSec} Hz / {wfx.nChannels} ch / {wfx.wBitsPerSample} bit / {tag}";
    }

    /// <summary>取设备的 Mix 格式描述（诊断用）。</summary>
    public static string DescribeDeviceMixFormat(string deviceId)
    {
        try
        {
            var device = GetDevice(deviceId);
            var client = ActivateAudioClient(device);
            client.GetMixFormat(out IntPtr mixPtr);
            try
            {
                var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
                return DescribeFormat(wfx);
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixPtr);
            }
        }
        catch (Exception ex)
        {
            return $"读取失败: {ex.Message}";
        }
    }
}
