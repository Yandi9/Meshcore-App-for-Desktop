using MC1.Core.Data;
using MC1.Core.Models;
using MC1.Core.Simulation;
using MeshCore;

namespace MC1.Core.Services;

public enum LinkStatus { Disconnected, Connecting, Syncing, Ready, Reconnecting, Failed }

public enum DataKind { Contacts, Conversations, Messages, Channels, Rooms, RoomMessages, RxLog, Discovered, Radio, Reactions, Repeats, Sessions, TracePaths, Snapshots }

public sealed record DataChange(DataKind Kind, string? Key = null, string? ItemId = null);

/// <param name="Phase">"Contacts", "Channels" or "Messages" (an identifier; use <see cref="Description"/> to show it).</param>
public sealed record SyncProgress(string Phase, int Current, int Total)
{
    /// <summary>The status line for this phase ("Syncing contacts…"), in the app's language.</summary>
    public string Description => Phase switch
    {
        "Contacts" => L.T("Syncing contacts…"),
        "Channels" => L.T("Syncing channels…"),
        "Messages" => L.T("Syncing messages…"),
        _ => L.T("Syncing…"),
    };
}

/// <summary>
/// The application core: owns the database, settings, the live radio session and all services.
/// Equivalent of the iOS ServiceContainer + ConnectionManager + SyncCoordinator.
/// </summary>
public sealed partial class MeshApp : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _reconnectCts;
    private Task? _eventLoop;
    private Timer? _batteryTimer;
    private bool _userDisconnect;

    public MeshApp(string? dataRoot = null, INotifier? notifier = null)
    {
        if (dataRoot is not null) AppPaths.Root = dataRoot;
        Directory.CreateDirectory(AppPaths.Root);
        Log = new AppLog();
        Settings = new SettingsStore(AppPaths.Settings);
        Log.DebugEnabled = Settings.Current.DebugLogging;
        Log.PruneOldFiles();
        Secrets = new SecretStore(AppPaths.Secrets);
        Db = new AppDatabase(AppPaths.Database);
        Notifier = notifier ?? new NullNotifier();
        Messages = new MessageService(this);
        Contacts = new ContactService(this);
        Channels = new ChannelService(this);
        Remote = new RemoteNodeService(this);
        Rooms = new RoomService(this);
        RxLog = new RxLogService(this);
        Device = new DeviceService(this);
        Tools = new ToolsService(this);
        NodeConfig = new NodeConfigService(this);
        Backup = new BackupService(this);
        PendingConfig = new PendingConfigService(this);
        Log.Info("App", $"MeshCore for Windows started. Data folder: {AppPaths.Root}");
    }

    public AppLog Log { get; }
    public SettingsStore Settings { get; }
    public SecretStore Secrets { get; }
    public AppDatabase Db { get; }
    public INotifier Notifier { get; set; }

    public MessageService Messages { get; }
    public ContactService Contacts { get; }
    public ChannelService Channels { get; }
    public RemoteNodeService Remote { get; }
    public RoomService Rooms { get; }
    public RxLogService RxLog { get; }
    public DeviceService Device { get; }
    public ToolsService Tools { get; }
    public NodeConfigService NodeConfig { get; }
    public BackupService Backup { get; }
    /// <summary>Radio settings/channels/contacts restored while disconnected, written when that radio connects.</summary>
    public PendingConfigService PendingConfig { get; }

    /// <summary>Factory for Bluetooth transports (provided by the Windows layer).</summary>
    public Func<ConnectionTarget, IMeshTransport>? BluetoothTransportFactory { get; set; }

    /// <summary>Overrides the demo radio (tests use a deterministic, quiet simulator).</summary>
    public Func<IMeshTransport>? SimulatorFactory { get; set; }

    public MeshCoreSession? Session { get; private set; }
    public ConnectionTarget? CurrentTarget { get; private set; }
    public RadioRecord? Radio { get; private set; }
    public SelfInfo? SelfInfo => Session?.SelfInfo ?? _lastSelfInfo;
    private SelfInfo? _lastSelfInfo;
    public DeviceCapabilities? Capabilities { get; private set; }
    public BatteryInfo? Battery { get; private set; }
    public LinkStatus Status { get; private set; } = LinkStatus.Disconnected;
    public string? StatusDetail { get; private set; }
    public SyncProgress? Sync { get; private set; }
    public bool ContactsFull { get; private set; }
    public DateTimeOffset? LastSyncAt { get; private set; }

    public string? RadioId => Radio?.Id;
    public bool IsReady => Status == LinkStatus.Ready && Session is { IsRunning: true };
    public bool IsConnected => Session is { IsRunning: true } && Status is LinkStatus.Ready or LinkStatus.Syncing;
    public string SelfName => SelfInfo?.Name ?? Radio?.Name ?? "";

    public event Action? StatusChanged;
    public event Action<DataChange>? DataChanged;

    public void Notify(DataKind kind, string? key = null, string? itemId = null)
    {
        try { DataChanged?.Invoke(new DataChange(kind, key, itemId)); }
        catch (Exception ex) { Log.Error("App", "DataChanged handler failed: " + ex.Message); }
    }

    private void SetStatus(LinkStatus s, string? detail = null)
    {
        Status = s;
        StatusDetail = detail;
        try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("App", "StatusChanged handler failed: " + ex.Message); }
    }

    internal void SetSync(SyncProgress? p)
    {
        Sync = p;
        try { StatusChanged?.Invoke(); } catch { /* ignore */ }
    }

    internal MeshCoreSession RequireSession() =>
        Session is { IsRunning: true } s ? s : throw new MeshCoreException(MeshCoreErrorKind.NotConnected);

    internal string RequireRadioId() => Radio?.Id ?? throw new MeshCoreException(MeshCoreErrorKind.NotConnected);

    // MARK: Connection lifecycle

    private IMeshTransport CreateTransport(ConnectionTarget t) => t.Kind switch
    {
        ConnectionKind.Serial => new SerialTransport(t.Address),
        ConnectionKind.Tcp => new TcpTransport(t.Address, t.Port),
        ConnectionKind.Bluetooth => BluetoothTransportFactory?.Invoke(t) ?? throw new NotSupportedException(L.T("Bluetooth is not available on this system.")),
        ConnectionKind.Simulator => SimulatorFactory?.Invoke() ?? new SimulatedRadio(),
        _ => throw new NotSupportedException(),
    };

    /// <summary>Connects to a radio and runs the initial sync. Throws on failure.</summary>
    public async Task ConnectAsync(ConnectionTarget target, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _reconnectCts?.Cancel();
            _userDisconnect = false;
            await TeardownSessionAsync(LinkStatus.Disconnected, disconnectTransport: true).ConfigureAwait(false);
            await ConnectCoreAsync(target, null, ct).ConfigureAwait(false);
        }
        finally { _connectLock.Release(); }
    }

    private async Task ConnectCoreAsync(ConnectionTarget target, int? reconnectAttempt, CancellationToken ct)
    {
        SetStatus(reconnectAttempt is null ? LinkStatus.Connecting : LinkStatus.Reconnecting, target.Describe());
        Log.Info("Connection", $"Connecting to {target.Describe()}");
        var transport = CreateTransport(target);
        var session = new MeshCoreSession(transport, SessionConfiguration.Default with { ClientIdentifier = "MC1W" });
        session.FrameTraced += (outgoing, frame) => Log.Debug("Frame", $"{(outgoing ? "→" : "←")} {frame.ToHex()}");
        try
        {
            await session.StartAsync(reconnectAttempt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Connection", $"Connect failed: {ex.Message}");
            try { await transport.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            SetStatus(LinkStatus.Failed, ex.Message);
            throw;
        }

        Session = session;
        CurrentTarget = target;
        _lastSelfInfo = session.SelfInfo;
        ContactsFull = false;
        _sessionCts = new CancellationTokenSource();
        var sessionToken = _sessionCts.Token;
        session.ConnectionStateChanged += s => OnSessionStateChanged(session, s);

        SetStatus(LinkStatus.Syncing, L.T("Reading radio info…"));
        try
        {
            Capabilities = await session.QueryDeviceAsync(ct).ConfigureAwait(false);
            var self = session.SelfInfo!;
            Radio = RegisterRadio(self, Capabilities, target);
            StoreRadioInfo();
            Settings.Update(s =>
            {
                s.LastConnection = target;
                s.RecentConnections.RemoveAll(r => r.Kind == target.Kind && r.Address == target.Address && r.Port == target.Port);
                s.RecentConnections.Insert(0, target);
                if (s.RecentConnections.Count > 8) s.RecentConnections.RemoveRange(8, s.RecentConnections.Count - 8);
            });
            Notify(DataKind.Radio);
            await SyncDeviceTimeAsync(session, ct).ConfigureAwait(false);

            // Event loop first so nothing pushed during sync is lost.
            _eventLoop = Task.Run(() => EventLoopAsync(session, sessionToken));
            await RunInitialSyncAsync(session, ct).ConfigureAwait(false);
            session.StartAutoMessageFetching();
            session.RequestMessageDrain();
            LastSyncAt = DateTimeOffset.Now;
            SetSync(null);
            SetStatus(LinkStatus.Ready, target.Describe());
            Log.Info("Connection", $"Ready: {self.Name} ({Capabilities.Model} {Capabilities.Version})");
            StartBatteryPolling(session);
            Messages.ResumePendingAfterConnect();
        }
        catch (Exception ex)
        {
            Log.Error("Connection", $"Initial sync failed: {ex.Message}");
            SetSync(null);
            if (session.IsRunning)
            {
                // The link is up; keep it usable even if part of the sync failed.
                session.StartAutoMessageFetching();
                SetStatus(LinkStatus.Ready, L.F("Connected (sync incomplete: {0})", ex.Message));
                StartBatteryPolling(session);
            }
            else
            {
                await TeardownSessionAsync(LinkStatus.Failed, true, ex.Message).ConfigureAwait(false);
                throw;
            }
        }
    }

    private RadioRecord RegisterRadio(SelfInfo self, DeviceCapabilities caps, ConnectionTarget target)
    {
        var id = self.PublicKey.ToHex();
        var r = Db.GetRadio(id) ?? new RadioRecord { Id = id };
        r.Name = self.Name;
        r.PublicKey = self.PublicKey;
        r.Model = caps.Model;
        r.FirmwareVersion = caps.Version;
        r.FirmwareBuild = caps.FirmwareBuild;
        r.FirmwareCode = caps.FirmwareVersion;
        r.MaxContacts = caps.MaxContacts;
        r.MaxChannels = caps.MaxChannels;
        r.LastConnected = Time.Now();
        r.LastConnection = System.Text.Json.JsonSerializer.Serialize(target);
        r.OcvPreset ??= Utilities.OcvPresets.ForManufacturer(caps.Model);
        Db.UpsertRadio(r);
        return r;
    }

    private async Task SyncDeviceTimeAsync(MeshCoreSession session, CancellationToken ct)
    {
        try
        {
            var deviceTime = await session.GetTimeAsync(ct).ConfigureAwait(false);
            var drift = Math.Abs((deviceTime - DateTimeOffset.UtcNow).TotalSeconds);
            if (drift > 5)
            {
                await session.SetTimeAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                Log.Info("Connection", $"Synced radio clock (was off by {drift:0}s)");
            }
        }
        catch (Exception ex) { Log.Warn("Connection", "Could not sync radio clock: " + ex.Message); }
    }

    private void StartBatteryPolling(MeshCoreSession session)
    {
        _batteryTimer?.Dispose();
        _batteryTimer = new Timer(async _ =>
        {
            if (!session.IsRunning) return;
            await RefreshBatteryAsync().ConfigureAwait(false);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
    }

    public async Task RefreshBatteryAsync()
    {
        try
        {
            var s = Session;
            if (s is null || !s.IsRunning) return;
            var previous = Battery;
            Battery = await s.GetBatteryAsync().ConfigureAwait(false);
            Notify(DataKind.Radio);
            try { StatusChanged?.Invoke(); } catch { /* ignore */ }
            var pct = BatteryPercent;
            if (Settings.Current.NotifyLowBattery && pct is <= 15 && (previous is null || Utilities.OcvPresets.Percent(previous.Level, OcvCurve) > 15))
                Notifier.ShowInfo(L.T("Radio battery low"), L.F("{0} is at {1}%.", SelfName, pct));
        }
        catch (Exception ex) { Log.Debug("Battery", ex.Message); }
    }

    public int[]? OcvCurve =>
        Radio?.OcvPreset == "custom" ? Utilities.OcvPresets.ParseCustom(Radio.CustomOcv) : Utilities.OcvPresets.Find(Radio?.OcvPreset)?.Curve ?? Utilities.OcvPresets.Default.Curve;

    public int? BatteryPercent => Battery is { Level: > 0 } b ? Utilities.OcvPresets.Percent(b.Level, OcvCurve) : null;

    private void OnSessionStateChanged(MeshCoreSession session, ConnectionState state)
    {
        if (state.Kind != ConnectionStateKind.Disconnected || !ReferenceEquals(session, Session) || _userDisconnect) return;
        Log.Warn("Connection", "Radio connection lost");
        _ = Task.Run(async () =>
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(session, Session)) return;
                var target = CurrentTarget;
                await TeardownSessionAsync(LinkStatus.Disconnected, disconnectTransport: true, L.T("Connection lost")).ConfigureAwait(false);
                if (target is not null && Settings.Current.AutoReconnect && !_userDisconnect) StartReconnectLoop(target);
            }
            finally { _connectLock.Release(); }
        });
    }

    private void StartReconnectLoop(ConnectionTarget target)
    {
        _reconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        _ = Task.Run(async () =>
        {
            var attempt = 0;
            while (!cts.IsCancellationRequested)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                SetStatus(LinkStatus.Reconnecting, L.F("Reconnecting (attempt {0}) in {1:0}s…", attempt, delay.TotalSeconds));
                try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                await _connectLock.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (cts.IsCancellationRequested || Session is { IsRunning: true }) return;
                    await ConnectCoreAsync(target, attempt, cts.Token).ConfigureAwait(false);
                    Log.Info("Connection", "Reconnected");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Warn("Connection", $"Reconnect attempt {attempt} failed: {ex.Message}"); }
                finally { _connectLock.Release(); }
            }
        });
    }

    public void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        if (Status == LinkStatus.Reconnecting) SetStatus(LinkStatus.Disconnected);
    }

    public async Task DisconnectAsync()
    {
        _userDisconnect = true;
        _reconnectCts?.Cancel();
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try { await TeardownSessionAsync(LinkStatus.Disconnected, disconnectTransport: true).ConfigureAwait(false); }
        finally { _connectLock.Release(); }
    }

    private async Task TeardownSessionAsync(LinkStatus finalStatus, bool disconnectTransport, string? detail = null)
    {
        var session = Session;
        _batteryTimer?.Dispose();
        _batteryTimer = null;
        _sessionCts?.Cancel();
        Remote.OnDisconnected();
        Messages.OnDisconnected();
        Tools.OnDisconnected();
        if (session is not null)
        {
            Session = null;
            try { await session.StopAsync(disconnectTransport).ConfigureAwait(false); } catch { /* ignore */ }
            try { await session.Transport.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            if (Radio is not null) Db.MarkAllSessionsDisconnected(Radio.Id);
            Notify(DataKind.Sessions);
        }
        if (_eventLoop is not null)
        {
            try { await _eventLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
            _eventLoop = null;
        }
        Battery = null;
        SetSync(null);
        SetStatus(finalStatus, detail);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        Db.Checkpoint();
    }

    /// <summary>Forgets a radio and all of its local data.</summary>
    public void ForgetRadio(string radioId)
    {
        Db.DeleteRadioData(radioId);
        if (Radio?.Id == radioId && !IsConnected) Radio = null;
        Notify(DataKind.Radio);
        Notify(DataKind.Conversations);
        Notify(DataKind.Contacts);
    }

    /// <summary>Opens the last-used radio's data read-only while offline.</summary>
    public void LoadOfflineRadio()
    {
        if (Radio is not null) return;
        Radio = Db.GetRadios().FirstOrDefault();
        if (Radio is not null) Notify(DataKind.Radio);
    }

    /// <summary>Shows another known radio's data while disconnected.</summary>
    public void SetOfflineRadio(string radioId)
    {
        if (IsConnected || Db.GetRadio(radioId) is not { } r) return;
        Radio = r;
        Notify(DataKind.Radio);
        Notify(DataKind.Contacts);
        Notify(DataKind.Channels);
        Notify(DataKind.Conversations);
    }

    // MARK: Last known radio settings (so backups can be made while disconnected)

    private sealed record StoredRadioInfo(SelfInfo? Self, DeviceCapabilities? Capabilities);

    private static string RadioInfoPath(string radioId) => Path.Combine(AppPaths.Root, "radio-info", radioId + ".json");

    private void StoreRadioInfo()
    {
        try
        {
            if (Radio is null || _lastSelfInfo is null) return;
            var path = RadioInfoPath(Radio.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new StoredRadioInfo(_lastSelfInfo, Capabilities)));
        }
        catch (Exception ex) { Log.Debug("App", "Couldn't store radio info: " + ex.Message); }
    }

    /// <summary>The radio's settings from this session, or as they were the last time it was connected.</summary>
    public (SelfInfo? Self, DeviceCapabilities? Capabilities) GetRadioInfo(string radioId)
    {
        if (Radio?.Id == radioId && _lastSelfInfo is not null) return (_lastSelfInfo, Capabilities);
        try
        {
            var path = RadioInfoPath(radioId);
            if (File.Exists(path) && System.Text.Json.JsonSerializer.Deserialize<StoredRadioInfo>(File.ReadAllText(path)) is { } info)
                return (info.Self, info.Capabilities);
        }
        catch (Exception ex) { Log.Debug("App", "Couldn't read radio info: " + ex.Message); }
        return (null, null);
    }

    public void UpdateRadioRecord(Action<RadioRecord> change)
    {
        if (Radio is null) return;
        change(Radio);
        Db.UpsertRadio(Radio);
        Notify(DataKind.Radio);
    }

    internal void RefreshSelfInfo(SelfInfo info)
    {
        _lastSelfInfo = info;
        StoreRadioInfo();
        if (Radio is not null && Radio.Name != info.Name) { Radio.Name = info.Name; Db.UpsertRadio(Radio); }
        Notify(DataKind.Radio);
        try { StatusChanged?.Invoke(); } catch { /* ignore */ }
    }
}
