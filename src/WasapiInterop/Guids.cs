// WASAPI COM 接口 GUID 集合。
// 来源：Windows SDK mmdeviceapi.h / audioclient.h / audiopolicy.h / audioclientactivationparams.h
// （经 mingw-w64 WIDL 生成头文件逐字核对，2026-10-15）
namespace PerAppAudio.WasapiInterop;

public static class Guids
{
    // MMDevice API
    public const string CLSID_MMDeviceEnumerator = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    public const string IID_IMMDeviceEnumerator = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    public const string IID_IMMDevice = "D666063F-1587-4E43-81F1-B948E807363F";
    public const string IID_IMMDeviceCollection = "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E";
    public const string IID_IMMNotificationClient = "7991EEC9-7E89-4D85-8390-6C703CEC60C0";

    // 激活
    public const string IID_IActivateAudioInterfaceCompletionHandler = "41D949AB-9862-444A-80F6-C261334DA5EB";
    public const string IID_IActivateAudioInterfaceAsyncOperation = "72A22D78-CDE4-431D-B8CC-843A71199B6D";
    public const string IID_IAgileObject = "94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90";

    // WASAPI
    public const string IID_IAudioClient = "1CB9AD4C-DBFA-4C32-B178-C2F568A703B2";
    public const string IID_IAudioCaptureClient = "C8ADBD64-E71E-48A0-A4DE-185C395CD317";
    public const string IID_IAudioRenderClient = "F294ACFC-3146-4483-A7BF-ADDCA7C260E2";
    public const string IID_ISimpleAudioVolume = "87CE5498-68D6-44E5-9215-6DA47EF883D8";

    // 会话
    public const string IID_IAudioSessionManager = "BFA971F1-4D5E-40BB-935E-967039BFBEE4";
    public const string IID_IAudioSessionManager2 = "77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F";
    public const string IID_IAudioSessionEnumerator = "E2F5BB11-0570-40CA-ACDD-3AA01277DEE8";
    public const string IID_IAudioSessionControl = "F4B1A599-7266-4319-A8CA-E70ACB11E8CD";
    public const string IID_IAudioSessionControl2 = "BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D";

    // 进程 Loopback 虚拟设备路径（ActivateAudioInterfaceAsync 的 deviceInterfacePath）
    // 注意：这不是普通设备 ID，是固定魔串；且该客户端不暴露 GetMixFormat，需自选格式。
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
}
