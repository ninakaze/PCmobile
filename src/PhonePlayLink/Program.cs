using System.Diagnostics;
using ImGuiNET;
using PhonePlayLink.Adb;
using PhonePlayLink.Core;
using PhonePlayLink.Keymap;
using PhonePlayLink.Media;
using PhonePlayLink.Rendering;
using PhonePlayLink.UI;

namespace PhonePlayLink;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string configPath = AppConfig.ResolveDefaultConfigPath();
        AppConfig config = AppConfig.LoadWithArgs(configPath, args);
        if (config.ShowHelp)
        {
            AppConfig.PrintHelp();
            return 0;
        }

        using var highResTimer = new NativeTiming.HighResolutionTimerScope();
        using var mmcssMain = new NativeTiming.MmcssThreadScope("Games");

        try
        {
            AssetPaths assets = AssetPaths.Resolve();
            assets.ValidateAll();

            FfmpegInitializer.Initialize(assets.FfmpegDirectory);

            var adb = new AdbClient(assets.AdbExecutablePath);
            await adb.EnsureServerRunningAsync();

            if (config.ListDevicesOnly)
            {
                var devices = await adb.ListDevicesAsync();
                PrintDevices(devices);
                return 0;
            }

            var stats = new LatencyStats();
            using var windowHost = new SdlWindowHost(
                "PhonePlay Link - Device Hub",
                1280,
                720,
                config.StartFullscreen,
                config.PreserveAspectRatio,
                hidden: false,
                initialX: config.RememberWindowBounds ? config.WindowX : null,
                initialY: config.RememberWindowBounds ? config.WindowY : null,
                overrideWidth: config.RememberWindowBounds ? config.WindowWidth : null,
                overrideHeight: config.RememberWindowBounds ? config.WindowHeight : null);

            using var renderer = new D3D11VideoRenderer(windowHost.Hwnd, windowHost.Width, windowHost.Height);
            using var imguiBackend = new ImGuiD3D11Backend(renderer.Device, renderer.Context);
            using var frameSlot = new LatestFrameSlot();
            var profileManager = new ProfileManager(ProfileManager.ResolveDefaultDirectory());

            await using var usbMonitor = new UsbDeviceMonitor(adb);
            var appUi = new AppOverlayUi();
            appUi.SyncDraftFromConfig(config);

            ActiveStreamingSession? activeSession = null;
            Task<ActiveStreamingSession>? connectTask = null;
            string? connectingDeviceLabel = null;
            string? lastConnectError = null;
            string? manualConnectSerial = null;
            IReadOnlyList<AdbDeviceInfo>? manualRefreshedDevices = null;
            bool reconnectRequested = false;
            bool disconnectToHubRequested = false;
            bool autoConnectSuppressedUntilPlug = false;

            usbMonitor.DevicesChanged += list =>
            {
                manualRefreshedDevices = null;
                if (list.Count == 0)
                    autoConnectSuppressedUntilPlug = false;
                frameSlot.SignalWakeup();
            };

            windowHost.TogglePerfHudRequested += () =>
            {
                config.ShowPerfHud = !config.ShowPerfHud;
                config.SaveToFile(configPath);
            };
            windowHost.ToggleSettingsRequested += () =>
            {
                appUi.ToggleSettings(config);
                windowHost.SyncTextInputMode(appUi.IsSettingsOpen || activeSession?.EditorOverlay.IsEditorOpen == true);
            };
            windowHost.TogglePhoneScreenRequested += () => activeSession?.TogglePhoneScreenPower();

            // Kết nối toàn bộ nút điều khiển từ Device Hub, Right Sidebar & Cài đặt
            appUi.ConnectDeviceRequested += serial =>
            {
                autoConnectSuppressedUntilPlug = false;
                manualConnectSerial = serial;
            };
            appUi.RefreshDevicesRequested += () =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        manualRefreshedDevices = await adb.ListDevicesAsync();
                        frameSlot.SignalWakeup();
                    }
                    catch { }
                });
            };
            appUi.ApplyAndReconnectRequested += () => reconnectRequested = true;
            appUi.ControlSettingsSaved += () =>
            {
                if (activeSession is not null)
                {
                    activeSession.KeymapEngine.GlobalMouseSensitivity = config.GlobalMouseSensitivity;
                    activeSession.KeymapEngine.OverrideAimToggleKey = config.DefaultAimToggleKey;
                }
            };
            appUi.ToggleKeymapEditorRequested += () =>
            {
                if (activeSession is null) return;
                activeSession.EditorOverlay.ToggleEditor();
                windowHost.SetRelativeMouseLock(false);
                windowHost.SyncTextInputMode(activeSession.EditorOverlay.IsEditorOpen || appUi.IsSettingsOpen);
            };
            appUi.ToggleAimLockRequested += () =>
            {
                if (activeSession is null || activeSession.EditorOverlay.IsEditorOpen) return;
                activeSession.KeymapEngine.SetAimLocked(!activeSession.KeymapEngine.IsAimLocked);
            };
            appUi.ToggleHintsRequested += () => activeSession?.EditorOverlay.ToggleHints();
            appUi.ToggleFullscreenRequested += () =>
            {
                windowHost.ToggleFullscreen();
                renderer.Resize(windowHost.Width, windowHost.Height);
            };
            appUi.ToggleAspectRatioRequested += () =>
            {
                windowHost.PreserveAspectRatio = !windowHost.PreserveAspectRatio;
                config.PreserveAspectRatio = windowHost.PreserveAspectRatio;
                config.SaveToFile(configPath);
            };
            appUi.TogglePhoneScreenRequested += () => activeSession?.TogglePhoneScreenPower();
            appUi.ToggleAudioMuteRequested += () => activeSession?.ToggleAudioMute();
            appUi.TogglePerfHudRequested += () =>
            {
                config.ShowPerfHud = !config.ShowPerfHud;
                config.SaveToFile(configPath);
            };
            appUi.DisconnectDeviceRequested += () =>
            {
                autoConnectSuppressedUntilPlug = true;
                disconnectToHubRequested = true;
            };
            appUi.SendAndroidNavKeyRequested += keycode => activeSession?.SendAndroidNavKey(keycode);

            using var appCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                appCts.Cancel();
            };

            long lastFrameTs = Stopwatch.GetTimestamp();
            long lastTitleUpdate = lastFrameTs;

            while (!appCts.IsCancellationRequested)
            {
                long nowTs = Stopwatch.GetTimestamp();
                float dt = (float)Stopwatch.GetElapsedTime(lastFrameTs, nowTs).TotalSeconds;
                lastFrameTs = nowTs;

                IReadOnlyList<AdbDeviceInfo> currentDevices = manualRefreshedDevices ?? usbMonitor.CurrentDevices;

                // 1. Hoàn tất kết nối bất đồng bộ
                if (connectTask is not null && connectTask.IsCompleted)
                {
                    try
                    {
                        activeSession = await connectTask;
                        lastConnectError = null;
                    }
                    catch (Exception ex)
                    {
                        lastConnectError = ex.Message;
                        Logger.Error("Connect", ex.Message);
                    }
                    connectTask = null;
                    connectingDeviceLabel = null;
                }

                // 2. Xử lý ngắt kết nối (rút cáp, nhấn Ngắt kết nối về Device Hub, hoặc Áp dụng & Kết nối lại)
                if (activeSession is not null)
                {
                    bool deviceMissingFromUsb = currentDevices.Count > 0 &&
                        !currentDevices.Any(d => string.Equals(d.Serial, activeSession.Device.Serial, StringComparison.OrdinalIgnoreCase) && d.IsOnline);
                    bool allDevicesUnplugged = usbMonitor.IsAdbConnected && currentDevices.Count == 0;

                    if (activeSession.IsDisconnected || reconnectRequested || disconnectToHubRequested || deviceMissingFromUsb || allDevicesUnplugged)
                    {
                        Logger.Info("Session", reconnectRequested ? "Đang áp dụng cấu hình và kết nối lại..." : "Đóng phiên stream và quay về Device Hub.");
                        await activeSession.DisposeAsync();
                        activeSession = null;
                        renderer.ResetVideoSurface();
                        windowHost.SetRelativeMouseLock(false);
                        windowHost.SyncTextInputMode(appUi.IsSettingsOpen);
                        reconnectRequested = false;
                        disconnectToHubRequested = false;
                        lastTitleUpdate = 0;
                    }
                }
                else
                {
                    reconnectRequested = false;
                    disconnectToHubRequested = false;
                }

                // 3. Tự động kết nối Plug & Play hoặc kết nối theo lựa chọn ở Device Hub
                if (activeSession is null && connectTask is null)
                {
                    AdbDeviceInfo? candidate = null;
                    if (!string.IsNullOrWhiteSpace(manualConnectSerial))
                    {
                        candidate = currentDevices.FirstOrDefault(d => d.IsOnline && string.Equals(d.Serial, manualConnectSerial, StringComparison.OrdinalIgnoreCase));
                        manualConnectSerial = null;
                    }
                    else if (!autoConnectSuppressedUntilPlug)
                    {
                        candidate = SelectPlugAndPlayCandidate(currentDevices, config.DeviceSerial);
                    }

                    if (candidate is not null)
                    {
                        connectingDeviceLabel = candidate.DisplayName;
                        lastConnectError = null;
                        connectTask = ActiveStreamingSession.ConnectAsync(
                            adb,
                            candidate,
                            assets.ScrcpyServerPath,
                            config,
                            windowHost,
                            renderer,
                            frameSlot,
                            profileManager,
                            stats,
                            appCts.Token);
                    }
                }

                float reservedRightPx = appUi.GetSidebarReservedWidth(activeSession is not null, config, windowHost.IsFullscreen);

                // 4. Xử lý sự kiện SDL2 (tách biệt Input UI & Game)
                bool forceBlockGame = activeSession is null || appUi.IsSettingsOpen || activeSession.EditorOverlay.IsEditorOpen;
                if (!windowHost.PollEvents(
                    renderer,
                    activeSession?.KeymapEngine,
                    activeSession?.EditorOverlay,
                    appUi,
                    forceBlockGame,
                    reservedRightPx,
                    out bool hadEvents))
                {
                    appCts.Cancel();
                    break;
                }

                if (activeSession is not null)
                {
                    activeSession.ProcessMainThreadUpdates(windowHost, renderer, stats);
                    if (!forceBlockGame)
                    {
                        activeSession.KeymapEngine.Update();
                    }
                }

                // 5. Lấy frame mới nhất (Zero-Buffer) & Render D3D11 + ImGui UI
                using DecodedVideoFrame? latestFrame = frameSlot.TakeLatest();

                bool shouldRenderNow =
                    latestFrame is not null ||
                    hadEvents ||
                    appUi.IsSettingsOpen ||
                    activeSession?.EditorOverlay.IsEditorOpen == true ||
                    activeSession is null;

                if (shouldRenderNow)
                {
                    imguiBackend.BeginFrame(windowHost.Width, windowHost.Height, dt);
                    int vidW = renderer.VideoWidth > 0 ? renderer.VideoWidth : 1280;
                    int vidH = renderer.VideoHeight > 0 ? renderer.VideoHeight : 720;

                    if (activeSession is not null)
                    {
                        activeSession.EditorOverlay.BuildUi(
                            windowHost.Width,
                            windowHost.Height,
                            vidW,
                            vidH,
                            windowHost.PreserveAspectRatio,
                            dt,
                            reservedRightPx);

                        appUi.DrawRightSidebar(
                            config,
                            windowHost.IsFullscreen,
                            activeSession.EditorOverlay.IsEditorOpen,
                            activeSession.KeymapEngine.IsAimLocked,
                            activeSession.KeymapEngine.Profile.ShowHints,
                            activeSession.ControlClient.IsScreenOn,
                            activeSession.IsAudioMuted,
                            activeSession.HasAudio);

                        appUi.DrawPerfHud(
                            stats,
                            config,
                            vidW,
                            vidH,
                            activeSession.ControlClient.IsScreenOn,
                            activeSession.PointerManager.ActivePointerCount,
                            activeSession.KeymapEngine.IsAimLocked,
                            activeSession.EditorOverlay.IsEditorOpen);
                    }
                    else
                    {
                        appUi.DrawDeviceHubScreen(
                            config,
                            currentDevices,
                            usbMonitor.IsAdbConnected,
                            connectingDeviceLabel,
                            lastConnectError,
                            imguiBackend.HeadingFont);
                    }

                    appUi.DrawQuickSettingsWindow(
                        config,
                        configPath,
                        currentDevices,
                        activeSession?.Device.Serial,
                        activeSession?.ControlClient.IsScreenOn ?? true,
                        activeSession is not null);

                    ImGui.Render();
                    renderer.RenderFrame(latestFrame, windowHost.PreserveAspectRatio, stats, imguiBackend, ImGui.GetDrawData(), reservedRightPx);
                }

                // 6. Cập nhật tiêu đề cửa sổ định kỳ 500ms
                if (Stopwatch.GetElapsedTime(lastTitleUpdate).TotalMilliseconds >= 500)
                {
                    lastTitleUpdate = nowTs;
                    if (activeSession is not null)
                    {
                        string mode = windowHost.IsFullscreen ? "Fullscreen" : "Windowed";
                        string aspect = windowHost.PreserveAspectRatio ? "Fit" : "Stretch";
                        string scr = activeSession.ControlClient.IsScreenOn ? "" : " | Screen:OFF";
                        windowHost.SetTitle(
                            $"PhonePlay Link - {activeSession.DeviceName} [{activeSession.KeymapEngine.Profile.Name}] | {stats.CurrentFps:F1} FPS | Dec: {stats.DecodeLatencyMs:F2}ms | Touch:{activeSession.PointerManager.ActivePointerCount}/10{scr} | [{stats.DecoderBackend}] [{mode}/{aspect}]");
                    }
                    else
                    {
                        windowHost.SetTitle($"PhonePlay Link - Device Hub ({currentDevices.Count} thiết bị)");
                    }
                }

                // 7. Event-Driven Pacing
                if (latestFrame is null && !hadEvents)
                {
                    if (activeSession is not null && activeSession.KeymapEngine.HasActiveInterpolations)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        NativeTiming.WaitForFrameOrWindowInput(
                            frameSlot.FrameAvailableHandle,
                            activeSession is not null ? 16 : 33);
                    }
                }
            }

            if (config.RememberWindowBounds && !windowHost.IsFullscreen)
            {
                windowHost.GetWindowBounds(out int wx, out int wy, out int ww, out int wh);
                if (ww > 320 && wh > 240)
                {
                    config.WindowX = wx;
                    config.WindowY = wy;
                    config.WindowWidth = ww;
                    config.WindowHeight = wh;
                    config.SaveToFile(configPath);
                }
            }

            if (activeSession is not null)
            {
                await activeSession.DisposeAsync();
            }
            windowHost.SetRelativeMouseLock(false);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal", ex.Message);
            return 1;
        }
    }

    private static AdbDeviceInfo? SelectPlugAndPlayCandidate(IReadOnlyList<AdbDeviceInfo> devices, string? preferredSerial)
    {
        var online = devices.Where(d => d.IsOnline).ToList();
        if (online.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredSerial))
        {
            return online.FirstOrDefault(d => string.Equals(d.Serial, preferredSerial, StringComparison.OrdinalIgnoreCase));
        }

        return online.Count == 1 ? online[0] : null;
    }

    private static void PrintDevices(IReadOnlyList<AdbDeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            Console.WriteLine("Không có thiết bị Android nào được kết nối qua ADB.");
            return;
        }

        Console.WriteLine($"Tìm thấy {devices.Count} thiết bị:");
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            Console.WriteLine($"  [{i + 1}] {d.DisplayName} | State={d.State} | {(d.IsUsb ? "USB" : "TCP")}");
        }
    }
}
