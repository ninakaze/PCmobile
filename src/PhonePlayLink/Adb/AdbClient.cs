using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PhonePlayLink.Core;

namespace PhonePlayLink.Adb;

/// <summary>
/// Giao tiếp với adb.exe để phát hiện thiết bị USB, đẩy scrcpy-server, mở tunnel và chạy app_process.
/// </summary>
public sealed class AdbClient
{
    private readonly string _adbPath;

    public AdbClient(string adbPath)
    {
        if (!File.Exists(adbPath))
            throw new FileNotFoundException("Không tìm thấy adb.exe", adbPath);
        _adbPath = adbPath;
    }

    public async Task EnsureServerRunningAsync(CancellationToken ct = default)
    {
        var result = await RunCommandAsync(["start-server"], timeoutMs: 15000, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb start-server thất bại: {result.StdErr}");
        }
    }

    public async Task<IReadOnlyList<AdbDeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var result = await RunCommandAsync(["devices", "-l"], timeoutMs: 10000, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb devices thất bại: {result.StdErr}");
        }

        return ParseDevicesOutput(result.StdOut);
    }

    public static IReadOnlyList<AdbDeviceInfo> ParseDevicesOutput(string output)
    {
        var list = new List<AdbDeviceInfo>();
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

            list.Add(new AdbDeviceInfo(serial, state, product, model, device, transportId));
        }

        return list;
    }

    public async Task PushFileAsync(string serial, string localPath, string remotePath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("Không tìm thấy tệp để đẩy lên Android", localPath);

        var result = await RunCommandAsync(["-s", serial, "push", localPath, remotePath], timeoutMs: 30000, ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb push thất bại: {result.StdErr} {result.StdOut}");
        }
    }

    public async Task<int> ForwardAbstractSocketAsync(string serial, string abstractSocketName, CancellationToken ct = default)
    {
        int localPort = FindFreeTcpPort();
        var result = await RunCommandAsync(
            ["-s", serial, "forward", $"tcp:{localPort}", $"localabstract:{abstractSocketName}"],
            timeoutMs: 10000,
            ct);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb forward thất bại: {result.StdErr}");
        }

        return localPort;
    }

    public async Task RemoveForwardAsync(string serial, int localPort)
    {
        try
        {
            await RunCommandAsync(["-s", serial, "forward", "--remove", $"tcp:{localPort}"], timeoutMs: 5000, CancellationToken.None);
        }
        catch
        {
            // Bỏ qua lỗi cleanup tunnel khi thiết bị đã rút cáp
        }
    }

    public async Task<int> GetDeviceSdkVersionAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunCommandAsync(["-s", serial, "shell", "getprop", "ro.build.version.sdk"], timeoutMs: 5000, ct);
        if (result.ExitCode == 0 && int.TryParse(result.StdOut.Trim(), out int sdk))
        {
            return sdk;
        }
        return 0;
    }

    public async Task<string?> GetForegroundPackageAsync(string serial, CancellationToken ct = default)
    {
        var result = await RunCommandAsync(
            ["-s", serial, "shell", "dumpsys window windows | grep -E 'mCurrentFocus|mFocusedApp'"],
            timeoutMs: 3000,
            ct);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
        {
            string? pkg = ParseForegroundPackage(result.StdOut);
            if (!string.IsNullOrEmpty(pkg))
                return pkg;
        }

        var fallback = await RunCommandAsync(["-s", serial, "shell", "dumpsys activity activities | grep -E 'mResumedActivity|topResumedActivity'"], timeoutMs: 3000, ct);
        if (fallback.ExitCode == 0 && !string.IsNullOrWhiteSpace(fallback.StdOut))
        {
            return ParseForegroundPackage(fallback.StdOut);
        }

        return null;
    }

    public static string? ParseForegroundPackage(string dumpsysOutput)
    {
        using var reader = new StringReader(dumpsysOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains("mCurrentFocus", StringComparison.Ordinal) &&
                !line.Contains("mFocusedApp", StringComparison.Ordinal) &&
                !line.Contains("mResumedActivity", StringComparison.Ordinal) &&
                !line.Contains("topResumedActivity", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("=null", StringComparison.OrdinalIgnoreCase))
                continue;

            Match m = Regex.Match(line, @"\b([a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)+)/");
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }
        return null;
    }

    public Process StartServerProcess(string serial, IEnumerable<string> serverArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("shell");
        foreach (string arg in serverArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Logger.Info("scrcpy-srv", e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Logger.Warn("scrcpy-srv", e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException("Không thể khởi chạy tiến trình scrcpy-server qua adb.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
        IReadOnlyList<string> args,
        int timeoutMs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Lệnh adb quá thời gian ({timeoutMs}ms): {string.Join(' ', args)}");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr);
    }
}
