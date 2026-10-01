using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Companion radio configuration (identity, position, radio, telemetry policy, security).</summary>
public sealed class DeviceService(MeshApp app)
{
    private MeshCoreSession S => app.RequireSession();

    private async Task RefreshSelfAsync()
    {
        var info = await S.SendAppStartAsync().ConfigureAwait(false);
        app.RefreshSelfInfo(info);
    }

    public async Task SetNameAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new MessageServiceException(L.T("Name can't be empty."));
        await S.SetNameAsync(name).ConfigureAwait(false);
        await RefreshSelfAsync().ConfigureAwait(false);
    }

    public async Task SetLocationAsync(double lat, double lon)
    {
        await S.SetCoordinatesAsync(lat, lon).ConfigureAwait(false);
        await RefreshSelfAsync().ConfigureAwait(false);
    }

    public async Task SendAdvertAsync(bool flood)
    {
        await S.SendAdvertisementAsync(flood).ConfigureAwait(false);
        app.Log.Info("Device", flood ? "Sent flood advert" : "Sent zero-hop advert");
    }

    public async Task SetRadioAsync(double freqMHz, double bwKHz, byte sf, byte cr, bool? clientRepeat = null)
    {
        if (freqMHz is < 150 or > 2500) throw new MessageServiceException(L.T("Frequency must be between 150 and 2500 MHz."));
        if (sf is < 5 or > 12) throw new MessageServiceException(L.T("Spreading factor must be 5–12."));
        if (cr is < 5 or > 8) throw new MessageServiceException(L.T("Coding rate must be 5–8."));
        app.Log.Info("Radio", string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Setting radio: {freqMHz:0.000} MHz, BW {bwKHz} kHz, SF{sf}, CR{cr}{(clientRepeat is { } rr ? ", repeat " + (rr ? "on" : "off") : "")}"));
        await S.SetRadioAsync(freqMHz, bwKHz, sf, cr, clientRepeat).ConfigureAwait(false);
        await RefreshSelfAsync().ConfigureAwait(false);
        if (clientRepeat is not null)
        {
            // Repeat mode lives in the device info: read it back so the app shows what the radio now does.
            try { await app.ResyncCapabilitiesAsync().ConfigureAwait(false); } catch { /* shown after the next sync */ }
        }
        // Like the iPhone app: read the settings back and make sure the radio really took them.
        if (app.SelfInfo is { } now && (Math.Abs(now.RadioFrequency - freqMHz) > 0.001 || Math.Abs(now.RadioBandwidth - bwKHz) > 0.001
                                        || now.RadioSpreadingFactor != sf || now.RadioCodingRate != cr))
        {
            var reported = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{now.RadioFrequency:0.000} MHz, BW {now.RadioBandwidth} kHz, SF{now.RadioSpreadingFactor}, CR{now.RadioCodingRate}");
            app.Log.Warn("Radio", "The radio didn't keep the new settings; it reports " + reported);
            throw new MessageServiceException(L.F("The radio didn't keep the new settings — it still reports {0}. Try again, or restart the radio.", reported));
        }
    }

    /// <summary>Puts the radio on a preset (and its path hash size, if the preset has one) and remembers the choice.</summary>
    public async Task ApplyPresetAsync(RadioPreset p, bool? clientRepeat = null)
    {
        await SetRadioAsync(p.FrequencyMHz, p.BandwidthKHz, p.SpreadingFactor, p.CodingRate, clientRepeat).ConfigureAwait(false);
        if (p.RepeatSectionHeader is not null) return;
        if (p.PathHashSize is { } size && app.Capabilities?.SupportsPathHashMode == true && app.Capabilities.PathHashMode != size - 1)
        {
            try { await SetPathHashModeAsync((byte)(size - 1)).ConfigureAwait(false); }
            catch (Exception ex) { app.Log.Warn("Radio", "Path hash size not changed: " + ex.Message); }
        }
        RememberPreset(p.Id);
        app.Notify(DataKind.Radio);
    }

    /// <summary>Remembers which preset was applied, to tell apart presets with the same settings (USA / Canada).</summary>
    public void RememberPreset(string? presetId)
    {
        if (app.RadioId is not { } radioId) return;
        app.Settings.Update(s =>
        {
            if (presetId is null) s.AppliedRadioPresets.Remove(radioId);
            else s.AppliedRadioPresets[radioId] = presetId;
        });
    }

    /// <summary>The preset the connected (or last) radio is on, or null for custom settings.</summary>
    public RadioPreset? CurrentPreset()
    {
        if (app.SelfInfo is not { } s) return null;
        var preferred = app.RadioId is { } id && app.Settings.Current.AppliedRadioPresets.TryGetValue(id, out var p) ? p : null;
        return RadioPresets.Resolve(s.RadioFrequency, s.RadioBandwidth, s.RadioSpreadingFactor, s.RadioCodingRate, preferred);
    }

    /// <summary>Reads the radio's settings again (name, position, radio, power, repeat mode…) so the app shows exactly what it has.</summary>
    public async Task RefreshFromRadioAsync()
    {
        await RefreshSelfAsync().ConfigureAwait(false);
        await app.ResyncCapabilitiesAsync().ConfigureAwait(false);
    }

    public async Task SetTxPowerAsync(int dbm)
    {
        var max = app.SelfInfo?.MaxTxPower ?? 30;
        if (dbm < PacketBuilder.TxPowerFloor || dbm > max) throw new MessageServiceException(L.F("TX power must be between {0} and {1} dBm.", PacketBuilder.TxPowerFloor, max));
        await S.SetTxPowerAsync((sbyte)dbm).ConfigureAwait(false);
        await RefreshSelfAsync().ConfigureAwait(false);
    }

    public async Task SetOtherParamsAsync(Action<OtherParamsConfig> change)
    {
        await S.MutateOtherParamsAsync(change).ConfigureAwait(false);
        if (S.SelfInfo is { } s) app.RefreshSelfInfo(s);
    }

    public Task<AutoAddConfig> GetAutoAddAsync() => S.GetAutoAddConfigAsync();
    public Task SetAutoAddAsync(AutoAddConfig c) => S.SetAutoAddConfigAsync(c);

    public async Task SetPathHashModeAsync(byte mode)
    {
        await S.SetPathHashModeAsync(mode).ConfigureAwait(false);
        await app.ResyncCapabilitiesAsync().ConfigureAwait(false);
    }

    public Task SetBlePinAsync(uint pin)
    {
        if (pin != 0 && (pin < 100000 || pin > 999999)) throw new MessageServiceException(L.T("The PIN must have 6 digits (or 0 to disable)."));
        return S.SetDevicePinAsync(pin);
    }

    public Task<TuningParamsResponse> GetTuningAsync() => S.GetTuningParamsAsync();
    public Task SetTuningAsync(double rxDelayBase, double airtimeFactor) =>
        S.SetTuningAsync((uint)Math.Round(rxDelayBase * 1000), (uint)Math.Round(airtimeFactor * 1000));

    public Task<IReadOnlyDictionary<string, string>> GetCustomVarsAsync() => S.GetCustomVarsAsync();
    public Task SetCustomVarAsync(string key, string value) => S.SetCustomVarAsync(key, value);

    public Task<byte[]> ExportPrivateKeyAsync() => S.ExportPrivateKeyAsync();

    public async Task ImportPrivateKeyAsync(byte[] key)
    {
        await S.ImportPrivateKeyAsync(key).ConfigureAwait(false);
        if (S.SelfInfo is { } s) app.RefreshSelfInfo(s);
    }

    public Task<IReadOnlyList<FrequencyRange>> GetRepeatFrequenciesAsync() => S.GetRepeatFreqAsync();

    public async Task<(CoreStats Core, RadioStats Radio, PacketStats Packets)> GetStatsAsync()
    {
        var core = await S.GetStatsCoreAsync().ConfigureAwait(false);
        var radio = await S.GetStatsRadioAsync().ConfigureAwait(false);
        var packets = await S.GetStatsPacketsAsync().ConfigureAwait(false);
        return (core, radio, packets);
    }

    public Task<TelemetryResponse> GetSelfTelemetryAsync() => S.GetSelfTelemetryAsync();

    public async Task<DefaultFloodScope?> GetDefaultFloodScopeAsync() => await S.GetDefaultFloodScopeAsync().ConfigureAwait(false);

    public async Task SetDefaultFloodScopeAsync(string? regionName)
    {
        if (string.IsNullOrWhiteSpace(regionName)) await S.SetDefaultFloodScopeAsync("", new FloodScope.Disabled()).ConfigureAwait(false);
        else await S.SetDefaultFloodScopeAsync(regionName.Trim(), new FloodScope.Region(regionName.Trim())).ConfigureAwait(false);
    }

    public async Task RebootAsync()
    {
        await S.RebootAsync().ConfigureAwait(false);
        app.Log.Info("Device", "Reboot requested");
    }

    public async Task FactoryResetAsync()
    {
        await S.FactoryResetAsync().ConfigureAwait(false);
        app.Log.Warn("Device", "Factory reset requested");
    }

    public Task SyncClockAsync() => S.SetTimeAsync(DateTimeOffset.UtcNow);
}

public sealed partial class MeshApp
{
    internal async Task ResyncCapabilitiesAsync()
    {
        if (Session is not { IsRunning: true } s) return;
        Capabilities = await s.QueryDeviceAsync().ConfigureAwait(false);
        StoreRadioInfo();
        Notify(DataKind.Radio);
        try { StatusChanged?.Invoke(); } catch { /* ignore */ }
    }
}
