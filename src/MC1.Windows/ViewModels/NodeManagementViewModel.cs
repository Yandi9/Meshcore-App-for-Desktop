using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MC1.Windows.Controls;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed record NeighbourRow(string Name, string KeyText, string HeardText, double Snr, string SnrText, string Color);
public sealed record AclRow(string Name, string KeyText, string Permission);

/// <summary>Remote administration of a repeater, room server or sensor.</summary>
public sealed partial class NodeManagementViewModel : ViewModelBase
{
    private ContactRecord _contact;

    public NodeManagementViewModel(ContactRecord contact)
    {
        _contact = contact;
        Title = contact.DisplayName;
        IsRoom = contact.ContactType == ContactType.Room;
        IsRepeater = contact.ContactType == ContactType.Repeater;
        TypeText = Formatters.TypeName(contact.ContactType);
        IconKey = Formatters.ContactIcon(contact.ContactType);
        Emoji = Formatters.AvatarEmoji(contact.DisplayName);
        Color = Formatters.ContactColor(contact.ContactType);
        KeyText = Convert.ToHexString(contact.PublicKey);
        RefreshSession();
        LoadHistory();
        Action<string, bool> onSession = (_, _) => Ui(RefreshSession);
        Core.Remote.SessionStateChanged += onSession;
        Track(() => Core.Remote.SessionStateChanged -= onSession);
    }

    public string Title { get; }
    public string TypeText { get; }
    public string IconKey { get; }
    public string? Emoji { get; }
    public string Color { get; }
    public string KeyText { get; }
    public bool IsRoom { get; }
    public bool IsRepeater { get; }
    public Action? Close { get; set; }

    public ObservableCollection<DetailRow> StatusRows { get; } = new();
    public ObservableCollection<DetailRow> TelemetryRows { get; } = new();
    public ObservableCollection<NeighbourRow> Neighbours { get; } = new();
    public ObservableCollection<AclRow> Acl { get; } = new();
    public ObservableCollection<ChartSeries> BatterySeries { get; } = new();
    public ObservableCollection<ChartSeries> NoiseSeries { get; } = new();
    public ObservableCollection<ChartSeries> SnrSeries { get; } = new();
    public ObservableCollection<CliLine> CliLines { get; } = new();
    public IReadOnlyList<string> HistoryWindows { get; } = [L.Plural(24, "{0} hour", "{0} hours"), L.Plural(7, "{0} day", "{0} days"), L.Plural(30, "{0} day", "{0} days")];

    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _routeText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _statusUpdated;
    [ObservableProperty] private string? _ownerInfo;
    [ObservableProperty] private string? _firmware;
    [ObservableProperty] private int _historyWindow;
    [ObservableProperty] private string _historySummary = "";
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private string _cliInput = "";
    [ObservableProperty] private int _revision;

    // Settings (read via CLI)
    [ObservableProperty] private bool _settingsLoaded;
    [ObservableProperty] private string? _settingsProgress;
    [ObservableProperty] private string _setName = "";
    [ObservableProperty] private string _setFrequency = "";
    [ObservableProperty] private string _setBandwidth = "";
    [ObservableProperty] private string _setSf = "";
    [ObservableProperty] private string _setCr = "";
    [ObservableProperty] private string _setTx = "";
    [ObservableProperty] private bool _setRepeat;
    [ObservableProperty] private string _setAdvertInterval = "";
    [ObservableProperty] private string _setFloodAdvertInterval = "";
    [ObservableProperty] private string _setFloodMax = "";
    [ObservableProperty] private string _setLat = "";
    [ObservableProperty] private string _setLon = "";
    [ObservableProperty] private string _setOwnerInfo = "";
    [ObservableProperty] private string _setGuestPassword = "";
    [ObservableProperty] private bool _setAllowReadOnly;
    [ObservableProperty] private string _nodeClock = "";
    [ObservableProperty] private string _nodeVersion = "";

    private CancellationTokenSource? _autoCts;

    partial void OnHistoryWindowChanged(int value) => LoadHistory();

    partial void OnAutoRefreshChanged(bool value)
    {
        _autoCts?.Cancel();
        _autoCts = null;
        if (!value) return;
        var cts = new CancellationTokenSource();
        _autoCts = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), cts.Token); } catch { return; }
                Ui(() => { if (!Busy) _ = RequestStatus(); });
            }
        });
    }

    private void RefreshSession()
    {
        _contact = Core.Db.GetContact(_contact.Id) ?? _contact;
        var s = Core.Remote.Sessions().FirstOrDefault(x => x.PublicKey.SequenceEqual(_contact.PublicKey));
        IsLoggedIn = s is { IsConnected: true };
        IsAdmin = s is { IsConnected: true, IsAdmin: true };
        SessionText = s switch
        {
            { IsConnected: true, IsAdmin: true } => L.F("Signed in as admin · {0}", Formatters.AgoMs(s.LastLogin)),
            { IsConnected: true } => s.PermissionLevel == RoomPermission.ReadWrite
                ? L.F("Signed in as member · {0}", Formatters.AgoMs(s.LastLogin))
                : L.F("Signed in as guest · {0}", Formatters.AgoMs(s.LastLogin)),
            _ => L.T("Not signed in — status and telemetry may still work if the node allows guests."),
        };
        RouteText = _contact.IsFloodRouted ? L.T("Flood routed") : _contact.HopCount == 0 ? L.T("Direct neighbour")
            : L.Plural(_contact.HopCount, "{0} hop · {1}", "{0} hops · {1}", Formatters.PathText(_contact.OutPath, _contact.HashSize));
    }

    /// <summary>
    /// Runs a request, showing <paramref name="progress"/> meanwhile; <paramref name="failed"/> turns the error message into the
    /// whole (translated) failure sentence.
    /// </summary>
    private async Task Run(Func<Task> action, string progress, Func<string, string> failed)
    {
        if (!Core.IsConnected) { Message = L.T("Connect to your radio first."); return; }
        Busy = true;
        Message = progress;
        try { await action(); if (Message == progress) Message = null; }
        catch (Exception ex) { Message = failed(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task Login()
    {
        var vm = new LoginViewModel(_contact);
        await AppHost.Dialogs.ShowDialog(vm, vm.Title, 440, 360);
        RefreshSession();
    }

    [RelayCommand]
    private async Task Logout()
    {
        var s = Core.Remote.Sessions().FirstOrDefault(x => x.PublicKey.SequenceEqual(_contact.PublicKey));
        if (s is null) return;
        await Run(() => Core.Remote.LogoutAsync(s), L.T("Signing out…"), e => L.F("Signing out failed: {0}", e));
        RefreshSession();
    }

    [RelayCommand]
    private async Task RequestStatus() => await Run(async () =>
    {
        var s = await Core.Remote.RequestStatusAsync(_contact);
        StatusRows.Clear();
        var pct = OcvPresets.LinearPercent(s.Battery);
        StatusRows.Add(new(L.T("Battery"), $"{s.Battery / 1000.0:0.00} V (~{pct}%)"));
        StatusRows.Add(new(L.T("Uptime"), Formatters.Duration(s.Uptime)));
        StatusRows.Add(new(L.T("Noise floor"), $"{s.NoiseFloor} dBm ({ToolsService.NoiseQuality((short)s.NoiseFloor)})"));
        StatusRows.Add(new(L.T("Last RSSI / SNR"), $"{s.LastRssi} dBm · {s.LastSnr:0.#} dB"));
        StatusRows.Add(new(L.T("Packets received"), L.F("{0:N0} (flood {1:N0} · direct {2:N0})", s.PacketsReceived, s.ReceivedFlood, s.ReceivedDirect)));
        StatusRows.Add(new(L.T("Packets sent"), L.F("{0:N0} (flood {1:N0} · direct {2:N0})", s.PacketsSent, s.SentFlood, s.SentDirect)));
        StatusRows.Add(new(L.T("Air time TX / RX"), $"{Formatters.Duration(s.Airtime)} · {Formatters.Duration(s.RxAirtime)}"));
        if (s.Uptime > 0) StatusRows.Add(new(L.T("Duty cycle (TX)"), $"{100.0 * s.Airtime / s.Uptime:0.00}%"));
        StatusRows.Add(new(L.T("TX queue"), s.TxQueueLength.ToString()));
        StatusRows.Add(new(L.T("Duplicates"), L.F("flood {0:N0} · direct {1:N0}", s.FloodDuplicates, s.DirectDuplicates)));
        StatusRows.Add(new(L.T("Queue full events"), s.FullEvents.ToString()));
        if (s.ReceiveErrors > 0) StatusRows.Add(new(L.T("Receive errors"), s.ReceiveErrors.ToString("N0")));
        if (s.RoomServerPostedCount is { } posted) StatusRows.Add(new(L.T("Posts stored / pushed"), $"{posted} / {s.RoomServerPostPushCount}"));
        StatusUpdated = L.F("Updated {0}", DateTime.Now.ToString("T"));
        LoadHistory();
        RefreshSession();
    }, L.T("Requesting status…"), e => L.F("Requesting status failed: {0}", e));

    [RelayCommand]
    private async Task RequestTelemetry() => await Run(async () =>
    {
        var t = await Core.Remote.RequestTelemetryAsync(_contact);
        TelemetryRows.Clear();
        foreach (var p in t.DataPoints) TelemetryRows.Add(new DetailRow(L.F("{0} · ch {1}", p.Type.DisplayName(), p.Channel), p.FormattedValue));
        if (TelemetryRows.Count == 0) Message = L.T("The node returned no telemetry (it may only share telemetry with admins or favourites).");
        LoadHistory();
    }, L.T("Requesting telemetry…"), e => L.F("Requesting telemetry failed: {0}", e));

    [RelayCommand]
    private async Task RequestNeighbours() => await Run(async () =>
    {
        var n = await Core.Remote.RequestNeighboursAsync(_contact);
        Neighbours.Clear();
        var contacts = Core.Contacts.GetAll();
        foreach (var x in n.Neighbours.OrderByDescending(x => x.Snr))
        {
            var c = contacts.FirstOrDefault(c => c.PublicKey.StartsWith(x.PublicKeyPrefix));
            var color = x.Snr >= 5 ? "#22A06B" : x.Snr >= -5 ? "#F59E0B" : "#E5484D";
            Neighbours.Add(new NeighbourRow(c?.DisplayName ?? L.T("Unknown"), Convert.ToHexString(x.PublicKeyPrefix), Formatters.Ago(DateTimeOffset.Now.AddSeconds(-x.SecondsAgo)), x.Snr, $"{x.Snr:0.#} dB", color));
        }
        if (Neighbours.Count == 0) Message = L.F("No neighbours reported (total {0}).", n.TotalCount);
    }, L.T("Requesting neighbours…"), e => L.F("Requesting neighbours failed: {0}", e));

    [RelayCommand]
    private async Task RequestOwnerInfo() => await Run(async () =>
    {
        var o = await Core.Remote.RequestOwnerInfoAsync(_contact);
        Firmware = o.FirmwareVersion;
        OwnerInfo = string.IsNullOrWhiteSpace(o.OwnerInfo) ? L.T("(no owner info set)") : o.OwnerInfo;
    }, L.T("Requesting owner info…"), e => L.F("Requesting owner info failed: {0}", e));

    [RelayCommand]
    private async Task RequestAcl() => await Run(async () =>
    {
        var a = await Core.Remote.RequestAclAsync(_contact);
        Acl.Clear();
        var contacts = Core.Contacts.GetAll();
        foreach (var e in a.Entries)
        {
            var c = contacts.FirstOrDefault(c => c.PublicKey.StartsWith(e.KeyPrefix));
            var perm = (e.Permissions & 0x03) switch { 3 => L.T("Admin"), 2 => L.T("Read/write"), 1 => L.T("Read only"), _ => L.T("Guest") };
            Acl.Add(new AclRow(c?.DisplayName ?? (Core.SelfInfo?.PublicKey.StartsWith(e.KeyPrefix) == true ? L.T("You") : L.T("Unknown")), Convert.ToHexString(e.KeyPrefix), perm));
        }
        if (Acl.Count == 0) Message = L.T("The access list is empty.");
    }, L.T("Requesting access list…"), e => L.F("Requesting access list failed: {0}", e));

    private void LoadHistory()
    {
        var window = HistoryWindow switch { 1 => TimeSpan.FromDays(7), 2 => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };
        var snaps = Core.Remote.History(_contact, window);
        BatterySeries.Clear();
        NoiseSeries.Clear();
        SnrSeries.Clear();
        var battery = snaps.Where(s => s.BatteryMv is > 0).Select(s => new ChartPoint(s.CapturedAt, s.BatteryMv!.Value / 1000.0)).ToList();
        var noise = snaps.Where(s => s.NoiseFloor is not null && s.NoiseFloor != 0).Select(s => new ChartPoint(s.CapturedAt, s.NoiseFloor!.Value)).ToList();
        var rssi = snaps.Where(s => s.LastRssi is not null && s.LastRssi != 0).Select(s => new ChartPoint(s.CapturedAt, s.LastRssi!.Value)).ToList();
        var snr = snaps.Where(s => s.LastSnr is not null).Select(s => new ChartPoint(s.CapturedAt, s.LastSnr!.Value)).ToList();
        if (battery.Count > 0) BatterySeries.Add(new ChartSeries { Name = L.T("Battery"), Color = "#22A06B", Points = battery, Fill = true, ShowDots = battery.Count < 40 });
        if (noise.Count > 0) NoiseSeries.Add(new ChartSeries { Name = L.T("Noise floor"), Color = "#E5484D", Points = noise, ShowDots = noise.Count < 40 });
        if (rssi.Count > 0) NoiseSeries.Add(new ChartSeries { Name = L.T("Last RSSI"), Color = "#2463EB", Points = rssi, ShowDots = rssi.Count < 40 });
        if (snr.Count > 0) SnrSeries.Add(new ChartSeries { Name = L.T("Last SNR"), Color = "#7C3AED", Points = snr, ShowDots = snr.Count < 40 });
        HistorySummary = snaps.Count == 0 ? L.T("No history yet — request status or telemetry to start recording.") : L.Plural(snaps.Count, "{0} snapshot in this window", "{0} snapshots in this window");
        Revision++;
    }

    // ---- settings ----

    [RelayCommand]
    private async Task LoadSettings()
    {
        if (!IsAdmin) { Message = L.T("Sign in as admin to read and change settings."); return; }
        await Run(async () =>
        {
            var queries = IsRoom ? RemoteNodeService.RoomSettingQueries : RemoteNodeService.RepeaterSettingQueries;
            // q is the CLI query sent to the node ("get radio"), shown as is.
            var progress = new Progress<string>(q => SettingsProgress = L.F("Reading {0}…", q));
            var r = await Core.Remote.ReadNodeSettingsAsync(_contact, queries, progress);
            string Raw(string q) => r.TryGetValue(q, out var v) ? v switch
            {
                CliResponse.Name n => n.Value,
                CliResponse.Version ver => ver.Value,
                CliResponse.DeviceTime t => t.Value,
                CliResponse.OwnerInfo o => o.Value,
                CliResponse.Raw raw => raw.Value,
                CliResponse.Error e => "⚠ " + e.Message,
                _ => v.ToString() ?? "",
            } : "";
            var inv = CultureInfo.InvariantCulture;
            NodeVersion = Raw("ver");
            NodeClock = Raw("clock");
            SetName = r.GetValueOrDefault("get name") is CliResponse.Name nm ? nm.Value : Raw("get name");
            if (r.GetValueOrDefault("get radio") is CliResponse.Radio radio)
            {
                SetFrequency = radio.Frequency.ToString("0.###", inv);
                SetBandwidth = radio.Bandwidth.ToString("0.#", inv);
                SetSf = radio.SpreadingFactor.ToString();
                SetCr = radio.CodingRate.ToString();
            }
            SetTx = r.GetValueOrDefault("get tx") is CliResponse.TxPower tx ? tx.Value.ToString() : Raw("get tx");
            SetRepeat = r.GetValueOrDefault("get repeat") is CliResponse.RepeatMode { On: true };
            SetAdvertInterval = r.GetValueOrDefault("get advert.interval") is CliResponse.AdvertInterval ai ? ai.Minutes.ToString() : Raw("get advert.interval");
            SetFloodAdvertInterval = r.GetValueOrDefault("get flood.advert.interval") is CliResponse.FloodAdvertInterval fi ? fi.Hours.ToString() : Raw("get flood.advert.interval");
            SetFloodMax = r.GetValueOrDefault("get flood.max") is CliResponse.FloodMax fm ? fm.Hops.ToString() : Raw("get flood.max");
            SetLat = r.GetValueOrDefault("get lat") is CliResponse.Latitude la ? la.Value.ToString("0.000000", inv) : Raw("get lat");
            SetLon = r.GetValueOrDefault("get lon") is CliResponse.Longitude lo ? lo.Value.ToString("0.000000", inv) : Raw("get lon");
            SetOwnerInfo = Raw("get owner.info");
            if (IsRoom)
            {
                SetGuestPassword = Raw("get guest.password");
                SetAllowReadOnly = Raw("get allow.read.only").Contains("on", StringComparison.OrdinalIgnoreCase);
            }
            SettingsLoaded = true;
            SettingsProgress = null;
        }, L.T("Reading settings…"), e => L.F("Reading settings failed: {0}", e));
    }

    /// <summary>Sends a settings command (English CLI, unchanged); <paramref name="label"/> is the setting's translated name shown in messages.</summary>
    private async Task Apply(string command, string label)
    {
        if (!IsAdmin) { Message = L.T("Sign in as admin first."); return; }
        await Run(async () =>
        {
            var reply = await Core.Remote.SendCliAsync(_contact, command);
            var parsed = CliResponse.Parse(reply);
            Message = parsed is CliResponse.Error or CliResponse.UnknownCommand ? $"{label}: {reply}" : L.F("{0} updated ({1}).", label, reply.Trim());
        }, L.F("Applying {0}…", label), e => L.F("Applying {0} failed: {1}", label, e));
    }

    [RelayCommand] private Task ApplyName() => Apply($"set name {SetName.Trim()}", L.T("Name"));
    [RelayCommand] private Task ApplyTx() => Apply($"set tx {SetTx.Trim()}", L.T("TX power"));
    [RelayCommand] private Task ApplyRepeat() => Apply($"set repeat {(SetRepeat ? "on" : "off")}", L.T("Repeat mode"));
    [RelayCommand] private Task ApplyAdvertInterval() => Apply($"set advert.interval {SetAdvertInterval.Trim()}", L.T("Advert interval"));
    [RelayCommand] private Task ApplyFloodAdvertInterval() => Apply($"set flood.advert.interval {SetFloodAdvertInterval.Trim()}", L.T("Flood advert interval"));
    [RelayCommand] private Task ApplyFloodMax() => Apply($"set flood.max {SetFloodMax.Trim()}", L.T("Max flood hops"));
    [RelayCommand] private Task ApplyOwnerInfo() => Apply($"set owner.info {SetOwnerInfo.Trim().Replace("\n", "|")}", L.T("Owner info"));
    [RelayCommand] private Task ApplyGuestPassword() => Apply($"set guest.password {SetGuestPassword.Trim()}", L.T("Guest password"));
    [RelayCommand] private Task ApplyAllowReadOnly() => Apply($"set allow.read.only {(SetAllowReadOnly ? "on" : "off")}", L.T("Read-only access"));

    [RelayCommand]
    private async Task ApplyLocation()
    {
        await Apply($"set lat {SetLat.Trim()}", L.T("Latitude"));
        await Apply($"set lon {SetLon.Trim()}", L.T("Longitude"));
    }

    [RelayCommand]
    private async Task ApplyRadio()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Change the node's radio settings?"),
                L.T("If the new settings don't match your radio, you will lose contact with this node until it is changed back locally. The node applies them after a reboot."), L.T("Change"), true)) return;
        await Apply($"set radio {SetFrequency.Trim()},{SetBandwidth.Trim()},{SetSf.Trim()},{SetCr.Trim()}", L.T("Radio"));
    }

    [RelayCommand]
    private async Task UseMyRadioSettings()
    {
        if (Core.SelfInfo is not { } s) return;
        var inv = CultureInfo.InvariantCulture;
        SetFrequency = s.RadioFrequency.ToString("0.###", inv);
        SetBandwidth = s.RadioBandwidth.ToString("0.#", inv);
        SetSf = s.RadioSpreadingFactor.ToString();
        SetCr = s.RadioCodingRate.ToString();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ChangePassword()
    {
        var pwd = await AppHost.Dialogs.Prompt(L.T("Change admin password"), L.T("New admin password for this node:"), "", password: true);
        if (string.IsNullOrEmpty(pwd)) return;
        await Apply($"password {pwd}", L.T("Admin password"));
        Core.Remote.ForgetPassword(_contact.PublicKey);
    }

    [RelayCommand] private Task SyncClock() => Apply("clock sync", L.T("Clock"));
    [RelayCommand] private Task SendAdvert() => Apply("advert", L.T("Advert"));

    [RelayCommand]
    private async Task Reboot()
    {
        if (!await AppHost.Dialogs.Confirm(L.F("Reboot {0}?", _contact.DisplayName), L.T("The node will be offline for a few seconds."), L.T("Reboot"), true)) return;
        await Apply("reboot", L.T("Reboot"));
    }

    // ---- inline CLI ----

    [RelayCommand]
    private async Task SendCli()
    {
        var cmd = CliInput.Trim();
        if (cmd.Length == 0) return;
        CliInput = "";
        CliLines.Add(new CliLine("> " + cmd, CliLineKind.Input));
        if (!Core.IsConnected) { CliLines.Add(new CliLine(L.T("Not connected."), CliLineKind.Error)); return; }
        try
        {
            var reply = await Core.Remote.SendCliAsync(_contact, cmd, TimeSpan.FromSeconds(12));
            foreach (var l in reply.Replace("\r", "").Split('\n')) CliLines.Add(new CliLine(l, CliLineKind.Output));
        }
        catch (Exception ex) { CliLines.Add(new CliLine(ex.Message, CliLineKind.Error)); }
    }

    [RelayCommand]
    private void OpenFullCli()
    {
        Close?.Invoke();
        if (AppHost.Main is not { } main) return;
        main.Navigate(Page.Tools);
        main.Tools.OpenCliFor(_contact);
    }

    [RelayCommand]
    private async Task ExportHistory()
    {
        var path = await AppHost.Dialogs.PickSaveFile(L.T("Export history"), $"{_contact.DisplayName}-history.csv", "csv", L.T("CSV file"));
        if (path is null) return;
        var snaps = Core.Remote.History(_contact, TimeSpan.FromDays(3650));
        var lines = new List<string> { "time,battery_mv,noise_floor,last_rssi,last_snr,uptime,rx,tx,airtime,telemetry" };
        lines.AddRange(snaps.Select(s => string.Join(',',
            DateTimeOffset.FromUnixTimeMilliseconds(s.CapturedAt).ToString("o"), s.BatteryMv, s.NoiseFloor, s.LastRssi,
            s.LastSnr?.ToString(CultureInfo.InvariantCulture), s.Uptime, s.PacketsReceived, s.PacketsSent, s.Airtime,
            s.TelemetryJson is null ? "" : "\"" + s.TelemetryJson.Replace("\"", "\"\"") + "\"")));
        await File.WriteAllLinesAsync(path, lines);
        Message = L.Plural(snaps.Count, "Exported {0} snapshot.", "Exported {0} snapshots.");
    }
}
