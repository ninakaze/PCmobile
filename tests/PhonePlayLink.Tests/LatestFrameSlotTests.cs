using System.Diagnostics;
using FFmpeg.AutoGen;
using PhonePlayLink.Core;
using PhonePlayLink.Media;

namespace PhonePlayLink.Tests;

public unsafe class LatestFrameSlotTests
{
    [Fact]
    public void Publish_DropsStaleUnrenderedFrame_AndKeepsOnlyLatest()
    {
        AssetPaths assets = AssetPaths.Resolve();
        assets.ValidateAll();
        FfmpegInitializer.Initialize(assets.FfmpegDirectory);

        var stats = new LatencyStats();
        using var slot = new LatestFrameSlot();

        AVFrame* raw1 = ffmpeg.av_frame_alloc();
        raw1->width = 640;
        raw1->height = 360;
        raw1->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

        AVFrame* raw2 = ffmpeg.av_frame_alloc();
        raw2->width = 1280;
        raw2->height = 720;
        raw2->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

        var f1 = new DecodedVideoFrame(raw1, Stopwatch.GetTimestamp());
        var f2 = new DecodedVideoFrame(raw2, Stopwatch.GetTimestamp());

        slot.Publish(f1, stats);
        slot.Publish(f2, stats); // f1 chưa kịp lấy => phải bị hủy và tăng DroppedFrames

        Assert.Equal(1, stats.DroppedFrames);

        using DecodedVideoFrame? taken = slot.TakeLatest();
        Assert.NotNull(taken);
        Assert.Equal(1280, taken!.Width);
        Assert.Equal(720, taken.Height);

        // Lần lấy tiếp theo khi chưa có frame mới phải trả về null
        Assert.Null(slot.TakeLatest());
    }
}
