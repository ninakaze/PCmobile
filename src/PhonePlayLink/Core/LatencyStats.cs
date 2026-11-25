using System.Diagnostics;

namespace PhonePlayLink.Core;

/// <summary>
/// Bộ đếm hiệu năng thời gian thực theo cửa sổ trượt (rolling window 250ms):
/// Render FPS, Stream FPS, Decode (ms), Render/Present (ms), E2E (ms), Audio Queue (ms), Dropped Frames.
/// </summary>
public sealed class LatencyStats
{
    private const double WindowSeconds = 0.25;
    private const int RingCapacity = 256;

    private readonly object _sync = new();

    private readonly long[] _streamTimestamps = new long[RingCapacity];
    private int _streamHead;
    private int _streamCount;

    private readonly long[] _renderTimestamps = new long[RingCapacity];
    private readonly double[] _decodeSamples = new double[RingCapacity];
    private readonly double[] _renderSamples = new double[RingCapacity];
    private readonly double[] _e2eSamples = new double[RingCapacity];
    private int _renderHead;
    private int _renderCount;

    private long _droppedFramesTotal;
    private long _renderedFramesTotal;
    private double _audioQueueMs;
    private string _decoderBackend = "Init";

    // Cached snapshots (đọc không lock ở render HUD)
    private double _renderFps;
    private double _streamFps;
    private double _decodeLatencyMs;
    private double _renderLatencyMs;
    private double _e2eLatencyMs;

    public double CurrentFps => Volatile.Read(ref _renderFps);
    public double StreamFps => Volatile.Read(ref _streamFps);
    public double DecodeLatencyMs => Volatile.Read(ref _decodeLatencyMs);
    public double RenderLatencyMs => Volatile.Read(ref _renderLatencyMs);
    public double EndToEndLatencyMs => Volatile.Read(ref _e2eLatencyMs);
    public double AudioQueueMs => Volatile.Read(ref _audioQueueMs);
    public long DroppedFrames => Interlocked.Read(ref _droppedFramesTotal);
    public long RenderedFrames => Interlocked.Read(ref _renderedFramesTotal);

    public string DecoderBackend
    {
        get => Volatile.Read(ref _decoderBackend);
        set => Volatile.Write(ref _decoderBackend, value);
    }

    public void UpdateAudioQueueMs(double ms)
    {
        Volatile.Write(ref _audioQueueMs, Math.Max(0.0, ms));
    }

    public void RecordDroppedFrame()
    {
        Interlocked.Increment(ref _droppedFramesTotal);
    }

    public void RecordDecode(double decodeMs)
        => RecordDecodeAt(decodeMs, Stopwatch.GetTimestamp());

    public void RecordDecodeAt(double decodeMs, long timestamp)
    {
        lock (_sync)
        {
            _streamTimestamps[_streamHead] = timestamp;
            _decodeSamples[_streamHead] = decodeMs;
            _streamHead = (_streamHead + 1) % RingCapacity;
            if (_streamCount < RingCapacity) _streamCount++;

            RecomputeStreamLocked(timestamp);
        }
    }

    public void RecordRenderedFrame(long packetRecvTimestamp)
        => RecordRenderedFrameAt(packetRecvTimestamp, renderDurationMs: 0.0, Stopwatch.GetTimestamp());

    public void RecordRenderedFrameAt(long packetRecvTimestamp, double renderDurationMs, long nowTimestamp)
    {
        Interlocked.Increment(ref _renderedFramesTotal);

        double e2eMs = packetRecvTimestamp > 0
            ? Stopwatch.GetElapsedTime(packetRecvTimestamp, nowTimestamp).TotalMilliseconds
            : 0.0;

        lock (_sync)
        {
            _renderTimestamps[_renderHead] = nowTimestamp;
            _renderSamples[_renderHead] = renderDurationMs;
            _e2eSamples[_renderHead] = e2eMs;
            _renderHead = (_renderHead + 1) % RingCapacity;
            if (_renderCount < RingCapacity) _renderCount++;

            RecomputeRenderLocked(nowTimestamp);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _streamHead = _streamCount = 0;
            _renderHead = _renderCount = 0;
        }
        Interlocked.Exchange(ref _droppedFramesTotal, 0);
        Interlocked.Exchange(ref _renderedFramesTotal, 0);
        Volatile.Write(ref _renderFps, 0);
        Volatile.Write(ref _streamFps, 0);
        Volatile.Write(ref _decodeLatencyMs, 0);
        Volatile.Write(ref _renderLatencyMs, 0);
        Volatile.Write(ref _e2eLatencyMs, 0);
        Volatile.Write(ref _audioQueueMs, 0);
    }

    private void RecomputeStreamLocked(long now)
    {
        double sumDec = 0;
        int valid = 0;
        long oldestInWin = now;

        for (int i = 0; i < _streamCount; i++)
        {
            int idx = (_streamHead - 1 - i + RingCapacity) % RingCapacity;
            long ts = _streamTimestamps[idx];
            if (Stopwatch.GetElapsedTime(ts, now).TotalSeconds > WindowSeconds)
                break;

            sumDec += _decodeSamples[idx];
            oldestInWin = ts;
            valid++;
        }

        if (valid > 0)
        {
            Volatile.Write(ref _decodeLatencyMs, sumDec / valid);
            double span = Math.Max(0.05, Stopwatch.GetElapsedTime(oldestInWin, now).TotalSeconds);
            Volatile.Write(ref _streamFps, valid >= 2 ? (valid - 1) / span : valid / WindowSeconds);
        }
    }

    private void RecomputeRenderLocked(long now)
    {
        double sumRender = 0;
        double sumE2e = 0;
        int valid = 0;
        int validE2e = 0;
        long oldestInWin = now;

        for (int i = 0; i < _renderCount; i++)
        {
            int idx = (_renderHead - 1 - i + RingCapacity) % RingCapacity;
            long ts = _renderTimestamps[idx];
            if (Stopwatch.GetElapsedTime(ts, now).TotalSeconds > WindowSeconds)
                break;

            sumRender += _renderSamples[idx];
            if (_e2eSamples[idx] > 0)
            {
                sumE2e += _e2eSamples[idx];
                validE2e++;
            }
            oldestInWin = ts;
            valid++;
        }

        if (valid > 0)
        {
            Volatile.Write(ref _renderLatencyMs, sumRender / valid);
            if (validE2e > 0)
                Volatile.Write(ref _e2eLatencyMs, sumE2e / validE2e);
            double span = Math.Max(0.05, Stopwatch.GetElapsedTime(oldestInWin, now).TotalSeconds);
            Volatile.Write(ref _renderFps, valid >= 2 ? (valid - 1) / span : valid / WindowSeconds);
        }
    }
}
