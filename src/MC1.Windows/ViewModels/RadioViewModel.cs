using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Models;
using MC1.Core.Services;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Windows.ViewModels;

public sealed record CustomVarRow(string Key, string Value);

public sealed record KnownRadioRow(RadioRecord Radio, bool IsCurrent)
{
    public string Name => Radio.Name;
    public string Detail => L.F("{0} · fw {1} · last connected {2}", Radio.Model, Radio.FirmwareVersion, Formatters.AgoMs(Radio.LastConnected));
}

/// <summary>Companion radio configuration.</summary>
public sealed partial class RadioViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly Debouncer _reload;
    private bool _loading;

    public RadioViewModel(MainWindowViewModel main)
    {
        _main = main;
        _reload = new Debouncer(TimeSpan.FromMilliseconds(250), Reload);
        OnStatus(_reload.Trigger);
        OnData(c => { if (c.Kind == DataKind.Radio) _reload.Trigger(); });
        Presets = RadioPresets.PresetsForCurrentLocale();
    }

    public IReadOnlyList<RadioPreset> Presets { get; }
    public IReadOnlyList<RadioPreset> RepeatPresets => RadioPresets.RepeatPresets;
    public IReadOnlyList<OcvPresets.Preset> OcvOptions => OcvPresets.All;
    public IReadOnlyList<double> Bandwidths { get; } = [7.8, 10.4, 15.6, 20.8, 31.25, 41.7, 62.5, 125, 250, 500];
    public IReadOnlyList<int> SpreadingFactors { get; } = [5, 6, 7, 8, 9, 10, 11, 12];
    public IReadOnlyList<int> CodingRates { get; } = [5, 6, 7, 8];
    public IReadOnlyList<string> HashModes { get; } = [L.T("1 byte (up to 63 hops)"), L.T("2 bytes (up to 32 hops)"), L.T("3 bytes (up to 21 hops)")];
    public IReadOnlyList<string> TelemetryModes { get; } = [L.T("Off"), L.T("Trusted contacts only"), L.T("Everyone")];
    public ObservableCollection<CustomVarRow> CustomVars { get; } = new();
    public ObservableCollection<KnownRadioRow> KnownRadios { get; } = new();
    public ObservableCollection<DetailRow> Stats { get; } = new();

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _firmware = "";
    [ObservableProperty] private string _publicKey = "";
    [ObservableProperty] private string _capacity = "";
    [ObservableProperty] private string _batteryText = "";
    [ObservableProperty] private string _storageText = "";
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _busy;

    // identity & position
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editLat = "";
    [ObservableProperty] private string _editLon = "";
    [ObservableProperty] private bool _shareLocationInAdverts;

    // radio
    [ObservableProperty] private RadioPreset? _selectedPreset;
    [ObservableProperty] private string _matchingPresetText = "";
    /// <summary>Preset name shown in the radio's header card ("USA", "Custom", "Repeat mode").</summary>
    [ObservableProperty] private string _currentPresetName = "";
    [ObservableProperty] private string _editFrequency = "";
    [ObservableProperty] private double _editBandwidth = 62.5;
    [ObservableProperty] private int _editSf = 7;
    [ObservableProperty] private int _editCr = 5;
    [ObservableProperty] private int _txPower;
    [ObservableProperty] private int _maxTxPower = 22;
    [ObservableProperty] private int _minTxPower = -9;
    [ObservableProperty] private bool _supportsRepeat;
    [ObservableProperty] private bool _repeatEnabled;
    [ObservableProperty] private RadioPreset? _selectedRepeatPreset;
    [ObservableProperty] private string _repeatRanges = "";

    // contacts & telemetry
    [ObservableProperty] private bool _autoAddEnabled;
    [ObservableProperty] private bool _autoAddContacts;
    [ObservableProperty] private bool _autoAddRepeaters;
    [ObservableProperty] private bool _autoAddRooms;
    [ObservableProperty] private bool _autoAddSensors;
    [ObservableProperty] private bool _overwriteOldest;
    [ObservableProperty] private int _autoAddMaxHops;
    [ObservableProperty] private int _telemetryBase;
    [ObservableProperty] private int _telemetryLocation;
    [ObservableProperty] private int _telemetryEnvironment;
    [ObservableProperty] private bool _multiAcks;

    // battery
    [ObservableProperty] private OcvPresets.Preset? _selectedOcv;
    [ObservableProperty] private string _customOcv = "";

    // advanced
    [ObservableProperty] private int _pathHashMode;
    [ObservableProperty] private bool _supportsPathHashMode;
    [ObservableProperty] private string _rxDelayBase = "";
    [ObservableProperty] private string _airtimeFactor = "";
    [ObservableProperty] private string _blePin = "";
    [ObservableProperty] private string _defaultScope = "";
    [ObservableProperty] private string _newVarKey = "";
    [ObservableProperty] private string _newVarValue = "";
    [ObservableProperty] private string _knownRegions = "";

    public void Reload()
    {
        _loading = true;
        try
        {
            var core = Core;
            IsConnected = core.IsConnected;
            var s = core.SelfInfo;
            var caps = core.Capabilities;
            var radio = core.Radio;
            DeviceName = s?.Name ?? radio?.Name ?? L.T("No radio");
            Model = caps?.Model ?? radio?.Model ?? "";
            Firmware = caps is not null ? L.F("{0} · build {1} · protocol v{2}", caps.Version, caps.FirmwareBuild, caps.FirmwareVersion) : radio is not null ? radio.FirmwareVersion : "";
            PublicKey = s is not null ? Convert.ToHexString(s.PublicKey) : radio is not null ? Convert.ToHexString(radio.PublicKey) : "";
            Capacity = caps is not null
                ? L.Plural(caps.MaxContacts, "{0} contact", "{0} contacts") + " · " + L.Plural(caps.MaxChannels, "{0} channel", "{0} channels")
                : "";
            BatteryText = core.Battery is { } b ? $"{core.BatteryPercent}% · {b.Level / 1000.0:0.00} V" : "–";
            StorageText = core.Battery is { UsedStorageKB: { } used, TotalStorageKB: { } total } ? L.F("{0:N0} KB of {1:N0} KB used", used, total) : "";
            ConnectionText = core.CurrentTarget?.Describe() ?? L.T("Not connected");
            KnownRegions = string.Join(", ", core.RxLog.KnownRegions());

            KnownRadios.Clear();
            foreach (var r in core.Db.GetRadios().OrderByDescending(r => r.LastConnected)) KnownRadios.Add(new KnownRadioRow(r, r.Id == core.RadioId));

            var ocvId = radio?.OcvPreset ?? (caps is not null ? OcvPresets.ForManufacturer(caps.Model) : null);
            SelectedOcv = ocvId == "custom" ? null : OcvPresets.Find(ocvId) ?? OcvPresets.Default;
            CustomOcv = radio?.CustomOcv ?? "";

            if (s is null) return;
            var inv = CultureInfo.InvariantCulture;
            // Background refreshes (battery, sync…) mustn't wipe out something being typed: a field keeps the user's
            // edit unless the radio's own value changed.
            EditName = Sync(nameof(EditName), EditName, s.Name);
            EditLat = Sync(nameof(EditLat), EditLat, s.Latitude.ToString("0.000000", inv));
            EditLon = Sync(nameof(EditLon), EditLon, s.Longitude.ToString("0.000000", inv));
            ShareLocationInAdverts = s.AdvertisementLocationPolicy != 0;
            EditFrequency = Sync(nameof(EditFrequency), EditFrequency, s.RadioFrequency.ToString("0.000", inv));
            EditBandwidth = Sync(nameof(EditBandwidth), EditBandwidth, Bandwidths.MinBy(b => Math.Abs(b - s.RadioBandwidth)));
            EditSf = Sync(nameof(EditSf), EditSf, (int)s.RadioSpreadingFactor);
            EditCr = Sync(nameof(EditCr), EditCr, (int)s.RadioCodingRate);
            // What the radio is on right now (the same rules as the iPhone app; repeat mode shows its repeat frequency).
            var repeating = caps?.ClientRepeat ?? false;
            var match = repeating ? null : core.Device.CurrentPreset();
            if (!_applyingPreset) SelectedPreset = match is null ? null : Presets.FirstOrDefault(p => p.Id == match.Id);
            if (repeating && RadioPresets.MatchingRepeat(s.RadioFrequency) is { } rp) SelectedRepeatPreset = RepeatPresets.FirstOrDefault(p => p.Id == rp.Id);
            MatchingPresetText = repeating
                ? L.F("The radio is in repeat mode: {0}", RadioPresets.Describe(RadioPresets.MatchingRepeat(s.RadioFrequency), s.RadioFrequency, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate))
                : L.F("On the radio now: {0}", RadioPresets.Describe(match, s.RadioFrequency, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate));
            CurrentPresetName = repeating ? L.T("Repeat mode") : match?.Name ?? L.T("Custom");
            MaxTxPower = s.MaxTxPower > 0 ? s.MaxTxPower : 22;
            MinTxPower = PacketBuilder.TxPowerFloor;
            TxPower = Sync(nameof(TxPower), TxPower, (int)s.TxPower);
            SupportsRepeat = caps is { FirmwareVersion: >= 9 };
            RepeatEnabled = caps?.ClientRepeat ?? false;
            AutoAddEnabled = !s.ManualAddContacts;
            TelemetryBase = Math.Min(2, (int)s.TelemetryModeBase);
            TelemetryLocation = Math.Min(2, (int)s.TelemetryModeLocation);
            TelemetryEnvironment = Math.Min(2, (int)s.TelemetryModeEnvironment);
            MultiAcks = s.MultiAcks > 0;
            SupportsPathHashMode = caps?.SupportsPathHashMode ?? false;
            PathHashMode = Math.Min(2, (int)(caps?.PathHashMode ?? 0));
            BlePin = caps is { BlePin: > 0 } ? caps.BlePin.ToString("000000") : "";
            DefaultScope = core.Channels.DeviceDefaultFloodScope ?? "";
        }
        finally { _loading = false; }
    }

    private async Task Run(Func<Task> action, string what, string? success = null)
    {
        if (!Core.IsConnected) { Message = L.T("Connect to your radio first."); return; }
        Busy = true;
        Message = null;
        try
        {
            await action();
            Message = success;
            if (success is not null) _main.ShowToast(what, success);
        }
        catch (Exception ex) { Message = L.F("{0} failed: {1}", what, ex.Message); }
        finally { Busy = false; Reload(); }
    }

    /// <summary>Loads values that require extra round trips (auto-add, tuning, custom vars, stats).</summary>
    [RelayCommand]
    private async Task LoadAdvanced()
    {
        if (!Core.IsConnected) return;
        Busy = true;
        try
        {
            try
            {
                var a = await Core.Device.GetAutoAddAsync();
                _loading = true;
                AutoAddContacts = (a.Bitmask & AutoAddConfig.ContactsBit) != 0;
                AutoAddRepeaters = (a.Bitmask & AutoAddConfig.RepeatersBit) != 0;
                AutoAddRooms = (a.Bitmask & AutoAddConfig.RoomServersBit) != 0;
                AutoAddSensors = (a.Bitmask & AutoAddConfig.SensorsBit) != 0;
                OverwriteOldest = (a.Bitmask & AutoAddConfig.OverwriteOldestBit) != 0;
                AutoAddMaxHops = a.MaxHops;
            }
            catch { /* older firmware */ }
            finally { _loading = false; }
            try
            {
                var t = await Core.Device.GetTuningAsync();
                RxDelayBase = t.RxDelayBase.ToString("0.###", CultureInfo.InvariantCulture);
                AirtimeFactor = t.AirtimeFactor.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch { /* unsupported */ }
            try
            {
                var vars = await Core.Device.GetCustomVarsAsync();
                CustomVars.Clear();
                foreach (var kv in vars) CustomVars.Add(new CustomVarRow(kv.Key, kv.Value));
            }
            catch { /* unsupported */ }
            try
            {
                var ranges = await Core.Device.GetRepeatFrequenciesAsync();
                RepeatRanges = ranges.Count == 0 ? "" : L.F("Allowed repeat ranges: {0}", string.Join(", ", ranges.Select(r => $"{r.LowerKHz / 1000.0:0.###}–{r.UpperKHz / 1000.0:0.###} MHz")));
            }
            catch { RepeatRanges = ""; }
            await RefreshStats();
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RefreshStats()
    {
        try
        {
            var (core, radio, packets) = await Core.Device.GetStatsAsync();
            Stats.Clear();
            Stats.Add(new(L.T("Uptime"), Formatters.Duration(core.UptimeSeconds)));
            Stats.Add(new(L.T("Battery"), $"{core.BatteryMV} mV"));
            Stats.Add(new(L.T("Noise floor"), $"{radio.NoiseFloor} dBm ({ToolsService.NoiseQuality(radio.NoiseFloor)})"));
            Stats.Add(new(L.T("Last RSSI / SNR"), $"{radio.LastRssi} dBm · {radio.LastSnr:0.#} dB"));
            Stats.Add(new(L.T("Air time TX / RX"), $"{Formatters.Duration(radio.TxAirtimeSeconds)} · {Formatters.Duration(radio.RxAirtimeSeconds)}"));
            Stats.Add(new(L.T("Packets RX / TX"), $"{packets.Received:N0} / {packets.Sent:N0}"));
            Stats.Add(new(L.T("Flood RX / TX"), $"{packets.FloodRx:N0} / {packets.FloodTx:N0}"));
            Stats.Add(new(L.T("Direct RX / TX"), $"{packets.DirectRx:N0} / {packets.DirectTx:N0}"));
            Stats.Add(new(L.T("Errors / queue"), $"{core.Errors} / {core.QueueLength}"));
        }
        catch (Exception ex) { Message = L.F("Couldn't read stats: {0}", ex.Message); }
    }

    /// <summary>Values last shown from the radio, per field (see <see cref="Sync{T}"/>).</summary>
    private readonly Dictionary<string, object?> _shown = new();

    private T Sync<T>(string field, T current, T fromRadio)
    {
        if (_shown.TryGetValue(field, out var last) && !Equals(current, last) && Equals(last, fromRadio)) return current;
        _shown[field] = fromRadio;
        return fromRadio;
    }

    /// <summary>After writing to the radio: show exactly what it now reports.</summary>
    private void ForgetEdits(params string[] fields)
    {
        foreach (var f in fields) _shown.Remove(f);
    }

    private static readonly string[] RadioFields = [nameof(EditFrequency), nameof(EditBandwidth), nameof(EditSf), nameof(EditCr)];
    private bool _applyingPreset;

    partial void OnSelectedPresetChanged(RadioPreset? value)
    {
        if (_loading || value is null) return;
        EditFrequency = value.FrequencyMHz.ToString("0.000", CultureInfo.InvariantCulture);
        EditBandwidth = Bandwidths.MinBy(b => Math.Abs(b - value.BandwidthKHz));
        EditSf = value.SpreadingFactor;
        EditCr = value.CodingRate;
        // Picking a preset puts the radio on it (like the iPhone app), unless it's already on it.
        if (!Core.IsConnected || Core.Device.CurrentPreset()?.Id == value.Id && !(Core.Capabilities?.ClientRepeat ?? false)) return;
        _ = ApplyPickedPresetAsync(value);
    }

    private async Task ApplyPickedPresetAsync(RadioPreset preset)
    {
        if (_applyingPreset) return;
        _applyingPreset = true;
        try
        {
            var details = RadioPresets.Describe(preset, preset.FrequencyMHz, preset.BandwidthKHz, preset.SpreadingFactor, preset.CodingRate);
            if (!await AppHost.Dialogs.Confirm(L.F("Switch the radio to {0}?", preset.Name),
                    details + "\n\n" + L.T("Everyone you talk to must use the same settings; the wrong ones take you off your mesh."), L.T("Switch")))
                return;
            await Run(async () =>
            {
                await Core.Device.ApplyPresetAsync(preset, RepeatEnabled ? false : null);
                ForgetEdits(RadioFields);
            }, L.T("Radio preset"), L.F("The radio is now on the {0} preset.", preset.Name));
        }
        finally
        {
            _applyingPreset = false;
            // Show what the radio really has (the new preset, or the old one if it was cancelled or failed).
            ForgetEdits(RadioFields);
            Reload();
        }
    }

    [RelayCommand]
    private async Task ApplyName() => await Run(async () => { await Core.Device.SetNameAsync(EditName); ForgetEdits(nameof(EditName)); },
        L.T("Name"), L.T("Name updated. Send an advert so others see it."));

    [RelayCommand]
    private async Task ApplyLocation()
    {
        if (!double.TryParse(EditLat.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || lat is < -90 or > 90 ||
            !double.TryParse(EditLon.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) || lon is < -180 or > 180)
        {
            Message = L.T("Enter a valid latitude (−90…90) and longitude (−180…180).");
            return;
        }
        await Run(async () => { await Core.Device.SetLocationAsync(lat, lon); ForgetEdits(nameof(EditLat), nameof(EditLon)); }, L.T("Location"), L.T("Position updated."));
    }

    [RelayCommand]
    private async Task UsePcLocation()
    {
        if (AppHost.GetPcLocation is null) { Message = L.T("Location services aren't available on this PC."); return; }
        Message = L.T("Getting this PC's location…");
        var loc = await AppHost.GetPcLocation();
        if (loc is not { } l) { Message = L.T("Couldn't get a location. Check Windows Settings → Privacy & security → Location."); return; }
        EditLat = l.Lat.ToString("0.000000", CultureInfo.InvariantCulture);
        EditLon = l.Lon.ToString("0.000000", CultureInfo.InvariantCulture);
        Message = L.T("Location filled in from this PC — press Apply to send it to the radio.");
    }

    [RelayCommand]
    private async Task ClearLocation()
    {
        EditLat = "0.000000";
        EditLon = "0.000000";
        await Run(async () => { await Core.Device.SetLocationAsync(0, 0); ForgetEdits(nameof(EditLat), nameof(EditLon)); }, L.T("Location"), L.T("Position cleared."));
    }

    partial void OnShareLocationInAdvertsChanged(bool value)
    {
        if (_loading) return;
        _ = Run(() => Core.Device.SetOtherParamsAsync(c => c.AdvertisementLocationPolicy = (byte)(value ? 1 : 0)), L.T("Location sharing"),
            value ? L.T("Your position will be included in adverts.") : L.T("Your position will no longer be included in adverts."));
    }

    [RelayCommand]
    private async Task Advert(string mode) => await Run(() => Core.Device.SendAdvertAsync(mode == "flood"), L.T("Advert"),
        mode == "flood" ? L.T("Flood advert sent across the mesh.") : L.T("Zero-hop advert sent to nearby nodes."));

    [RelayCommand]
    private async Task ApplyRadio()
    {
        if (!double.TryParse(EditFrequency.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            Message = L.T("Enter a valid frequency in MHz.");
            return;
        }
        if (!await AppHost.Dialogs.Confirm(L.T("Change radio settings?"), L.T("All nodes you talk to must use the same frequency, bandwidth, spreading factor and coding rate. Wrong settings take you off your mesh."), L.T("Apply"))) return;
        var preset = SelectedPreset is { } sp && RadioPresets.Matching(f, EditBandwidth, (byte)EditSf, (byte)EditCr).Any(m => m.Id == sp.Id) ? sp : null;
        await Run(async () =>
        {
            await Core.Device.SetRadioAsync(f, EditBandwidth, (byte)EditSf, (byte)EditCr, RepeatEnabled ? false : null);
            Core.Device.RememberPreset(preset?.Id);
            ForgetEdits(RadioFields);
        }, L.T("Radio settings"), preset is null ? L.T("Radio settings applied.") : L.F("Radio set to the {0} preset.", preset.Name));
    }

    /// <summary>The Radio page was opened: show the stored values at once, then read them from the radio again.</summary>
    public void OnShown()
    {
        Reload();
        if (!Core.IsConnected || _refreshing) return;
        _refreshing = true;
        _ = Task.Run(async () =>
        {
            try { await Core.Device.RefreshFromRadioAsync(); }
            catch (Exception ex) { Core.Log.Debug("Radio", "Couldn't re-read the radio settings: " + ex.Message); }
            finally { _refreshing = false; }
        });
    }

    private volatile bool _refreshing;

    [RelayCommand]
    private async Task ApplyTxPower() => await Run(async () => { await Core.Device.SetTxPowerAsync(TxPower); ForgetEdits(nameof(TxPower)); },
        L.T("TX power"), L.F("TX power set to {0} dBm.", TxPower));

    partial void OnRepeatEnabledChanged(bool value)
    {
        if (_loading) return;
        var radioId = Core.RadioId;
        if (value)
        {
            if (SelectedRepeatPreset is null) { Message = L.T("Pick a repeat frequency, then enable repeat mode."); _loading = true; RepeatEnabled = false; _loading = false; return; }
            // Keep the mesh settings to go back to when repeat mode is switched off (like the iPhone app).
            if (Core.SelfInfo is { } now && radioId is not null)
                Core.Settings.Update(st => st.PreRepeatRadio[radioId] = string.Create(CultureInfo.InvariantCulture,
                    $"{now.RadioFrequency:0.000},{now.RadioBandwidth},{now.RadioSpreadingFactor},{now.RadioCodingRate}"));
            _ = Run(() => Core.Device.ApplyPresetAsync(SelectedRepeatPreset, clientRepeat: true), L.T("Repeat mode"), L.F("Repeat mode on ({0}).", SelectedRepeatPreset.Name));
        }
        else
        {
            string? saved = null;
            if (radioId is not null) Core.Settings.Current.PreRepeatRadio.TryGetValue(radioId, out saved);
            var parts = saved?.Split(',');
            if (parts is { Length: 4 } && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bw)
                && byte.TryParse(parts[2], out var sf) && byte.TryParse(parts[3], out var cr))
            {
                _ = Run(async () =>
                {
                    await Core.Device.SetRadioAsync(f, bw, sf, cr, false);
                    Core.Settings.Update(st => st.PreRepeatRadio.Remove(radioId!));
                }, L.T("Repeat mode"), L.T("Repeat mode off — your mesh settings are back."));
                return;
            }
            var p = SelectedPreset ?? Core.Device.CurrentPreset();
            if (p is null) { _ = Run(async () => { var s = Core.SelfInfo!; await Core.Device.SetRadioAsync(s.RadioFrequency, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate, false); }, L.T("Repeat mode"), L.T("Repeat mode off.")); return; }
            _ = Run(() => Core.Device.ApplyPresetAsync(p, clientRepeat: false), L.T("Repeat mode"), L.T("Repeat mode off."));
        }
    }

    [RelayCommand]
    private async Task ApplyAutoAdd()
    {
        byte mask = 0;
        if (AutoAddContacts) mask |= AutoAddConfig.ContactsBit;
        if (AutoAddRepeaters) mask |= AutoAddConfig.RepeatersBit;
        if (AutoAddRooms) mask |= AutoAddConfig.RoomServersBit;
        if (AutoAddSensors) mask |= AutoAddConfig.SensorsBit;
        if (OverwriteOldest) mask |= AutoAddConfig.OverwriteOldestBit;
        await Run(async () =>
        {
            await Core.Device.SetOtherParamsAsync(c => c.ManualAddContacts = !AutoAddEnabled);
            try { await Core.Device.SetAutoAddAsync(new AutoAddConfig(mask, (byte)Math.Clamp(AutoAddMaxHops, 0, 63))); } catch (MeshCoreException) { /* older firmware: only the manual flag */ }
        }, L.T("Contact settings"), L.T("Contact settings saved."));
    }

    [RelayCommand]
    private async Task ApplyTelemetry() => await Run(() => Core.Device.SetOtherParamsAsync(c =>
    {
        c.TelemetryModeBase = (byte)TelemetryBase;
        c.TelemetryModeLocation = (byte)TelemetryLocation;
        c.TelemetryModeEnvironment = (byte)TelemetryEnvironment;
    }), L.T("Telemetry"), L.T("Telemetry sharing updated."));

    partial void OnMultiAcksChanged(bool value)
    {
        if (_loading) return;
        _ = Run(() => Core.Device.SetOtherParamsAsync(c => c.MultiAcks = (byte)(value ? 1 : 0)), L.T("Extra ACKs"), value ? L.T("Extra ACKs enabled.") : L.T("Extra ACKs disabled."));
    }

    partial void OnSelectedOcvChanged(OcvPresets.Preset? value)
    {
        if (_loading || value is null || Core.Radio is null) return;
        Core.UpdateRadioRecord(r => r.OcvPreset = value.Id);
        BatteryText = Core.Battery is { } b ? $"{Core.BatteryPercent}% · {b.Level / 1000.0:0.00} V" : "–";
    }

    [RelayCommand]
    private void ApplyCustomOcv()
    {
        if (OcvPresets.ParseCustom(CustomOcv) is null) { Message = L.T("Enter 11 millivolt values from 100% down to 0%, separated by commas."); return; }
        Core.UpdateRadioRecord(r => { r.OcvPreset = "custom"; r.CustomOcv = CustomOcv; });
        Message = L.T("Custom battery curve saved.");
        Reload();
    }

    partial void OnPathHashModeChanged(int value)
    {
        if (_loading) return;
        _ = Run(() => Core.Device.SetPathHashModeAsync((byte)value), L.T("Path hash size"),
            L.Plural(value + 1, "Path hash size set to {0} byte.", "Path hash size set to {0} bytes."));
    }

    [RelayCommand]
    private async Task ApplyTuning()
    {
        if (!double.TryParse(RxDelayBase, NumberStyles.Float, CultureInfo.InvariantCulture, out var rx) ||
            !double.TryParse(AirtimeFactor, NumberStyles.Float, CultureInfo.InvariantCulture, out var af)) { Message = L.T("Enter numeric tuning values."); return; }
        await Run(() => Core.Device.SetTuningAsync(rx, af), L.T("Tuning"), L.T("Tuning parameters saved."));
    }

    [RelayCommand]
    private async Task ApplyBlePin()
    {
        if (!uint.TryParse(BlePin.Trim().Length == 0 ? "0" : BlePin.Trim(), out var pin)) { Message = L.T("The PIN must be 6 digits (or empty to use a random PIN)."); return; }
        await Run(() => Core.Device.SetBlePinAsync(pin), L.T("Bluetooth PIN"), L.T("Bluetooth PIN saved. It applies after the radio reboots."));
    }

    [RelayCommand]
    private async Task ApplyDefaultScope() => await Run(async () =>
    {
        await Core.Device.SetDefaultFloodScopeAsync(DefaultScope);
        await Core.Channels.RefreshAsync();
    }, L.T("Default region"), string.IsNullOrWhiteSpace(DefaultScope)
        ? L.T("Default flood scope cleared.")
        : L.F("Messages now flood only within region \"{0}\" by default.", DefaultScope.Trim()));

    [RelayCommand]
    private void SaveKnownRegions()
    {
        Core.RxLog.SetKnownRegions(KnownRegions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Message = L.T("Known regions saved (used to label region-scoped packets).");
    }

    [RelayCommand]
    private async Task SetVar()
    {
        if (string.IsNullOrWhiteSpace(NewVarKey)) return;
        await Run(() => Core.Device.SetCustomVarAsync(NewVarKey.Trim(), NewVarValue.Trim()), L.T("Custom variable"), L.F("{0} saved.", NewVarKey.Trim()));
        await LoadAdvanced();
    }

    [RelayCommand]
    private void EditVar(CustomVarRow row) { NewVarKey = row.Key; NewVarValue = row.Value; }

    [RelayCommand]
    private async Task SyncClock() => await Run(() => Core.Device.SyncClockAsync(), L.T("Clock"), L.T("Radio clock set to this PC's time."));

    [RelayCommand]
    private async Task ExportKey()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Export private key?"), L.T("Anyone with this key can impersonate your radio and read your direct messages. Store it somewhere safe."), L.T("Export"))) return;
        await Run(async () =>
        {
            var key = await Core.Device.ExportPrivateKeyAsync();
            var path = await AppHost.Dialogs.PickSaveFile(L.T("Save private key"), $"{DeviceName}-private-key.txt", "txt", L.T("Text file"));
            if (path is not null) await File.WriteAllTextAsync(path, Convert.ToHexString(key));
        }, L.T("Export key"), L.T("Private key exported."));
    }

    [RelayCommand]
    private async Task ImportKey()
    {
        var hex = await AppHost.Dialogs.Prompt(L.T("Import private key"), L.T("Paste the 64-byte private key (128 hex characters). This replaces your radio's identity."), "", password: true);
        if (string.IsNullOrWhiteSpace(hex)) return;
        var key = Bytes.FromHex(hex.Trim());
        if (key is not { Length: 64 }) { Message = L.T("That isn't a 64-byte hex key."); return; }
        if (!await AppHost.Dialogs.Confirm(L.T("Replace identity?"), L.T("Your radio will take on the imported identity. Contacts will see it as that node."), L.T("Replace"), true)) return;
        await Run(() => Core.Device.ImportPrivateKeyAsync(key), L.T("Import key"), L.T("Private key imported."));
    }

    [RelayCommand]
    private async Task Reboot()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Reboot radio?"), L.T("The connection will drop and reconnect automatically."), L.T("Reboot"))) return;
        await Run(() => Core.Device.RebootAsync(), L.T("Reboot"), L.T("Reboot requested."));
    }

    [RelayCommand]
    private async Task FactoryReset()
    {
        if (!await AppHost.Dialogs.Confirm(L.T("Factory reset radio?"), L.T("Erases ALL settings, contacts, channels and the identity on the radio. This can't be undone."), L.T("Erase everything"), true)) return;
        // The word to type stays "RESET" in every language (it's compared below).
        var confirm = await AppHost.Dialogs.Prompt(L.T("Confirm factory reset"), L.F("Type {0} to confirm.", "RESET"), "");
        if (confirm?.Trim() != "RESET") return;
        await Run(() => Core.Device.FactoryResetAsync(), L.T("Factory reset"), L.T("Factory reset requested."));
    }

    [RelayCommand]
    private async Task ForgetRadio(KnownRadioRow row)
    {
        if (row.IsCurrent && Core.IsConnected) { Message = L.T("Disconnect before forgetting the connected radio."); return; }
        if (!await AppHost.Dialogs.Confirm(L.F("Forget {0}?", row.Name), L.T("Deletes all messages, contacts and history stored on this PC for this radio."), L.T("Forget"), true)) return;
        Core.ForgetRadio(row.Radio.Id);
        Reload();
        _main.Chats.Reload();
    }

    [RelayCommand]
    private async Task CopyPublicKey()
    {
        await AppHost.Dialogs.CopyToClipboard(PublicKey);
        Message = L.T("Public key copied.");
    }

    [RelayCommand]
    private void OpenConnect() => _ = _main.ShowConnectDialog();

    [RelayCommand]
    private async Task Resync() => await Run(() => Core.ResyncAsync(), L.T("Sync"), L.T("Contacts, channels and messages re-synced."));
}
