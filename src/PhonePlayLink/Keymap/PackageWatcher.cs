using PhonePlayLink.Adb;
using PhonePlayLink.Core;

namespace PhonePlayLink.Keymap;

/// <summary>
/// Theo dõi tên Android package đang chạy ở foreground mỗi 2.5 giây trên luồng nền (không bao giờ block render/input).
/// </summary>
public sealed class PackageWatcher : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly ProfileManager _profileManager;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private string? _currentPackage;
    private string? _lastAutoSwitchedPackage;

    public string? CurrentPackage => Volatile.Read(ref _currentPackage);
    public bool AutoSwitchEnabled { get; set; } = true;

    public event Action<string, string, KeymapProfile>? MatchedProfileFound;

    public PackageWatcher(AdbClient adb, string serial, ProfileManager profileManager)
    {
        _adb = adb;
        _serial = serial;
        _profileManager = profileManager;
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Chờ 1.5s sau khi mở session rồi mới bắt đầu poll nhẹ
        try
        {
            await Task.Delay(1500, ct);
            while (!ct.IsCancellationRequested)
            {
                string? pkg = null;
                try
                {
                    pkg = await _adb.GetForegroundPackageAsync(_serial, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Bỏ qua lỗi tạm thời khi dumpsys
                }

                if (!string.IsNullOrWhiteSpace(pkg))
                {
                    string? prev = Interlocked.Exchange(ref _currentPackage, pkg);
                    if (!string.Equals(prev, pkg, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("Package", $"Phát hiện ứng dụng đang mở: {pkg}");
                    }

                    if (AutoSwitchEnabled && !string.Equals(_lastAutoSwitchedPackage, pkg, StringComparison.OrdinalIgnoreCase))
                    {
                        var match = _profileManager.FindByPackageName(pkg);
                        if (match is { } found)
                        {
                            _lastAutoSwitchedPackage = pkg;
                            MatchedProfileFound?.Invoke(pkg, found.FilePath, found.Profile);
                        }
                    }
                }

                await Task.Delay(2500, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void NotifyManualProfileBound(string packageName)
    {
        _lastAutoSwitchedPackage = packageName;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loopTask; } catch { }
        _cts.Dispose();
    }
}
