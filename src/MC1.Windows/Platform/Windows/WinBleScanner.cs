using MC1.Windows.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace MC1.Windows.Platform.Windows;

/// <summary>Finds MeshCore radios: already-paired LE devices plus live advertisements carrying the Nordic UART service.</summary>
public sealed class WinBleScanner : IBluetoothScanner
{
    public async Task<string?> CheckAvailabilityAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null) return L.T("No Bluetooth adapter was found on this PC.");
            if (!adapter.IsLowEnergySupported) return L.T("This PC's Bluetooth adapter doesn't support Bluetooth Low Energy.");
            var radio = await adapter.GetRadioAsync();
            if (radio is not null && radio.State != RadioState.On)
                return L.T("Bluetooth is turned off. Turn it on in Windows Settings → Bluetooth & devices, then scan again.");
            return null;
        }
        catch (Exception ex) { return L.F("Bluetooth is unavailable: {0}", ex.Message); }
    }

    private static bool LooksLikeMeshCore(string? name) =>
        !string.IsNullOrEmpty(name) && (name.StartsWith("MeshCore", StringComparison.OrdinalIgnoreCase) || name.Contains("MeshCore", StringComparison.OrdinalIgnoreCase));

    public async Task ScanAsync(Action<BleDeviceInfo> onFound, CancellationToken ct)
    {
        // Paired devices (they may not advertise while bonded to us).
        try
        {
            var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            foreach (var di in paired)
            {
                if (ct.IsCancellationRequested) break;
                if (!LooksLikeMeshCore(di.Name)) continue;
                try
                {
                    using var dev = await BluetoothLEDevice.FromIdAsync(di.Id);
                    if (dev is not null) onFound(new BleDeviceInfo(dev.BluetoothAddress.ToString("X12"), di.Name, null, true));
                }
                catch { /* stale pairing */ }
            }
        }
        catch { /* enumeration unavailable */ }

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, a) =>
        {
            var name = a.Advertisement.LocalName;
            var hasService = a.Advertisement.ServiceUuids.Contains(WinBleTransport.ServiceUuid);
            if (hasService || LooksLikeMeshCore(name))
                onFound(new BleDeviceInfo(a.BluetoothAddress.ToString("X12"), string.IsNullOrEmpty(name) ? L.T("MeshCore radio") : name, a.RawSignalStrengthInDBm, false));
        };
        var stopped = new TaskCompletionSource();
        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success) stopped.TrySetException(new InvalidOperationException(L.F("Bluetooth scan stopped: {0}", e.Error)));
            else stopped.TrySetResult();
        };
        watcher.Start();
        try
        {
            await Task.WhenAny(stopped.Task, Task.Delay(Timeout.Infinite, ct));
            if (stopped.Task.IsFaulted) await stopped.Task;
        }
        finally
        {
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
    }
}
