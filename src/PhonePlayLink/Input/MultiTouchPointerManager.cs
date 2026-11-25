using PhonePlayLink.Core;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Input;

public enum TouchSourceKind
{
    DirectMouse = 1,
    WasdJoystick = 2,
    TapKey = 3,
    AimLook = 4,
    SkillCast = 5
}

/// <summary>
/// Định danh duy nhất cho từng thao tác chạm logic (không cấp phát heap).
/// </summary>
public readonly record struct TouchSourceId(TouchSourceKind Kind, int Channel = 0)
{
    public static readonly TouchSourceId DirectMouse = new(TouchSourceKind.DirectMouse, 0);
    public static readonly TouchSourceId WasdJoystick = new(TouchSourceKind.WasdJoystick, 0);
}

/// <summary>
/// Quản lý tối đa 10 pointerId (0..9) độc lập đồng thời cho cảm ứng đa điểm trên Android.
/// Thread-safe giữa Main Event Loop, Joystick tick và Session rotation/disconnect.
/// </summary>
public sealed class MultiTouchPointerManager : IDisposable
{
    private struct PointerSlot
    {
        public bool Active;
        public TouchSourceId Owner;
        public float LastNormX;
        public float LastNormY;
        public int LastPixelX;
        public int LastPixelY;
    }

    private readonly IScrcpyTouchSender _sender;
    private readonly PointerSlot[] _slots = new PointerSlot[ScrcpyConstants.MaxTouchPointers];
    private readonly object _sync = new();
    private bool _disposed;

    public int ActivePointerCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Active) count++;
                }
                return count;
            }
        }
    }

    public MultiTouchPointerManager(IScrcpyTouchSender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public bool TryGetAssignedPointerId(TouchSourceId source, out ulong pointerId)
    {
        lock (_sync)
        {
            int idx = FindSlotIndex(source);
            if (idx >= 0)
            {
                pointerId = (ulong)idx;
                return true;
            }
            pointerId = 0;
            return false;
        }
    }

    public bool TryTouchDown(TouchSourceId source, float normX, float normY)
    {
        normX = Math.Clamp(normX, 0f, 1f);
        normY = Math.Clamp(normY, 0f, 1f);

        lock (_sync)
        {
            if (_disposed)
                return false;

            int existing = FindSlotIndex(source);
            if (existing >= 0)
            {
                return MoveSlotLocked(existing, normX, normY);
            }

            int freeIdx = FindFreeSlotIndex();
            if (freeIdx < 0)
            {
                Logger.Warn("MultiTouch", $"Đã đạt giới hạn {ScrcpyConstants.MaxTouchPointers} ngón tay đồng thời, bỏ qua {source}.");
                return false;
            }

            (int screenW, int screenH) = _sender.CurrentScreenSize;
            (int px, int py) = ViewportMapper.NormalizedToDevicePixels(normX, normY, screenW, screenH);

            _slots[freeIdx] = new PointerSlot
            {
                Active = true,
                Owner = source,
                LastNormX = normX,
                LastNormY = normY,
                LastPixelX = px,
                LastPixelY = py
            };

            _sender.SendTouchEvent(AndroidMotionAction.Down, (ulong)freeIdx, px, py, pressure: 1.0f);
            return true;
        }
    }

    public bool TouchMove(TouchSourceId source, float normX, float normY)
    {
        normX = Math.Clamp(normX, 0f, 1f);
        normY = Math.Clamp(normY, 0f, 1f);

        lock (_sync)
        {
            if (_disposed)
                return false;

            int idx = FindSlotIndex(source);
            if (idx < 0)
                return false;

            return MoveSlotLocked(idx, normX, normY);
        }
    }

    public bool TouchUp(TouchSourceId source)
    {
        lock (_sync)
        {
            int idx = FindSlotIndex(source);
            if (idx < 0)
                return false;

            ReleaseSlotLocked(idx);
            return true;
        }
    }

    /// <summary>
    /// Chống kẹt ngón tay (Anti-Stuck Touch): nhả toàn bộ các pointerId đang giữ.
    /// Bắt buộc gọi khi mất focus (Alt+Tab), minimize, xoay màn hình hoặc thoát app.
    /// </summary>
    public void ReleaseAll()
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Active)
                {
                    ReleaseSlotLocked(i);
                }
            }
        }
    }

    public void OnScreenSizeChanged(int newWidth, int newHeight)
    {
        lock (_sync)
        {
            // Nhả ngón tay đang giữ trước khi đổi screenSize để tránh server bỏ qua event vì lệch kích thước khung hình
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Active)
                {
                    ReleaseSlotLocked(i);
                }
            }

            _sender.UpdateScreenSize(newWidth, newHeight);
        }
    }

    private bool MoveSlotLocked(int idx, float normX, float normY)
    {
        (int screenW, int screenH) = _sender.CurrentScreenSize;
        (int px, int py) = ViewportMapper.NormalizedToDevicePixels(normX, normY, screenW, screenH);

        ref PointerSlot slot = ref _slots[idx];
        if (px == slot.LastPixelX && py == slot.LastPixelY)
        {
            slot.LastNormX = normX;
            slot.LastNormY = normY;
            return true; // Tọa độ pixel không đổi -> tránh spam gói MOVE dư thừa
        }

        slot.LastNormX = normX;
        slot.LastNormY = normY;
        slot.LastPixelX = px;
        slot.LastPixelY = py;

        _sender.SendTouchEvent(AndroidMotionAction.Move, (ulong)idx, px, py, pressure: 1.0f);
        return true;
    }

    private void ReleaseSlotLocked(int idx)
    {
        ref PointerSlot slot = ref _slots[idx];
        int px = slot.LastPixelX;
        int py = slot.LastPixelY;
        slot = default;

        _sender.SendTouchEvent(AndroidMotionAction.Up, (ulong)idx, px, py, pressure: 0.0f);
    }

    private int FindSlotIndex(TouchSourceId source)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Active && _slots[i].Owner == source)
                return i;
        }
        return -1;
    }

    private int FindFreeSlotIndex()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_slots[i].Active)
                return i;
        }
        return -1;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Active)
                {
                    ReleaseSlotLocked(i);
                }
            }
            _disposed = true;
        }
    }
}
