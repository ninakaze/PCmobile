using System.Runtime.InteropServices;

namespace PhonePlayLink.Core;

/// <summary>
/// Tối ưu lịch trình hệ điều hành Windows để đạt độ trễ siêu thấp:
/// - timeBeginPeriod(1) / timeEndPeriod(1) (winmm.dll)
/// - MMCSS AvSetMmThreadCharacteristicsW("Games") (avrt.dll)
/// - MsgWaitForMultipleObjectsEx (user32.dll) cho Event-Driven Render & Input Pacing
/// </summary>
public static partial class NativeTiming
{
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [LibraryImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx")]
    private static unsafe partial uint MsgWaitForMultipleObjectsEx(
        uint nCount,
        IntPtr* pHandles,
        uint dwMilliseconds,
        uint dwWakeMask,
        uint dwFlags);

    /// <summary>
    /// Ngủ ở kernel không tốn CPU và tỉnh dậy tức thì (<0.1ms) ngay khi có frame video mới HOẶC có input chuột/bàn phím/window event.
    /// </summary>
    public static unsafe void WaitForFrameOrWindowInput(WaitHandle frameAvailableHandle, int maxTimeoutMs)
    {
        IntPtr handle = frameAvailableHandle.SafeWaitHandle.DangerousGetHandle();
        MsgWaitForMultipleObjectsEx(1, &handle, (uint)Math.Max(0, maxTimeoutMs), QsAllInput, MwmoInputAvailable);
    }

    public sealed class HighResolutionTimerScope : IDisposable
    {
        private bool _active;

        public HighResolutionTimerScope()
        {
            _active = TimeBeginPeriod(1) == 0;
            if (_active)
                Logger.Ok("Timing", "Đã kích hoạt Windows High-Resolution Timer (1ms)");
        }

        public void Dispose()
        {
            if (_active)
            {
                TimeEndPeriod(1);
                _active = false;
            }
        }
    }

    public sealed class MmcssThreadScope : IDisposable
    {
        private IntPtr _handle;

        public MmcssThreadScope(string taskName = "Games")
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch
            {
            }

            try
            {
                uint taskIndex = 0;
                _handle = AvSetMmThreadCharacteristicsW(taskName, ref taskIndex);
            }
            catch
            {
                _handle = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                try { AvRevertMmThreadCharacteristics(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
    }
}
