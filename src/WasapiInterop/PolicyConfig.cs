using System;
using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

/// <summary>
/// 系统默认端点切换（未公开接口 IPolicyConfig，由 AudioSes.dll 实现）。
/// 与 <see cref="AudioPolicyConfigFactory"/> 的「每进程」路由不同，这里改的是**系统默认设备**：
/// 把虚拟麦克风设为默认录音设备后，所有按默认设备取音的应用都会收到我们的混音——
/// 这是「驱动级接管默认麦克风」在用户态可落地的最接近形态。
/// 注意：未公开接口，Windows 更新可能变动；失败时应回退为引导用户手动设置。
/// </summary>
public static class PolicyConfig
{
    private const string ClsidPolicyConfigClient = "870af99c-171d-4f9e-af0d-e63df40c2bc9";
    private const string IidPolicyConfig = "f8679f50-850a-41cf-9c72-430f290290c8";

    /// <summary>
    /// vtable 顺序必须与 PolicyConfig.h 完全一致（SetDefaultEndpoint 是第 11 个方法）。
    /// 不能删减/重排，否则会调到错误的函数指针。
    /// </summary>
    [ComImport]
    [Guid(IidPolicyConfig)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr ppFormat);

        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bDefault, out IntPtr ppFormat);

        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);

        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            IntPtr pEndpointFormat, IntPtr mixFormat);

        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);

        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref long pmftPeriod);

        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr pMode);

        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);

        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            ref PROPERTYKEY key, out PROPVARIANT value);

        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            ref PROPERTYKEY key, ref PROPVARIANT value);

        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ERole role);

        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }

    /// <summary>
    /// 把指定端点设为某个角色的系统默认设备。
    /// 返回 HRESULT：0 = 成功；0x80040154 = 未注册；0x80070005 = 拒绝访问。
    /// </summary>
    public static int SetDefaultEndpoint(string deviceId, ERole role)
    {
        object? comObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(ClsidPolicyConfigClient), throwOnError: false);
            if (type is null)
            {
                return unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG
            }

            comObject = Activator.CreateInstance(type);
            if (comObject is not IPolicyConfig policy)
            {
                return unchecked((int)0x80004002); // E_NOINTERFACE
            }

            return policy.SetDefaultEndpoint(deviceId, role);
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
    }

    /// <summary>三个默认角色（Console / Multimedia / Communications）全部指向该端点。</summary>
    public static int SetDefaultEndpointAllRoles(string deviceId)
    {
        foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
        {
            int hr = SetDefaultEndpoint(deviceId, role);
            if (hr < 0)
            {
                return hr;
            }
        }

        return 0;
    }

    /// <summary>取指定角色的默认捕获端点 ID；null = 取不到。</summary>
    public static string? GetDefaultCaptureId(ERole role = ERole.Console)
        => DeviceManager.GetDefaultEndpointId(EDataFlow.Capture, role);
}
