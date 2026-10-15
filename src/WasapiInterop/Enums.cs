namespace PerAppAudio.WasapiInterop;

public enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

public enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[Flags]
public enum DeviceState
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F,
}

public enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

public enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1,
}

[Flags]
public enum AudioClientStreamFlags : uint
{
    None = 0,
    CrossProcess = 0x00010000,
    Loopback = 0x00020000,
    EventCallback = 0x00040000,
    NoPersist = 0x00080000,
    RateAdjust = 0x00100000,
    SrcDefaultQuality = 0x08000000,
    AutoConvertPcm = 0x80000000,
}

[Flags]
public enum AudioClientBufferFlags
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
}

// PROCESS_LOOPBACK_MODE（注意：Include = 0，Exclude = 1）
public enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

// AUDIOCLIENT_ACTIVATION_TYPE
public enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}
