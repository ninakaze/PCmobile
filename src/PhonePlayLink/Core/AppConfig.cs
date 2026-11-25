using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhonePlayLink.Keymap;

namespace PhonePlayLink.Core;

/// <summary>
/// Cấu hình khởi chạy và tùy chỉnh hiệu năng của PhonePlay Link (lưu/đọc từ config.json).
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string? DeviceSerial { get; set; }

    [JsonIgnore]
    public bool ListDevicesOnly { get; set; }

    [JsonIgnore]
    public bool ShowHelp { get; set; }

    public int VideoBitRate { get; set; } = 16_000_000;
    public float MaxFps { get; set; } = 120f;
    public int MaxSize { get; set; } = 0; // 0 = độ phân giải gốc thiết bị
    public string VideoCodec { get; set; } = "h264"; // h264 | h265

    public bool EnableAudio { get; set; } = true;
    public int AudioBitRate { get; set; } = 128_000;

    public bool StartFullscreen { get; set; }
    public bool PreserveAspectRatio { get; set; } = true;
    public bool PreferHardwareDecode { get; set; } = true;

    public bool AutoTurnScreenOff { get; set; }
    public bool ShowPerfHud { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public bool AutoHideSidebarInFullscreen { get; set; } = false;

    public InputKey DefaultAimToggleKey { get; set; } = InputKey.Grave;
    public float GlobalMouseSensitivity { get; set; } = 1.0f;
    public bool RememberWindowBounds { get; set; } = true;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    public static string ResolveDefaultConfigPath()
    {
        // Tìm config.json ở thư mục chạy hoặc repo root; mặc định lưu tại AppContext.BaseDirectory/config.json hoặc repo root nếu đang chạy dotnet run
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "config.json");
            if (File.Exists(candidate))
                return candidate;
            if (File.Exists(Path.Combine(dir, "PhonePlayLink.sln")))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    public static AppConfig LoadOrDefault(string configPath)
    {
        if (!File.Exists(configPath))
            return new AppConfig();

        try
        {
            string json = File.ReadAllText(configPath);
            AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is not null)
            {
                loaded.Normalize();
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Config", $"Không thể đọc {configPath}: {ex.Message}. Dùng cấu hình mặc định.");
        }

        return new AppConfig();
    }

    public void SaveToFile(string configPath)
    {
        Normalize();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(configPath, json);
    }

    public void Normalize()
    {
        VideoBitRate = Math.Clamp(VideoBitRate, 1_000_000, 50_000_000);
        MaxFps = Math.Clamp(MaxFps, 0f, 240f);
        MaxSize = Math.Max(0, MaxSize);
        VideoCodec = string.Equals(VideoCodec, "h265", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
            ? "h265"
            : "h264";
        AudioBitRate = Math.Clamp(AudioBitRate, 32_000, 512_000);
        GlobalMouseSensitivity = Math.Clamp(GlobalMouseSensitivity, 0.1f, 5.0f);
        if (DefaultAimToggleKey == InputKey.None)
            DefaultAimToggleKey = InputKey.Grave;
    }

    public static AppConfig LoadWithArgs(string configPath, string[] args)
    {
        AppConfig cfg = LoadOrDefault(configPath);
        ApplyArgs(cfg, args);
        cfg.Normalize();
        return cfg;
    }

    public static AppConfig ParseArgs(string[] args)
    {
        var cfg = new AppConfig();
        ApplyArgs(cfg, args);
        cfg.Normalize();
        return cfg;
    }

    private static void ApplyArgs(AppConfig cfg, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    cfg.ShowHelp = true;
                    break;
                case "-l":
                case "--list-devices":
                    cfg.ListDevicesOnly = true;
                    break;
                case "-s":
                case "--serial":
                    if (i + 1 < args.Length) cfg.DeviceSerial = args[++i];
                    break;
                case "-b":
                case "--bitrate":
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int br))
                        cfg.VideoBitRate = br;
                    break;
                case "--fps":
                    if (i + 1 < args.Length && float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float fps))
                        cfg.MaxFps = fps;
                    break;
                case "--max-size":
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                        cfg.MaxSize = ms;
                    break;
                case "--codec":
                    if (i + 1 < args.Length)
                    {
                        string c = args[++i].ToLowerInvariant();
                        if (c is "h264" or "h265" or "hevc")
                            cfg.VideoCodec = c == "hevc" ? "h265" : c;
                    }
                    break;
                case "--no-audio":
                    cfg.EnableAudio = false;
                    break;
                case "--fullscreen":
                    cfg.StartFullscreen = true;
                    break;
                case "--stretch":
                    cfg.PreserveAspectRatio = false;
                    break;
                case "--sw-decode":
                    cfg.PreferHardwareDecode = false;
                    break;
                case "--turn-screen-off":
                    cfg.AutoTurnScreenOff = true;
                    break;
            }
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            PhonePlay Link - Ultra-Low-Latency Android USB Game Streaming (Giai đoạn 4)

            Cách dùng:
              PhonePlayLink [tùy chọn]

            Tùy chọn:
              -s, --serial <id>       Chọn serial thiết bị Android (mặc định: tự động chọn)
              -l, --list-devices      Liệt kê các thiết bị Android đang cắm USB rồi thoát
              -b, --bitrate <bps>     Bitrate video (mặc định: 16000000 = 16 Mbps)
              --fps <n>               Giới hạn FPS tối đa (mặc định: 120, 0 = không giới hạn)
              --max-size <px>         Giới hạn cạnh dài nhất (mặc định: 0 = độ phân giải gốc)
              --codec <h264|h265>     Codec video (mặc định: h264)
              --no-audio              Tắt stream âm thanh Opus
              --fullscreen            Mở chế độ Borderless Fullscreen ngay khi khởi động
              --stretch               Kéo đầy khung hình (bỏ giữ tỷ lệ gốc)
              --sw-decode             Tắt giải mã phần cứng D3D11VA/DXVA2, dùng CPU
              --turn-screen-off       Tự tắt màn hình điện thoại khi bắt đầu stream
              -h, --help              Hiển thị hướng dẫn này

            Phím tắt trong cửa sổ game:
              F1                      Mở / Đóng Visual Keymap Editor (kéo thả & lưu JSON)
              F3                      Ẩn / Hiện gợi ý phím khi chơi
              F4                      Ẩn / Hiện HUD đo FPS & độ trễ
              F9                      Mở / Đóng bảng Cài đặt nhanh (Stream & Thiết bị)
              Alt+O                   Tắt / Bật màn hình điện thoại (chống nóng & tiết kiệm pin)
              ~ hoặc Right Ctrl       Khóa / Mở khóa con trỏ chuột (FPS Aim Look 360°)
              F11 hoặc Alt+Enter      Bật/Tắt Borderless Fullscreen
              F10                     Chuyển chế độ Giữ tỷ lệ khung hình / Kéo đầy màn hình
            """);
    }
}
