using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using MC1.Core.Services;
using MC1.Windows.Services;
using MeshCore;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace MC1.Windows.Platform.Windows;

/// <summary>MeshCore companion link over Bluetooth LE (Nordic UART service) using WinRT.</summary>
public sealed class WinBleTransport(ulong address, string? name, Func<string> pinProvider, AppLog log) : IMeshTransport
{
    public static readonly Guid ServiceUuid = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid WriteUuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid NotifyUuid = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _write;
    private GattCharacteristic? _notify;
    private bool _writeWithoutResponse;
    private volatile bool _connected;

    public string Description => name ?? address.ToString("X12");
    public bool IsConnected => _connected;
    public bool SupportsPipelinedReads => _writeWithoutResponse;
    public ChannelReader<byte[]> Received => _channel.Reader;

    public static ulong ParseAddress(string text)
    {
        var hex = text.Replace(":", "").Replace("-", "").Trim();
        return Convert.ToUInt64(hex, 16);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false)
                      ?? throw new TransportException(L.T("The radio wasn't found. Make sure it's switched on and in range."));
            log.Info("BLE", $"Device {_device.Name} ({address:X12}), paired={_device.DeviceInformation.Pairing.IsPaired}");

            if (!_device.DeviceInformation.Pairing.IsPaired) await PairAsync(ct).ConfigureAwait(false);

            try
            {
                _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
                _session.MaintainConnection = true;
            }
            catch (Exception ex) { log.Debug("BLE", "GattSession unavailable: " + ex.Message); }

            _service = await FindServiceAsync(ct).ConfigureAwait(false);
            _write = await FindCharacteristicAsync(_service, WriteUuid, ct).ConfigureAwait(false);
            _notify = await FindCharacteristicAsync(_service, NotifyUuid, ct).ConfigureAwait(false);
            _writeWithoutResponse = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

            _notify.ValueChanged += OnValueChanged;
            var status = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
            {
                // Usually "insufficient authentication": the bond is missing or stale. Pair again and retry once.
                log.Warn("BLE", $"Enable notifications failed ({status}); re-pairing");
                await PairAsync(ct, force: true).ConfigureAwait(false);
                status = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct).ConfigureAwait(false);
                if (status != GattCommunicationStatus.Success)
                    throw PairingError(L.F("The radio refused the connection ({0}). Remove it from Windows Bluetooth settings and pair again with the PIN shown on the radio.", status));
            }
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _connected = true;
            log.Info("BLE", $"Connected (write {(_writeWithoutResponse ? "without" : "with")} response, max PDU {_session?.MaxPduSize})");
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PairAsync(CancellationToken ct, bool force = false)
    {
        var info = _device!.DeviceInformation;
        if (force && info.Pairing.IsPaired)
        {
            try { await info.Pairing.UnpairAsync().AsTask(ct).ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (!info.Pairing.CanPair && !info.Pairing.IsPaired)
            log.Warn("BLE", "Device reports it can't pair; trying anyway");
        var custom = info.Pairing.Custom;
        var pin = pinProvider();
        void Handler(DeviceInformationCustomPairing s, DevicePairingRequestedEventArgs a)
        {
            log.Info("BLE", $"Pairing requested: {a.PairingKind}");
            if (a.PairingKind == DevicePairingKinds.ProvidePin) a.Accept(pin);
            else a.Accept();
        }
        custom.PairingRequested += Handler;
        try
        {
            var result = await custom.PairAsync(
                DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin,
                DevicePairingProtectionLevel.EncryptionAndAuthentication).AsTask(ct).ConfigureAwait(false);
            log.Info("BLE", "Pairing result: " + result.Status);
            if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                throw PairingError(result.Status switch
                {
                    DevicePairingResultStatus.AuthenticationFailure or DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.AuthenticationTimeout =>
                        L.F("Bluetooth pairing failed — wrong PIN? The app used {0}; set the PIN shown on the radio in the connect dialog.", pin),
                    DevicePairingResultStatus.ConnectionRejected => L.T("The radio rejected pairing. It may already be paired with another device."),
                    _ => L.F("Bluetooth pairing failed ({0}).", result.Status),
                });
        }
        finally { custom.PairingRequested -= Handler; }
    }

    /// <summary>A pairing failure, marked so the connect dialog adds its PIN hint whatever language the message is in.</summary>
    private static TransportException PairingError(string message)
    {
        var ex = new TransportException(message);
        ex.Data[BluetoothErrors.PairingKey] = true;
        return ex;
    }

    private async Task<GattDeviceService> FindServiceAsync(CancellationToken ct)
    {
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var r = await _device!.GetGattServicesForUuidAsync(ServiceUuid, mode).AsTask(ct).ConfigureAwait(false);
            if (r.Status == GattCommunicationStatus.Success && r.Services.Count > 0) return r.Services[0];
            log.Warn("BLE", $"Service lookup ({mode}) returned {r.Status}");
        }
        throw new TransportException(L.T("This Bluetooth device doesn't offer the MeshCore service. Is it running companion BLE firmware?"));
    }

    private static async Task<GattCharacteristic> FindCharacteristicAsync(GattDeviceService service, Guid uuid, CancellationToken ct)
    {
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var r = await service.GetCharacteristicsForUuidAsync(uuid, mode).AsTask(ct).ConfigureAwait(false);
            if (r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0) return r.Characteristics[0];
        }
        throw new TransportException(L.F("The radio's Bluetooth characteristic {0} is missing.", uuid));
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        if (data.Length > 0) _channel.Writer.TryWrite(data);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _connected)
        {
            log.Warn("BLE", "Link lost");
            _connected = false;
            _channel.Writer.TryComplete(new TransportException(L.T("Bluetooth connection lost")));
        }
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        var ch = _write ?? throw new TransportException(L.T("Not connected"));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var maxUnack = (_session?.MaxPduSize ?? 23) - 3;
            var option = _writeWithoutResponse && payload.Length <= maxUnack ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            var result = await ch.WriteValueWithResultAsync(payload.AsBuffer(), option).AsTask(ct).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
                throw new TransportException(result.ProtocolError is { } pe
                    ? L.F("Bluetooth write failed ({0}, error {1})", result.Status, pe)
                    : L.F("Bluetooth write failed ({0})", result.Status));
        }
        finally { _writeLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        _connected = false;
        await CleanupAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_notify is not null)
            {
                _notify.ValueChanged -= OnValueChanged;
                try { await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* the link may already be gone */ }
            }
        }
        catch { /* ignore */ }
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        try { _service?.Dispose(); } catch { /* ignore */ }
        try { _session?.Dispose(); } catch { /* ignore */ }
        try { _device?.Dispose(); } catch { /* ignore */ }
        _service = null;
        _session = null;
        _device = null;
        _write = null;
        _notify = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
