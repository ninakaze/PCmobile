namespace PhonePlayLink.Scrcpy;

/// <summary>
/// Hằng số giao thức scrcpy-server v5.0.1 (Video, Audio, Control).
/// </summary>
public static class ScrcpyConstants
{
    public const string ServerVersion = "5.0.1";
    public const string DeviceServerPath = "/data/local/tmp/scrcpy-server.jar";
    public const string SocketNamePrefix = "scrcpy_";

    public const int DeviceNameLength = 64;
    public const int PacketHeaderSize = 12;

    // Packet flags trong 3 bit cao nhất của PTS 64-bit Big Endian
    public const ulong PacketFlagSession = 1UL << 63;
    public const ulong PacketFlagConfig = 1UL << 62;
    public const ulong PacketFlagKeyFrame = 1UL << 61;
    public const ulong PacketPtsMask = PacketFlagKeyFrame - 1;

    // Codec IDs (4-byte ASCII Big Endian)
    public const uint CodecIdDisabled = 0;
    public const uint CodecIdError = 1;
    public const uint CodecIdH264 = 0x68323634; // "h264"
    public const uint CodecIdH265 = 0x68323635; // "h265"
    public const uint CodecIdAv1  = 0x00617631; // "\0av1"
    public const uint CodecIdVp8  = 0x00767038; // "\0vp8"
    public const uint CodecIdVp9  = 0x00767039; // "\0vp9"
    public const uint CodecIdOpus = 0x6f707573; // "opus"
    public const uint CodecIdAac  = 0x00616163; // "\0aac"
    public const uint CodecIdFlac = 0x666c6163; // "flac"
    public const uint CodecIdRaw  = 0x00726177; // "\0raw"

    // Control Message Types (scrcpy v5.0.1 control_msg.h)
    public const byte ControlMsgTypeInjectKeycode = 0;
    public const byte ControlMsgTypeInjectText = 1;
    public const byte ControlMsgTypeInjectTouchEvent = 2;
    public const byte ControlMsgTypeInjectScrollEvent = 3;
    public const byte ControlMsgTypeBackOrScreenOn = 4;
    public const byte ControlMsgTypeSetDisplayPower = 10;

    public const int InjectKeycodePacketSize = 14;
    public const int InjectTouchEventPacketSize = 32;
    public const int MaxTouchPointers = 10;

    // Android KeyCodes (scrcpy android/keycodes.h)
    public const int AndroidKeyCodeHome = 3;
    public const int AndroidKeyCodeBack = 4;
    public const int AndroidKeyCodeAppSwitch = 187;
}

public enum AndroidKeyAction : byte
{
    Down = 0,
    Up = 1,
    Multiple = 2
}

/// <summary>
/// Android MotionEvent actions (android/input.h).
/// </summary>
public enum AndroidMotionAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3,
    PointerDown = 5,
    PointerUp = 6,
    HoverMove = 7
}
