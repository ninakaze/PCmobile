# PhonePlay Link

Ứng dụng Windows Desktop (.NET 8 + SDL2 + Direct3D 11 + FFmpeg + ImGui.NET) cho phép cắm cáp USB kết nối điện thoại Android với PC để chơi game mobile bằng chuột và bàn phím với độ trễ siêu thấp, chạy trực tiếp trên phần cứng điện thoại (không giả lập).

## Kiến trúc thư mục

```text
D:\PCmobile\
├── PhonePlayLink.sln
├── tools\
│   ├── setup-deps.ps1              # Tự động kiểm tra & tải adb.exe, scrcpy-server (v5.0.1), FFmpeg 9.0 DLLs, SDL2.dll
│   └── publish-release.ps1         # Đóng gói bản Release ra dist\PhonePlayLink\
├── assets\                         # Chứa binary ngoài (tự sinh bởi setup-deps.ps1)
├── profiles\                       # Hồ sơ Keymap JSON (fps_default.json, moba_default.json...)
├── dist\PhonePlayLink\             # Thư mục phát hành sau khi chạy publish-release.ps1
├── src\PhonePlayLink\              # Ứng dụng chính (.NET 8, x64)
│   ├── Adb\                        # AdbClient, AdbDeviceInfo, UsbDeviceMonitor (host:track-devices Plug & Play)
│   ├── Core\                       # AppConfig (config.json), NativeTiming (timeBeginPeriod/MMCSS/MsgWait), LatencyStats, ActiveStreamingSession
│   ├── Input\                      # ViewportMapper, MultiTouchPointerManager (10 pointerId), WasdJoystickController
│   ├── Keymap\                     # KeymapProfile, ProfileManager, KeymapEngine (Tap/WASD/FPS Aim/MOBA Skill), PackageWatcher
│   ├── Media\                      # FfmpegInitializer, LatestFrameSlot (Zero-Buffer + Event-Driven), VideoDecoder (D3D11VA/DXVA2), AudioDecoder, AudioPlayer
│   ├── Rendering\                  # SdlWindowHost, D3D11VideoRenderer (FlipDiscard ALLOW_TEARING), ImGuiD3D11Backend
│   └── UI\                         # KeymapEditorOverlay (F1/F3), AppOverlayUi (HUD F4, Settings F9, Plug & Play Waiting UI)
└── tests\PhonePlayLink.Tests\        # Bộ kiểm thử xUnit (Giai đoạn 1-4)
```

## Phím tắt nhanh
- `F1`: Mở / Đóng Visual Keymap Editor (kéo thả trực quan, gán phím, chỉnh Opacity, gắn theo game đang mở)
- `F3`: Ẩn / Hiện gợi ý phím khi đang chơi
- `F4`: Ẩn / Hiện Performance & Latency HUD (Render/Stream FPS, Decode/Render/AudioQ ms, Drop, Backend)
- `F9`: Mở / Đóng Cài đặt nhanh (Codec, FPS, Bitrate, Max Size, Audio, chọn thiết bị USB)
- `Alt+O`: Tắt / Bật màn hình điện thoại tức thì chống nóng & tiết kiệm pin
- `~` hoặc `Right Ctrl`: Khóa / Mở khóa con trỏ chuột (FPS Aim Look 360°)
- `F11` hoặc `Alt+Enter`: Bật / Tắt Borderless Fullscreen
- `F10`: Chuyển đổi Giữ tỷ lệ khung hình / Kéo đầy màn hình

## Đóng gói bản Release & Chạy

```powershell
# 1. Tải dependencies (chỉ cần chạy lần đầu)
powershell -ExecutionPolicy Bypass -File .\tools\setup-deps.ps1

# 2. Đóng gói bản Release ra dist\PhonePlayLink\
powershell -ExecutionPolicy Bypass -File .\tools\publish-release.ps1

# 3. Chạy trực tiếp
.\dist\PhonePlayLink\PhonePlayLink.exe
```
