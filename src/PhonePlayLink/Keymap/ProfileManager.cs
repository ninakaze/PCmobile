using System.Text.Json;
using System.Text.Json.Serialization;
using PhonePlayLink.Core;

namespace PhonePlayLink.Keymap;

/// <summary>
/// Quản lý đọc/ghi các Profile Keymap JSON trong thư mục profiles\ và khớp tự động theo Android package name.
/// </summary>
public sealed class ProfileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProfilesDirectory { get; }

    public ProfileManager(string profilesDirectory)
    {
        ProfilesDirectory = Path.GetFullPath(profilesDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
    }

    public static string ResolveDefaultDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "profiles");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "profiles");
    }

    public IReadOnlyList<string> ListProfileFiles()
    {
        if (!Directory.Exists(ProfilesDirectory))
            return [];

        return Directory.GetFiles(ProfilesDirectory, "*.json")
            .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public KeymapProfile LoadFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        KeymapProfile? profile = JsonSerializer.Deserialize<KeymapProfile>(json, JsonOptions);
        if (profile is null)
            throw new InvalidDataException($"Không thể đọc KeymapProfile từ {filePath}");

        NormalizeProfile(profile);
        return profile;
    }

    public string SaveToFile(KeymapProfile profile, string? fileNameWithoutExt = null)
    {
        NormalizeProfile(profile);
        string safeBase = SanitizeFileName(string.IsNullOrWhiteSpace(fileNameWithoutExt) ? profile.Name : fileNameWithoutExt);
        string fullPath = Path.Combine(ProfilesDirectory, $"{safeBase}.json");
        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(fullPath, json);
        Logger.Ok("Profile", $"Đã lưu profile '{profile.Name}' -> {fullPath}");
        return fullPath;
    }

    public (string FilePath, KeymapProfile Profile)? FindByPackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return null;

        foreach (string file in ListProfileFiles())
        {
            try
            {
                KeymapProfile p = LoadFromFile(file);
                if (!string.IsNullOrWhiteSpace(p.PackageName) &&
                    string.Equals(p.PackageName.Trim(), packageName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return (file, p);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Profile", $"Bỏ qua file profile lỗi {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return null;
    }

    public (string FilePath, KeymapProfile Profile) LoadInitialOrCreateDefault()
    {
        var files = ListProfileFiles();
        string? fps = files.FirstOrDefault(f => Path.GetFileName(f).Equals("fps_default.json", StringComparison.OrdinalIgnoreCase));
        if (fps is not null)
        {
            return (fps, LoadFromFile(fps));
        }

        if (files.Count > 0)
        {
            return (files[0], LoadFromFile(files[0]));
        }

        var fallback = new KeymapProfile
        {
            Name = "Default",
            Joysticks = [new WasdJoystickBinding()],
            Taps = [new TapBinding { Label = "Action", Key = InputKey.Space, X = 0.85f, Y = 0.75f }]
        };
        string path = SaveToFile(fallback, "default");
        return (path, fallback);
    }

    private static void NormalizeProfile(KeymapProfile profile)
    {
        profile.HintOpacity = Math.Clamp(profile.HintOpacity, 0f, 1f);
        profile.Taps ??= [];
        profile.Joysticks ??= [];
        profile.FpsAims ??= [];
        profile.MobaSkills ??= [];
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "profile" : cleaned.ToLowerInvariant().Replace(' ', '_');
    }
}
