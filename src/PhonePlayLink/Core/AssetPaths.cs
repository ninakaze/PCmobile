namespace PhonePlayLink.Core;

/// <summary>
/// Định vị và kiểm tra các tệp phụ thuộc native (adb.exe, scrcpy-server, FFmpeg DLLs).
/// </summary>
public sealed class AssetPaths
{
    public string AssetsRoot { get; }
    public string AdbExecutablePath { get; }
    public string ScrcpyServerPath { get; }
    public string FfmpegDirectory { get; }

    private AssetPaths(string assetsRoot)
    {
        AssetsRoot = assetsRoot;
        AdbExecutablePath = Path.Combine(assetsRoot, "adb", "adb.exe");
        ScrcpyServerPath = Path.Combine(assetsRoot, "scrcpy", "scrcpy-server");
        FfmpegDirectory = Path.Combine(assetsRoot, "ffmpeg");
    }

    public static AssetPaths Resolve()
    {
        string? envDir = Environment.GetEnvironmentVariable("PHONEPLAY_ASSETS_DIR");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            return new AssetPaths(Path.GetFullPath(envDir));
        }

        // Tìm từ thư mục thực thi đi ngược lên các cấp cha (hỗ trợ cả dotnet run và release build)
        var candidates = new List<string>();
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            candidates.Add(Path.Combine(dir, "assets"));
            dir = Directory.GetParent(dir)?.FullName;
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "assets"));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(candidate, "adb", "adb.exe")) &&
                Directory.Exists(Path.Combine(candidate, "ffmpeg")))
            {
                return new AssetPaths(candidate);
            }
        }

        // Trả về vị trí mặc định nếu có thư mục assets để hiển thị thông báo thiếu chi tiết
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return new AssetPaths(candidate);
            }
        }

        throw new DirectoryNotFoundException(
            "Không tìm thấy thư mục 'assets'. Hãy chạy script: powershell -ExecutionPolicy Bypass -File .\\tools\\setup-deps.ps1");
    }

    public void ValidateAll()
    {
        var missing = new List<string>();

        if (!File.Exists(AdbExecutablePath))
            missing.Add($"adb.exe ({AdbExecutablePath})");

        if (!File.Exists(ScrcpyServerPath))
            missing.Add($"scrcpy-server ({ScrcpyServerPath})");

        if (!Directory.Exists(FfmpegDirectory) ||
            Directory.GetFiles(FfmpegDirectory, "avcodec-*.dll").Length == 0 ||
            Directory.GetFiles(FfmpegDirectory, "avutil-*.dll").Length == 0)
        {
            missing.Add($"FFmpeg shared DLLs ({FfmpegDirectory})");
        }

        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"Thiếu thành phần bắt buộc:\n - {string.Join("\n - ", missing)}\n" +
                "Vui lòng chạy: powershell -ExecutionPolicy Bypass -File .\\tools\\setup-deps.ps1");
        }
    }
}
