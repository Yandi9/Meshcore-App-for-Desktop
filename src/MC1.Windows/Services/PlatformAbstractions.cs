namespace MC1.Windows.Services;

public sealed record BleDeviceInfo(string Address, string Name, int? Rssi, bool IsPaired);

/// <summary>Scans for MeshCore companion radios over Bluetooth LE (implemented with WinRT on Windows).</summary>
public interface IBluetoothScanner
{
    /// <summary>Null when available; otherwise a user-facing reason (no adapter, radio off…).</summary>
    Task<string?> CheckAvailabilityAsync();

    /// <summary>Scans until cancelled, reporting each device (repeatedly, with updated RSSI).</summary>
    Task ScanAsync(Action<BleDeviceInfo> onFound, CancellationToken ct);
}

/// <summary>Marks Bluetooth errors whose (possibly translated) message can't be recognised by its text.</summary>
public static class BluetoothErrors
{
    /// <summary><see cref="Exception.Data"/> key set on pairing failures, so the connect dialog adds its PIN hint.</summary>
    public const string PairingKey = "MeshCore.BluetoothPairing";
}

/// <summary>Optional friendly names for COM ports ("USB Serial Device (COM5)").</summary>
public static class SerialPortNames
{
    public static Func<IReadOnlyDictionary<string, string>>? Provider { get; set; }

    public static IReadOnlyDictionary<string, string> Get()
    {
        try { return Provider?.Invoke() ?? new Dictionary<string, string>(); }
        catch { return new Dictionary<string, string>(); }
    }
}
