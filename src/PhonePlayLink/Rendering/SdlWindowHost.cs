using System.Runtime.InteropServices;
using ImGuiNET;
using PhonePlayLink.Core;
using PhonePlayLink.Input;
using PhonePlayLink.Keymap;
using PhonePlayLink.UI;
using Silk.NET.SDL;
using SdlVersion = Silk.NET.SDL.Version;

namespace PhonePlayLink.Rendering;

/// <summary>
/// Quản lý cửa sổ SDL2, chuyển tiếp sự kiện cho ImGui.NET + KeymapEngine,
/// tách biệt chặt chẽ Input UI (WantCaptureMouse/WantCaptureKeyboard) và Game,
/// hỗ trợ Raw Mouse Relative Mode (FPS Aim 360°), Borderless Fullscreen và lưu vị trí cửa sổ.
/// </summary>
public sealed unsafe class SdlWindowHost : IDisposable
{
    public Sdl SdlApi { get; }
    public Window* Window { get; }
    public IntPtr Hwnd { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsFullscreen { get; private set; }
    public bool PreserveAspectRatio { get; set; }

    public event Action? TogglePerfHudRequested;
    public event Action? ToggleSettingsRequested;
    public event Action? TogglePhoneScreenRequested;

    public SdlWindowHost(
        string title,
        int videoWidth,
        int videoHeight,
        bool startFullscreen,
        bool preserveAspectRatio,
        bool hidden = false,
        int? initialX = null,
        int? initialY = null,
        int? overrideWidth = null,
        int? overrideHeight = null)
    {
        PreserveAspectRatio = preserveAspectRatio;
        SdlApi = Sdl.GetApi();

        SdlApi.SetHint("SDL_WINDOWS_DPI_AWARENESS", "permonitorv2");
        SdlApi.SetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", "0");

        if (SdlApi.Init(Sdl.InitVideo | Sdl.InitAudio | Sdl.InitEvents) < 0)
        {
            throw new InvalidOperationException($"SDL_Init thất bại: {SdlApi.GetErrorS()}");
        }

        if (overrideWidth is > 320 && overrideHeight is > 240)
        {
            Width = overrideWidth.Value;
            Height = overrideHeight.Value;
        }
        else
        {
            (Width, Height) = ComputeInitialWindowSize(videoWidth, videoHeight);
        }

        int posX = initialX ?? Sdl.WindowposCentered;
        int posY = initialY ?? Sdl.WindowposCentered;

        uint flags = (uint)((hidden ? WindowFlags.Hidden : WindowFlags.Shown) | WindowFlags.Resizable | WindowFlags.AllowHighdpi);
        if (startFullscreen && !hidden)
        {
            flags |= (uint)WindowFlags.FullscreenDesktop;
            IsFullscreen = true;
        }

        Window = SdlApi.CreateWindow(
            title,
            posX,
            posY,
            Width,
            Height,
            flags);

        if (Window == null)
        {
            throw new InvalidOperationException($"SDL_CreateWindow thất bại: {SdlApi.GetErrorS()}");
        }

        int actualW = 0, actualH = 0;
        SdlApi.GetWindowSize(Window, &actualW, &actualH);
        Width = actualW;
        Height = actualH;

        Hwnd = ExtractHwnd(SdlApi, Window);
        Logger.Ok("SDL2", $"Đã tạo cửa sổ {Width}x{Height} (HWND=0x{Hwnd:X}, Fullscreen={IsFullscreen})");
    }

    public void SetTitle(string title) => SdlApi.SetWindowTitle(Window, title);

    public void SetRelativeMouseLock(bool locked)
    {
        SdlApi.SetRelativeMouseMode(locked ? SdlBool.True : SdlBool.False);
    }

    public void GetWindowBounds(out int x, out int y, out int w, out int h)
    {
        int px = 0, py = 0, pw = 0, ph = 0;
        SdlApi.GetWindowPosition(Window, &px, &py);
        SdlApi.GetWindowSize(Window, &pw, &ph);
        x = px; y = py; w = pw; h = ph;
    }

    public void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        uint mode = IsFullscreen ? (uint)WindowFlags.FullscreenDesktop : 0u;
        SdlApi.SetWindowFullscreen(Window, mode);
        int w = 0, h = 0;
        SdlApi.GetWindowSize(Window, &w, &h);
        Width = w;
        Height = h;
    }

    public void AdaptSizeForVideoOrientation(int newVideoW, int newVideoH, float reservedRightPx = 0f)
    {
        if (IsFullscreen || newVideoW <= 0 || newVideoH <= 0)
            return;

        (int targetW, int targetH) = ComputeInitialWindowSize(newVideoW, newVideoH);
        targetW += (int)MathF.Round(reservedRightPx);
        SdlApi.SetWindowSize(Window, targetW, targetH);
        Width = targetW;
        Height = targetH;
    }

    public bool PollEvents(
        D3D11VideoRenderer renderer,
        KeymapEngine? keymapEngine = null,
        KeymapEditorOverlay? editorOverlay = null,
        AppOverlayUi? appUi = null,
        bool forceBlockGame = false,
        float reservedRightPx = 0f)
    {
        return PollEvents(renderer, keymapEngine, editorOverlay, appUi, forceBlockGame, reservedRightPx, out _);
    }

    public bool PollEvents(
        D3D11VideoRenderer renderer,
        KeymapEngine? keymapEngine,
        KeymapEditorOverlay? editorOverlay,
        AppOverlayUi? appUi,
        bool forceBlockGame,
        float reservedRightPx,
        out bool hadEvents)
    {
        hadEvents = false;
        ImGuiIOPtr io = ImGui.GetCurrentContext() != IntPtr.Zero ? ImGui.GetIO() : default;
        bool hasImGui = io.NativePtr != null;
        bool editorOpen = editorOverlay?.IsEditorOpen == true;
        bool isAimLocked = keymapEngine?.IsAimLocked == true;

        Event ev;
        while (SdlApi.PollEvent(&ev) != 0)
        {
            hadEvents = true;
            switch ((EventType)ev.Type)
            {
                case EventType.Quit:
                    keymapEngine?.ReleaseAllInputs();
                    SetRelativeMouseLock(false);
                    return false;

                case EventType.Windowevent:
                    var winEv = (WindowEventID)ev.Window.Event;
                    if (winEv is WindowEventID.Resized or WindowEventID.SizeChanged)
                    {
                        Width = ev.Window.Data1;
                        Height = ev.Window.Data2;
                        renderer.Resize(Width, Height);
                    }
                    else if (winEv is WindowEventID.FocusGained)
                    {
                        if (hasImGui) io.AddFocusEvent(true);
                    }
                    else if (winEv is WindowEventID.FocusLost or WindowEventID.Minimized or WindowEventID.Hidden)
                    {
                        if (hasImGui) io.AddFocusEvent(false);
                        keymapEngine?.ReleaseAllInputs();
                        SetRelativeMouseLock(false);
                    }
                    break;

                case EventType.Textinput:
                    if (hasImGui && !isAimLocked)
                    {
                        string? txt = Marshal.PtrToStringUTF8((IntPtr)ev.Text.Text);
                        if (!string.IsNullOrEmpty(txt))
                        {
                            io.AddInputCharactersUTF8(txt);
                        }
                    }
                    break;

                case EventType.Mousewheel:
                    if (hasImGui && !isAimLocked)
                    {
                        io.AddMouseWheelEvent(ev.Wheel.PreciseX, ev.Wheel.PreciseY);
                    }
                    break;

                case EventType.Mousemotion:
                    if (hasImGui && !isAimLocked)
                    {
                        io.AddMousePosEvent(ev.Motion.X, ev.Motion.Y);
                    }

                    bool mouseMotionBlocked = forceBlockGame || editorOpen || (!isAimLocked && hasImGui && io.WantCaptureMouse);
                    if (!mouseMotionBlocked && keymapEngine is not null)
                    {
                        float? nx = null, ny = null;
                        if (!isAimLocked && ViewportMapper.TryWindowToNormalized(
                            ev.Motion.X,
                            ev.Motion.Y,
                            Width,
                            Height,
                            renderer.VideoWidth,
                            renderer.VideoHeight,
                            PreserveAspectRatio,
                            out float mappedX,
                            out float mappedY,
                            clampOutsideToEdge: true,
                            reservedRightPx: reservedRightPx))
                        {
                            nx = mappedX;
                            ny = mappedY;
                        }

                        keymapEngine.HandleMouseMove(nx, ny, ev.Motion.Xrel, ev.Motion.Yrel);
                    }
                    break;

                case EventType.Mousebuttondown:
                case EventType.Mousebuttonup:
                {
                    bool isDown = (EventType)ev.Type == EventType.Mousebuttondown;
                    if (hasImGui && !isAimLocked)
                    {
                        int imBtn = ev.Button.Button switch { 1 => 0, 3 => 1, 2 => 2, _ => -1 };
                        if (imBtn >= 0) io.AddMouseButtonEvent(imBtn, isDown);
                    }

                    InputKey mouseKey = SdlInputMapper.FromSdlMouseButton(ev.Button.Button);
                    if (isDown && editorOpen && editorOverlay is not null && editorOverlay.IsListeningForKey)
                    {
                        editorOverlay.TryCaptureKeyForEditor(mouseKey);
                        break;
                    }
                    if (isDown && appUi is not null && appUi.IsListeningForSettingsKey)
                    {
                        appUi.TryCaptureSettingsKey(mouseKey);
                        break;
                    }

                    // Nếu UI (Sidebar / Cài đặt / Editor / Device Hub) đang bắt chuột và không trong chế độ Aim Lock => tuyệt đối không chạm xuống điện thoại
                    bool mouseBtnBlocked = forceBlockGame || editorOpen || (!isAimLocked && hasImGui && io.WantCaptureMouse);
                    if (!mouseBtnBlocked && keymapEngine is not null && mouseKey != InputKey.None)
                    {
                        float? nx = null, ny = null;
                        if (!isAimLocked && ViewportMapper.TryWindowToNormalized(
                            ev.Button.X,
                            ev.Button.Y,
                            Width,
                            Height,
                            renderer.VideoWidth,
                            renderer.VideoHeight,
                            PreserveAspectRatio,
                            out float mappedX,
                            out float mappedY,
                            clampOutsideToEdge: !isDown,
                            reservedRightPx: reservedRightPx))
                        {
                            nx = mappedX;
                            ny = mappedY;
                        }

                        keymapEngine.HandleMouseButton(mouseKey, isDown, nx, ny);
                    }
                    break;
                }

                case EventType.Keydown:
                case EventType.Keyup:
                {
                    bool isDown = (EventType)ev.Type == EventType.Keydown;
                    bool isRepeat = ev.Key.Repeat != 0;
                    var key = (KeyCode)ev.Key.Keysym.Sym;
                    var mod = (Keymod)ev.Key.Keysym.Mod;

                    if (hasImGui && !isAimLocked)
                    {
                        ImGuiKey igKey = SdlInputMapper.ToImGuiKey(key);
                        if (igKey != ImGuiKey.None)
                        {
                            io.AddKeyEvent(igKey, isDown);
                        }
                        io.AddKeyEvent(ImGuiKey.ModCtrl, (mod & (Keymod.Lctrl | Keymod.Rctrl)) != 0);
                        io.AddKeyEvent(ImGuiKey.ModShift, (mod & (Keymod.Lshift | Keymod.Rshift)) != 0);
                        io.AddKeyEvent(ImGuiKey.ModAlt, (mod & (Keymod.Lalt | Keymod.Ralt)) != 0);
                    }

                    InputKey inputKey = SdlInputMapper.FromSdlKeyCode(key);
                    if (isDown && !isRepeat && appUi is not null && appUi.IsListeningForSettingsKey)
                    {
                        appUi.TryCaptureSettingsKey(inputKey);
                        break;
                    }
                    if (isDown && !isRepeat && editorOpen && editorOverlay is not null && editorOverlay.IsListeningForKey)
                    {
                        editorOverlay.TryCaptureKeyForEditor(inputKey);
                        break;
                    }

                    // Phím tắt hệ thống
                    if (isDown && !isRepeat)
                    {
                        bool altPressed = (mod & (Keymod.Lalt | Keymod.Ralt)) != 0;
                        if (key == KeyCode.KF11 || (altPressed && key is KeyCode.KReturn or KeyCode.KKPEnter))
                        {
                            ToggleFullscreen();
                            renderer.Resize(Width, Height);
                            break;
                        }
                        if (key == KeyCode.KF10)
                        {
                            PreserveAspectRatio = !PreserveAspectRatio;
                            Logger.Info("Window", $"Tỷ lệ khung hình: {(PreserveAspectRatio ? "Giữ nguyên tỷ lệ gốc" : "Kéo đầy màn hình")}");
                            break;
                        }
                        if (key == KeyCode.KF4)
                        {
                            TogglePerfHudRequested?.Invoke();
                            break;
                        }
                        if (key == KeyCode.KF9)
                        {
                            keymapEngine?.ReleaseAllInputs();
                            SetRelativeMouseLock(false);
                            ToggleSettingsRequested?.Invoke();
                            break;
                        }
                        if (altPressed && key == KeyCode.KO)
                        {
                            TogglePhoneScreenRequested?.Invoke();
                            break;
                        }
                        if (key == KeyCode.KF1 && editorOverlay is not null)
                        {
                            editorOverlay.ToggleEditor();
                            SetRelativeMouseLock(false);
                            SyncTextInputMode(editorOverlay.IsEditorOpen || appUi?.IsSettingsOpen == true);
                            break;
                        }
                        if (key == KeyCode.KF3 && editorOverlay is not null)
                        {
                            editorOverlay.ToggleHints();
                            break;
                        }
                    }

                    bool kbBlockedByUi = forceBlockGame || editorOpen || (!isAimLocked && hasImGui && io.WantCaptureKeyboard);
                    if (!kbBlockedByUi && keymapEngine is not null && inputKey != InputKey.None)
                    {
                        keymapEngine.HandleKeyInput(inputKey, isDown, isRepeat);
                    }
                    break;
                }
            }
        }

        return true;
    }

    public void SyncTextInputMode(bool needsTextInput)
    {
        if (needsTextInput) SdlApi.StartTextInput();
        else SdlApi.StopTextInput();
    }

    private (int W, int H) ComputeInitialWindowSize(int videoWidth, int videoHeight)
    {
        int w = Math.Max(360, videoWidth);
        int h = Math.Max(360, videoHeight);

        DisplayMode dm;
        int maxW = 1600;
        int maxH = 900;
        if (SdlApi.GetCurrentDisplayMode(0, &dm) == 0 && dm.W > 0 && dm.H > 0)
        {
            maxW = (int)(dm.W * 0.85f);
            maxH = (int)(dm.H * 0.85f);
        }

        float scale = Math.Min(1f, Math.Min((float)maxW / w, (float)maxH / h));
        return ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
    }

    private static IntPtr ExtractHwnd(Sdl sdl, Window* window)
    {
        SysWMInfo info = default;
        SdlVersion ver = default;
        sdl.GetVersion(&ver);
        info.Version = ver;

        if (!sdl.GetWindowWMInfo(window, &info))
        {
            throw new InvalidOperationException($"SDL_GetWindowWMInfo thất bại: {sdl.GetErrorS()}");
        }

        if (info.Info.Win.Hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Không lấy được Windows HWND từ cửa sổ SDL2.");
        }

        return info.Info.Win.Hwnd;
    }

    public void Dispose()
    {
        SetRelativeMouseLock(false);
        if (Window != null)
        {
            SdlApi.DestroyWindow(Window);
        }
        SdlApi.Quit();
    }
}
