using FFmpeg.AutoGen;
using PhonePlayLink.Core;

namespace PhonePlayLink.Media;

/// <summary>
/// Bọc 1 AVFrame đã giải mã (có thể là GPU surface AV_PIX_FMT_D3D11 hoặc CPU YUV420P/NV12).
/// </summary>
public sealed unsafe class DecodedVideoFrame : IDisposable
{
    private IntPtr _framePtr;

    public int Width { get; }
    public int Height { get; }
    public AVPixelFormat PixelFormat { get; }
    public long PacketRecvTimestamp { get; }

    public AVFrame* Pointer => (AVFrame*)_framePtr;

    public DecodedVideoFrame(AVFrame* ownedFrame, long packetRecvTimestamp)
    {
        _framePtr = (IntPtr)ownedFrame;
        Width = ownedFrame->width;
        Height = ownedFrame->height;
        PixelFormat = (AVPixelFormat)ownedFrame->format;
        PacketRecvTimestamp = packetRecvTimestamp;
    }

    public void Dispose()
    {
        IntPtr ptr = Interlocked.Exchange(ref _framePtr, IntPtr.Zero);
        if (ptr != IntPtr.Zero)
        {
            AVFrame* f = (AVFrame*)ptr;
            ffmpeg.av_frame_free(&f);
        }
    }
}

/// <summary>
/// Zero-Buffer Frame Dropping Slot kèm tín hiệu đánh thức Event-Driven:
/// Luôn giữ đúng 1 frame mới nhất; nếu frame cũ chưa kịp render mà frame mới đến, hủy ngay frame cũ.
/// Đánh thức Render Thread tức thì khi có frame mới mà không ngủ thừa.
/// </summary>
public sealed class LatestFrameSlot : IDisposable
{
    private DecodedVideoFrame? _latest;
    private readonly AutoResetEvent _frameAvailable = new(false);
    private volatile bool _disposed;

    public WaitHandle FrameAvailableHandle => _frameAvailable;

    public void Publish(DecodedVideoFrame frame, LatencyStats? stats = null)
    {
        if (_disposed)
        {
            frame.Dispose();
            return;
        }

        DecodedVideoFrame? previous = Interlocked.Exchange(ref _latest, frame);
        if (previous is not null)
        {
            previous.Dispose();
            stats?.RecordDroppedFrame();
        }

        try { _frameAvailable.Set(); } catch (ObjectDisposedException) { }
    }

    public DecodedVideoFrame? TakeLatest()
    {
        return Interlocked.Exchange(ref _latest, null);
    }

    public bool WaitForNewFrame(int timeoutMs)
    {
        if (_disposed || Volatile.Read(ref _latest) is not null)
            return true;

        try
        {
            return _frameAvailable.WaitOne(timeoutMs);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void SignalWakeup()
    {
        if (!_disposed)
        {
            try { _frameAvailable.Set(); } catch (ObjectDisposedException) { }
        }
    }

    public void Clear()
    {
        DecodedVideoFrame? current = Interlocked.Exchange(ref _latest, null);
        current?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _frameAvailable.Set();
        _frameAvailable.Dispose();
    }
}
