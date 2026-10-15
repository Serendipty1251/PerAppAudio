using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

/// <summary>
/// 手段B2：每进程默认端点设置（免注入）。
/// 机制：WinRT 内部类 Windows.Media.Internal.AudioPolicyConfig 的激活工厂即路由接口
/// IAudioPolicyConfigFactory——EarTrumpet / Windows 现代声音设置同款机制。
/// 注意：不是 COM 类 CPolicyConfigClient(870af99c-…)，那个只有全局 SetDefaultEndpoint。
/// GUID 与 vtable 布局参照 EarTrumpet 与 SystemManager（均为 MIT）的逆向定义；
/// 需 Windows 10 21H2+ / Windows 11。
/// </summary>
public static class AudioPolicyConfigFactory
{
    private const string ActivatableClassId = "Windows.Media.Internal.AudioPolicyConfig";

    // Win10 21H2+ / Win11 的 IAudioPolicyConfigFactory
    public static readonly Guid Iid = new("ab3d4648-e242-459f-b02f-541c70306324");

    // 端点 ID 包装格式：\\?\SWD#MMDEVAPI#{endpointId}#{DEVINTERFACE_AUDIO_RENDER}
    private const string MMDeviceApiTokenPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceGuid = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    /// <summary>绑定路由接口；失败返回 null（不抛异常）。</summary>
    public static IAudioPolicyConfigFactory? TryCreate()
    {
        var className = IntPtr.Zero;
        try
        {
            if (WindowsCreateString(ActivatableClassId, ActivatableClassId.Length, out className) < 0)
            {
                return null;
            }

            int hr = RoGetActivationFactory(className, in Iid, out IntPtr factory);
            if (hr < 0 || factory == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (className != IntPtr.Zero)
            {
                WindowsDeleteString(className);
            }
        }
    }

    /// <summary>设置进程的持久化默认渲染端点（Multimedia + Console 双角色）。endpointId 为空 = 清除覆盖。返回第一个失败的 HRESULT（&gt;=0 成功）。</summary>
    public static int SetPersistedDefaultEndpoint(IAudioPolicyConfigFactory factory, uint processId, string endpointId)
    {
        var device = string.IsNullOrEmpty(endpointId) ? string.Empty : ToPolicyEndpointId(endpointId);
        var route = IntPtr.Zero;
        try
        {
            if (device.Length > 0 && WindowsCreateString(device, device.Length, out route) < 0)
            {
                return -1;
            }

            int hr1 = factory.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, route);
            int hr2 = factory.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Console, route);
            if (hr1 < 0) return hr1;
            return hr2;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
        finally
        {
            if (route != IntPtr.Zero)
            {
                WindowsDeleteString(route);
            }
        }
    }

    /// <summary>读回进程的持久化默认端点（纯端点 ID）。返回 null = 读取失败；string.Empty = 跟随系统默认。</summary>
    public static string? GetPersistedDefaultEndpoint(IAudioPolicyConfigFactory factory, uint processId)
    {
        var route = IntPtr.Zero;
        try
        {
            int hr = factory.GetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, out route);
            if (hr < 0)
            {
                return null;
            }

            return FromPolicyEndpointId(TextOf(route));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (route != IntPtr.Zero)
            {
                WindowsDeleteString(route);
            }
        }
    }

    /// <summary>把纯端点 ID 包装成 \\?\SWD#MMDEVAPI#… 形式。</summary>
    public static string ToPolicyEndpointId(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return string.Empty;
        if (endpointId.StartsWith(MMDeviceApiTokenPrefix, StringComparison.OrdinalIgnoreCase)) return endpointId;
        return $"{MMDeviceApiTokenPrefix}{endpointId}#{RenderInterfaceGuid}";
    }

    /// <summary>从持久化路由字符串中取回纯端点 ID。</summary>
    public static string FromPolicyEndpointId(string policyEndpointId)
    {
        var id = policyEndpointId;
        if (id.StartsWith(MMDeviceApiTokenPrefix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[MMDeviceApiTokenPrefix.Length..];
        }
        var suffix = "#" + RenderInterfaceGuid;
        if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^suffix.Length];
        }
        return id;
    }

    private static string TextOf(IntPtr handle)
    {
        var chars = WindowsGetStringRawBuffer(handle, out uint length);
        return chars == IntPtr.Zero || length == 0 ? string.Empty : Marshal.PtrToStringUni(chars, (int)length);
    }

    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string sourceString, int length, out IntPtr handle);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr handle);

    [DllImport("combase.dll")]
    public static extern IntPtr WindowsGetStringRawBuffer(IntPtr handle, out uint length);
}

/// <summary>
/// 未公开接口 IAudioPolicyConfigFactory（WinRT，IInspectable 派生）。
/// .NET 内置 COM 互操作不投影 IInspectable，故声明在 IUnknown 上并把 IInspectable
/// 的三个方法作为前三个槽位；其后 19 个槽位为占位（从不调用，仅保持 vtable 顺序），
/// 最后两个是 set/get 每进程默认端点。布局与 EarTrumpet 一致。
/// </summary>
[ComImport]
[Guid("ab3d4648-e242-459f-b02f-541c70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioPolicyConfigFactory
{
    // IInspectable
    [PreserveSig] int GetIids();
    [PreserveSig] int GetRuntimeClassName();
    [PreserveSig] int GetTrustLevel();

    // 占位槽位（保持 vtable 顺序，从不调用）
    [PreserveSig] int Slot00_add_CtxVolumeChange();
    [PreserveSig] int Slot01_remove_CtxVolumeChanged();
    [PreserveSig] int Slot02_add_RingerVibrateStateChanged();
    [PreserveSig] int Slot03_remove_RingerVibrateStateChanged();
    [PreserveSig] int Slot04_SetVolumeGroupGainForId();
    [PreserveSig] int Slot05_GetVolumeGroupGainForId();
    [PreserveSig] int Slot06_GetActiveVolumeGroupForEndpointId();
    [PreserveSig] int Slot07_GetVolumeGroupsForEndpoint();
    [PreserveSig] int Slot08_GetCurrentVolumeContext();
    [PreserveSig] int Slot09_SetVolumeGroupMuteForId();
    [PreserveSig] int Slot10_GetVolumeGroupMuteForId();
    [PreserveSig] int Slot11_SetRingerVibrateState();
    [PreserveSig] int Slot12_GetRingerVibrateState();
    [PreserveSig] int Slot13_SetPreferredChatApplication();
    [PreserveSig] int Slot14_ResetPreferredChatApplication();
    [PreserveSig] int Slot15_GetPreferredChatApplication();
    [PreserveSig] int Slot16_GetCurrentChatApplications();
    [PreserveSig] int Slot17_add_ChatContextChanged();
    [PreserveSig] int Slot18_remove_ChatContextChanged();

    // deviceId 为 HSTRING 句柄；null = 清除覆盖（跟随系统默认）
    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    // deviceId 以 HSTRING 句柄返回，调用方负责 WindowsDeleteString；null = 无覆盖
    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);
}
