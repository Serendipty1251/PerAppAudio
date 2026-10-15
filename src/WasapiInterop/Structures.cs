using System.Runtime.InteropServices;

namespace PerAppAudio.WasapiInterop;

[StructLayout(LayoutKind.Sequential)]
public struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public const int SizeOf = 18;
    public const ushort WaveFormatIeeeFloat = 3;
    public const ushort WaveFormatExtensible = 0xFFFE;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROPERTYKEY
{
    public Guid fmtid;
    public int pid;
}

/// <summary>
/// PROPVARIANT 最小声明（PoC-1 只需 VT_BLOB 写入与 VT_LPWSTR 读取）。
/// 布局（x64，24 字节）：vt@0；VT_BLOB 时 cbSize@8、pBlobData@16；VT_LPWSTR 时 pwszVal@8。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public uint cbSize;        // BLOB.cbSize
    [FieldOffset(8)] public IntPtr pwszVal;     // VT_LPWSTR
    [FieldOffset(16)] public IntPtr pBlobData;  // BLOB.pBlobData

    public const ushort VT_LPWSTR = 31;
    public const ushort VT_BLOB = 65;
}

/// <summary>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

/// <summary>AUDIOCLIENT_ACTIVATION_PARAMS（ActivateType + union，进程 Loopback 场景）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AUDIOCLIENT_ACTIVATION_PARAMS
{
    public AudioClientActivationType ActivationType;
    public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
}
