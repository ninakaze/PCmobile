using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using PhonePlayLink.Core;

namespace PhonePlayLink.Media;

/// <summary>
/// Khởi tạo và cấu hình đường dẫn native DLL cho FFmpeg.AutoGen 9.0.1.1.
/// </summary>
public static unsafe class FfmpegInitializer
{
    private static int _initialized;
    private static readonly object Sync = new();

    public static void Initialize(string ffmpegDirectory)
    {
        if (Volatile.Read(ref _initialized) == 1)
            return;

        lock (Sync)
        {
            if (_initialized == 1)
                return;

            if (!Directory.Exists(ffmpegDirectory))
                throw new DirectoryNotFoundException($"Không tìm thấy thư mục FFmpeg DLLs: {ffmpegDirectory}");

            string fullPath = Path.GetFullPath(ffmpegDirectory);
            ffmpeg.RootPath = fullPath;

            // Thêm vào PATH tiến trình để các DLL phụ thuộc chéo (avcodec -> avutil, swresample...) resolve chính xác
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!currentPath.Contains(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", $"{fullPath};{currentPath}");
            }

            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            uint ver = ffmpeg.avcodec_version();
            uint major = (ver >> 16) & 0xFF;
            uint minor = (ver >> 8) & 0xFF;
            uint micro = ver & 0xFF;
            Logger.Ok("FFmpeg", $"Đã nạp FFmpeg native (avcodec {major}.{minor}.{micro}) từ {fullPath}");

            Volatile.Write(ref _initialized, 1);
        }
    }

    public static string GetErrorMessage(int error)
    {
        const int bufferSize = 256;
        byte* buffer = stackalloc byte[bufferSize];
        ffmpeg.av_strerror(error, buffer, bufferSize);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"FFmpeg error {error}";
    }
}
