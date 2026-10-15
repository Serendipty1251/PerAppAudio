using System.Runtime.InteropServices;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine.Audio;

/// <summary>设备级 Loopback 探针：捕获指定设备正在播放的内容，统计信号能量（PoC 验证用）。</summary>
public sealed class DeviceLoopbackProbe
{
    private enum SampleEncoding { Float32, Pcm16, Pcm32 }

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public double Rms { get; private set; }
    public double Peak { get; private set; }
    public int SampleCount { get; private set; }

    /// <summary>
    /// 在指定设备上开启系统级 Loopback 采集 listenMs 毫秒，统计采集到的信号能量。
    /// </summary>
    public static DeviceLoopbackProbe Listen(string deviceId, int listenMs)
    {
        var device = DeviceManager.GetDevice(deviceId);
        var client = DeviceManager.ActivateAudioClient(device);

        client.GetMixFormat(out IntPtr mixPtr);
        var format = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
        var encoding = DetectEncoding(mixPtr, format);
        try
        {
            var sessionGuid = Guid.Empty;
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                200_000, // 20ms
                0,
                mixPtr,
                ref sessionGuid);
        }
        finally
        {
            Marshal.FreeCoTaskMem(mixPtr);
        }

        var iid = Guid.Parse(Guids.IID_IAudioCaptureClient);
        client.GetService(ref iid, out object service);
        var capture = (IAudioCaptureClient)service;

        var packetEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(packetEvent.SafeWaitHandle.DangerousGetHandle());
        client.Start();

        var probe = new DeviceLoopbackProbe();
        var deadline = Environment.TickCount64 + listenMs;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                packetEvent.WaitOne(200);
                int packetSize;
                capture.GetNextPacketSize(out packetSize);
                while (packetSize > 0)
                {
                    capture.GetBuffer(out IntPtr data, out int frames, out _, out _, out _);
                    probe.Accumulate(data, frames, format, encoding);
                    capture.ReleaseBuffer(frames);
                    capture.GetNextPacketSize(out packetSize);
                }
            }
        }
        finally
        {
            try { client.Stop(); } catch { }
            try { client.Reset(); } catch { }
            try { Marshal.ReleaseComObject(capture); } catch { }
            try { Marshal.ReleaseComObject(client); } catch { }
            packetEvent.Dispose();
        }

        return probe;
    }

    /// <summary>
    /// 判定采样编码：tag=3 是 IEEE Float；Extensible 读 SubFormat GUID
    /// （WAVEFORMATEXTENSIBLE 布局：18 字节 WAVEFORMATEX + validBits(2) + channelMask(4) + SubFormat GUID(16)）。
    /// </summary>
    private static SampleEncoding DetectEncoding(IntPtr formatPtr, WAVEFORMATEX format)
    {
        if (format.wFormatTag == WAVEFORMATEX.WaveFormatIeeeFloat)
        {
            return SampleEncoding.Float32;
        }

        if (format.wFormatTag == WAVEFORMATEX.WaveFormatExtensible && format.cbSize >= 22)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(formatPtr, 24));
            if (subFormat == SubFormatIeeeFloat)
            {
                return SampleEncoding.Float32;
            }
        }

        return format.wBitsPerSample switch
        {
            32 => SampleEncoding.Pcm32,
            _ => SampleEncoding.Pcm16,
        };
    }

    private void Accumulate(IntPtr data, int frames, WAVEFORMATEX format, SampleEncoding encoding)
    {
        int channels = format.nChannels == 0 ? 1 : format.nChannels;
        int totalSamples = frames * channels;
        SampleCount += totalSamples;

        unsafe
        {
            switch (encoding)
            {
                case SampleEncoding.Float32:
                {
                    var span = new ReadOnlySpan<float>((void*)data, totalSamples);
                    foreach (var s in span) Add(s);
                    break;
                }
                case SampleEncoding.Pcm32:
                {
                    var span = new ReadOnlySpan<int>((void*)data, totalSamples);
                    foreach (var s in span) Add(s / 2147483648.0);
                    break;
                }
                default:
                {
                    var span = new ReadOnlySpan<short>((void*)data, totalSamples);
                    foreach (var s in span) Add(s / 32768.0);
                    break;
                }
            }
        }
    }

    private void Add(double v)
    {
        Rms += v * v;
        double abs = Math.Abs(v);
        if (abs > Peak) Peak = abs;
    }

    /// <summary>计算并返回最终 RMS。</summary>
    public double FinalRms() => SampleCount == 0 ? 0 : Math.Sqrt(Rms / SampleCount);
}
