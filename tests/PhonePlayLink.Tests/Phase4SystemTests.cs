using System.Diagnostics;
using System.Text;
using ImGuiNET;
using PhonePlayLink.Adb;
using PhonePlayLink.Core;
using PhonePlayLink.Keymap;
using PhonePlayLink.Rendering;
using PhonePlayLink.UI;

namespace PhonePlayLink.Tests;

[Collection("SdlWindow")]
public class Phase4SystemTests
{
    [Fact]
    public void UsbDeviceMonitor_BuildAdbServiceRequest_FormatsHexPrefixCorrectly()
    {
        byte[] req = UsbDeviceMonitor.BuildAdbServiceRequest("host:track-devices-l");
        Assert.Equal("0014host:track-devices-l", Encoding.ASCII.GetString(req));

        byte[] req2 = UsbDeviceMonitor.BuildAdbServiceRequest("host:track-devices");
        Assert.Equal("0012host:track-devices", Encoding.ASCII.GetString(req2));
    }

    [Fact]
    public void UsbDeviceMonitor_ParseTrackDevicesPayload_HandlesPlugUnplugAndMultiDevice()
    {
        const string twoDevices = """
            192.168.1.99:5555      device product:husky model:Pixel_8_Pro device:husky transport_id:4
            R5CX987654Z            device product:e3qxxx model:Galaxy_S24_Ultra device:e3q transport_id:2
            ABCD1111               unauthorized usb:1-3 transport_id:3
            """;

        var list = UsbDeviceMonitor.ParseTrackDevicesPayload(twoDevices);
        Assert.Equal(3, list.Count);
        // USB ưu tiên lên đầu trước TCP
        Assert.True(list[0].IsUsb);
        Assert.True(list[1].IsUsb);
        Assert.False(list[2].IsUsb);

        // Khi rút hết cáp => payload rỗng => 0 thiết bị
        var empty = UsbDeviceMonitor.ParseTrackDevicesPayload(string.Empty);
        Assert.Empty(empty);
    }

    [Fact]
    public void LatencyStats_RollingWindow250ms_ComputesAccurateAveragesAndIgnoresStaleSamples()
    {
        var stats = new LatencyStats();
        long t0 = Stopwatch.GetTimestamp();
        long tickPerMs = Stopwatch.Frequency / 1000;

        // Mẫu cũ cách đây 400ms (ngoài cửa sổ 250ms -> không được làm sai trung bình hiện tại)
        stats.RecordDecodeAt(50.0, t0);
        stats.RecordRenderedFrameAt(t0, 40.0, t0 + 2 * tickPerMs);

        // 4 mẫu mới trong vòng 100ms gần nhất
        long recentStart = t0 + 400 * tickPerMs;
        for (int i = 0; i < 4; i++)
        {
            long ts = recentStart + i * 20 * tickPerMs;
            stats.RecordDecodeAt(2.0, ts);
            stats.RecordRenderedFrameAt(ts - 3 * tickPerMs, 1.5, ts);
        }

        stats.UpdateAudioQueueMs(12.5);

        Assert.InRange(stats.DecodeLatencyMs, 1.9, 2.1);
        Assert.InRange(stats.RenderLatencyMs, 1.4, 1.6);
        Assert.InRange(stats.EndToEndLatencyMs, 2.8, 3.2);
        Assert.InRange(stats.AudioQueueMs, 12.4, 12.6);
        Assert.True(stats.CurrentFps > 30.0);
        Assert.True(stats.StreamFps > 30.0);

        stats.Reset();
        Assert.Equal(0, stats.RenderedFrames);
        Assert.Equal(0, stats.CurrentFps);
    }

    [Fact]
    public void AppConfig_SavesLoadsAndAppliesCliOverrides()
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"ppl_cfg_{Guid.NewGuid():N}.json");
        try
        {
            var cfg = new AppConfig
            {
                DeviceSerial = "R5CX123",
                VideoBitRate = 12_000_000,
                MaxFps = 90f,
                MaxSize = 1080,
                VideoCodec = "h265",
                EnableAudio = false,
                AutoTurnScreenOff = true,
                ShowPerfHud = false,
                DefaultAimToggleKey = InputKey.Tab,
                GlobalMouseSensitivity = 1.75f,
                RememberWindowBounds = false,
                WindowWidth = 1440,
                WindowHeight = 900
            };
            cfg.SaveToFile(tmpFile);

            AppConfig loaded = AppConfig.LoadOrDefault(tmpFile);
            Assert.Equal("R5CX123", loaded.DeviceSerial);
            Assert.Equal(12_000_000, loaded.VideoBitRate);
            Assert.Equal(90f, loaded.MaxFps);
            Assert.Equal(1080, loaded.MaxSize);
            Assert.Equal("h265", loaded.VideoCodec);
            Assert.False(loaded.EnableAudio);
            Assert.True(loaded.AutoTurnScreenOff);
            Assert.False(loaded.ShowPerfHud);
            Assert.Equal(InputKey.Tab, loaded.DefaultAimToggleKey);
            Assert.InRange(loaded.GlobalMouseSensitivity, 1.74f, 1.76f);
            Assert.False(loaded.RememberWindowBounds);
            Assert.Equal(1440, loaded.WindowWidth);
            Assert.Equal(900, loaded.WindowHeight);

            AppConfig overridden = AppConfig.LoadWithArgs(tmpFile, ["--fps", "120", "--codec", "h264", "--bitrate", "24000000"]);
            Assert.Equal(120f, overridden.MaxFps);
            Assert.Equal("h264", overridden.VideoCodec);
            Assert.Equal(24_000_000, overridden.VideoBitRate);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [Fact]
    public void AppOverlayUi_RendersWaitingScreenPerfHudAndQuickSettingsWithoutErrors()
    {
        var stats = new LatencyStats();
        var config = new AppConfig { ShowPerfHud = true };

        using var window = new SdlWindowHost("PhonePlayLink-Phase4UiTest", 1280, 720, false, true, hidden: true);
        using var renderer = new D3D11VideoRenderer(window.Hwnd, window.Width, window.Height);
        using var imgui = new ImGuiD3D11Backend(renderer.Device, renderer.Context);

        var ui = new AppOverlayUi();
        ui.ToggleSettings(config);
        Assert.True(ui.IsSettingsOpen);

        var devices = new List<AdbDeviceInfo>
        {
            new("R5CX123", "device", "e3q", "Galaxy_S24_Ultra", "e3q", "1"),
            new("XYZ999", "unauthorized", null, "Pixel_8", null, "2")
        };

        imgui.BeginFrame(window.Width, window.Height, 1f / 60f);
        ui.DrawDeviceHubScreen(config, devices, isAdbConnected: true, connectingDeviceLabel: null, lastError: null, imgui.HeadingFont);
        ui.DrawRightSidebar(config, isFullscreen: false, isEditorOpen: false, isAimLocked: true, showHints: true, isPhoneScreenOn: false, isAudioMuted: false, hasAudio: true);
        ui.DrawPerfHud(stats, config, 2400, 1080, isPhoneScreenOn: false, activePointers: 2, isAimLocked: true, editorOpen: false);
        ui.DrawQuickSettingsWindow(config, Path.Combine(Path.GetTempPath(), "dummy_cfg.json"), devices, "R5CX123", isPhoneScreenOn: false, isConnected: true);
        ImGui.Render();
        renderer.RenderFrame(null, true, stats, imgui, ImGui.GetDrawData(), AppOverlayUi.SidebarWidthPx);
    }
}
