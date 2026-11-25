using System.Text.Json.Serialization;

namespace PhonePlayLink.Keymap;

/// <summary>
/// Mã phím và nút chuột chuẩn hóa cho hệ thống Keymap (độc lập với SDL2 để serialize JSON sạch và dễ unit test).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputKey
{
    None = 0,

    // Chuột
    MouseLeft,
    MouseRight,
    MouseMiddle,
    MouseX1,
    MouseX2,

    // Chữ cái
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // Số hàng trên
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    // Điều khiển & Bổ trợ
    Space,
    Enter,
    Tab,
    Escape,
    Backspace,
    LeftShift,
    RightShift,
    LeftCtrl,
    RightCtrl,
    LeftAlt,
    RightAlt,
    Grave, // Phím ~ / `
    CapsLock,

    // Mũi tên
    Up,
    Down,
    Left,
    Right,

    // F-keys (chú ý F1/F2/F3/F10/F11 dùng cho hệ thống, vẫn có thể map F4..F9/F12)
    F4, F5, F6, F7, F8, F9, F12
}

public static class InputKeyExtensions
{
    public static string ToShortLabel(this InputKey key) => key switch
    {
        InputKey.None => "?",
        InputKey.MouseLeft => "LMB",
        InputKey.MouseRight => "RMB",
        InputKey.MouseMiddle => "MMB",
        InputKey.MouseX1 => "MB4",
        InputKey.MouseX2 => "MB5",
        InputKey.Space => "SPC",
        InputKey.LeftShift => "LSH",
        InputKey.RightShift => "RSH",
        InputKey.LeftCtrl => "LCT",
        InputKey.RightCtrl => "RCT",
        InputKey.LeftAlt => "LAL",
        InputKey.RightAlt => "RAL",
        InputKey.Grave => "~",
        InputKey.Enter => "ENT",
        InputKey.Backspace => "BSP",
        InputKey.CapsLock => "CAP",
        InputKey.D0 => "0",
        InputKey.D1 => "1",
        InputKey.D2 => "2",
        InputKey.D3 => "3",
        InputKey.D4 => "4",
        InputKey.D5 => "5",
        InputKey.D6 => "6",
        InputKey.D7 => "7",
        InputKey.D8 => "8",
        InputKey.D9 => "9",
        _ => key.ToString().ToUpperInvariant()
    };
}
