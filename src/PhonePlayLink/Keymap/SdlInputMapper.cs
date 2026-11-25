using ImGuiNET;
using Silk.NET.SDL;

namespace PhonePlayLink.Keymap;

/// <summary>
/// Ánh xạ sự kiện phím/chuột từ SDL2 sang InputKey và ImGuiKey.
/// </summary>
public static class SdlInputMapper
{
    public static InputKey FromSdlMouseButton(byte button) => button switch
    {
        1 => InputKey.MouseLeft,
        2 => InputKey.MouseMiddle,
        3 => InputKey.MouseRight,
        4 => InputKey.MouseX1,
        5 => InputKey.MouseX2,
        _ => InputKey.None
    };

    public static InputKey FromSdlKeyCode(KeyCode key) => key switch
    {
        KeyCode.KA => InputKey.A,
        KeyCode.KB => InputKey.B,
        KeyCode.KC => InputKey.C,
        KeyCode.KD => InputKey.D,
        KeyCode.KE => InputKey.E,
        KeyCode.KF => InputKey.F,
        KeyCode.KG => InputKey.G,
        KeyCode.KH => InputKey.H,
        KeyCode.KI => InputKey.I,
        KeyCode.KJ => InputKey.J,
        KeyCode.KK => InputKey.K,
        KeyCode.KL => InputKey.L,
        KeyCode.KM => InputKey.M,
        KeyCode.KN => InputKey.N,
        KeyCode.KO => InputKey.O,
        KeyCode.KP => InputKey.P,
        KeyCode.KQ => InputKey.Q,
        KeyCode.KR => InputKey.R,
        KeyCode.KS => InputKey.S,
        KeyCode.KT => InputKey.T,
        KeyCode.KU => InputKey.U,
        KeyCode.KV => InputKey.V,
        KeyCode.KW => InputKey.W,
        KeyCode.KX => InputKey.X,
        KeyCode.KY => InputKey.Y,
        KeyCode.KZ => InputKey.Z,
        KeyCode.K0 => InputKey.D0,
        KeyCode.K1 => InputKey.D1,
        KeyCode.K2 => InputKey.D2,
        KeyCode.K3 => InputKey.D3,
        KeyCode.K4 => InputKey.D4,
        KeyCode.K5 => InputKey.D5,
        KeyCode.K6 => InputKey.D6,
        KeyCode.K7 => InputKey.D7,
        KeyCode.K8 => InputKey.D8,
        KeyCode.K9 => InputKey.D9,
        KeyCode.KSpace => InputKey.Space,
        KeyCode.KReturn or KeyCode.KKPEnter => InputKey.Enter,
        KeyCode.KTab => InputKey.Tab,
        KeyCode.KEscape => InputKey.Escape,
        KeyCode.KBackspace => InputKey.Backspace,
        KeyCode.KLshift => InputKey.LeftShift,
        KeyCode.KRshift => InputKey.RightShift,
        KeyCode.KLctrl => InputKey.LeftCtrl,
        KeyCode.KRctrl => InputKey.RightCtrl,
        KeyCode.KLalt => InputKey.LeftAlt,
        KeyCode.KRalt => InputKey.RightAlt,
        KeyCode.KBackquote => InputKey.Grave,
        KeyCode.KCapslock => InputKey.CapsLock,
        KeyCode.KUp => InputKey.Up,
        KeyCode.KDown => InputKey.Down,
        KeyCode.KLeft => InputKey.Left,
        KeyCode.KRight => InputKey.Right,
        KeyCode.KF4 => InputKey.F4,
        KeyCode.KF5 => InputKey.F5,
        KeyCode.KF6 => InputKey.F6,
        KeyCode.KF7 => InputKey.F7,
        KeyCode.KF8 => InputKey.F8,
        KeyCode.KF9 => InputKey.F9,
        KeyCode.KF12 => InputKey.F12,
        _ => InputKey.None
    };

    public static ImGuiKey ToImGuiKey(KeyCode key) => key switch
    {
        KeyCode.KTab => ImGuiKey.Tab,
        KeyCode.KLeft => ImGuiKey.LeftArrow,
        KeyCode.KRight => ImGuiKey.RightArrow,
        KeyCode.KUp => ImGuiKey.UpArrow,
        KeyCode.KDown => ImGuiKey.DownArrow,
        KeyCode.KPageup => ImGuiKey.PageUp,
        KeyCode.KPagedown => ImGuiKey.PageDown,
        KeyCode.KHome => ImGuiKey.Home,
        KeyCode.KEnd => ImGuiKey.End,
        KeyCode.KInsert => ImGuiKey.Insert,
        KeyCode.KDelete => ImGuiKey.Delete,
        KeyCode.KBackspace => ImGuiKey.Backspace,
        KeyCode.KSpace => ImGuiKey.Space,
        KeyCode.KReturn or KeyCode.KKPEnter => ImGuiKey.Enter,
        KeyCode.KEscape => ImGuiKey.Escape,
        KeyCode.KBackquote => ImGuiKey.GraveAccent,
        KeyCode.KMinus => ImGuiKey.Minus,
        KeyCode.KEquals => ImGuiKey.Equal,
        KeyCode.KLctrl => ImGuiKey.LeftCtrl,
        KeyCode.KRctrl => ImGuiKey.RightCtrl,
        KeyCode.KLshift => ImGuiKey.LeftShift,
        KeyCode.KRshift => ImGuiKey.RightShift,
        KeyCode.KLalt => ImGuiKey.LeftAlt,
        KeyCode.KRalt => ImGuiKey.RightAlt,
        >= KeyCode.KA and <= KeyCode.KZ => ImGuiKey.A + (key - KeyCode.KA),
        >= KeyCode.K0 and <= KeyCode.K9 => ImGuiKey._0 + (key - KeyCode.K0),
        >= KeyCode.KF1 and <= KeyCode.KF12 => ImGuiKey.F1 + (key - KeyCode.KF1),
        _ => ImGuiKey.None
    };
}
