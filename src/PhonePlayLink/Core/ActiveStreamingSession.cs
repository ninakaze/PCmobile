using System.Collections.Concurrent;
using System.Diagnostics;
using PhonePlayLink.Adb;
using PhonePlayLink.Input;
using PhonePlayLink.Keymap;
using PhonePlayLink.Media;
using PhonePlayLink.Rendering;
using PhonePlayLink.Scrcpy;
using PhonePlayLink.UI;

namespace PhonePlayLink.Core;

/// <summary>
/// Quản lý toàn bộ vòng đời của một phiên stream với thiết bị Android đang kết nối.
/// Hỗ trợ ngắt kết nối sạch khi rút cáp (Plug & Play) hoặc khởi động lại stream khi đổi Settings.
/// </summary>
public sealed class ActiveStreamingSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly ScrcpySession _scrcpySession;
    private readonly VideoDecoder _videoDecoder;
    private readonly AudioDecoder? _audioDecoder;
    private readonly AudioPlayer? _audioPlayer;
    private readonly PackageWatcher _packageWatcher;
    private readonly LatestFrameSlot _frameSlot;
    private readonly Task _videoTask;
    private readonly Task? _audioTask;
    private readonly ConcurrentQueue<(string Path, KeymapProfile Profile)> _pendingProfileSwitches = new();

    private volatile bool _disconnected;
    private int _pendingVideoWidth;
    private int _pendingVideoHeight;
    private int _hasOrientationChange;
    private bool _disposed;

    public AdbDeviceInfo Device { get; }
    public string DeviceName => _scrcpySession.DeviceName;
    public ScrcpyControlClient ControlClient { get; }
    public MultiTouchPointerManager PointerManager { get; }
    public KeymapEngine KeymapEngine { get; }
    public KeymapEditorOverlay EditorOverlay { get; }
    public bool IsDisconnected => _disconnected;
    public double AudioQueueMs => _audioPlayer?.CurrentQueueMs ?? 0.0;
    public bool HasAudio => _audioPlayer is { IsReady: true };
    public bool IsAudioMuted => _audioPlayer?.IsMuted ?? false;

    private ActiveStreamingSession(
        AdbDeviceInfo device,
        ScrcpySession scrcpySession,
        VideoDecoder videoDecoder,
        AudioDecoder? audioDecoder,
        AudioPlayer? audioPlayer,
        ScrcpyControlClient controlClient,
        MultiTouchPointerManager pointerManager,
        KeymapEngine keymapEngine,
        PackageWatcher packageWatcher,
        KeymapEditorOverlay editorOverlay,
        LatestFrameSlot frameSlot,
        AppConfig config,
        LatencyStats stats)
    {
        Device = device;
        _scrcpySession = scrcpySession;
        _videoDecoder = videoDecoder;
        _audioDecoder = audioDecoder;
        _audioPlayer = audioPlayer;
        ControlClient = controlClient;
        PointerManager = pointerManager;
        KeymapEngine = keymapEngine;
        _packageWatcher = packageWatcher;
        EditorOverlay = editorOverlay;
        _frameSlot = frameSlot;

        _packageWatcher.MatchedProfileFound += (_, filePath, matchedProfile) =>
        {
            _pendingProfileSwitches.Enqueue((filePath, matchedProfile));
        };

        if (config.AutoTurnScreenOff)
        {
            ControlClient.SetDisplayPower(false);
        }

        _videoTask = Task.Run(() => RunVideoLoopAsync(stats, _sessionCts.Token));
        if (_scrcpySession.AudioEnabled && _scrcpySession.AudioStream is not null && _audioDecoder is not null && _audioPlayer is not null)
        {
            _audioTask = Task.Run(() => RunAudioLoopAsync(stats, _sessionCts.Token));
        }
    }

    public static async Task<ActiveStreamingSession> ConnectAsync(
        AdbClient adb,
        AdbDeviceInfo device,
        string scrcpyServerPath,
        AppConfig config,
        SdlWindowHost windowHost,
        D3D11VideoRenderer renderer,
        LatestFrameSlot frameSlot,
        ProfileManager profileManager,
        LatencyStats stats,
        CancellationToken ct)
    {
        stats.Reset();
        frameSlot.Clear();

        var scrcpySession = new ScrcpySession(adb, device.Serial, scrcpyServerPath, config);
        try
        {
            await scrcpySession.StartAsync(ct);

            var videoDecoder = new VideoDecoder(
                scrcpySession.VideoCodecId,
                renderer.DeviceNativePointer,
                renderer.ContextNativePointer,
                config.PreferHardwareDecode,
                stats);

            AudioPlayer? audioPlayer = scrcpySession.AudioEnabled ? new AudioPlayer(windowHost.SdlApi) : null;
            AudioDecoder? audioDecoder = scrcpySession.AudioEnabled ? new AudioDecoder(scrcpySession.AudioCodecId) : null;

            var controlClient = new ScrcpyControlClient(
                scrcpySession.ControlSocket!,
                scrcpySession.InitialVideoSession.Width,
                scrcpySession.InitialVideoSession.Height);
            var pointerManager = new MultiTouchPointerManager(controlClient);

            var (initialProfilePath, initialProfile) = profileManager.LoadInitialOrCreateDefault();
            var keymapEngine = new KeymapEngine(pointerManager, initialProfile)
            {
                GlobalMouseSensitivity = config.GlobalMouseSensitivity,
                OverrideAimToggleKey = config.DefaultAimToggleKey
            };
            keymapEngine.AimLockChanged += locked => windowHost.SetRelativeMouseLock(locked);

            var packageWatcher = new PackageWatcher(adb, device.Serial, profileManager);
            var editorOverlay = new KeymapEditorOverlay(keymapEngine, profileManager, initialProfilePath, packageWatcher);

            windowHost.AdaptSizeForVideoOrientation(
                scrcpySession.InitialVideoSession.Width,
                scrcpySession.InitialVideoSession.Height);
            renderer.Resize(windowHost.Width, windowHost.Height);

            return new ActiveStreamingSession(
                device,
                scrcpySession,
                videoDecoder,
                audioDecoder,
                audioPlayer,
                controlClient,
                pointerManager,
                keymapEngine,
                packageWatcher,
                editorOverlay,
                frameSlot,
                config,
                stats);
        }
        catch
        {
            await scrcpySession.DisposeAsync();
            throw;
        }
    }

    public void ProcessMainThreadUpdates(SdlWindowHost windowHost, D3D11VideoRenderer renderer, LatencyStats stats)
    {
        while (_pendingProfileSwitches.TryDequeue(out var sw))
        {
            if (!EditorOverlay.IsEditorOpen)
            {
                KeymapEngine.LoadProfile(sw.Profile);
                EditorOverlay.OnProfileSwitchedExternally(sw.Path, sw.Profile);
            }
        }

        if (Interlocked.Exchange(ref _hasOrientationChange, 0) == 1)
        {
            int newW = Volatile.Read(ref _pendingVideoWidth);
            int newH = Volatile.Read(ref _pendingVideoHeight);
            windowHost.AdaptSizeForVideoOrientation(newW, newH);
            renderer.Resize(windowHost.Width, windowHost.Height);
        }

        stats.UpdateAudioQueueMs(AudioQueueMs);
    }

    public void TogglePhoneScreenPower()
    {
        ControlClient.SetDisplayPower(!ControlClient.IsScreenOn);
    }

    public void ToggleAudioMute()
    {
        if (_audioPlayer is not null)
        {
            _audioPlayer.IsMuted = !_audioPlayer.IsMuted;
        }
    }

    public void SendAndroidNavKey(int androidKeyCode)
    {
        ControlClient.SendAndroidKeyPress(androidKeyCode);
    }

    private async Task RunVideoLoopAsync(LatencyStats stats, CancellationToken ct)
    {
        using var mmcss = new NativeTiming.MmcssThreadScope("Games");
        try
        {
            var reader = new ScrcpyDemuxer.VideoReader(_scrcpySession.VideoStream!, _scrcpySession.VideoCodecId);
            while (!ct.IsCancellationRequested)
            {
                using ScrcpyVideoPacket packet = await reader.ReadNextAsync(ct);
                if (packet.Kind == ScrcpyPacketKind.EndOfStream)
                {
                    Logger.Warn("Video", $"Luồng video từ {Device.DisplayName} đã kết thúc.");
                    break;
                }

                if (packet.Kind == ScrcpyPacketKind.SessionChanged)
                {
                    Logger.Info("Video", $"Đổi kích thước/xoay màn hình: {packet.SessionMeta.Width}x{packet.SessionMeta.Height}");
                    _videoDecoder.ResetForSessionChange();
                    KeymapEngine.ReleaseAllInputs();
                    PointerManager.OnScreenSizeChanged(packet.SessionMeta.Width, packet.SessionMeta.Height);
                    Volatile.Write(ref _pendingVideoWidth, packet.SessionMeta.Width);
                    Volatile.Write(ref _pendingVideoHeight, packet.SessionMeta.Height);
                    Volatile.Write(ref _hasOrientationChange, 1);
                    _frameSlot.SignalWakeup();
                    continue;
                }

                long recvTs = Stopwatch.GetTimestamp();
                _videoDecoder.DecodePacket(packet.Payload, packet.Pts, packet.IsKeyFrame, recvTs, _frameSlot);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Logger.Warn("Video", $"Ngắt luồng video: {ex.Message}");
        }
        finally
        {
            _disconnected = true;
            _frameSlot.SignalWakeup();
        }
    }

    private async Task RunAudioLoopAsync(LatencyStats stats, CancellationToken ct)
    {
        try
        {
            var reader = new ScrcpyDemuxer.AudioReader(_scrcpySession.AudioStream!);
            while (!ct.IsCancellationRequested)
            {
                using ScrcpyAudioPacket packet = await reader.ReadNextAsync(ct);
                if (packet.IsEndOfStream)
                    break;

                DecodeAndQueueAudio(packet, stats);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Logger.Warn("Audio", $"Luồng audio kết thúc: {ex.Message}");
        }
    }

    private void DecodeAndQueueAudio(ScrcpyAudioPacket packet, LatencyStats stats)
    {
        if (_audioDecoder is null || _audioPlayer is null)
            return;

        if (_audioDecoder.DecodePacket(packet.Payload, packet.IsConfig, out ReadOnlySpan<byte> pcm) > 0)
        {
            _audioPlayer.EnqueuePcm(pcm);
            stats.UpdateAudioQueueMs(_audioPlayer.CurrentQueueMs);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            KeymapEngine.ReleaseAllInputs();
            if (!ControlClient.IsScreenOn)
            {
                // Tự động bật lại màn hình điện thoại khi ngắt kết nối sạch hoặc thoát app
                ControlClient.SetDisplayPower(true);
            }
        }
        catch
        {
        }

        _sessionCts.Cancel();
        await _packageWatcher.DisposeAsync();

        // Đóng socket trước để unblock mọi ReadAsync trên videoTask/audioTask
        await _scrcpySession.DisposeAsync();

        try { await _videoTask; } catch { }
        if (_audioTask is not null)
        {
            try { await _audioTask; } catch { }
        }

        ControlClient.Dispose();
        PointerManager.Dispose();
        _audioPlayer?.Dispose();
        _audioDecoder?.Dispose();
        _videoDecoder.Dispose();
        _frameSlot.Clear();
        _sessionCts.Dispose();
    }
}
