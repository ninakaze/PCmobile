using System.Diagnostics;
using PhonePlayLink.Input;
using PhonePlayLink.Scrcpy;

namespace PhonePlayLink.Tests;

public class MultiTouchPointerManagerTests
{
    private sealed class FakeTouchSender : IScrcpyTouchSender
    {
        public record SentEvent(AndroidMotionAction Action, ulong PointerId, int X, int Y, int ScreenW, int ScreenH, float Pressure);

        public List<SentEvent> Events { get; } = [];
        public (int Width, int Height) CurrentScreenSize { get; private set; }
        public bool IsScreenOn { get; private set; } = true;

        public FakeTouchSender(int w = 2400, int h = 1080)
        {
            CurrentScreenSize = (w, h);
        }

        public void UpdateScreenSize(int width, int height) => CurrentScreenSize = (width, height);
        public void SetDisplayPower(bool powerOn) => IsScreenOn = powerOn;
        public void SendAndroidKeyPress(int androidKeyCode) { }

        public void SendTouchEvent(AndroidMotionAction action, ulong pointerId, int x, int y, float pressure)
        {
            Events.Add(new SentEvent(action, pointerId, x, y, CurrentScreenSize.Width, CurrentScreenSize.Height, pressure));
        }
    }

    [Fact]
    public void PointerPool_Supports10ConcurrentPointers_ReusesReleasedSlot_AndRejects11th()
    {
        var sender = new FakeTouchSender();
        using var mgr = new MultiTouchPointerManager(sender);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(mgr.TryTouchDown(new TouchSourceId(TouchSourceKind.TapKey, i), 0.1f * i, 0.5f));
        }
        Assert.Equal(10, mgr.ActivePointerCount);

        // Ngón thứ 11 phải bị từ chối không đá mất 10 ngón đang giữ
        Assert.False(mgr.TryTouchDown(new TouchSourceId(TouchSourceKind.TapKey, 99), 0.5f, 0.5f));
        Assert.Equal(10, mgr.ActivePointerCount);

        // Nhả ngón channel 3 (pointerId = 3), rồi chạm ngón mới => tái sử dụng đúng pointerId = 3
        Assert.True(mgr.TryGetAssignedPointerId(new TouchSourceId(TouchSourceKind.TapKey, 3), out ulong id3));
        Assert.Equal(3UL, id3);
        Assert.True(mgr.TouchUp(new TouchSourceId(TouchSourceKind.TapKey, 3)));
        Assert.Equal(9, mgr.ActivePointerCount);

        var extraSource = new TouchSourceId(TouchSourceKind.TapKey, 100);
        Assert.True(mgr.TryTouchDown(extraSource, 0.8f, 0.8f));
        Assert.True(mgr.TryGetAssignedPointerId(extraSource, out ulong reusedId));
        Assert.Equal(3UL, reusedId);
    }

    [Fact]
    public void ReleaseAll_SendsActionUpForEveryActivePointer_AntiStuck()
    {
        var sender = new FakeTouchSender();
        using var mgr = new MultiTouchPointerManager(sender);

        mgr.TryTouchDown(TouchSourceId.WasdJoystick, 0.18f, 0.75f);
        mgr.TryTouchDown(TouchSourceId.DirectMouse, 0.75f, 0.50f);
        Assert.Equal(2, mgr.ActivePointerCount);

        mgr.ReleaseAll();

        Assert.Equal(0, mgr.ActivePointerCount);
        Assert.Equal(2, sender.Events.Count(e => e.Action == AndroidMotionAction.Up && e.Pressure == 0f));
    }

    [Fact]
    public void OnScreenSizeChanged_ReleasesOldTouchesAndUsesNewDimensions()
    {
        var sender = new FakeTouchSender(1080, 2400);
        using var mgr = new MultiTouchPointerManager(sender);

        mgr.TryTouchDown(TouchSourceId.DirectMouse, 0.5f, 0.5f);
        mgr.OnScreenSizeChanged(2400, 1080);

        Assert.Equal(0, mgr.ActivePointerCount);
        Assert.Contains(sender.Events, e => e.Action == AndroidMotionAction.Up && e.ScreenW == 1080 && e.ScreenH == 2400);

        mgr.TryTouchDown(TouchSourceId.DirectMouse, 0.5f, 0.5f);
        var last = sender.Events[^1];
        Assert.Equal(AndroidMotionAction.Down, last.Action);
        Assert.Equal(2400, last.ScreenW);
        Assert.Equal(1080, last.ScreenH);
    }

    [Fact]
    public void WasdAndDirectMouse_RunConcurrentlyWithDistinctPointerIds()
    {
        var sender = new FakeTouchSender(2000, 1000);
        using var mgr = new MultiTouchPointerManager(sender);
        var wasd = new WasdJoystickController(mgr, centerX: 0.18f, centerY: 0.75f, radius: 0.10f);

        long t0 = Stopwatch.GetTimestamp();
        wasd.HandleKey(WasdKey.W, isDown: true);
        Assert.True(wasd.IsTouchActive);
        Assert.True(mgr.TryGetAssignedPointerId(TouchSourceId.WasdJoystick, out ulong wasdPtr));

        // Đồng thời click & kéo chuột ở nửa phải màn hình
        Assert.True(mgr.TryTouchDown(TouchSourceId.DirectMouse, 0.80f, 0.50f));
        Assert.True(mgr.TryGetAssignedPointerId(TouchSourceId.DirectMouse, out ulong mousePtr));
        Assert.NotEqual(wasdPtr, mousePtr);
        Assert.Equal(2, mgr.ActivePointerCount);

        // Giả lập trôi qua 10ms => WASD hoàn tất nội suy lên đỉnh (0.18, 0.65)
        long t10ms = t0 + (long)(Stopwatch.Frequency * 0.015);
        wasd.UpdateAtTimestamp(t10ms);

        var lastWasdMove = sender.Events.Last(e => e.PointerId == wasdPtr && e.Action == AndroidMotionAction.Move);
        Assert.Equal((int)MathF.Round(0.18f * 1999), lastWasdMove.X);
        Assert.Equal((int)MathF.Round(0.65f * 999), lastWasdMove.Y);

        // Tắt WASD (F2) phải nhả riêng ngón WASD, giữ nguyên ngón chuột
        wasd.Enabled = false;
        Assert.False(wasd.IsTouchActive);
        Assert.Equal(1, mgr.ActivePointerCount);
        Assert.True(mgr.TryGetAssignedPointerId(TouchSourceId.DirectMouse, out _));
    }
}
