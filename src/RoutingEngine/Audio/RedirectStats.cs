namespace PerAppAudio.RoutingEngine.Audio;

/// <summary>手段A 运行统计（PoC-1 延迟/CPU/丢帧/爆音量测）。</summary>
public sealed class RedirectStats
{
    private long _packets;
    private long _framesCaptured;
    private long _framesWritten;
    private long _framesDropped;
    private long _discontinuities;
    private long _silentPackets;
    private double _processingMsTotal;
    private double _processingMsMax;
    private readonly object _gate = new();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public void RecordPacket(int framesCaptured, int framesWritten, int framesDropped,
        bool discontinuity, bool silent, double processingMs)
    {
        lock (_gate)
        {
            _packets++;
            _framesCaptured += framesCaptured;
            _framesWritten += framesWritten;
            _framesDropped += framesDropped;
            if (discontinuity) _discontinuities++;
            if (silent) _silentPackets++;
            _processingMsTotal += processingMs;
            if (processingMs > _processingMsMax) _processingMsMax = processingMs;
        }
    }

    public (long Packets, long FramesCaptured, long FramesWritten, long FramesDropped,
        long Discontinuities, long SilentPackets, double AvgProcessingMs, double MaxProcessingMs) Snapshot()
    {
        lock (_gate)
        {
            return (
                _packets,
                _framesCaptured,
                _framesWritten,
                _framesDropped,
                _discontinuities,
                _silentPackets,
                _packets == 0 ? 0 : _processingMsTotal / _packets,
                _processingMsMax);
        }
    }
}
