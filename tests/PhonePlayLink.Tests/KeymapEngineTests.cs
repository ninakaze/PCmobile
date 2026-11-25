using System.Diagnostics;
using PhonePlayLink.Input;
using PhonePlayLink.Keymap;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Tests;

public class KeymapEngineTests
{
    private sealed class FakeTouchSender(int w = 2000, int h = 1000) : IScrcpyTouchSender
    {
        public record SentEvent(AndroidMotionAction Action, ulong PointerId, int X, int Y, float Pressure);
        public List<SentEvent> Events { get; } = [];
        public (int Width, int Height) CurrentScreenSize { get; private set; } = (w, h);
        public bool IsScreenOn { get; private set; } = true;
        public void UpdateScreenSize(int width, int height) => CurrentScreenSize = (width, height);
        public void SetDisplayPower(bool powerOn) => IsScreenOn = powerOn;

        public void SendTouchEvent(AndroidMotionAction action, ulong pointerId, int x, int y, float pressure)
            => Events.Add(new SentEvent(action, pointerId, x, y, pressure));
    }

    [Fact]
    public void TapKey_OnlyWhenAimLocked_RespectsLockStateAndDirectMouseFallback()
    {
        var sender = new FakeTouchSender();
        using var ptrMgr = new MultiTouchPointerManager(sender);
        var profile = new KeymapProfile
        {
            Taps = [new TapBinding { Key = InputKey.MouseLeft, X = 0.86f, Y = 0.72f, OnlyWhenAimLocked = true }],
            FpsAims = [new FpsAimBinding { ToggleKey = InputKey.Grave }]
        };
        var engine = new KeymapEngine(ptrMgr, profile);

        // Khi CHƯA khóa tâm: click chuột trái tại (0.25, 0.30) phải chạm đúng (0.25, 0.30) (DirectMouse)
        engine.HandleMouseButton(InputKey.MouseLeft, isDown: true, 0.25f, 0.30f);
        Assert.True(engine.IsDirectMouseActive);
        Assert.Equal((int)MathF.Round(0.25f * 1999), sender.Events[0].X);
        engine.HandleMouseButton(InputKey.MouseLeft, isDown: false, 0.25f, 0.30f);

        sender.Events.Clear();

        // Bật khóa tâm bằng phím ~ (Grave) => LMB phải chạm nút Bắn (0.86, 0.72)
        engine.HandleKeyInput(InputKey.Grave, isDown: true);
        Assert.True(engine.IsAimLocked);
        engine.HandleMouseButton(InputKey.MouseLeft, isDown: true, null, null);
        Assert.False(engine.IsDirectMouseActive);
        var fireDown = sender.Events.Last(e => e.Action == AndroidMotionAction.Down);
        Assert.Equal((int)MathF.Round(0.86f * 1999), fireDown.X);
        Assert.Equal((int)MathF.Round(0.72f * 999), fireDown.Y);
    }

    [Fact]
    public void WasdJoystick_WithSprintModifier_ExpandsRadiusSmoothly()
    {
        var sender = new FakeTouchSender();
        using var ptrMgr = new MultiTouchPointerManager(sender);
        var profile = new KeymapProfile
        {
            Joysticks =
            [
                new WasdJoystickBinding
                {
                    CenterX = 0.20f,
                    CenterY = 0.70f,
                    Radius = 0.10f,
                    SprintModifierKey = InputKey.LeftShift,
                    SprintRadius = 0.16f,
                    TransitionMs = 8.0f
                }
            ]
        };
        var engine = new KeymapEngine(ptrMgr, profile);

        long t0 = Stopwatch.GetTimestamp();
        engine.HandleKeyInputAt(InputKey.D, isDown: true, t0);
        engine.UpdateAtTimestamp(t0 + (long)(Stopwatch.Frequency * 0.015));

        var walkMove = sender.Events.Last(e => e.Action == AndroidMotionAction.Move);
        Assert.Equal((int)MathF.Round(0.30f * 1999), walkMove.X); // 0.20 + 0.10

        // Nhấn thêm LeftShift => kéo ra bán kính Sprint 0.16 => X = 0.36
        long t1 = t0 + (long)(Stopwatch.Frequency * 0.020);
        engine.HandleKeyInputAt(InputKey.LeftShift, isDown: true, t1);
        engine.UpdateAtTimestamp(t1 + (long)(Stopwatch.Frequency * 0.015));

        var sprintMove = sender.Events.Last(e => e.Action == AndroidMotionAction.Move);
        Assert.Equal((int)MathF.Round(0.36f * 1999), sprintMove.X);
    }

    [Fact]
    public void FpsAim_SmartEdgeReset_MaintainsContinuous360RotationAcrossBoundaries()
    {
        var sender = new FakeTouchSender();
        using var ptrMgr = new MultiTouchPointerManager(sender);
        var profile = new KeymapProfile
        {
            FpsAims =
            [
                new FpsAimBinding
                {
                    ToggleKey = InputKey.Grave,
                    CenterX = 0.70f,
                    CenterY = 0.50f,
                    ZoneHalfWidth = 0.20f,
                    ZoneHalfHeight = 0.30f,
                    SensitivityX = 0.002f, // 100px => +0.20 (chạm biên 0.90)
                    SensitivityY = 0.002f
                }
            ]
        };
        var engine = new KeymapEngine(ptrMgr, profile);

        engine.HandleKeyInput(InputKey.Grave, isDown: true);
        Assert.True(engine.IsAimLocked);
        sender.Events.Clear();

        // Di chuyển vượt biên phải (+150px => +0.30 > ZoneHalfWidth 0.20)
        engine.HandleMouseMove(null, null, relDx: 150, relDy: 0);

        // Smart Edge Reset phải sinh chuỗi: Up -> Down tại tâm (0.70) -> Move tiếp phần dư (0.70 + 0.10 = 0.80)
        Assert.Contains(sender.Events, e => e.Action == AndroidMotionAction.Up);
        int downIdx = sender.Events.FindIndex(e => e.Action == AndroidMotionAction.Down);
        Assert.True(downIdx >= 0);
        Assert.Equal((int)MathF.Round(0.70f * 1999), sender.Events[downIdx].X);

        var lastMove = sender.Events.Last(e => e.Action == AndroidMotionAction.Move);
        Assert.InRange(lastMove.X, (int)MathF.Round(0.795f * 1999), (int)MathF.Round(0.805f * 1999));
    }

    [Fact]
    public void MobaSkillCast_AimsAlongMouseVector_AndSupportsRightClickCancel()
    {
        var sender = new FakeTouchSender(1000, 1000);
        using var ptrMgr = new MultiTouchPointerManager(sender);
        var profile = new KeymapProfile
        {
            MobaSkills =
            [
                new MobaSkillBinding
                {
                    Key = InputKey.Q,
                    SkillX = 0.80f,
                    SkillY = 0.80f,
                    CastRadius = 0.10f,
                    AnchorX = 0.50f,
                    AnchorY = 0.50f,
                    CancelX = 0.90f,
                    CancelY = 0.20f,
                    CancelWithRightClick = true
                }
            ]
        };
        var engine = new KeymapEngine(ptrMgr, profile);

        // Con trỏ chuột ở phía trên tâm nhân vật (0.50, 0.20) => vector (0, -0.30) => kéo skill thẳng lên trên (0.80, 0.70)
        engine.HandleMouseMove(0.50f, 0.20f, 0, 0);
        engine.HandleKeyInput(InputKey.Q, isDown: true);

        var moveUp = sender.Events.Last(e => e.Action == AndroidMotionAction.Move);
        Assert.InRange(moveUp.X, 799, 800);
        Assert.InRange(moveUp.Y, 698, 701);

        // Xoay chuột sang phải (0.80, 0.50) => kéo skill sang phải (0.90, 0.80)
        engine.HandleMouseMove(0.80f, 0.50f, 0, 0);
        var moveRight = sender.Events.Last(e => e.Action == AndroidMotionAction.Move);
        Assert.InRange(moveRight.X, 898, 901);
        Assert.InRange(moveRight.Y, 799, 800);

        // Bấm Chuột phải để hủy chiêu => kéo về (0.90, 0.20) rồi nhả Q
        engine.HandleMouseButton(InputKey.MouseRight, isDown: true, 0.80f, 0.50f);
        var cancelMove = sender.Events.Last(e => e.Action == AndroidMotionAction.Move);
        Assert.InRange(cancelMove.X, 898, 901);
        Assert.InRange(cancelMove.Y, 199, 201);

        engine.HandleKeyInput(InputKey.Q, isDown: false);
        var release = sender.Events.Last(e => e.Action == AndroidMotionAction.Up);
        Assert.InRange(release.X, 898, 901);
        Assert.InRange(release.Y, 199, 201);
    }
}
