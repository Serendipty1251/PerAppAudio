using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

[ComImport]
[Guid(Guids.IID_IAudioClient)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient
{
    void Initialize(
        AudioClientShareMode ShareMode,
        AudioClientStreamFlags StreamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        [In] IntPtr pFormat,
        [In] ref Guid AudioSessionGuid);

    int GetBufferSize();

    long GetStreamLatency();

    // 注意：vtable 顺序必须与 audioclient.h 严格一致：
    // Initialize(3) GetBufferSize(4) GetStreamLatency(5) GetCurrentPadding(6)
    // IsFormatSupported(7) GetMixFormat(8) GetDevicePeriod(9) Start(10) Stop(11)
    // Reset(12) SetEventHandle(13) GetService(14)
    // 漏掉 IsFormatSupported 会让其后所有方法整体错位一位
    int GetCurrentPadding();

    void IsFormatSupported(
        AudioClientShareMode ShareMode,
        [In] IntPtr pFormat,
        out IntPtr ppClosestMatchToRequestedFormat);

    void GetMixFormat(out IntPtr ppDeviceFormat);

    void GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    void Start();

    void Stop();

    void Reset();

    void SetEventHandle(IntPtr eventHandle);

    void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport]
[Guid(Guids.IID_IAudioCaptureClient)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient
{
    // 注意：Win8+ 的 GetBuffer 有 5 个参数（Vista 原版只有前 3 个）。
    // x64 下第 5 个参数起走栈——少声明后两个 out 参数会导致原生代码
    // 往调用方栈帧的垃圾位置写设备位置/QPC 时间戳，直接 AccessViolation。
    void GetBuffer(
        out IntPtr ppData,
        out int pNumFramesToRead,
        out AudioClientBufferFlags pdwFlags,
        out ulong pu64DevicePosition,
        out ulong pu64QPCPosition);

    void ReleaseBuffer(int NumFramesRead);

    void GetNextPacketSize(out int pNumFramesInNextPacket);
}

[ComImport]
[Guid(Guids.IID_IAudioRenderClient)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioRenderClient
{
    void GetBuffer(int NumFramesRequested, out IntPtr ppData);

    void ReleaseBuffer(int NumFramesWritten, AudioClientBufferFlags dwFlags);
}

[ComImport]
[Guid(Guids.IID_ISimpleAudioVolume)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    void SetMasterVolume(float fLevel, ref Guid EventContext);

    float GetMasterVolume();

    void SetMute(bool bMute, ref Guid EventContext);

    bool GetMute();
}
