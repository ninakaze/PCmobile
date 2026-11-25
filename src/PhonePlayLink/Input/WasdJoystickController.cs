using System.Diagnostics;

namespace PhonePlayLink.Input;

/// <summary>
/// Điều khiển cụm Joystick 8 hướng bằng phím W/A/S/D (test nhanh Giai đoạn 2, bật/tắt bằng F2).
/// Tự động đặt ngón tay vào tâm (cx, cy) rồi nội suy mượt 8ms ra bán kính R.
/// </summary>
public sealed class WasdJoystickController
{
    private const double TransitionDurationMs = 8.0;

    private readonly MultiTouchPointerManager _pointerManager;
    private readonly float _centerX;
    private readonly float _centerY;
    private readonly float _radius;

    private bool _enabled = true;
    private bool _wDown;
    private bool _aDown;
    private bool _sDown;
    private bool _dDown;

    private bool _touchActive;
    private float _currentX;
    private float _currentY;
    private float _startX;
    private float _startY;
    private float _targetX;
    private float _targetY;
    private long _transitionStartTicks;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value)
            {
                ResetAndRelease();
            }
        }
    }

    public bool IsTouchActive => _touchActive;

    public WasdJoystickController(
        MultiTouchPointerManager pointerManager,
        float centerX = 0.18f,
        float centerY = 0.75f,
        float radius = 0.10f)
    {
        _pointerManager = pointerManager ?? throw new ArgumentNullException(nameof(pointerManager));
        _centerX = centerX;
        _centerY = centerY;
        _radius = radius;
        _currentX = centerX;
        _currentY = centerY;
    }

    public bool HandleKey(WasdKey key, bool isDown)
    {
        if (!_enabled)
            return false;

        bool changed = false;
        switch (key)
        {
            case WasdKey.W: changed = _wDown != isDown; _wDown = isDown; break;
            case WasdKey.A: changed = _aDown != isDown; _aDown = isDown; break;
            case WasdKey.S: changed = _sDown != isDown; _sDown = isDown; break;
            case WasdKey.D: changed = _dDown != isDown; _dDown = isDown; break;
        }

        if (changed)
        {
            RecomputeTarget(Stopwatch.GetTimestamp());
        }
        return true;
    }

    public void ResetAndRelease()
    {
        _wDown = _aDown = _sDown = _dDown = false;
        if (_touchActive)
        {
            _pointerManager.TouchUp(TouchSourceId.WasdJoystick);
            _touchActive = false;
        }
        _currentX = _centerX;
        _currentY = _centerY;
        _targetX = _centerX;
        _targetY = _centerY;
    }

    public void Update() => UpdateAtTimestamp(Stopwatch.GetTimestamp());

    /// <summary>
    /// Cho phép test deterministic theo timestamp.
    /// </summary>
    public void UpdateAtTimestamp(long timestamp)
    {
        if (!_touchActive)
            return;

        double elapsedMs = Stopwatch.GetElapsedTime(_transitionStartTicks, timestamp).TotalMilliseconds;
        float t = elapsedMs >= TransitionDurationMs ? 1f : Math.Clamp((float)(elapsedMs / TransitionDurationMs), 0f, 1f);

        float nextX = _startX + (_targetX - _startX) * t;
        float nextY = _startY + (_targetY - _startY) * t;

        if (MathF.Abs(nextX - _currentX) > 1e-5f || MathF.Abs(nextY - _currentY) > 1e-5f)
        {
            _currentX = nextX;
            _currentY = nextY;
            _pointerManager.TouchMove(TouchSourceId.WasdJoystick, _currentX, _currentY);
        }
    }

    private void RecomputeTarget(long nowTicks)
    {
        int dx = (_dDown ? 1 : 0) - (_aDown ? 1 : 0);
        int dy = (_sDown ? 1 : 0) - (_wDown ? 1 : 0);

        if (dx == 0 && dy == 0)
        {
            if (_touchActive)
            {
                _pointerManager.TouchUp(TouchSourceId.WasdJoystick);
                _touchActive = false;
                _currentX = _centerX;
                _currentY = _centerY;
            }
            return;
        }

        float fx = dx;
        float fy = dy;
        float len = MathF.Sqrt(fx * fx + fy * fy);
        fx /= len;
        fy /= len;

        float destX = Math.Clamp(_centerX + fx * _radius, 0f, 1f);
        float destY = Math.Clamp(_centerY + fy * _radius, 0f, 1f);

        if (!_touchActive)
        {
            // Đặt ngón tay vào đúng tâm joystick trước rồi mới trượt mượt 8ms ra hướng mục tiêu
            _currentX = _centerX;
            _currentY = _centerY;
            if (_pointerManager.TryTouchDown(TouchSourceId.WasdJoystick, _centerX, _centerY))
            {
                _touchActive = true;
                _startX = _centerX;
                _startY = _centerY;
                _targetX = destX;
                _targetY = destY;
                _transitionStartTicks = nowTicks;
            }
        }
        else
        {
            // Cập nhật điểm bắt đầu về vị trí hiện tại để đổi hướng mượt (không giật)
            UpdateAtTimestamp(nowTicks);
            _startX = _currentX;
            _startY = _currentY;
            _targetX = destX;
            _targetY = destY;
            _transitionStartTicks = nowTicks;
        }
    }
}

public enum WasdKey
{
    W,
    A,
    S,
    D
}
