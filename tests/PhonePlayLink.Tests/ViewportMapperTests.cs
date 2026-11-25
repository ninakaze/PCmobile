using PhonePlayLink.Input;

namespace PhonePlayLink.Tests;

public class ViewportMapperTests
{
    [Fact]
    public void TryWindowToNormalized_PillarboxFit_MapsInsideAndRejectsBlackBars()
    {
        // Cửa sổ 1920x1080, video dọc 1080x2160 (tỷ lệ 1:2) => vùng hiển thị cao 1080, rộng 540, nằm giữa X=[690..1230]
        bool blackBarLeft = ViewportMapper.TryWindowToNormalized(
            windowX: 300, windowY: 540,
            windowWidth: 1920, windowHeight: 1080,
            videoWidth: 1080, videoHeight: 2160,
            preserveAspectRatio: true,
            out _, out _, clampOutsideToEdge: false);
        Assert.False(blackBarLeft);

        bool center = ViewportMapper.TryWindowToNormalized(
            windowX: 960, windowY: 540,
            windowWidth: 1920, windowHeight: 1080,
            videoWidth: 1080, videoHeight: 2160,
            preserveAspectRatio: true,
            out float nx, out float ny, clampOutsideToEdge: false);

        Assert.True(center);
        Assert.InRange(nx, 0.499f, 0.501f);
        Assert.InRange(ny, 0.499f, 0.501f);

        // Kéo ra dải đen khi clampOutsideToEdge=true sẽ bám sát mép 0.0
        bool clamped = ViewportMapper.TryWindowToNormalized(
            windowX: 100, windowY: 540,
            windowWidth: 1920, windowHeight: 1080,
            videoWidth: 1080, videoHeight: 2160,
            preserveAspectRatio: true,
            out float cnx, out _, clampOutsideToEdge: true);

        Assert.True(clamped);
        Assert.Equal(0f, cnx);
    }

    [Fact]
    public void TryWindowToNormalized_StretchMode_UsesFullWindow()
    {
        bool ok = ViewportMapper.TryWindowToNormalized(
            windowX: 480, windowY: 270,
            windowWidth: 1920, windowHeight: 1080,
            videoWidth: 1080, videoHeight: 2160,
            preserveAspectRatio: false,
            out float nx, out float ny);

        Assert.True(ok);
        Assert.InRange(nx, 0.249f, 0.251f);
        Assert.InRange(ny, 0.249f, 0.251f);
    }

    [Fact]
    public void NormalizedToDevicePixels_ScalesBothPortraitAndLandscapeCorrectly()
    {
        var (px1, py1) = ViewportMapper.NormalizedToDevicePixels(0.5f, 1.0f, 1080, 2400);
        Assert.Equal(540, px1);
        Assert.Equal(2399, py1);

        var (px2, py2) = ViewportMapper.NormalizedToDevicePixels(0.18f, 0.75f, 2400, 1080);
        Assert.Equal((int)MathF.Round(0.18f * 2399), px2);
        Assert.Equal((int)MathF.Round(0.75f * 1079), py2);
    }
}
