using System.Runtime.InteropServices;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.RoutingEngine.Audio;

/// <summary>数据包回调：data 指向 frames × bytesPerFrame 字节，仅在回调内有效。</summary>
public delegate void PacketCallback(IntPtr data, int frames, AudioClientBufferFlags flags);

/// <summary>
/// 手段A 采集端：进程级 Loopback 捕获（Win10 2004+）。
/// 经 ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK) 激活，
/// 捕获不绑定具体端点——目标进程渲染的音频无论从哪个设备播出都会被捕获。
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly EventWaitHandle _packetEvent;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _started;

    public WAVEFORMATEX Format { get; }
    public int BufferFrames { get; }
    public int BytesPerFrame { get; }

    private ProcessLoopbackCapture(IAudioClient client, IAudioCaptureClient capture, EventWaitHandle packetEvent, WAVEFORMATEX format)
    {
        _client = client;
        _capture = capture;
        _packetEvent = packetEvent;
        Format = format;
        BufferFrames = client.GetBufferSize();
        BytesPerFrame = format.nBlockAlign;
    }

    /// <summary>
    /// 激活并启动进程 Loopback 捕获。
    /// requestedFormatPtr/requestedFormatSize：请求的采集格式（通常为目标设备的 Mix 格式，实现零转换直通）。
    /// </summary>
    public static async Task<ProcessLoopbackCapture> StartAsync(
        uint targetProcessId,
        IntPtr requestedFormatPtr,
        int requestedFormatSize,
        int bufferMilliseconds,
        PacketCallback onPacket,
        Action<Exception>? onError = default,
        CancellationToken ct = default)
    {
        var client = await AudioClientActivation.ActivateProcessLoopbackAsync(targetProcessId, ct)
            .ConfigureAwait(false);

        // 进程 Loopback 虚拟设备对格式支持有限：参考实现（MS 示例 / AppLoopback / Voxis）
        // 均请求普通 WAVEFORMATEX（cbSize=0），且带 AutoConvertPcm|SrcDefaultQuality 标志。
        // 从目标设备 Mix 格式派生等价的 plain 格式——IEEE float 的字节布局与 Extensible 完全一致，
        // 因此渲染端仍可用设备 Mix 格式零转换直通。
        var requested = Marshal.PtrToStructure<WAVEFORMATEX>(requestedFormatPtr);
        var plain = new WAVEFORMATEX
        {
            wFormatTag = requested.wFormatTag == WAVEFORMATEX.WaveFormatExtensible || requested.wFormatTag == WAVEFORMATEX.WaveFormatIeeeFloat
                ? WAVEFORMATEX.WaveFormatIeeeFloat
                : (ushort)1, // WAVE_FORMAT_PCM
            nChannels = requested.nChannels,
            nSamplesPerSec = requested.nSamplesPerSec,
            wBitsPerSample = requested.wBitsPerSample,
            nBlockAlign = requested.nBlockAlign,
            nAvgBytesPerSec = requested.nSamplesPerSec * requested.nBlockAlign,
            cbSize = 0,
        };

        var formatPtr = Marshal.AllocHGlobal(WAVEFORMATEX.SizeOf);
        try
        {
            Marshal.StructureToPtr(plain, formatPtr, false);

            var sessionGuid = Guid.Empty;
            long hnsBufferDuration = bufferMilliseconds * 10_000L;
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                hnsBufferDuration,
                0,
                formatPtr,
                ref sessionGuid);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        var captureIid = Guid.Parse(Guids.IID_IAudioCaptureClient);
        client.GetService(ref captureIid, out object captureService);
        var capture = (IAudioCaptureClient)captureService;

        var packetEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(packetEvent.SafeWaitHandle.DangerousGetHandle());
        client.Start();

        var instance = new ProcessLoopbackCapture(client, capture, packetEvent, plain);

        var thread = new Thread(() => instance.CaptureLoop(onPacket, onError, instance._cts.Token))
        {
            IsBackground = true,
            Name = "PerAppAudio.LoopbackCapture",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        instance._thread = thread;
        instance._started = true;
        thread.Start();
        return instance;
    }

    private void CaptureLoop(PacketCallback onPacket, Action<Exception>? onError, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _packetEvent.WaitOne(200);

                int packetSize;
                _capture.GetNextPacketSize(out packetSize);
                while (packetSize > 0)
                {
                    _capture.GetBuffer(
                        out IntPtr data,
                        out int frames,
                        out var flags,
                        out _,
                        out _);
                    if (frames > 0)
                    {
                        onPacket(data, frames, flags);
                    }
                    _capture.ReleaseBuffer(frames);
                    _capture.GetNextPacketSize(out packetSize);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        try
        {
            _cts.Cancel();
            _packetEvent.Set();
            if (_thread is { IsAlive: true }) _thread.Join(2000);
        }
        catch
        {
        }

        try { _client.Stop(); } catch { }
        try { _client.Reset(); } catch { }
        try { Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
        _packetEvent.Dispose();
        _cts.Dispose();
    }
}
