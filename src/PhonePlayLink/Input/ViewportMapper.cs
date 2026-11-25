namespace PhonePlayLink.Input;

public readonly record struct VideoViewport(float X, float Y, float Width, float Height);

/// <summary>
/// Tính toán vùng hiển thị video trong cửa sổ SDL2 (có trừ chiều rộng Sidebar bên phải)
/// và quy đổi tọa độ chuột <-> tọa độ chuẩn hóa (0.0 -> 1.0) <-> pixel thiết bị.
/// Hỗ trợ chính xác cả chế độ Giữ tỷ lệ khung hình (Letterbox/Pillarbox), Kéo đầy (Stretch) và Fullscreen.
/// </summary>
public static class ViewportMapper
{
    public static VideoViewport ComputeViewport(
        int windowWidth,
        int windowHeight,
        int videoWidth,
        int videoHeight,
        bool preserveAspectRatio,
        float reservedRightPx = 0f)
    {
        float availW = Math.Max(1f, windowWidth - Math.Max(0f, reservedRightPx));
        float winH = Math.Max(1f, windowHeight);

        if (!preserveAspectRatio || videoWidth <= 0 || videoHeight <= 0)
        {
            return new VideoViewport(0f, 0f, availW, winH);
        }

        float videoAspect = (float)videoWidth / videoHeight;
        float winAspect = availW / winH;

        float vpWidth, vpHeight, vpX, vpY;
        if (winAspect > videoAspect)
        {
            vpHeight = winH;
            vpWidth = vpHeight * videoAspect;
            vpX = (availW - vpWidth) * 0.5f;
            vpY = 0f;
        }
        else
        {
            vpWidth = availW;
            vpHeight = vpWidth / videoAspect;
            vpX = 0f;
            vpY = (winH - vpHeight) * 0.5f;
        }

        return new VideoViewport(vpX, vpY, vpWidth, vpHeight);
    }

    public static bool TryWindowToNormalized(
        float windowX,
        float windowY,
        int windowWidth,
        int windowHeight,
        int videoWidth,
        int videoHeight,
        bool preserveAspectRatio,
        out float normX,
        out float normY,
        bool clampOutsideToEdge = false,
        float reservedRightPx = 0f)
    {
        VideoViewport vp = ComputeViewport(windowWidth, windowHeight, videoWidth, videoHeight, preserveAspectRatio, reservedRightPx);
        if (vp.Width <= 0f || vp.Height <= 0f)
        {
            normX = 0f;
            normY = 0f;
            return false;
        }

        float nx = (windowX - vp.X) / vp.Width;
        float ny = (windowY - vp.Y) / vp.Height;

        if (!clampOutsideToEdge && (nx < 0f || nx > 1f || ny < 0f || ny > 1f))
        {
            normX = 0f;
            normY = 0f;
            return false;
        }

        normX = Math.Clamp(nx, 0f, 1f);
        normY = Math.Clamp(ny, 0f, 1f);
        return true;
    }

    public static (int PixelX, int PixelY) NormalizedToDevicePixels(float normX, float normY, int screenWidth, int screenHeight)
    {
        int maxX = Math.Max(0, screenWidth - 1);
        int maxY = Math.Max(0, screenHeight - 1);

        int px = Math.Clamp((int)MathF.Round(Math.Clamp(normX, 0f, 1f) * maxX), 0, maxX);
        int py = Math.Clamp((int)MathF.Round(Math.Clamp(normY, 0f, 1f) * maxY), 0, maxY);
        return (px, py);
    }
}
