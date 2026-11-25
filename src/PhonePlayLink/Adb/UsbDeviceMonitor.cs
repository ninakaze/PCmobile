using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PhonePlayLink.Core;

namespace PhonePlayLink.Adb;

/// <summary>
/// Theo dõi trạng thái cắm/rút thiết bị Android theo thời gian thực qua giao thức nhị phân ADB `host:track-devices-l` (TCP 127.0.0.1:5037).
/// Hoàn toàn event-driven, không spawn tiến trình adb.exe định kỳ gây giật frame.
/// </summary>
public sealed class UsbDeviceMonitor : IAsyncDisposable
{
    public const int AdbServerPort = 5037;

    private readonly AdbClient _adbClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _monitorTask;

    private volatile IReadOnlyList<AdbDeviceInfo> _devices = [];
    private volatile bool _isAdbConnected;

    public IReadOnlyList<AdbDeviceInfo> CurrentDevices => _devices;
    public bool IsAdbConnected => _isAdbConnected;

    public event Action<IReadOnlyList<AdbDeviceInfo>>? DevicesChanged;

    public UsbDeviceMonitor(AdbClient adbClient)
    {
        _adbClient = adbClient ?? throw new ArgumentNullException(nameof(adbClient));
        _monitorTask = Task.Run(() => RunMonitorLoopAsync(_cts.Token));
    }

    public static byte[] BuildAdbServiceRequest(string service)
    {
        byte[] payload = Encoding.ASCII.GetBytes(service);
        string prefix = payload.Length.ToString("x4", CultureInfo.InvariantCulture);
        byte[] req = new byte[4 + payload.Length];
        Encoding.ASCII.GetBytes(prefix, req.AsSpan(0, 4));
        payload.CopyTo(req, 4);
        return req;
    }

    public static IReadOnlyList<AdbDeviceInfo> ParseTrackDevicesPayload(string payload)
    {
        var result = new List<AdbDeviceInfo>();
        if (string.IsNullOrWhiteSpace(payload))
            return result;

        using var reader = new StringReader(payload);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;

            string serial = tokens[0];
            string state = tokens[1];
            string? product = null;
            string? model = null;
            string? device = null;
            string? transportId = null;

            for (int i = 2; i < tokens.Length; i++)
            {
                int colon = tokens[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = tokens[i][..colon];
                string val = tokens[i][(colon + 1)..];
                switch (key)
                {
                    case "product": product = val; break;
                    case "model": model = val; break;
                    case "device": device = val; break;
                    case "transport_id": transportId = val; break;
                }
            }

            result.Add(new AdbDeviceInfo(serial, state, product, model, device, transportId));
        }

        // Ưu tiên thiết bị USB lên trước
        result.Sort((a, b) =>
        {
            int usbCmp = b.IsUsb.CompareTo(a.IsUsb);
            return usbCmp != 0 ? usbCmp : string.Compare(a.Serial, b.Serial, StringComparison.Ordinal);
        });

        return result;
    }

    private async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, AdbServerPort, ct);
                }
                catch (SocketException)
                {
                    _isAdbConnected = false;
                    Logger.Info("USB", "ADB daemon chưa chạy, đang khởi động adb start-server...");
                    await _adbClient.EnsureServerRunningAsync(ct);
                    await socket.ConnectAsync(IPAddress.Loopback, AdbServerPort, ct);
                }

                socket.NoDelay = true;
                using var stream = new NetworkStream(socket, ownsSocket: false);

                if (!await TryStartTrackServiceAsync(stream, "host:track-devices-l", ct))
                {
                    // Đóng và thử fallback host:track-devices chuẩn trên socket mới
                    socket.Dispose();
                    using var fallbackSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await fallbackSock.ConnectAsync(IPAddress.Loopback, AdbServerPort, ct);
                    using var fallbackStream = new NetworkStream(fallbackSock, ownsSocket: false);
                    if (!await TryStartTrackServiceAsync(fallbackStream, "host:track-devices", ct))
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    await ReadUpdatesLoopAsync(fallbackStream, ct);
                }
                else
                {
                    await ReadUpdatesLoopAsync(stream, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _isAdbConnected = false;
                Logger.Warn("USB", $"Mất kết nối ADB track-devices: {ex.Message}. Đang thử kết nối lại...");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReadUpdatesLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        _isAdbConnected = true;
        Logger.Ok("USB", "Đã kết nối UsbDeviceMonitor (host:track-devices)");

        byte[] lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, lenBuf, ct))
                break;

            string hex = Encoding.ASCII.GetString(lenBuf);
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int len) || len < 0 || len > 1024 * 1024)
                throw new InvalidDataException($"Header track-devices không hợp lệ: '{hex}'");

            string payload = string.Empty;
            if (len > 0)
            {
                byte[] buf = new byte[len];
                if (!await ReadExactAsync(stream, buf, ct))
                    break;
                payload = Encoding.UTF8.GetString(buf);
            }

            IReadOnlyList<AdbDeviceInfo> parsed = ParseTrackDevicesPayload(payload);
            _devices = parsed;
            DevicesChanged?.Invoke(parsed);
        }
    }

    private static async Task<bool> TryStartTrackServiceAsync(NetworkStream stream, string service, CancellationToken ct)
    {
        byte[] req = BuildAdbServiceRequest(service);
        await stream.WriteAsync(req, ct);

        byte[] statusBuf = new byte[4];
        if (!await ReadExactAsync(stream, statusBuf, ct))
            return false;

        string status = Encoding.ASCII.GetString(statusBuf);
        if (status == "OKAY")
            return true;

        // Nếu FAIL, đọc error msg nếu có rồi trả false
        byte[] lenBuf = new byte[4];
        if (await ReadExactAsync(stream, lenBuf, ct) &&
            int.TryParse(Encoding.ASCII.GetString(lenBuf), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int errLen) &&
            errLen > 0 && errLen < 4096)
        {
            byte[] errBuf = new byte[errLen];
            await ReadExactAsync(stream, errBuf, ct);
        }
        return false;
    }

    private static async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> dest, CancellationToken ct)
    {
        int total = 0;
        while (total < dest.Length)
        {
            int n = await stream.ReadAsync(dest[total..], ct);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _monitorTask; } catch { }
        _cts.Dispose();
    }
}
