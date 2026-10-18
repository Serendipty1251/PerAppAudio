using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using PerAppAudio.RoutingEngine.Audio;
using PerAppAudio.WasapiInterop;

namespace PerAppAudio.App.Wpf;

/// <summary>
/// 麦克风混音路由（Block voice 的可选项）：捕获真实默认麦克风，渲染到虚拟麦克风设备。
/// 与音板各音效的渲染会话相互独立，由音频引擎在虚拟麦克风端点自动混合——
/// 故「混入人声」= 启用本路由；「Block voice（只送音效）」= 不启用本路由。
/// 捕获走原生 WASAPI（项目已封装的 IAudioClient / IAudioCaptureClient，不依赖 NAudio 的 WasapiCapture），
/// 捕获格式与虚拟设备 Mix 格式可能不同，这里做手动解码 + 线性重采样 + 声道重排。
/// </summary>
internal sealed class MicRouter : IDisposable
{
    // KSDATAFORMAT_SUBTYPE 子格式 GUID（Extensible 格式里判定 PCM / IEEE Float 用）
    private static readonly Guid KsdSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid KsdSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly string _deviceId;
    private WasapiRenderer? _renderer;
    private IAudioClient? _captureClient;
    private IAudioCaptureClient? _capture;
    private Thread? _captureThread;
    private Thread? _pump;
    private volatile bool _stop;
    private readonly ConcurrentQueue<float> _queue = new();
    private IntPtr _mixFormatPtr;

    private int _srcChannels;
    private int _srcRate;
    private bool _srcFloat;
    private int _srcBytesPerSample;

    private int _dstRate;
    private int _dstChannels;
    private bool _dstFloat;
    private int _dstBytesPerSample;

    public MicRouter(string deviceId)
    {
        _deviceId = deviceId;
        _mixFormatPtr = IntPtr.Zero;
    }

    public string DeviceId => _deviceId;

    public void Start()
    {
        try
        {
            _renderer = WasapiRenderer.Start(_deviceId, 100);
            _dstRate = (int)_renderer.Format.nSamplesPerSec;
            _dstChannels = Math.Max(1, (int)_renderer.Format.nChannels);
            _dstFloat = _renderer.Format.wFormatTag == WAVEFORMATEX.WaveFormatIeeeFloat;
            _dstBytesPerSample = _dstFloat ? 4 : 2;

            // 取默认捕获端点（麦克风），激活 IAudioClient
            var enumerator = DeviceManager.CreateEnumerator();
            var micDevice = enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console);
            _captureClient = DeviceManager.ActivateAudioClient(micDevice);

            // 请求设备 Mix 格式做共享模式捕获（最简单、最稳）
            _captureClient.GetMixFormat(out _mixFormatPtr);
            var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(_mixFormatPtr);
            _srcChannels = Math.Max(1, (int)wfx.nChannels);
            _srcRate = (int)wfx.nSamplesPerSec;
            ResolveCaptureSampleFormat(wfx);

            long hnsBuffer = 80 * 10_000L; // 80ms
            var sessionGuid = Guid.Empty;
            _captureClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                hnsBuffer,
                0,
                _mixFormatPtr,
                ref sessionGuid);

            var capIid = Guid.Parse(Guids.IID_IAudioCaptureClient);
            _captureClient.GetService(ref capIid, out object capObj);
            _capture = (IAudioCaptureClient)capObj;
            _captureClient.Start();

            App.AppendFileLog($"[混音路由] 启动：捕获默认麦克风（{_srcRate}Hz/{_srcChannels}ch {( _srcFloat ? "Float" : "PCM" + _srcBytesPerSample * 8 + "bit")}）→ 虚拟麦克风（{_dstRate}Hz/{_dstChannels}ch）");

            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "MicRouter-Capture" };
            _captureThread.Start();
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "MicRouter" };
            _pump.Start();
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[混音路由] 启动失败: {ex.Message}");
            Dispose();
        }
    }

    /// <summary>从捕获 WAVEFORMATEX（可能含 Extensible）判定浮点/整型与每样本字节数。</summary>
    private void ResolveCaptureSampleFormat(WAVEFORMATEX wfx)
    {
        if (wfx.wFormatTag == WAVEFORMATEX.WaveFormatIeeeFloat)
        {
            _srcFloat = true;
            _srcBytesPerSample = 4;
        }
        else if (wfx.wFormatTag == WAVEFORMATEX.WaveFormatExtensible && wfx.cbSize >= 22)
        {
            // WAVEFORMATEXTENSIBLE：SubFormat GUID 位于基结构之后 6 字节（wValidBitsPerSample 2 + dwChannelMask 4）处
            var sub = Marshal.PtrToStructure<Guid>(_mixFormatPtr + 24);
            if (sub == KsdSubtypeIeeeFloat)
            {
                _srcFloat = true;
                _srcBytesPerSample = 4;
            }
            else
            {
                _srcFloat = false;
                _srcBytesPerSample = Math.Max(1, wfx.wBitsPerSample / 8);
            }
        }
        else
        {
            _srcFloat = false;
            _srcBytesPerSample = Math.Max(1, wfx.wBitsPerSample / 8);
        }
    }

    private void CaptureLoop()
    {
        try
        {
            while (!_stop)
            {
                _capture!.GetNextPacketSize(out int packetSize);
                while (packetSize > 0)
                {
                    _capture.GetBuffer(
                        out IntPtr data,
                        out int frames,
                        out AudioClientBufferFlags flags,
                        out _,
                        out _);
                    if (frames > 0 && (flags & AudioClientBufferFlags.Silent) == 0)
                    {
                        EnqueueFrames(data, frames);
                    }
                    _capture.ReleaseBuffer(frames);
                    _capture.GetNextPacketSize(out packetSize);
                }
                Thread.Sleep(8);
            }
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[混音路由] 采集异常: {ex.Message}");
        }
    }

    private void EnqueueFrames(IntPtr data, int frames)
    {
        int frameBytes = frames * _srcChannels * _srcBytesPerSample;
        if (frameBytes <= 0)
        {
            return;
        }
        var raw = new byte[frameBytes];
        Marshal.Copy(data, raw, 0, frameBytes);

        int total = frames * _srcChannels;
        var floats = new float[total];
        for (int i = 0; i < total; i++)
        {
            float v;
            if (_srcFloat)
            {
                v = BitConverter.ToSingle(raw, i * 4);
            }
            else if (_srcBytesPerSample == 2)
            {
                v = BitConverter.ToInt16(raw, i * 2) / 32768f;
            }
            else if (_srcBytesPerSample == 3)
            {
                int s = (raw[i * 3 + 2] << 16) | (raw[i * 3 + 1] << 8) | raw[i * 3];
                if ((s & 0x800000) != 0)
                {
                    s -= 0x1000000;
                }
                v = s / 8388608f;
            }
            else if (_srcBytesPerSample == 4)
            {
                v = BitConverter.ToInt32(raw, i * 4) / 2147483648f;
            }
            else
            {
                v = 0f;
            }
            floats[i] = v;
        }

        // 重采样到目标采样率（线性），入队供渲染线程消费
        float[] dst = ResampleLinear(floats, _srcChannels, _srcRate, _dstRate);
        foreach (float s in dst)
        {
            _queue.Enqueue(s);
        }
    }

    private static float[] ResampleLinear(float[] src, int channels, int srcRate, int dstRate)
    {
        if (srcRate == dstRate)
        {
            return src;
        }
        int srcFrames = src.Length / channels;
        int dstFrames = (int)Math.Round(srcFrames * (double)dstRate / srcRate);
        var dst = new float[dstFrames * channels];
        double ratio = (double)srcRate / dstRate;
        for (int f = 0; f < dstFrames; f++)
        {
            double pos = f * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, srcFrames - 1);
            double frac = pos - i0;
            for (int c = 0; c < channels; c++)
            {
                float a = src[(i0 * channels) + c];
                float b = src[(i1 * channels) + c];
                dst[(f * channels) + c] = a + (b - a) * (float)frac;
            }
        }
        return dst;
    }

    private void PumpLoop()
    {
        try
        {
            var byteBuffer = new byte[480 * _dstChannels * _dstBytesPerSample];
            while (!_stop)
            {
                int free = _renderer!.GetFreeFrames();
                if (free <= 0)
                {
                    _renderer.WaitForPacket(200);
                    continue;
                }
                int frames = Math.Min(free, 480);
                int needed = frames * _dstChannels;
                if (_queue.Count < needed)
                {
                    Thread.Sleep(5);
                    continue;
                }
                var src = new float[needed];
                for (int i = 0; i < needed; i++)
                {
                    _queue.TryDequeue(out src[i]);
                }
                SoundPadService.PackSamples(src, _dstChannels, byteBuffer, frames, _dstChannels, _dstFloat, _dstBytesPerSample, 1f);
                var handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                try
                {
                    _renderer.Write(handle.AddrOfPinnedObject(), frames);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
        catch (Exception ex)
        {
            App.AppendFileLog($"[混音路由] 渲染异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            _captureClient?.Stop();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _captureThread?.Join(1000);
        }
        catch
        {
        }
        try
        {
            _pump?.Join(1000);
        }
        catch
        {
        }
        if (_mixFormatPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_mixFormatPtr);
            _mixFormatPtr = IntPtr.Zero;
        }
        try
        {
            if (_capture is not null)
            {
                Marshal.ReleaseComObject(_capture);
            }
        }
        catch
        {
        }
        try
        {
            if (_captureClient is not null)
            {
                Marshal.ReleaseComObject(_captureClient);
            }
        }
        catch
        {
        }
        _capture = null;
        _captureClient = null;
        _renderer?.Dispose();
        _renderer = null;
        App.AppendFileLog("[混音路由] 已停止");
    }
}
