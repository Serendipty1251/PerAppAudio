using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

[ComImport]
[Guid(Guids.IID_IMMNotificationClient)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, DeviceState dwNewState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
}

[ComImport]
[Guid(Guids.IID_IMMDeviceEnumerator)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask);

    IMMDevice GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role);

    IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId);

    void RegisterEndpointNotificationCallback(IMMNotificationClient pClient);

    void UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[ComImport]
[Guid(Guids.IID_IMMDevice)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    // 注意：vtable 顺序必须与 mmdeviceapi.h 严格一致：
    // Activate(3) -> OpenPropertyStore(4) -> GetId(5) -> GetState(6)
    void OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetId();

    DeviceState GetState();
}

[ComImport]
[Guid(Guids.IID_IMMDeviceCollection)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    int GetCount();

    IMMDevice Item(int nDevice);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    uint GetCount();

    void GetAt(uint iProp, out PROPERTYKEY pkey);

    void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

    void SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

    void Commit();
}
