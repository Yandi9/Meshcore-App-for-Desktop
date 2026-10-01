using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Services;
using MC1.Windows.Services;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed record SerialPortRow(string Port, string Description)
{
    public string Title => Description.Length > 0 ? Description : Port;
}

public sealed partial class BleDeviceRow : ObservableObject
{
    public required string Address { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _signal = "";
    [ObservableProperty] private bool _isPaired;
    public string AddressText => Address.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => Address.Substring(i * 2, 2))) : Address;
}

public sealed record RecentConnectionRow(ConnectionTarget Target)
{
    // The demo radio's stored display name stays English ("Demo radio"); it's shown in the app's language.
    public string Title => Target.Kind == ConnectionKind.Simulator && Target.DisplayName is null or "Demo radio" ? L.T("Demo radio") : Target.DisplayName ?? Target.Address;
    public string Subtitle => Target.Describe();
    public string IconKey => Target.Kind switch
    {
        ConnectionKind.Bluetooth => "Icon.Bluetooth",
        ConnectionKind.Tcp => "Icon.Wifi",
        ConnectionKind.Serial => "Icon.Usb",
        _ => "Icon.CellphoneLink",
    };
}

public sealed partial class ConnectViewModel : ViewModelBase
{
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _connectCts;

    public ConnectViewModel()
    {
        foreach (var r in Core.Settings.Current.RecentConnections) Recent.Add(new RecentConnectionRow(r));
        HasRecent = Recent.Count > 0;
        RefreshPorts();
        BluetoothPin = Core.Settings.Current.BluetoothPin;
        var last = Core.Settings.Current.LastConnection;
        if (last is { Kind: ConnectionKind.Tcp }) { Host = last.Address; Port = last.Port.ToString(); }
        TabIndex = last?.Kind switch { ConnectionKind.Serial => 1, ConnectionKind.Tcp => 2, ConnectionKind.Simulator => 3, _ => 0 };
        BluetoothSupported = AppHost.Bluetooth is not null;
    }

    public ObservableCollection<SerialPortRow> Ports { get; } = new();
    public ObservableCollection<BleDeviceRow> BleDevices { get; } = new();
    public ObservableCollection<RecentConnectionRow> Recent { get; } = new();
    public Action? Close { get; set; }

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private bool _hasRecent;
    [ObservableProperty] private SerialPortRow? _selectedPort;
    [ObservableProperty] private BleDeviceRow? _selectedBle;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "5000";
    [ObservableProperty] private string _bluetoothPin = "123456";
    [ObservableProperty] private bool _bluetoothSupported;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _bleStatus;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;

    partial void OnTabIndexChanged(int value)
    {
        if (value == 0 && BleDevices.Count == 0 && !IsScanning) _ = Scan();
        if (value != 0) StopScan();
        if (value == 1) RefreshPorts();
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var names = SerialPortNames.Get();
        var selected = SelectedPort?.Port;
        Ports.Clear();
        foreach (var p in SerialTransport.AvailablePorts()) Ports.Add(new SerialPortRow(p, names.TryGetValue(p, out var d) ? d : ""));
        SelectedPort = Ports.FirstOrDefault(p => p.Port == selected) ?? Ports.FirstOrDefault(p => p.Description.Contains("USB", StringComparison.OrdinalIgnoreCase)) ?? Ports.FirstOrDefault();
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (AppHost.Bluetooth is not { } bt) { BleStatus = L.T("Bluetooth isn't supported in this build."); return; }
        StopScan();
        var reason = await bt.CheckAvailabilityAsync();
        if (reason is not null) { BleStatus = reason; return; }
        _scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IsScanning = true;
        var scanning = L.T("Scanning for MeshCore radios… make sure the radio is on and not connected to another app.");
        BleStatus = scanning;
        try
        {
            await bt.ScanAsync(d => Ui(() =>
            {
                var row = BleDevices.FirstOrDefault(x => x.Address == d.Address);
                if (row is null)
                {
                    row = new BleDeviceRow { Address = d.Address };
                    BleDevices.Add(row);
                    SelectedBle ??= row;
                }
                if (!string.IsNullOrWhiteSpace(d.Name)) row.Name = d.Name;
                if (d.Rssi is { } rssi) row.Signal = $"{rssi} dBm";
                row.IsPaired |= d.IsPaired;
            }), _scanCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { BleStatus = L.F("Scan failed: {0}", ex.Message); }
        finally
        {
            IsScanning = false;
            // Still showing the scanning message (not an error): replace it with the result.
            if (BleStatus == scanning)
                BleStatus = BleDevices.Count == 0 ? L.T("No MeshCore radios found. Is Bluetooth enabled on the radio?") : L.Plural(BleDevices.Count, "Found {0} radio.", "Found {0} radios.");
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
        _scanCts = null;
    }

    [RelayCommand]
    private Task ConnectBluetooth()
    {
        if (SelectedBle is not { } d) { Error = L.T("Pick a radio from the list."); return Task.CompletedTask; }
        if (BluetoothPin.Trim() != Core.Settings.Current.BluetoothPin) Core.Settings.Update(s => s.BluetoothPin = BluetoothPin.Trim());
        StopScan();
        return Connect(new ConnectionTarget(ConnectionKind.Bluetooth, d.Address, string.IsNullOrWhiteSpace(d.Name) ? d.AddressText : d.Name));
    }

    [RelayCommand]
    private Task ConnectSerial()
    {
        if (SelectedPort is not { } p) { Error = L.T("No serial port selected. Plug the radio in with a data USB cable and press Refresh."); return Task.CompletedTask; }
        return Connect(new ConnectionTarget(ConnectionKind.Serial, p.Port, p.Title));
    }

    [RelayCommand]
    private Task ConnectTcp()
    {
        var host = Host.Trim();
        if (host.Length == 0) { Error = L.T("Enter the radio's IP address or host name."); return Task.CompletedTask; }
        if (host.Contains(':') && !host.Contains("::") && int.TryParse(host[(host.LastIndexOf(':') + 1)..], out var embedded))
        {
            Port = embedded.ToString();
            host = host[..host.LastIndexOf(':')];
            Host = host;
        }
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535) { Error = L.T("Enter a valid port (default 5000)."); return Task.CompletedTask; }
        return Connect(new ConnectionTarget(ConnectionKind.Tcp, host, $"{host}:{port}", port));
    }

    // "Demo radio" is saved with the recent connections, so it stays English here (translated in RecentConnectionRow.Title).
    [RelayCommand]
    private Task ConnectDemo() => Connect(new ConnectionTarget(ConnectionKind.Simulator, "demo", "Demo radio"));

    [RelayCommand]
    private Task ConnectRecent(RecentConnectionRow row) => Connect(row.Target);

    [RelayCommand]
    private void RemoveRecent(RecentConnectionRow row)
    {
        Recent.Remove(row);
        HasRecent = Recent.Count > 0;
        Core.Settings.Update(s => s.RecentConnections.RemoveAll(r => r == row.Target));
    }

    private async Task Connect(ConnectionTarget target)
    {
        Error = null;
        IsConnecting = true;
        Status = L.F("Connecting to {0}…", target.Describe());
        _connectCts = new CancellationTokenSource();
        var closed = false;
        void OnStatus()
        {
            Ui(() =>
            {
                if (Core.Status == LinkStatus.Syncing) Status = L.T("Connected — syncing contacts and messages…");
                if (!closed && Core.Status is LinkStatus.Syncing or LinkStatus.Ready && Core.CurrentTarget == target)
                {
                    closed = true;
                    Close?.Invoke();
                }
            });
        }
        Core.StatusChanged += OnStatus;
        try
        {
            await Core.ConnectAsync(target, _connectCts.Token);
            if (!closed) { closed = true; Close?.Invoke(); }
        }
        catch (OperationCanceledException) { Status = null; }
        catch (Exception ex)
        {
            if (closed) AppHost.Main?.ShowToast(L.T("Connection problem"), ex.Message);
            else Error = Friendly(target, ex);
        }
        finally
        {
            Core.StatusChanged -= OnStatus;
            IsConnecting = false;
            if (!closed) Status = null;
        }
    }

    private static string Friendly(ConnectionTarget t, Exception ex)
    {
        var msg = ex.Message;
        return t.Kind switch
        {
            ConnectionKind.Serial when msg.Contains("denied", StringComparison.OrdinalIgnoreCase) =>
                L.F("{0} is in use by another program (another MeshCore app, a serial monitor or Arduino IDE). Close it and try again.", t.Address),
            ConnectionKind.Serial when ex is MeshCoreException { Kind: MeshCoreErrorKind.Timeout } =>
                L.T("The radio didn't answer. Make sure it runs MeshCore *companion USB* firmware (not repeater firmware)."),
            ConnectionKind.Tcp when ex is System.Net.Sockets.SocketException =>
                L.F("Couldn't reach {0}:{1}. Check the radio is on the same network and runs companion WiFi firmware.", t.Address, t.Port),
            // Pairing errors are marked by the Bluetooth transport (their text may be translated); "pair" catches untranslated ones.
            ConnectionKind.Bluetooth when ex.Data.Contains(BluetoothErrors.PairingKey) || msg.Contains("pair", StringComparison.OrdinalIgnoreCase) =>
                L.F("{0} Check the PIN shown on the radio's screen (default 123456).", msg),
            _ => msg,
        };
    }

    [RelayCommand]
    private void CancelConnect()
    {
        _connectCts?.Cancel();
        _ = Core.DisconnectAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        StopScan();
        Close?.Invoke();
    }
}
