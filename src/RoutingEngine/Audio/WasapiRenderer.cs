using System.Runtime.InteropServices;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine.Audio;

/// <summary>
/// 手段A 输出端：WASAPI 渲染客户端（共享模式 + 事件驱动）。
/// 格式取目标设备 Mix 格式，与采集端请求的格式一致时实现零转换直通。
/// </summary>
public sealed class WasapiRenderer : IDisposable
{
    private readonly IAudioClient _client;
    private readonly IAudioRenderClient _render;
    private readonly EventWaitHandle _packetEvent;
    private bool _started;

    public WAVEFORMATEX Format { get; }
    public int BufferFrames { get; }
    public int BytesPerFrame { get; }

    private WasapiRenderer(IAudioClient client, IAudioRenderClient render, EventWaitHandle packetEvent, WAVEFORMATEX format)
    {
        _client = client;
        _render = render;
        _packetEvent = packetEvent;
        Format = format;
        BufferFrames = client.GetBufferSize();
        BytesPerFrame = format.nBlockAlign;
    }

    public static WasapiRenderer Start(string deviceId, int bufferMilliseconds)
    {
        var device = DeviceManager.GetDevice(deviceId);
        var client = DeviceManager.ActivateAudioClient(device);

        client.GetMixFormat(out IntPtr mixPtr);
        WAVEFORMATEX format;
        try
        {
            format = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
            var sessionGuid = Guid.Empty;
            long hnsBufferDuration = bufferMilliseconds * 10_000L;
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback,
                hnsBufferDuration,
                0,
                mixPtr,
                ref sessionGuid);
        }
        finally
        {
            Marshal.FreeCoTaskMem(mixPtr);
        }

        var renderIid = Guid.Parse(Guids.IID_IAudioRenderClient);
        client.GetService(ref renderIid, out object renderService);
        var render = (IAudioRenderClient)renderService;

        var packetEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(packetEvent.SafeWaitHandle.DangerousGetHandle());
        client.Start();

        return new WasapiRenderer(client, render, packetEvent, format) { _started = true };
    }

    /// <summary>当前渲染缓冲区已填充帧数。</summary>
    public int GetPaddingFrames() => _client.GetCurrentPadding();

    /// <summary>缓冲区剩余可写帧数。</summary>
    public int GetFreeFrames() => BufferFrames - _client.GetCurrentPadding();

    /// <summary>
    /// 写入一包音频（格式须与 Format 一致）。返回实际写入帧数；
    /// 缓冲区满时丢弃多余帧（PoC 计数为 dropped，P1 换环形队列）。
    /// </summary>
    public int Write(IntPtr data, int frames)
    {
        int free = GetFreeFrames();
        int toWrite = Math.Min(free, frames);
        if (toWrite <= 0) return 0;

        int bytes = toWrite * BytesPerFrame;
        _render.GetBuffer(toWrite, out IntPtr dest);
        unsafe
        {
            Buffer.MemoryCopy((void*)data, (void*)dest, bytes, bytes);
        }
        _render.ReleaseBuffer(toWrite, AudioClientBufferFlags.None);
        return toWrite;
    }

    /// <summary>填充静音，把 padding 抬到目标水位（防止目标进程无声时渲染缓冲区欠载爆音）。</summary>
    public int FillSilenceUpTo(int targetPaddingFrames)
    {
        int free = GetFreeFrames();
        int toWrite = Math.Min(free, Math.Max(0, targetPaddingFrames - (BufferFrames - free)));
        if (toWrite <= 0) return 0;

        int bytes = toWrite * BytesPerFrame;
        _render.GetBuffer(toWrite, out IntPtr dest);
        unsafe
        {
            new Span<byte>((void*)dest, bytes).Clear();
        }
        _render.ReleaseBuffer(toWrite, AudioClientBufferFlags.None);
        return toWrite;
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        try { _client.Stop(); } catch { }
        try { _client.Reset(); } catch { }
        try { Marshal.ReleaseComObject(_render); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
        _packetEvent.Dispose();
    }
}
