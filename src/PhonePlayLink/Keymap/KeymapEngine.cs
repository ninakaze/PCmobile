using System.Diagnostics;
using PhonePlayLink.Input;

namespace PhonePlayLink.Keymap;

/// <summary>
/// Bộ điều phối Keymap hoàn chỉnh:
/// Tap Key (có OnlyWhenAimLocked), WASD Joystick (8 hướng + Shift Sprint/Walk),
/// FPS Aim Look 360° (Smart Edge Reset), MOBA Skill Cast (định hướng chuột + hủy chiêu),
/// và Direct Mouse Touch khi không bị bind ăn chuột.
/// </summary>
public sealed class KeymapEngine
{
    private sealed class JoystickRuntime
    {
        public WasdJoystickBinding Config = null!;
        public TouchSourceId SourceId;
        public bool Up, Left, Down, Right, Sprint;
        public bool Active;
        public float CurrentX, CurrentY;
        public float StartX, StartY;
        public float TargetX, TargetY;
        public long TransitionStartTicks;
    }

    private sealed class FpsAimRuntime
    {
        public FpsAimBinding Config = null!;
        public TouchSourceId SourceId;
        public bool Active;
        public float CurrentX;
        public float CurrentY;
    }

    private sealed class MobaSkillRuntime
    {
        public MobaSkillBinding Config = null!;
        public TouchSourceId SourceId;
        public bool Active;
        public bool IsCancelling;
    }

    private readonly MultiTouchPointerManager _pointerManager;
    private readonly HashSet<InputKey> _pressedKeys = [];
    private readonly Dictionary<int, bool> _activeTaps = [];
    private readonly List<JoystickRuntime> _joysticks = [];
    private readonly List<FpsAimRuntime> _fpsAims = [];
    private readonly List<MobaSkillRuntime> _mobaSkills = [];

    private KeymapProfile _profile;
    private bool _isAimLocked;
    private bool _directMouseActive;
    private float _lastMouseNormX = 0.5f;
    private float _lastMouseNormY = 0.5f;

    public KeymapProfile Profile => _profile;
    public bool IsAimLocked => _isAimLocked;
    public bool IsDirectMouseActive => _directMouseActive;
    public float GlobalMouseSensitivity { get; set; } = 1.0f;
    public InputKey? OverrideAimToggleKey { get; set; }
    public event Action<bool>? AimLockChanged;

    public KeymapEngine(MultiTouchPointerManager pointerManager, KeymapProfile initialProfile)
    {
        _pointerManager = pointerManager ?? throw new ArgumentNullException(nameof(pointerManager));
        _profile = initialProfile ?? throw new ArgumentNullException(nameof(initialProfile));
        RebuildRuntimes();
    }

    public void LoadProfile(KeymapProfile profile)
    {
        ReleaseAllInputs();
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        RebuildRuntimes();
    }

    /// <summary>
    /// Cập nhật lại bảng runtime sau khi Editor thêm/xóa/sửa nút.
    /// </summary>
    public void RebuildRuntimes()
    {
        ReleaseAllInputs();

        _joysticks.Clear();
        for (int i = 0; i < _profile.Joysticks.Count; i++)
        {
            _joysticks.Add(new JoystickRuntime
            {
                Config = _profile.Joysticks[i],
                SourceId = new TouchSourceId(TouchSourceKind.WasdJoystick, i)
            });
        }

        _fpsAims.Clear();
        for (int i = 0; i < _profile.FpsAims.Count; i++)
        {
            _fpsAims.Add(new FpsAimRuntime
            {
                Config = _profile.FpsAims[i],
                SourceId = new TouchSourceId(TouchSourceKind.AimLook, i)
            });
        }

        _mobaSkills.Clear();
        for (int i = 0; i < _profile.MobaSkills.Count; i++)
        {
            _mobaSkills.Add(new MobaSkillRuntime
            {
                Config = _profile.MobaSkills[i],
                SourceId = new TouchSourceId(TouchSourceKind.SkillCast, i)
            });
        }
    }

    public void SetAimLocked(bool locked)
    {
        if (_isAimLocked == locked)
            return;

        if (locked && _fpsAims.Count == 0)
            return;

        _isAimLocked = locked;

        if (locked)
        {
            // Nhả direct mouse nếu đang giữ khi bật khóa tâm
            if (_directMouseActive)
            {
                _pointerManager.TouchUp(TouchSourceId.DirectMouse);
                _directMouseActive = false;
            }

            foreach (var aim in _fpsAims)
            {
                aim.CurrentX = Math.Clamp(aim.Config.CenterX, 0.01f, 0.99f);
                aim.CurrentY = Math.Clamp(aim.Config.CenterY, 0.01f, 0.99f);
                aim.Active = _pointerManager.TryTouchDown(aim.SourceId, aim.CurrentX, aim.CurrentY);
            }
        }
        else
        {
            foreach (var aim in _fpsAims)
            {
                if (aim.Active)
                {
                    _pointerManager.TouchUp(aim.SourceId);
                    aim.Active = false;
                }
            }

            // Nếu đang giữ Tap gắn OnlyWhenAimLocked (như nút bắn chuột trái), nhả luôn để tránh kẹt
            for (int i = 0; i < _profile.Taps.Count; i++)
            {
                if (_profile.Taps[i].OnlyWhenAimLocked && _activeTaps.Remove(i))
                {
                    _pointerManager.TouchUp(new TouchSourceId(TouchSourceKind.TapKey, i));
                }
            }
        }

        AimLockChanged?.Invoke(_isAimLocked);
    }

    public bool HandleKeyInput(InputKey key, bool isDown, bool isRepeat = false)
        => HandleKeyInputAt(key, isDown, Stopwatch.GetTimestamp(), isRepeat);

    public bool HandleKeyInputAt(InputKey key, bool isDown, long timestamp, bool isRepeat = false)
    {
        if (key == InputKey.None || (isDown && isRepeat))
            return false;

        if (isDown)
        {
            if (!_pressedKeys.Add(key))
                return false;
        }
        else
        {
            _pressedKeys.Remove(key);
        }

        bool handled = false;

        // 1. Kiểm tra Toggle FPS Aim Lock
        if (isDown)
        {
            for (int i = 0; i < _fpsAims.Count; i++)
            {
                var aim = _fpsAims[i].Config;
                InputKey primaryToggle = OverrideAimToggleKey is { } ov && ov != InputKey.None ? ov : aim.ToggleKey;
                if (key == primaryToggle || (aim.SecondaryToggleKey != InputKey.None && key == aim.SecondaryToggleKey))
                {
                    SetAimLocked(!_isAimLocked);
                    return true;
                }
            }
        }

        // 2. Nếu nhấn chuột phải trong khi đang ngắm MOBA Skill => Hủy chiêu
        if (isDown && key == InputKey.MouseRight)
        {
            bool cancelledAnySkill = false;
            foreach (var skill in _mobaSkills)
            {
                if (skill.Active && skill.Config.CancelWithRightClick && !skill.IsCancelling)
                {
                    skill.IsCancelling = true;
                    _pointerManager.TouchMove(
                        skill.SourceId,
                        Math.Clamp(skill.Config.CancelX, 0f, 1f),
                        Math.Clamp(skill.Config.CancelY, 0f, 1f));
                    cancelledAnySkill = true;
                }
            }
            if (cancelledAnySkill)
                return true;
        }

        // 3. WASD Joysticks (+ Sprint Modifier)
        foreach (var joy in _joysticks)
        {
            bool matched = false;
            if (key == joy.Config.UpKey) { joy.Up = isDown; matched = true; }
            else if (key == joy.Config.LeftKey) { joy.Left = isDown; matched = true; }
            else if (key == joy.Config.DownKey) { joy.Down = isDown; matched = true; }
            else if (key == joy.Config.RightKey) { joy.Right = isDown; matched = true; }
            else if (joy.Config.SprintModifierKey != InputKey.None && key == joy.Config.SprintModifierKey)
            {
                joy.Sprint = isDown;
                matched = true;
            }

            if (matched)
            {
                RecomputeJoystickTarget(joy, timestamp);
                handled = true;
            }
        }

        // 4. MOBA Skill Casts
        foreach (var skill in _mobaSkills)
        {
            if (skill.Config.Key != key)
                continue;

            if (isDown)
            {
                if (!skill.Active)
                {
                    skill.IsCancelling = false;
                    float sx = Math.Clamp(skill.Config.SkillX, 0f, 1f);
                    float sy = Math.Clamp(skill.Config.SkillY, 0f, 1f);
                    if (_pointerManager.TryTouchDown(skill.SourceId, sx, sy))
                    {
                        skill.Active = true;
                        UpdateSkillAimPosition(skill);
                    }
                }
            }
            else
            {
                if (skill.Active)
                {
                    if (!skill.IsCancelling)
                    {
                        UpdateSkillAimPosition(skill);
                    }
                    _pointerManager.TouchUp(skill.SourceId);
                    skill.Active = false;
                    skill.IsCancelling = false;
                }
            }
            handled = true;
        }

        // 5. Tap Keys
        for (int i = 0; i < _profile.Taps.Count; i++)
        {
            TapBinding tap = _profile.Taps[i];
            if (tap.Key != key)
                continue;

            var tapSource = new TouchSourceId(TouchSourceKind.TapKey, i);
            if (isDown)
            {
                if (tap.OnlyWhenAimLocked && !_isAimLocked)
                    continue;

                if (!_activeTaps.ContainsKey(i) && _pointerManager.TryTouchDown(tapSource, tap.X, tap.Y))
                {
                    _activeTaps[i] = true;
                    handled = true;
                }
            }
            else
            {
                if (_activeTaps.Remove(i))
                {
                    _pointerManager.TouchUp(tapSource);
                    handled = true;
                }
            }
        }

        return handled;
    }

    public void HandleMouseButton(InputKey mouseButton, bool isDown, float? viewportNormX, float? viewportNormY)
    {
        if (viewportNormX.HasValue && viewportNormY.HasValue)
        {
            _lastMouseNormX = Math.Clamp(viewportNormX.Value, 0f, 1f);
            _lastMouseNormY = Math.Clamp(viewportNormY.Value, 0f, 1f);
        }

        bool consumedByBinding = HandleKeyInput(mouseButton, isDown, isRepeat: false);
        if (consumedByBinding)
            return;

        // Khi không bị keymap ăn và không khóa tâm => cho phép click/kéo trực tiếp màn hình game bằng chuột trái
        if (mouseButton == InputKey.MouseLeft && !_isAimLocked)
        {
            if (isDown)
            {
                if (viewportNormX.HasValue && viewportNormY.HasValue)
                {
                    _directMouseActive = _pointerManager.TryTouchDown(
                        TouchSourceId.DirectMouse,
                        _lastMouseNormX,
                        _lastMouseNormY);
                }
            }
            else if (_directMouseActive)
            {
                _pointerManager.TouchUp(TouchSourceId.DirectMouse);
                _directMouseActive = false;
            }
        }
    }

    public void HandleMouseMove(float? viewportNormX, float? viewportNormY, int relDx, int relDy)
    {
        if (viewportNormX.HasValue && viewportNormY.HasValue)
        {
            _lastMouseNormX = Math.Clamp(viewportNormX.Value, 0f, 1f);
            _lastMouseNormY = Math.Clamp(viewportNormY.Value, 0f, 1f);
        }

        if (_isAimLocked)
        {
            if (relDx != 0 || relDy != 0)
            {
                ApplyFpsAimDelta(relDx, relDy);
            }
            return;
        }

        if (_directMouseActive && viewportNormX.HasValue && viewportNormY.HasValue)
        {
            _pointerManager.TouchMove(TouchSourceId.DirectMouse, _lastMouseNormX, _lastMouseNormY);
        }

        foreach (var skill in _mobaSkills)
        {
            if (skill.Active && !skill.IsCancelling)
            {
                UpdateSkillAimPosition(skill);
            }
        }
    }

    public void Update() => UpdateAtTimestamp(Stopwatch.GetTimestamp());

    public void UpdateAtTimestamp(long timestamp)
    {
        foreach (var joy in _joysticks)
        {
            UpdateJoystickAt(joy, timestamp);
        }
    }

    public bool HasActiveInterpolations
    {
        get
        {
            foreach (var joy in _joysticks)
            {
                if (joy.Active) return true;
            }
            return false;
        }
    }

    public void ReleaseAllInputs()
    {
        _pressedKeys.Clear();
        _activeTaps.Clear();
        _directMouseActive = false;

        foreach (var joy in _joysticks)
        {
            joy.Up = joy.Left = joy.Down = joy.Right = joy.Sprint = false;
            joy.Active = false;
        }

        foreach (var aim in _fpsAims)
        {
            aim.Active = false;
        }

        foreach (var skill in _mobaSkills)
        {
            skill.Active = false;
            skill.IsCancelling = false;
        }

        if (_isAimLocked)
        {
            _isAimLocked = false;
            AimLockChanged?.Invoke(false);
        }

        _pointerManager.ReleaseAll();
    }

    private void ApplyFpsAimDelta(int relDx, int relDy)
    {
        foreach (var aim in _fpsAims)
        {
            if (!aim.Active)
                continue;

            float cx = Math.Clamp(aim.Config.CenterX, 0.05f, 0.95f);
            float cy = Math.Clamp(aim.Config.CenterY, 0.05f, 0.95f);
            float halfW = Math.Clamp(aim.Config.ZoneHalfWidth, 0.04f, 0.48f);
            float halfH = Math.Clamp(aim.Config.ZoneHalfHeight, 0.04f, 0.48f);

            float minX = Math.Clamp(cx - halfW, 0.01f, 0.99f);
            float maxX = Math.Clamp(cx + halfW, 0.01f, 0.99f);
            float minY = Math.Clamp(cy - halfH, 0.01f, 0.99f);
            float maxY = Math.Clamp(cy + halfH, 0.01f, 0.99f);

            float sensScale = Math.Clamp(GlobalMouseSensitivity, 0.1f, 5.0f);
            float remDx = relDx * aim.Config.SensitivityX * sensScale;
            float remDy = relDy * aim.Config.SensitivityY * sensScale * (aim.Config.InvertY ? -1f : 1f);

            // Cắt nhỏ nếu vẩy chuột quá mạnh vượt nửa chiều rộng vùng xoay để không mất góc
            for (int step = 0; step < 6 && (MathF.Abs(remDx) > 1e-6f || MathF.Abs(remDy) > 1e-6f); step++)
            {
                float nextX = aim.CurrentX + remDx;
                float nextY = aim.CurrentY + remDy;

                if (nextX >= minX && nextX <= maxX && nextY >= minY && nextY <= maxY)
                {
                    aim.CurrentX = nextX;
                    aim.CurrentY = nextY;
                    _pointerManager.TouchMove(aim.SourceId, aim.CurrentX, aim.CurrentY);
                    break;
                }

                // Tính phần dư sau khi đụng mép để xoay tiếp từ tâm (Smart Edge Reset 360°)
                float stepDx = Math.Clamp(nextX, minX, maxX) - aim.CurrentX;
                float stepDy = Math.Clamp(nextY, minY, maxY) - aim.CurrentY;
                remDx -= stepDx;
                remDy -= stepDy;

                _pointerManager.TouchUp(aim.SourceId);
                aim.CurrentX = cx;
                aim.CurrentY = cy;
                if (!_pointerManager.TryTouchDown(aim.SourceId, cx, cy))
                {
                    aim.Active = false;
                    break;
                }

                // Đưa lượng còn dư (bảo vệ không vượt quá halfW/halfH trong 1 bước tiếp)
                float chunkDx = Math.Clamp(remDx, -halfW * 0.9f, halfW * 0.9f);
                float chunkDy = Math.Clamp(remDy, -halfH * 0.9f, halfH * 0.9f);
                remDx -= chunkDx;
                remDy -= chunkDy;

                aim.CurrentX = Math.Clamp(cx + chunkDx, minX, maxX);
                aim.CurrentY = Math.Clamp(cy + chunkDy, minY, maxY);
                _pointerManager.TouchMove(aim.SourceId, aim.CurrentX, aim.CurrentY);
            }
        }
    }

    private void UpdateSkillAimPosition(MobaSkillRuntime skill)
    {
        float vecX = _lastMouseNormX - skill.Config.AnchorX;
        float vecY = _lastMouseNormY - skill.Config.AnchorY;
        float dist = MathF.Sqrt(vecX * vecX + vecY * vecY);

        if (dist < 1e-4f)
            return;

        float dirX = vecX / dist;
        float dirY = vecY / dist;

        // Kéo tối đa tới CastRadius (tỉ lệ tuyến tính trong bán kính 0.25f quanh nhân vật để hỗ trợ cả skill định vị vùng AOE)
        float strength = Math.Clamp(dist / 0.25f, 0.25f, 1.0f);
        float offset = skill.Config.CastRadius * strength;

        float targetX = Math.Clamp(skill.Config.SkillX + dirX * offset, 0f, 1f);
        float targetY = Math.Clamp(skill.Config.SkillY + dirY * offset, 0f, 1f);
        _pointerManager.TouchMove(skill.SourceId, targetX, targetY);
    }

    private void RecomputeJoystickTarget(JoystickRuntime joy, long nowTicks)
    {
        int dx = (joy.Right ? 1 : 0) - (joy.Left ? 1 : 0);
        int dy = (joy.Down ? 1 : 0) - (joy.Up ? 1 : 0);

        if (dx == 0 && dy == 0)
        {
            if (joy.Active)
            {
                _pointerManager.TouchUp(joy.SourceId);
                joy.Active = false;
                joy.CurrentX = joy.Config.CenterX;
                joy.CurrentY = joy.Config.CenterY;
            }
            return;
        }

        float fx = dx;
        float fy = dy;
        float len = MathF.Sqrt(fx * fx + fy * fy);
        fx /= len;
        fy /= len;

        float radius = (joy.Sprint && joy.Config.SprintModifierKey != InputKey.None)
            ? joy.Config.SprintRadius
            : joy.Config.Radius;

        float destX = Math.Clamp(joy.Config.CenterX + fx * radius, 0f, 1f);
        float destY = Math.Clamp(joy.Config.CenterY + fy * radius, 0f, 1f);

        if (!joy.Active)
        {
            joy.CurrentX = Math.Clamp(joy.Config.CenterX, 0f, 1f);
            joy.CurrentY = Math.Clamp(joy.Config.CenterY, 0f, 1f);
            if (_pointerManager.TryTouchDown(joy.SourceId, joy.CurrentX, joy.CurrentY))
            {
                joy.Active = true;
                joy.StartX = joy.CurrentX;
                joy.StartY = joy.CurrentY;
                joy.TargetX = destX;
                joy.TargetY = destY;
                joy.TransitionStartTicks = nowTicks;
            }
        }
        else
        {
            UpdateJoystickAt(joy, nowTicks);
            joy.StartX = joy.CurrentX;
            joy.StartY = joy.CurrentY;
            joy.TargetX = destX;
            joy.TargetY = destY;
            joy.TransitionStartTicks = nowTicks;
        }
    }

    private void UpdateJoystickAt(JoystickRuntime joy, long timestamp)
    {
        if (!joy.Active)
            return;

        double durationMs = Math.Clamp(joy.Config.TransitionMs, 0f, 50f);
        double elapsedMs = Stopwatch.GetElapsedTime(joy.TransitionStartTicks, timestamp).TotalMilliseconds;
        float t = durationMs <= 0.01 || elapsedMs >= durationMs
            ? 1f
            : Math.Clamp((float)(elapsedMs / durationMs), 0f, 1f);

        float nextX = joy.StartX + (joy.TargetX - joy.StartX) * t;
        float nextY = joy.StartY + (joy.TargetY - joy.StartY) * t;

        if (MathF.Abs(nextX - joy.CurrentX) > 1e-5f || MathF.Abs(nextY - joy.CurrentY) > 1e-5f)
        {
            joy.CurrentX = nextX;
            joy.CurrentY = nextY;
            _pointerManager.TouchMove(joy.SourceId, joy.CurrentX, joy.CurrentY);
        }
    }
}
