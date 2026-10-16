using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

[ComImport]
[Guid(Guids.IID_IAudioSessionManager)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager
{
    void GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, out IAudioSessionControl SessionControl);

    void GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, out ISimpleAudioVolume AudioVolume);
}

[ComImport]
[Guid(Guids.IID_IAudioSessionManager2)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    // IAudioSessionManager 方法在前（vtable 顺序）
    void GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, out IAudioSessionControl SessionControl);

    void GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, out ISimpleAudioVolume AudioVolume);

    // IAudioSessionManager2 新增
    void GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
}

[ComImport]
[Guid(Guids.IID_IAudioSessionEnumerator)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    int GetCount();

    IAudioSessionControl GetSession(int SessionCount);
}

[ComImport]
[Guid(Guids.IID_IAudioSessionControl)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl
{
    AudioSessionState GetState();

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetDisplayName();

    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetIconPath();

    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);

    void GetGroupingParam(out Guid pRetVal);

    void SetGroupingParam(ref Guid Override, ref Guid EventContext);

    void RegisterAudioSessionNotification(IAudioSessionEvents NewNotifications);

    void UnregisterAudioSessionNotification(IAudioSessionEvents NewNotifications);
}

// vtable = IAudioSessionControl 全部方法 + 下列 5 个方法；不得重新声明基接口方法。
// Win11 26200 实测（sessiondump，2026-10-16）：IsSystemSoundsSession 带 BOOL* 出参
// （无参调用直接 AccessViolation），因此用 bool retval 形态（隐藏 BOOL* 指针）签名才正确。
// GetSessionIdentifier / GetSessionInstanceIdentifier 返回 LPWSTR（调用方 CoTaskMemFree）。
[ComImport]
[Guid(Guids.IID_IAudioSessionControl2)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2 : IAudioSessionControl
{
    [PreserveSig]
    int GetSessionIdentifier(out IntPtr pRetVal);

    [PreserveSig]
    int GetSessionInstanceIdentifier(out IntPtr pRetVal);

    [PreserveSig]
    int GetProcessId(out int processId);

    bool IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference(bool optOut);
}

/// <summary>IAudioSessionEvents（P0-1 仅占位，P1 实现事件监听）。</summary>
[ComImport]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEvents
{
    void OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string NewDisplayName, ref Guid EventContext);
    void OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string NewIconPath, ref Guid EventContext);
    void OnSimpleVolumeChanged(float NewVolume, bool NewMute, ref Guid EventContext);
    void OnChannelVolumeChanged(uint ChannelCount, [In] float[] NewChannelVolumeArray, int ChangedChannel, ref Guid EventContext);
    void OnGroupingParamChanged(ref Guid NewGroupingParam, ref Guid EventContext);
    void OnStateChanged(AudioSessionState NewState);
    void OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason);
}

public enum AudioSessionDisconnectReason
{
    DeviceRemoval = 0,
    ServerShutdown = 1,
    FormatChanged = 2,
    SessionLogoff = 3,
    SessionDisconnected = 4,
    ExclusiveModeOverride = 5,
}
