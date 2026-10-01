using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MeshCore;

namespace MC1.Windows.ViewModels;

public enum CliLineKind { Input, Output, Error, Info }

public sealed record CliLine(string Text, CliLineKind Kind)
{
    public string Color => Kind switch
    {
        CliLineKind.Input => "#60A5FA",
        CliLineKind.Error => "#F87171",
        CliLineKind.Info => "#9CA3AF",
        _ => "#E5E7EB",
    };
}

public sealed record CliTarget(string Name, ContactRecord? Contact)
{
    public bool IsLocal => Contact is null;
    public override string ToString() => Name;
}

public sealed partial class CliViewModel : ToolPage
{
    private readonly MainWindowViewModel _main;
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string? _pendingConfirm;

    private static readonly string[] RemoteCommands =
    [
        "ver", "board", "clock", "clock sync", "time ", "reboot", "advert", "neighbors", "stats-core", "stats-radio", "stats-packets",
        "get name", "set name ", "get radio", "set radio ", "get freq", "set freq ", "get tx", "set tx ", "get repeat", "set repeat on", "set repeat off",
        "get advert.interval", "set advert.interval ", "get flood.advert.interval", "set flood.advert.interval ", "get flood.max", "set flood.max ",
        "get lat", "set lat ", "get lon", "set lon ", "get owner.info", "set owner.info ", "get af", "set af ", "get rxdelay", "set rxdelay ",
        "get txdelay", "set txdelay ", "get direct.txdelay", "set direct.txdelay ", "get guest.password", "set guest.password ", "password ",
        "get allow.read.only", "set allow.read.only on", "set allow.read.only off", "get multi.acks", "set multi.acks ", "get bridge.enabled",
        "get agc.reset.interval", "set agc.reset.interval ", "get int.thresh", "set int.thresh ", "region", "region list", "log start", "log stop", "log erase",
        "erase", "clear stats", "powersaving", "powersaving on", "powersaving off", "get path.hash.mode", "set path.hash.mode ",
    ];

    private static readonly string[] LocalCommands =
    [
        "help", "clear", "ver", "board", "clock", "clock sync", "advert", "floodadv", "reboot", "stats",
        "get name", "get lat", "get lon", "get tx", "get radio", "get freq", "get public.key", "get multi.acks", "get path.hash.mode", "get bat", "get vars",
        "set name ", "set lat ", "set lon ", "set tx ", "set radio ", "set freq ", "set multi.acks ", "set path.hash.mode ", "set var ",
    ];

    public CliViewModel(MainWindowViewModel main)
    {
        _main = main;
        Action<string, string> onOutput = (prefix, text) => Ui(() =>
        {
            // Unsolicited replies (e.g. late answers) for the selected node.
            if (Target?.Contact is { } c && Convert.ToHexString(c.PublicKey, 0, 6).Equals(prefix, StringComparison.OrdinalIgnoreCase) && !_awaiting)
                Append(text, CliLineKind.Output);
        });
        Core.Remote.CliOutput += onOutput;
        Track(() => Core.Remote.CliOutput -= onOutput);
        Lines.Add(new CliLine(L.T("MeshCore CLI — type 'help' for local commands. Pick a remote node above to administer it."), CliLineKind.Info));
    }

    public ObservableCollection<CliLine> Lines { get; } = new();
    public ObservableCollection<CliTarget> Targets { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    [ObservableProperty] private CliTarget? _target;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _prompt = "radio>";
    [ObservableProperty] private string? _sessionStatus;
    [ObservableProperty] private bool _needsLogin;
    private bool _awaiting;

    public event Action? LinesAppended;

    public override void OnShown() => ReloadTargets();

    private void ReloadTargets()
    {
        var selected = Target?.Contact?.Id;
        Targets.Clear();
        Targets.Add(new CliTarget(L.T("This radio (local)"), null));
        foreach (var c in Core.Contacts.GetAll().Where(c => c.ContactType is ContactType.Repeater or ContactType.Room or ContactType.Sensor).OrderBy(c => c.DisplayName))
            Targets.Add(new CliTarget($"{c.DisplayName} · {Formatters.TypeName(c.ContactType)}", c));
        Target = Targets.FirstOrDefault(t => t.Contact?.Id == selected) ?? Targets[0];
    }

    public void SelectTarget(ContactRecord contact)
    {
        ReloadTargets();
        Target = Targets.FirstOrDefault(t => t.Contact?.Id == contact.Id) ?? Target;
    }

    partial void OnTargetChanged(CliTarget? value)
    {
        _pendingConfirm = null;
        if (value is null) return;
        if (value.IsLocal)
        {
            Prompt = $"{(Core.SelfName is { Length: > 0 } n ? n : "radio")}>";
            SessionStatus = L.T("Commands run on your companion radio.");
            NeedsLogin = false;
        }
        else
        {
            var c = value.Contact!;
            Prompt = $"{c.DisplayName}>";
            var session = Core.Remote.Sessions().FirstOrDefault(s => s.PublicKey.SequenceEqual(c.PublicKey));
            NeedsLogin = session is not { IsConnected: true, IsAdmin: true };
            SessionStatus = session switch
            {
                { IsConnected: true, IsAdmin: true } => L.T("Signed in as admin."),
                { IsConnected: true } => L.T("Signed in as guest — most commands need admin access."),
                _ => L.T("Not signed in. Sign in as admin to run commands."),
            };
            Append($"── {c.DisplayName} ──", CliLineKind.Info);
        }
        UpdateSuggestions();
    }

    partial void OnInputChanged(string value) => UpdateSuggestions();

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        var q = Input.TrimStart();
        if (q.Length == 0) return;
        var source = Target?.IsLocal != false ? LocalCommands : RemoteCommands;
        foreach (var s in source.Where(s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase) && !s.Equals(q, StringComparison.OrdinalIgnoreCase)).Take(8)) Suggestions.Add(s);
    }

    /// <summary>Tab completion: completes to the longest common prefix of the matches.</summary>
    public void Complete()
    {
        if (Suggestions.Count == 0) return;
        if (Suggestions.Count == 1) { Input = Suggestions[0]; return; }
        var prefix = Suggestions[0];
        foreach (var s in Suggestions.Skip(1))
        {
            var i = 0;
            while (i < prefix.Length && i < s.Length && char.ToLowerInvariant(prefix[i]) == char.ToLowerInvariant(s[i])) i++;
            prefix = prefix[..i];
        }
        if (prefix.Length > Input.Length) Input = prefix;
    }

    public void HistoryPrevious()
    {
        if (_history.Count == 0) return;
        _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    public void HistoryNext()
    {
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count) { _historyIndex = -1; Input = ""; }
        else Input = _history[_historyIndex];
    }

    private void Append(string text, CliLineKind kind)
    {
        foreach (var line in text.Replace("\r", "").Split('\n')) Lines.Add(new CliLine(line, kind));
        while (Lines.Count > 3000) Lines.RemoveAt(0);
        LinesAppended?.Invoke();
    }

    [RelayCommand] private void ClearOutput() => Lines.Clear();

    [RelayCommand]
    private void UseSuggestion(string s) => Input = s;

    [RelayCommand]
    private async Task Login()
    {
        if (Target?.Contact is not { } c) return;
        var vm = new LoginViewModel(c);
        await AppHost.Dialogs.ShowDialog(vm, L.F("Sign in to {0}", c.DisplayName), 420, 360);
        OnTargetChanged(Target);
    }

    [RelayCommand]
    private async Task Submit()
    {
        var command = Input.Trim();
        Input = "";
        _historyIndex = -1;
        if (command.Length == 0) return;
        if (_history.Count == 0 || _history[^1] != command) _history.Add(command);
        Append($"{Prompt} {command}", CliLineKind.Input);

        if (_pendingConfirm is not null)
        {
            var confirmed = command.Equals("y", StringComparison.OrdinalIgnoreCase) || command.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var pending = _pendingConfirm;
            _pendingConfirm = null;
            if (!confirmed) { Append(L.T("Cancelled."), CliLineKind.Info); return; }
            await Execute(pending, confirmed: true);
            return;
        }
        if (command.Equals("clear", StringComparison.OrdinalIgnoreCase)) { Lines.Clear(); return; }
        await Execute(command, confirmed: false);
    }

    private async Task Execute(string command, bool confirmed)
    {
        if (!Core.IsConnected) { Append(L.T("Not connected to a radio."), CliLineKind.Error); return; }
        IsBusy = true;
        try
        {
            if (Target?.Contact is { } contact) await ExecuteRemote(contact, command, confirmed);
            else await ExecuteLocal(command, confirmed);
        }
        catch (Exception ex) { Append(L.F("Error: {0}", ex.Message), CliLineKind.Error); }
        finally { IsBusy = false; }
    }

    private async Task ExecuteRemote(ContactRecord contact, string command, bool confirmed)
    {
        var lower = command.ToLowerInvariant();
        if (!confirmed && (lower == "reboot" || lower.StartsWith("set radio") || lower.StartsWith("set freq") || lower == "erase"))
        {
            _pendingConfirm = command;
            // The answer is still checked against 'y' / 'yes' (see Submit).
            Append(L.F("'{0}' can take the node off the air. Type 'y' to confirm.", command), CliLineKind.Info);
            return;
        }
        _awaiting = true;
        try
        {
            var reply = await Core.Remote.SendCliAsync(contact, command, TimeSpan.FromSeconds(12));
            Append(reply.Length == 0 ? L.T("(empty reply)") : reply, CliLineKind.Output);
        }
        finally { _awaiting = false; }
    }

    private static bool TryDouble(string s, out double v) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private async Task ExecuteLocal(string command, bool confirmed)
    {
        var session = Core.Session ?? throw new InvalidOperationException(L.T("Not connected."));
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var self = Core.SelfInfo;
        var caps = Core.Capabilities;
        switch (verb)
        {
            case "help":
                // The command syntax lines are what the user types, so only the two sentences are translated.
                Append(L.T("Local commands (this radio):") + "\n" + """
                      ver | board | clock | clock sync | advert | floodadv | reboot | stats
                      get name|lat|lon|tx|radio|freq|public.key|multi.acks|path.hash.mode|bat|vars
                      set name <text> | set lat <deg> | set lon <deg> | set tx <dBm>
                      set radio <freqMHz>,<bwKHz>,<sf>,<cr> | set freq <MHz>
                      set multi.acks <0|1> | set path.hash.mode <0|1|2> | set var <key> <value>
                      clear
                    """ + "\n" + L.T("Pick a repeater or room above to send its firmware CLI commands."), CliLineKind.Info);
                return;
            case "ver":
                Append(caps is null ? "unknown" : $"{caps.Version} (build {caps.FirmwareBuild}, protocol v{caps.FirmwareVersion})", CliLineKind.Output);
                return;
            case "board":
                Append(caps?.Model ?? "unknown", CliLineKind.Output);
                return;
            case "clock" when parts.Length > 1 && parts[1].Equals("sync", StringComparison.OrdinalIgnoreCase):
                await Core.Device.SyncClockAsync();
                Append("OK - clock set to " + DateTime.Now.ToString("HH:mm:ss dd/MM/yyyy"), CliLineKind.Output);
                return;
            case "clock":
                var t = await session.GetTimeAsync();
                Append($"{t.LocalDateTime:HH:mm:ss - dd/MM/yyyy} (offset {(t - DateTimeOffset.UtcNow).TotalSeconds:+0;-0}s from this PC)", CliLineKind.Output);
                return;
            case "advert":
                await Core.Device.SendAdvertAsync(false);
                Append("OK - zero-hop advert sent", CliLineKind.Output);
                return;
            case "floodadv":
                await Core.Device.SendAdvertAsync(true);
                Append("OK - flood advert sent", CliLineKind.Output);
                return;
            case "reboot":
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Reboot the radio? The connection will drop. Type 'y' to confirm."), CliLineKind.Info); return; }
                await Core.Device.RebootAsync();
                Append("Rebooting…", CliLineKind.Output);
                return;
            case "stats":
                var (core, radio, packets) = await Core.Device.GetStatsAsync();
                Append($"battery {core.BatteryMV} mV · uptime {Formatters.Duration(core.UptimeSeconds)} · errors {core.Errors} · queue {core.QueueLength}\n" +
                       $"noise floor {radio.NoiseFloor} dBm · last RSSI {radio.LastRssi} dBm · last SNR {radio.LastSnr:0.#} dB · tx air {radio.TxAirtimeSeconds}s · rx air {radio.RxAirtimeSeconds}s\n" +
                       $"packets rx {packets.Received} / tx {packets.Sent} · flood tx {packets.FloodTx} rx {packets.FloodRx} · direct tx {packets.DirectTx} rx {packets.DirectRx}", CliLineKind.Output);
                return;
            case "get" when parts.Length >= 2:
                await LocalGet(parts[1].ToLowerInvariant(), self, caps);
                return;
            case "set" when parts.Length >= 3:
                await LocalSet(parts[1].ToLowerInvariant(), parts[2].Trim(), confirmed, command);
                return;
            default:
                Append(L.F("Unknown command '{0}'. Type 'help'.", verb), CliLineKind.Error);
                return;
        }
    }

    private async Task LocalGet(string key, SelfInfo? self, DeviceCapabilities? caps)
    {
        if (self is null) { Append(L.T("No device info yet."), CliLineKind.Error); return; }
        var inv = CultureInfo.InvariantCulture;
        switch (key)
        {
            case "name": Append(self.Name, CliLineKind.Output); break;
            case "lat": Append(self.Latitude.ToString("0.000000", inv), CliLineKind.Output); break;
            case "lon": Append(self.Longitude.ToString("0.000000", inv), CliLineKind.Output); break;
            case "tx": Append($"{self.TxPower} dBm (max {self.MaxTxPower})", CliLineKind.Output); break;
            case "radio": Append(string.Create(inv, $"{self.RadioFrequency:0.###},{self.RadioBandwidth:0.#},{self.RadioSpreadingFactor},{self.RadioCodingRate}"), CliLineKind.Output); break;
            case "freq": Append(self.RadioFrequency.ToString("0.###", inv), CliLineKind.Output); break;
            case "public.key": Append(Convert.ToHexString(self.PublicKey), CliLineKind.Output); break;
            case "multi.acks": Append(self.MultiAcks.ToString(), CliLineKind.Output); break;
            case "path.hash.mode": Append((caps?.PathHashMode ?? 0).ToString(), CliLineKind.Output); break;
            case "bat":
                await Core.RefreshBatteryAsync();
                Append(Core.Battery is { } b ? $"{b.Level} mV ({Core.BatteryPercent}%)" : "unknown", CliLineKind.Output);
                break;
            case "vars":
                var vars = await Core.Device.GetCustomVarsAsync();
                Append(vars.Count == 0 ? L.T("(no custom variables)") : string.Join("\n", vars.Select(kv => $"{kv.Key} = {kv.Value}")), CliLineKind.Output);
                break;
            default: Append(L.F("Unknown key '{0}'.", key), CliLineKind.Error); break;
        }
    }

    private async Task LocalSet(string key, string value, bool confirmed, string command)
    {
        switch (key)
        {
            case "name":
                await Core.Device.SetNameAsync(value);
                break;
            case "lat" when TryDouble(value, out var lat) && lat is >= -90 and <= 90:
                await Core.Device.SetLocationAsync(lat, Core.SelfInfo?.Longitude ?? 0);
                break;
            case "lon" when TryDouble(value, out var lon) && lon is >= -180 and <= 180:
                await Core.Device.SetLocationAsync(Core.SelfInfo?.Latitude ?? 0, lon);
                break;
            case "tx" when int.TryParse(value, out var tx):
                await Core.Device.SetTxPowerAsync(tx);
                break;
            case "radio":
                var p = value.Split(',', StringSplitOptions.TrimEntries);
                if (p.Length != 4 || !TryDouble(p[0], out var f) || !TryDouble(p[1], out var bw) || !byte.TryParse(p[2], out var sf) || !byte.TryParse(p[3], out var cr))
                { Append(L.F("Usage: {0}", "set radio <freqMHz>,<bwKHz>,<sf>,<cr>"), CliLineKind.Error); return; }
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Changing radio settings can take you off your mesh. Type 'y' to confirm."), CliLineKind.Info); return; }
                await Core.Device.SetRadioAsync(f, bw, sf, cr);
                break;
            case "freq" when TryDouble(value, out var freq):
                if (!confirmed) { _pendingConfirm = command; Append(L.T("Changing frequency can take you off your mesh. Type 'y' to confirm."), CliLineKind.Info); return; }
                var s = Core.SelfInfo!;
                await Core.Device.SetRadioAsync(freq, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate);
                break;
            case "multi.acks" when byte.TryParse(value, out var ma) && ma <= 1:
                await Core.Device.SetOtherParamsAsync(c => c.MultiAcks = ma);
                break;
            case "path.hash.mode" when byte.TryParse(value, out var pm) && pm <= 2:
                await Core.Device.SetPathHashModeAsync(pm);
                break;
            case "var":
                var kv = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length != 2) { Append(L.F("Usage: {0}", "set var <key> <value>"), CliLineKind.Error); return; }
                await Core.Device.SetCustomVarAsync(kv[0], kv[1]);
                break;
            default:
                Append(L.F("Invalid value or unknown key '{0}'. Type 'help'.", key), CliLineKind.Error);
                return;
        }
        Append("OK", CliLineKind.Output);
    }
}
