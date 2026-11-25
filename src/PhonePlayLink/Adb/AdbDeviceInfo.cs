namespace PhonePlayLink.Adb;

public sealed record AdbDeviceInfo(
    string Serial,
    string State,
    string? Product,
    string? Model,
    string? Device,
    string? TransportId)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public bool IsUsb => !Serial.Contains(':', StringComparison.Ordinal);

    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model.Replace('_', ' ')} ({Serial})";
}
