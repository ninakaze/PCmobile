using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PhonePlayLink.Adb;
using PhonePlayLink.Core;

namespace PhonePlayLink.Scrcpy;

/// <summary>
/// Quản lý vòng đời scrcpy-server v5.0.1 trên thiết bị Android và 2 socket Video/Audio độc lập qua adb forward.
/// </summary>
public sealed class ScrcpySession : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly string _scrcpyServerPath;
    private readonly AppConfig _config;

    private Process? _serverProcess;
    private int _forwardedPort;
    private bool _tunnelActive;

    public string DeviceName { get; private set; } = string.Empty;
    public uint VideoCodecId { get; private set; }
    public ScrcpySessionMeta InitialVideoSession { get; private set; }
    public uint AudioCodecId { get; private set; }
    public bool AudioEnabled { get; private set; }

    public Socket? VideoSocket { get; private set; }
    public NetworkStream? VideoStream { get; private set; }

    public Socket? AudioSocket { get; private set; }
    public NetworkStream? AudioStream { get; private set; }

    public Socket? ControlSocket { get; private set; }
    public NetworkStream? ControlStream { get; private set; }

    public ScrcpySession(AdbClient adb, string serial, string scrcpyServerPath, AppConfig config)
    {
        _adb = adb;
        _serial = serial;
        _scrcpyServerPath = scrcpyServerPath;
        _config = config;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        int scid = Random.Shared.Next(1, int.MaxValue);
        string scidHex = scid.ToString("x8", CultureInfo.InvariantCulture);
        string socketName = ScrcpyConstants.SocketNamePrefix + scidHex;

        Logger.Info("Session", $"Đang đẩy scrcpy-server v{ScrcpyConstants.ServerVersion} lên {_serial}...");
        await _adb.PushFileAsync(_serial, _scrcpyServerPath, ScrcpyConstants.DeviceServerPath, ct);

        _forwardedPort = await _adb.ForwardAbstractSocketAsync(_serial, socketName, ct);
        _tunnelActive = true;
        Logger.Info("Session", $"Đã mở tunnel adb forward tcp:{_forwardedPort} -> localabstract:{socketName}");

        var serverArgs = BuildServerArgs(scidHex);
        _serverProcess = _adb.StartServerProcess(_serial, serverArgs);

        try
        {
            // 1. Kết nối Video Socket (socket đầu tiên nhận 1 dummy byte khi server accept)
            VideoSocket = await ConnectFirstSocketWithRetryAsync(_forwardedPort, _serverProcess, ct);
            VideoSocket.NoDelay = true;
            VideoSocket.ReceiveBufferSize = 1024 * 1024;
            VideoStream = new NetworkStream(VideoSocket, ownsSocket: false);

            // 2. Nếu bật Audio, kết nối Audio Socket (không có dummy byte)
            if (_config.EnableAudio)
            {
                AudioSocket = await ConnectSubsequentSocketAsync(_forwardedPort, ct);
                AudioSocket.NoDelay = true;
                AudioSocket.ReceiveBufferSize = 256 * 1024;
                AudioStream = new NetworkStream(AudioSocket, ownsSocket: false);
            }

            // 3. Kết nối Control Socket (socket thứ 3 trong thứ tự video -> audio -> control của scrcpy v5.0.1)
            ControlSocket = await ConnectSubsequentSocketAsync(_forwardedPort, ct);
            ControlSocket.NoDelay = true;
            ControlSocket.SendBufferSize = 64 * 1024;
            ControlStream = new NetworkStream(ControlSocket, ownsSocket: false);
        }
        finally
        {
            // Tunnel chỉ dùng khi bắt tay kết nối; đóng ngay sau khi các socket đã thiết lập
            if (_tunnelActive)
            {
                await _adb.RemoveForwardAsync(_serial, _forwardedPort);
                _tunnelActive = false;
            }
        }

        // 3. Đọc 64-byte Device Name trên socket đầu tiên
        DeviceName = await ScrcpyDemuxer.ReadDeviceNameAsync(VideoStream!, ct);
        Logger.Ok("Session", $"Đã kết nối thiết bị: {DeviceName}");

        // 4. Đọc Video Codec ID & Session Header ban đầu (kích thước thực tế màn hình)
        VideoCodecId = await ScrcpyDemuxer.ReadCodecIdAsync(VideoStream!, ct);
        if (VideoCodecId is ScrcpyConstants.CodecIdDisabled or ScrcpyConstants.CodecIdError)
        {
            throw new InvalidOperationException($"scrcpy-server báo lỗi khởi tạo video stream (code={VideoCodecId}).");
        }

        InitialVideoSession = await ScrcpyDemuxer.ReadInitialVideoSessionAsync(VideoStream!, ct);
        Logger.Ok("Session", $"Video stream: codec=0x{VideoCodecId:X8}, resolution={InitialVideoSession.Width}x{InitialVideoSession.Height}");

        // 5. Đọc Audio Codec ID nếu có
        if (AudioStream is not null)
        {
            AudioCodecId = await ScrcpyDemuxer.ReadCodecIdAsync(AudioStream, ct);
            if (AudioCodecId == ScrcpyConstants.CodecIdDisabled)
            {
                Logger.Warn("Session", "Thiết bị từ chối/tắt audio stream (Android < 11 hoặc app chặn audio capture). Tiếp tục với Video-only.");
                AudioEnabled = false;
                await AudioStream.DisposeAsync();
                AudioSocket?.Dispose();
                AudioStream = null;
                AudioSocket = null;
            }
            else if (AudioCodecId == ScrcpyConstants.CodecIdError)
            {
                Logger.Warn("Session", "Lỗi cấu hình audio trên thiết bị. Tiếp tục với Video-only.");
                AudioEnabled = false;
            }
            else
            {
                AudioEnabled = true;
                Logger.Ok("Session", $"Audio stream: codec=0x{AudioCodecId:X8} (48kHz Stereo)");
            }
        }
    }

    private List<string> BuildServerArgs(string scidHex)
    {
        var args = new List<string>
        {
            $"CLASSPATH={ScrcpyConstants.DeviceServerPath}",
            "app_process",
            "/",
            "com.genymobile.scrcpy.Server",
            ScrcpyConstants.ServerVersion,
            $"scid={scidHex}",
            "log_level=info",
            "tunnel_forward=true",
            "video=true",
            $"video_codec={_config.VideoCodec}",
            $"video_bit_rate={_config.VideoBitRate}",
            "control=true",
            "stay_awake=true",
            "clipboard_autosync=false"
        };

        if (_config.MaxSize > 0)
            args.Add($"max_size={_config.MaxSize}");

        if (_config.MaxFps > 0)
            args.Add($"max_fps={_config.MaxFps.ToString(CultureInfo.InvariantCulture)}");

        if (_config.EnableAudio)
        {
            args.Add("audio=true");
            args.Add("audio_codec=opus");
            args.Add($"audio_bit_rate={_config.AudioBitRate}");
        }
        else
        {
            args.Add("audio=false");
        }

        return args;
    }

    private static async Task<Socket> ConnectFirstSocketWithRetryAsync(int port, Process serverProcess, CancellationToken ct)
    {
        const int maxAttempts = 100; // ~5s
        byte[] dummy = new byte[1];

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (serverProcess.HasExited)
            {
                throw new InvalidOperationException($"scrcpy-server thoát sớm với mã lỗi {serverProcess.ExitCode}.");
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                // Đọc 1 dummy byte xác nhận scrcpy-server đã thực sự accept phía sau adb forward
                int received = await socket.ReceiveAsync(dummy, SocketFlags.None, ct);
                if (received == 1)
                {
                    return socket;
                }
            }
            catch (SocketException)
            {
                // Chưa sẵn sàng -> retry
            }

            socket.Dispose();
            await Task.Delay(50, ct);
        }

        throw new TimeoutException("Hết thời gian chờ scrcpy-server mở socket kết nối trên Android.");
    }

    private static async Task<Socket> ConnectSubsequentSocketAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_tunnelActive)
        {
            await _adb.RemoveForwardAsync(_serial, _forwardedPort);
            _tunnelActive = false;
        }

        VideoStream?.Dispose();
        VideoSocket?.Dispose();
        AudioStream?.Dispose();
        AudioSocket?.Dispose();
        ControlStream?.Dispose();
        ControlSocket?.Dispose();

        if (_serverProcess is not null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                {
                    await _serverProcess.WaitForExitAsync(new CancellationTokenSource(400).Token);
                }
            }
            catch
            {
                try { _serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            _serverProcess.Dispose();
            _serverProcess = null;
        }
    }
}
