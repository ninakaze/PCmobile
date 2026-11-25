using System.Buffers.Binary;
using System.Net.Sockets;
using PhonePlayLink.Core;

namespace PhonePlayLink.Scrcpy;

/// <summary>
/// Giao diện trừu tượng gửi gói tin cảm ứng nhị phân lên Android (hỗ trợ mock cho unit test).
/// </summary>
public interface IScrcpyTouchSender
{
    (int Width, int Height) CurrentScreenSize { get; }
    bool IsScreenOn { get; }
    void UpdateScreenSize(int width, int height);
    void SendTouchEvent(AndroidMotionAction action, ulong pointerId, int x, int y, float pressure);
    void SetDisplayPower(bool powerOn);
    void SendAndroidKeyPress(int androidKeyCode);
}

/// <summary>
/// Gửi gói tin điều khiển nhị phân scrcpy v5.0.1 trực tiếp qua Control Socket (TCP NoDelay).
/// Tuyệt đối không dùng adb shell input.
/// </summary>
public sealed class ScrcpyControlClient : IScrcpyTouchSender, IDisposable
{
    private readonly Socket _controlSocket;
    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _drainCts = new();
    private readonly Task _drainTask;

    // Đóng gói (width << 32) | height để cập nhật & đọc atomic không bao giờ bị xé khi xoay màn hình
    private long _packedScreenSize;
    private volatile bool _isScreenOn = true;
    private volatile bool _disposed;

    public bool IsScreenOn => _isScreenOn;

    public (int Width, int Height) CurrentScreenSize
    {
        get
        {
            long packed = Volatile.Read(ref _packedScreenSize);
            int w = (int)(packed >> 32);
            int h = (int)(packed & 0xFFFF_FFFFL);
            return (w, h);
        }
    }

    public ScrcpyControlClient(Socket controlSocket, int initialWidth, int initialHeight)
    {
        _controlSocket = controlSocket ?? throw new ArgumentNullException(nameof(controlSocket));
        _controlSocket.NoDelay = true;
        UpdateScreenSize(initialWidth, initialHeight);

        _drainTask = Task.Run(() => DrainDeviceMessagesAsync(_drainCts.Token));
        Logger.Ok("Control", $"Control Socket sẵn sàng (NoDelay=true, Screen={initialWidth}x{initialHeight})");
    }

    public void UpdateScreenSize(int width, int height)
    {
        int w = Math.Clamp(width, 1, ushort.MaxValue);
        int h = Math.Clamp(height, 1, ushort.MaxValue);
        long packed = ((long)w << 32) | (uint)h;
        Volatile.Write(ref _packedScreenSize, packed);
    }

    public void SendTouchEvent(AndroidMotionAction action, ulong pointerId, int x, int y, float pressure)
    {
        if (_disposed)
            return;

        (int screenW, int screenH) = CurrentScreenSize;
        int clampedX = Math.Clamp(x, 0, Math.Max(0, screenW - 1));
        int clampedY = Math.Clamp(y, 0, Math.Max(0, screenH - 1));

        Span<byte> packet = stackalloc byte[ScrcpyConstants.InjectTouchEventPacketSize];
        SerializeTouchEvent(
            packet,
            action,
            pointerId,
            clampedX,
            clampedY,
            (ushort)screenW,
            (ushort)screenH,
            pressure);

        SendPacketBytes(packet);
    }

    public void SetDisplayPower(bool powerOn)
    {
        if (_disposed)
            return;

        Span<byte> packet = stackalloc byte[2];
        SerializeSetDisplayPower(packet, powerOn);
        if (SendPacketBytes(packet))
        {
            _isScreenOn = powerOn;
            Logger.Info("Power", $"Màn hình điện thoại: {(powerOn ? "BẬT" : "TẮT (tiết kiệm pin/chống nóng)")}");
        }
    }

    public void SendAndroidKeyPress(int androidKeyCode)
    {
        if (_disposed)
            return;

        Span<byte> down = stackalloc byte[ScrcpyConstants.InjectKeycodePacketSize];
        Span<byte> up = stackalloc byte[ScrcpyConstants.InjectKeycodePacketSize];
        SerializeInjectKeycode(down, AndroidKeyAction.Down, androidKeyCode);
        SerializeInjectKeycode(up, AndroidKeyAction.Up, androidKeyCode);

        SendPacketBytes(down);
        SendPacketBytes(up);
    }

    public static void SerializeInjectKeycode(
        Span<byte> destination14,
        AndroidKeyAction action,
        int androidKeyCode,
        int repeat = 0,
        int metaState = 0)
    {
        if (destination14.Length < ScrcpyConstants.InjectKeycodePacketSize)
            throw new ArgumentException($"Buffer phải có ít nhất {ScrcpyConstants.InjectKeycodePacketSize} bytes.", nameof(destination14));

        destination14[0] = ScrcpyConstants.ControlMsgTypeInjectKeycode;
        destination14[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(destination14.Slice(2, 4), androidKeyCode);
        BinaryPrimitives.WriteInt32BigEndian(destination14.Slice(6, 4), repeat);
        BinaryPrimitives.WriteInt32BigEndian(destination14.Slice(10, 4), metaState);
    }

    private bool SendPacketBytes(ReadOnlySpan<byte> packet)
    {
        lock (_sendLock)
        {
            if (_disposed)
                return false;

            try
            {
                int offset = 0;
                while (offset < packet.Length)
                {
                    int sent = _controlSocket.Send(packet[offset..], SocketFlags.None);
                    if (sent <= 0)
                        return false;
                    offset += sent;
                }
                return true;
            }
            catch (SocketException ex)
            {
                Logger.Warn("Control", $"Lỗi gửi gói control: {ex.Message}");
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Tuần tự hóa gói SET_DISPLAY_POWER dài 2 byte theo chuẩn scrcpy v5.0.1 control_msg.c:
    /// [0] type = 10 (SC_CONTROL_MSG_TYPE_SET_DISPLAY_POWER)
    /// [1] on = 1 | 0
    /// </summary>
    public static void SerializeSetDisplayPower(Span<byte> destination2, bool powerOn)
    {
        if (destination2.Length < 2)
            throw new ArgumentException("Buffer phải có ít nhất 2 bytes.", nameof(destination2));

        destination2[0] = ScrcpyConstants.ControlMsgTypeSetDisplayPower;
        destination2[1] = powerOn ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Tuần tự hóa gói INJECT_TOUCH_EVENT dài đúng 32 byte theo chuẩn scrcpy v5.0.1 control_msg.c.
    /// Layout Big Endian:
    /// [0]     type (1B = 2)
    /// [1]     action (1B)
    /// [2..9]  pointerId (8B uint64)
    /// [10..13] x (4B int32)
    /// [14..17] y (4B int32)
    /// [18..19] screenWidth (2B uint16)
    /// [20..21] screenHeight (2B uint16)
    /// [22..23] pressure (2B u16 fixed-point: 1.0f => 0xFFFF)
    /// [24..27] actionButton (4B uint32)
    /// [28..31] buttons (4B uint32)
    /// </summary>
    public static void SerializeTouchEvent(
        Span<byte> destination32,
        AndroidMotionAction action,
        ulong pointerId,
        int x,
        int y,
        ushort screenWidth,
        ushort screenHeight,
        float pressure,
        uint actionButton = 0,
        uint buttons = 0)
    {
        if (destination32.Length < ScrcpyConstants.InjectTouchEventPacketSize)
            throw new ArgumentException($"Buffer phải có ít nhất {ScrcpyConstants.InjectTouchEventPacketSize} bytes.", nameof(destination32));

        destination32[0] = ScrcpyConstants.ControlMsgTypeInjectTouchEvent;
        destination32[1] = (byte)action;
        BinaryPrimitives.WriteUInt64BigEndian(destination32.Slice(2, 8), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(destination32.Slice(10, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(destination32.Slice(14, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(destination32.Slice(18, 2), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(destination32.Slice(20, 2), screenHeight);
        BinaryPrimitives.WriteUInt16BigEndian(destination32.Slice(22, 2), FloatToU16FixedPoint(pressure));
        BinaryPrimitives.WriteUInt32BigEndian(destination32.Slice(24, 4), actionButton);
        BinaryPrimitives.WriteUInt32BigEndian(destination32.Slice(28, 4), buttons);
    }

    public static ushort FloatToU16FixedPoint(float value)
    {
        float clamped = Math.Clamp(value, 0f, 1f);
        uint scaled = (uint)(clamped * 65536f);
        return scaled >= 0xFFFFu ? (ushort)0xFFFF : (ushort)scaled;
    }

    private async Task DrainDeviceMessagesAsync(CancellationToken ct)
    {
        byte[] buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _controlSocket.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n <= 0)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drainCts.Cancel();
        try { _drainTask.Wait(200); } catch { }
        _drainCts.Dispose();
    }
}
