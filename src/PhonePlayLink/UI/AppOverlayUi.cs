using System.Numerics;
using ImGuiNET;
using PhonePlayLink.Adb;
using PhonePlayLink.Core;
using PhonePlayLink.Keymap;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.UI;

/// <summary>
/// Quản lý toàn bộ các thành phần giao diện ImGui theo phong cách LDPlayer / MuMu / BlueStacks:
/// A) Device Hub (Màn hình chờ & chọn thiết bị USB)
/// B) Right Sidebar (Thanh công cụ 48px bên phải + 3 nút Back/Home/Đa nhiệm)
/// D) Bảng Cài đặt nhanh 2 tab (Hình ảnh & Độ trễ | Điều khiển & Phím tắt)
/// E) Thanh HUD FPS & Độ trễ (F4, góc trên bên trái)
/// </summary>
public sealed unsafe class AppOverlayUi
{
    public const float SidebarWidthPx = 48f;

    private static readonly int[] BitrateValues = [8_000_000, 12_000_000, 16_000_000, 24_000_000];
    private static readonly string[] BitrateLabels = ["8 Mbps (Siêu nhẹ)", "12 Mbps (Cân bằng)", "16 Mbps (Sắc nét)", "24 Mbps (Tối đa)"];

    private static readonly float[] FpsValues = [60f, 90f, 120f, 0f];
    private static readonly string[] FpsLabels = ["60 FPS", "90 FPS", "120 FPS", "Không giới hạn"];

    private static readonly int[] MaxSizeValues = [0, 1080, 720];
    private static readonly string[] MaxSizeLabels = ["Gốc (Native)", "1080p", "720p (Mát máy)"];

    // Tab 1: Hình ảnh & Độ trễ
    private int _editCodecIdx;
    private int _editFpsIdx;
    private int _editBitrateIdx;
    private int _editMaxSizeIdx;
    private bool _editEnableAudio;
    private bool _editAutoScreenOff;
    private bool _editPreferHw;
    private string? _editSelectedSerial;

    // Tab 2: Điều khiển & Phím tắt
    private InputKey _editAimToggleKey = InputKey.Grave;
    private float _editMouseSens = 1.0f;
    private bool _editRememberWindowBounds = true;
    private bool _editAutoHideSidebarFullscreen;
    private bool _listeningForAimToggleKey;

    public bool IsSettingsOpen { get; private set; }
    public bool IsListeningForSettingsKey => IsSettingsOpen && _listeningForAimToggleKey;

    public event Action<string?>? ConnectDeviceRequested;
    public event Action? RefreshDevicesRequested;
    public event Action? ApplyAndReconnectRequested;
    public event Action? ControlSettingsSaved;
    public event Action? ToggleKeymapEditorRequested;
    public event Action? ToggleAimLockRequested;
    public event Action? ToggleHintsRequested;
    public event Action? ToggleFullscreenRequested;
    public event Action? ToggleAspectRatioRequested;
    public event Action? TogglePhoneScreenRequested;
    public event Action? ToggleAudioMuteRequested;
    public event Action? TogglePerfHudRequested;
    public event Action? DisconnectDeviceRequested;
    public event Action<int>? SendAndroidNavKeyRequested;

    public float GetSidebarReservedWidth(bool isConnected, AppConfig config, bool isFullscreen)
    {
        if (!isConnected || config.SidebarCollapsed)
            return 0f;
        if (isFullscreen && config.AutoHideSidebarInFullscreen)
            return 0f;
        return SidebarWidthPx;
    }

    public void SyncDraftFromConfig(AppConfig config)
    {
        _editCodecIdx = string.Equals(config.VideoCodec, "h265", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _editFpsIdx = Array.IndexOf(FpsValues, config.MaxFps);
        if (_editFpsIdx < 0) _editFpsIdx = 2;

        _editBitrateIdx = FindClosestIndex(BitrateValues, config.VideoBitRate);
        _editMaxSizeIdx = FindClosestIndex(MaxSizeValues, config.MaxSize);
        _editEnableAudio = config.EnableAudio;
        _editAutoScreenOff = config.AutoTurnScreenOff;
        _editPreferHw = config.PreferHardwareDecode;
        _editSelectedSerial = config.DeviceSerial;

        _editAimToggleKey = config.DefaultAimToggleKey;
        _editMouseSens = config.GlobalMouseSensitivity;
        _editRememberWindowBounds = config.RememberWindowBounds;
        _editAutoHideSidebarFullscreen = config.AutoHideSidebarInFullscreen;
    }

    public void ToggleSettings(AppConfig config)
    {
        IsSettingsOpen = !IsSettingsOpen;
        _listeningForAimToggleKey = false;
        if (IsSettingsOpen)
        {
            SyncDraftFromConfig(config);
        }
    }

    public void OpenSettings(AppConfig config)
    {
        if (!IsSettingsOpen)
            ToggleSettings(config);
    }

    public bool TryCaptureSettingsKey(InputKey key)
    {
        if (!IsSettingsOpen || !_listeningForAimToggleKey)
            return false;

        if (key == InputKey.Escape)
        {
            _listeningForAimToggleKey = false;
            return true;
        }

        if (key != InputKey.None)
        {
            _editAimToggleKey = key;
            _listeningForAimToggleKey = false;
        }
        return true;
    }

    // =========================================================================
    // A. MÀN HÌNH CHỜ & KẾT NỐI THIẾT BỊ (DEVICE HUB)
    // =========================================================================
    public void DrawDeviceHubScreen(
        AppConfig config,
        IReadOnlyList<AdbDeviceInfo> devices,
        bool isAdbConnected,
        string? connectingDeviceLabel,
        string? lastError,
        ImFontPtr headingFont)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(display.X * 0.5f, display.Y * 0.5f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(Math.Min(640f, Math.Max(420f, display.X - 48f)), 0f), ImGuiCond.Always);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##DeviceHub", flags))
        {
            ImGui.End();
            return;
        }

        // Header Logo + Trạng thái USB
        if (headingFont.NativePtr != null) ImGui.PushFont(headingFont);
        ImGui.TextColored(new Vector4(0.00f, 0.78f, 0.94f, 1f), "PhonePlay Link");
        if (headingFont.NativePtr != null) ImGui.PopFont();

        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f);
        ImGui.TextDisabled(" | Chơi game Android trực tiếp qua USB (Zero-Emulator)");

        ImGui.Spacing();
        Vector4 statusCol = isAdbConnected ? new Vector4(0.20f, 0.92f, 0.62f, 1f) : new Vector4(1f, 0.78f, 0.25f, 1f);
        string statusMsg = isAdbConnected
            ? "Đang quét thiết bị USB theo thời gian thực (ADB sẵn sàng)"
            : "Đang khởi động dịch vụ ADB...";
        ImGui.TextColored(statusCol, $"● {statusMsg}");

        ImGui.SameLine();
        float buttonsW = 240f;
        float rightSpace = ImGui.GetContentRegionAvail().X;
        if (rightSpace > buttonsW)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + rightSpace - buttonsW);

        if (ImGui.Button("Làm mới", new Vector2(88f, 0)))
        {
            RefreshDevicesRequested?.Invoke();
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.23f, 0.32f, 1f));
        if (ImGui.Button("Cài đặt chất lượng", new Vector2(144f, 0)))
        {
            OpenSettings(config);
        }
        ImGui.PopStyleColor();

        ImGui.Separator();

        if (!string.IsNullOrEmpty(connectingDeviceLabel))
        {
            ImGui.BeginChild("##ConnectingBox", new Vector2(-1, 86f), ImGuiChildFlags.Borders);
            ImGui.TextColored(new Vector4(0.00f, 0.80f, 0.95f, 1f), $"Đang kết nối tới {connectingDeviceLabel}...");
            ImGui.Spacing();
            ImGui.TextWrapped("Đang khởi chạy scrcpy-server v5.0.1 và mở 3 luồng Video / Audio / Control độ trễ thấp...");
            ImGui.EndChild();
            ImGui.End();
            return;
        }

        if (!string.IsNullOrEmpty(lastError))
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.40f, 1f), $"Lỗi kết nối gần nhất: {lastError}");
            ImGui.Spacing();
        }

        if (devices.Count == 0)
        {
            ImGui.BeginChild("##GuideBox", new Vector2(-1, 152f), ImGuiChildFlags.Borders);
            ImGui.TextColored(new Vector4(1f, 0.84f, 0.32f, 1f), "Chưa phát hiện thiết bị Android — Hướng dẫn kết nối 3 bước:");
            ImGui.Spacing();
            ImGui.TextWrapped("1. Cắm cáp USB truyền dữ liệu từ điện thoại vào máy tính.");
            ImGui.TextWrapped("2. Mở Cài đặt > Tùy chọn nhà phát triển > Bật 'Gỡ lỗi USB (USB Debugging)' (trên Xiaomi/OPPO/Realme bật thêm 'Gỡ lỗi USB (Cài đặt bảo mật)').");
            ImGui.TextWrapped("3. Nhấn 'Cho phép' trên hộp thoại xác nhận kết nối USB hiện trên màn hình điện thoại — app sẽ tự động vào game.");
            ImGui.EndChild();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.30f, 0.94f, 0.65f, 1f), $"Phát hiện {devices.Count} thiết bị Android:");
            ImGui.Spacing();

            float listH = MathF.Min(210f, devices.Count * 54f + 12f);
            ImGui.BeginChild("##DeviceList", new Vector2(-1, listH), ImGuiChildFlags.Borders);
            foreach (AdbDeviceInfo dev in devices)
            {
                ImGui.PushID(dev.Serial);
                string connBadge = dev.IsUsb ? "USB" : "Wi-Fi";
                Vector4 stCol = dev.IsOnline ? new Vector4(0.25f, 0.95f, 0.62f, 1f) : new Vector4(1f, 0.72f, 0.25f, 1f);
                string stText = dev.IsOnline ? "Sẵn sàng" : "Chưa cấp quyền USB";

                ImGui.Text($"{dev.DisplayName}  [{connBadge}]");
                ImGui.SameLine(330f);
                ImGui.TextColored(stCol, stText);
                ImGui.SameLine(490f);
                if (dev.IsOnline)
                {
                    if (ImGui.Button("Kết nối", new Vector2(-1, 0)))
                    {
                        ConnectDeviceRequested?.Invoke(dev.Serial);
                    }
                }
                else
                {
                    ImGui.TextDisabled("Chờ cấp quyền");
                }
                ImGui.PopID();
            }
            ImGui.EndChild();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Phím tắt: F1 Keymap Editor  |  F4 HUD  |  F9 Cài đặt  |  F11 Toàn màn hình");
        ImGui.End();
    }

    // =========================================================================
    // B. THANH CÔNG CỤ BÊN PHẢI GIỐNG LDPLAYER / BLUESTACKS (RIGHT SIDEBAR)
    // =========================================================================
    public void DrawRightSidebar(
        AppConfig config,
        bool isFullscreen,
        bool isEditorOpen,
        bool isAimLocked,
        bool showHints,
        bool isPhoneScreenOn,
        bool isAudioMuted,
        bool hasAudio)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        bool hiddenByFullscreen = isFullscreen && config.AutoHideSidebarInFullscreen;
        bool collapsed = config.SidebarCollapsed || hiddenByFullscreen;

        if (collapsed)
        {
            ImGui.SetNextWindowPos(new Vector2(display.X - 34f, 10f), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(28f, 28f), ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
            const ImGuiWindowFlags miniFlags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings;
            if (ImGui.Begin("##SidebarExpand", miniFlags))
            {
                if (ImGui.Button("<", new Vector2(24f, 24f)))
                {
                    config.SidebarCollapsed = false;
                    if (isFullscreen) config.AutoHideSidebarInFullscreen = false;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mở rộng thanh công cụ");
            }
            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(display.X - SidebarWidthPx, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(SidebarWidthPx, display.Y), ImGuiCond.Always);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 5f));

        const ImGuiWindowFlags barFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("##RightSidebar", barFlags))
        {
            Vector2 btnSize = new(38f, 29f);

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.17f, 0.24f, 1f));
            if (ImGui.Button(">##CollapseBar", new Vector2(38f, 22f)))
                config.SidebarCollapsed = true;
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Thu gọn thanh công cụ");

            ImGui.Separator();

            // 1. Keymap (F1)
            if (SidebarToggleButton("Key##sb1", "Keymap (F1): Mở/đóng giao diện chỉnh phím kéo thả", isEditorOpen, btnSize))
                ToggleKeymapEditorRequested?.Invoke();

            // 2. Khóa chuột FPS (~)
            if (SidebarToggleButton("Aim##sb2", $"Khóa chuột FPS ({config.DefaultAimToggleKey.ToShortLabel()}): Bật/tắt xoay camera bằng chuột", isAimLocked, btnSize))
                ToggleAimLockRequested?.Invoke();

            // 3. Gợi ý phím (F3)
            if (SidebarToggleButton("Phím##sb3", "Gợi ý phím (F3): Ẩn/hiện lớp chữ phím tắt mờ khi chơi", showHints, btnSize))
                ToggleHintsRequested?.Invoke();

            // 4. Toàn màn hình (F11)
            if (SidebarToggleButton("Full##sb4", "Toàn màn hình (F11): Bật/tắt Borderless Fullscreen", isFullscreen, btnSize))
                ToggleFullscreenRequested?.Invoke();

            // 5. Tỷ lệ khung hình (F10)
            if (SidebarToggleButton(config.PreserveAspectRatio ? "TỷLệ##sb5" : "Đầy##sb5", "Tỷ lệ khung hình (F10): Giữ tỷ lệ gốc / Kéo đầy màn hình", !config.PreserveAspectRatio, btnSize))
                ToggleAspectRatioRequested?.Invoke();

            // 6. Tắt màn hình ĐT (Alt+O)
            if (SidebarToggleButton(isPhoneScreenOn ? "Màn##sb6" : "TắtM##sb6", "Tắt màn hình ĐT (Alt+O): Tắt/mở màn hình điện thoại chống nóng", !isPhoneScreenOn, btnSize))
                TogglePhoneScreenRequested?.Invoke();

            // 7. Âm thanh
            string volLbl = !hasAudio ? "NoAu##sb7" : (isAudioMuted ? "Mute##sb7" : "Loa##sb7");
            if (SidebarToggleButton(volLbl, "Âm thanh: Bật/tắt tiếng nhanh (Mute/Unmute)", hasAudio && !isAudioMuted, btnSize))
                ToggleAudioMuteRequested?.Invoke();

            // 8. HUD Hiệu năng (F4)
            if (SidebarToggleButton("HUD##sb8", "HUD Hiệu năng (F4): Ẩn/hiện bảng đo FPS & độ trễ", config.ShowPerfHud, btnSize))
                TogglePerfHudRequested?.Invoke();

            // 9. Cài đặt (F9)
            if (SidebarToggleButton("Cài##sb9", "Cài đặt (F9): Mở bảng cấu hình chi tiết", IsSettingsOpen, btnSize))
                ToggleSettings(config);

            // 10. Ngắt kết nối
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.60f, 0.18f, 0.22f, 0.88f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.80f, 0.24f, 0.28f, 1.00f));
            if (ImGui.Button("Ngắt##sb10", btnSize))
                DisconnectDeviceRequested?.Invoke();
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ngắt kết nối: Quay về Device Hub để đổi điện thoại khác");

            // 3 nút điều hướng nhanh Android ở cuối Sidebar (Back / Home / Đa nhiệm)
            float navBlockHeight = 108f;
            float targetY = display.Y - navBlockHeight;
            if (targetY > ImGui.GetCursorPosY() + 8f)
            {
                ImGui.SetCursorPosY(targetY);
            }

            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.17f, 0.24f, 0.95f));
            if (ImGui.Button("<##navBack", new Vector2(38f, 26f)))
                SendAndroidNavKeyRequested?.Invoke(ScrcpyConstants.AndroidKeyCodeBack);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back (Quay lại trên Android)");

            if (ImGui.Button("O##navHome", new Vector2(38f, 26f)))
                SendAndroidNavKeyRequested?.Invoke(ScrcpyConstants.AndroidKeyCodeHome);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Home (Màn hình chính Android)");

            if (ImGui.Button("[]##navRecents", new Vector2(38f, 26f)))
                SendAndroidNavKeyRequested?.Invoke(ScrcpyConstants.AndroidKeyCodeAppSwitch);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Đa nhiệm (Recent Apps)");
            ImGui.PopStyleColor();
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    private static bool SidebarToggleButton(string label, string tooltip, bool active, Vector2 size)
    {
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.000f, 0.706f, 0.847f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.11f, 0.80f, 0.94f, 1.00f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.125f, 0.150f, 0.212f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.19f, 0.24f, 0.34f, 1.00f));
        }

        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
        return clicked;
    }

    // =========================================================================
    // D. BẢNG CÀI ĐẶT NHANH 2 TAB (F9)
    // =========================================================================
    public void DrawQuickSettingsWindow(
        AppConfig config,
        string configPath,
        IReadOnlyList<AdbDeviceInfo> devices,
        string? activeSerial,
        bool isPhoneScreenOn,
        bool isConnected)
    {
        if (!IsSettingsOpen)
            return;

        ImGui.SetNextWindowPos(new Vector2(24f, 52f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430f, 0f), ImGuiCond.FirstUseEver);

        bool open = IsSettingsOpen;
        if (!ImGui.Begin("Cài đặt PhonePlay Link (F9)", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            IsSettingsOpen = open;
            ImGui.End();
            return;
        }
        IsSettingsOpen = open;

        if (ImGui.BeginTabBar("##SettingsTabs"))
        {
            if (ImGui.BeginTabItem("Hình ảnh & Độ trễ"))
            {
                var onlineDevices = devices.Where(d => d.IsOnline).ToList();
                string selectedPreview = string.IsNullOrWhiteSpace(_editSelectedSerial)
                    ? "Tự động chọn thiết bị đầu tiên"
                    : _editSelectedSerial;

                if (ImGui.BeginCombo("Thiết bị USB", selectedPreview))
                {
                    if (ImGui.Selectable("Tự động chọn thiết bị đầu tiên", string.IsNullOrWhiteSpace(_editSelectedSerial)))
                        _editSelectedSerial = null;

                    foreach (var d in onlineDevices)
                    {
                        bool sel = string.Equals(_editSelectedSerial, d.Serial, StringComparison.OrdinalIgnoreCase);
                        string tag = string.Equals(d.Serial, activeSerial, StringComparison.OrdinalIgnoreCase) ? " [Đang nối]" : "";
                        if (ImGui.Selectable($"{d.DisplayName}{tag}", sel))
                            _editSelectedSerial = d.Serial;
                    }
                    ImGui.EndCombo();
                }

                ImGui.Separator();
                string[] codecs = ["H.264 (Giải mã nhanh nhất)", "H.265 / HEVC (Tiết kiệm băng thông)"];
                ImGui.Combo("Video Codec", ref _editCodecIdx, codecs, codecs.Length);
                ImGui.Combo("FPS tối đa", ref _editFpsIdx, FpsLabels, FpsLabels.Length);
                ImGui.Combo("Bitrate", ref _editBitrateIdx, BitrateLabels, BitrateLabels.Length);
                ImGui.Combo("Độ phân giải tối đa", ref _editMaxSizeIdx, MaxSizeLabels, MaxSizeLabels.Length);

                ImGui.Checkbox("Bật âm thanh Opus (Android 11+)", ref _editEnableAudio);
                ImGui.Checkbox("Giải mã phần cứng D3D11VA / DXVA2", ref _editPreferHw);
                ImGui.Checkbox("Tự tắt màn hình điện thoại khi kết nối", ref _editAutoScreenOff);

                if (isConnected)
                {
                    string pwrText = isPhoneScreenOn ? "Tắt màn hình điện thoại ngay (Alt+O)" : "Bật lại màn hình điện thoại (Alt+O)";
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.24f, 0.33f, 1f));
                    if (ImGui.Button(pwrText, new Vector2(-1, 0)))
                        TogglePhoneScreenRequested?.Invoke();
                    ImGui.PopStyleColor();
                }

                ImGui.Spacing();
                if (ImGui.Button("Lưu & Áp dụng", new Vector2(-1, 32f)))
                {
                    ApplyDraftToConfig(config);
                    config.SaveToFile(configPath);
                    IsSettingsOpen = false;
                    if (isConnected)
                        ApplyAndReconnectRequested?.Invoke();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Điều khiển & Phím tắt"))
            {
                ImGui.Text("Phím khóa chuột FPS mặc định:");
                ImGui.SameLine(220f);
                string btnLbl = _listeningForAimToggleKey
                    ? ">> Nhấn phím bất kỳ... <<"
                    : $"{_editAimToggleKey}##AimToggleGlobal";
                if (ImGui.Button(btnLbl, new Vector2(-1, 0)))
                {
                    _listeningForAimToggleKey = !_listeningForAimToggleKey;
                }

                ImGui.SliderFloat("Độ nhạy chuột chung", ref _editMouseSens, 0.2f, 3.0f, "%.2fx");
                ImGui.Checkbox("Tự lưu vị trí & kích thước cửa sổ", ref _editRememberWindowBounds);
                ImGui.Checkbox("Tự thu gọn thanh công cụ khi Toàn màn hình", ref _editAutoHideSidebarFullscreen);

                bool hud = config.ShowPerfHud;
                if (ImGui.Checkbox("Hiển thị HUD FPS & Độ trễ (F4)", ref hud))
                    config.ShowPerfHud = hud;

                ImGui.Spacing();
                if (ImGui.Button("Lưu cài đặt", new Vector2(-1, 32f)))
                {
                    ApplyDraftToConfig(config);
                    config.SaveToFile(configPath);
                    ControlSettingsSaved?.Invoke();
                    IsSettingsOpen = false;
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // =========================================================================
    // E. THANH HUD ĐO FPS & ĐỘ TRỄ (F4) — GÓC TRÊN BÊN TRÁI (1–2 DÒNG GỌN)
    // =========================================================================
    public void DrawPerfHud(
        LatencyStats stats,
        AppConfig config,
        int videoWidth,
        int videoHeight,
        bool isPhoneScreenOn,
        int activePointers,
        bool isAimLocked,
        bool editorOpen)
    {
        if (!config.ShowPerfHud)
            return;

        float topY = editorOpen ? 104f : 12f;
        ImGui.SetNextWindowPos(new Vector2(12f, topY), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.55f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 6f));
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs;

        if (ImGui.Begin("##PerfHud", flags))
        {
            ImGui.TextColored(
                new Vector4(0.25f, 0.95f, 0.65f, 1f),
                $"FPS: {stats.CurrentFps:F1}  |  Decode: {stats.DecodeLatencyMs:F2}ms  |  {videoWidth}x{videoHeight}  |  {stats.DecoderBackend}");

            string scrTag = isPhoneScreenOn ? "Màn ĐT: BẬT" : "Màn ĐT: TẮT";
            string aimTag = isAimLocked ? "  |  KHÓA TÂM" : "";
            ImGui.Text(
                $"Touch: {activePointers}/10  |  Drop: {stats.DroppedFrames}  |  {scrTag}{aimTag}");

            ImGui.End();
        }
        ImGui.PopStyleVar();
    }

    private void ApplyDraftToConfig(AppConfig config)
    {
        config.DeviceSerial = _editSelectedSerial;
        config.VideoCodec = _editCodecIdx == 1 ? "h265" : "h264";
        config.MaxFps = FpsValues[Math.Clamp(_editFpsIdx, 0, FpsValues.Length - 1)];
        config.VideoBitRate = BitrateValues[Math.Clamp(_editBitrateIdx, 0, BitrateValues.Length - 1)];
        config.MaxSize = MaxSizeValues[Math.Clamp(_editMaxSizeIdx, 0, MaxSizeValues.Length - 1)];
        config.EnableAudio = _editEnableAudio;
        config.AutoTurnScreenOff = _editAutoScreenOff;
        config.PreferHardwareDecode = _editPreferHw;

        config.DefaultAimToggleKey = _editAimToggleKey;
        config.GlobalMouseSensitivity = _editMouseSens;
        config.RememberWindowBounds = _editRememberWindowBounds;
        config.AutoHideSidebarInFullscreen = _editAutoHideSidebarFullscreen;
        config.Normalize();
    }

    private static int FindClosestIndex(int[] values, int target)
    {
        int bestIdx = 0;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            int diff = Math.Abs(values[i] - target);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
