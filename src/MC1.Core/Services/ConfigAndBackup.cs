using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>MeshCore node config JSON (compatible with the MeshCore apps' config export).</summary>
public sealed class NodeConfigFile
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
    [JsonPropertyName("private_key")] public string? PrivateKey { get; set; }
    [JsonPropertyName("radio_settings")] public RadioSettingsConfig? RadioSettings { get; set; }
    [JsonPropertyName("position_settings")] public PositionConfig? PositionSettings { get; set; }
    [JsonPropertyName("other_settings")] public OtherSettingsConfig? OtherSettings { get; set; }
    [JsonPropertyName("channels")] public List<ChannelConfig>? Channels { get; set; }
    [JsonPropertyName("contacts")] public List<ContactConfig>? Contacts { get; set; }

    public sealed class RadioSettingsConfig
    {
        [JsonPropertyName("frequency")] public uint Frequency { get; set; }
        [JsonPropertyName("bandwidth")] public uint Bandwidth { get; set; }
        [JsonPropertyName("spreading_factor")] public byte SpreadingFactor { get; set; }
        [JsonPropertyName("coding_rate")] public byte CodingRate { get; set; }
        [JsonPropertyName("tx_power")] public sbyte TxPower { get; set; }
    }

    public sealed class PositionConfig
    {
        [JsonPropertyName("latitude")] public string Latitude { get; set; } = "0";
        [JsonPropertyName("longitude")] public string Longitude { get; set; } = "0";
    }

    public sealed class OtherSettingsConfig
    {
        [JsonPropertyName("manual_add_contacts")] public byte? ManualAddContacts { get; set; }
        [JsonPropertyName("advert_location_policy")] public byte? AdvertLocationPolicy { get; set; }
        [JsonPropertyName("telemetry_mode_base")] public byte? TelemetryModeBase { get; set; }
        [JsonPropertyName("telemetry_mode_location")] public byte? TelemetryModeLocation { get; set; }
        [JsonPropertyName("telemetry_mode_environment")] public byte? TelemetryModeEnvironment { get; set; }
        [JsonPropertyName("multi_acks")] public byte? MultiAcks { get; set; }
        [JsonPropertyName("advertisement_type")] public byte? AdvertisementType { get; set; }
    }

    public sealed class ChannelConfig
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("secret")] public string Secret { get; set; } = "";
    }

    public sealed class ContactConfig
    {
        [JsonPropertyName("type")] public byte Type { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("custom_name")] public string? CustomName { get; set; }
        [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
        [JsonPropertyName("flags")] public byte Flags { get; set; }
        [JsonPropertyName("latitude")] public string Latitude { get; set; } = "0";
        [JsonPropertyName("longitude")] public string Longitude { get; set; } = "0";
        [JsonPropertyName("last_advert")] public uint LastAdvert { get; set; }
        [JsonPropertyName("last_modified")] public uint LastModified { get; set; }
        [JsonPropertyName("out_path")] public string? OutPath { get; set; }
        [JsonPropertyName("path_hash_mode")] public byte? PathHashMode { get; set; }
    }
}

[Flags]
public enum ConfigSections { None = 0, Identity = 1, Radio = 2, Position = 4, Other = 8, Channels = 16, Contacts = 32, All = 63 }

public sealed class NodeConfigService(MeshApp app)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<string> ExportAsync(ConfigSections sections)
    {
        var s = app.RequireSession();
        var self = await s.SendAppStartAsync().ConfigureAwait(false);
        var cfg = new NodeConfigFile();
        if (sections.HasFlag(ConfigSections.Identity))
        {
            cfg.Name = self.Name;
            cfg.PublicKey = self.PublicKey.ToHex();
            try { cfg.PrivateKey = (await s.ExportPrivateKeyAsync().ConfigureAwait(false)).ToHex(); }
            catch (MeshCoreException ex) when (ex.Kind == MeshCoreErrorKind.FeatureDisabled) { app.Log.Warn("Config", "Private key export disabled by firmware"); }
        }
        if (sections.HasFlag(ConfigSections.Radio))
            cfg.RadioSettings = new() { Frequency = (uint)Math.Round(self.RadioFrequency * 1000), Bandwidth = (uint)Math.Round(self.RadioBandwidth * 1000), SpreadingFactor = self.RadioSpreadingFactor, CodingRate = self.RadioCodingRate, TxPower = self.TxPower };
        if (sections.HasFlag(ConfigSections.Position))
            cfg.PositionSettings = new() { Latitude = self.Latitude.ToString(Inv), Longitude = self.Longitude.ToString(Inv) };
        if (sections.HasFlag(ConfigSections.Other))
            cfg.OtherSettings = new() { ManualAddContacts = self.ManualAddContacts ? (byte)1 : (byte)0, AdvertLocationPolicy = self.AdvertisementLocationPolicy };
        if (sections.HasFlag(ConfigSections.Channels))
        {
            var caps = await s.QueryDeviceAsync().ConfigureAwait(false);
            var (received, _) = await s.GetChannelsAsync(Enumerable.Range(0, caps.MaxChannels).Select(i => (byte)i).ToList()).ConfigureAwait(false);
            cfg.Channels = received.Where(c => c.IsConfigured).Select(c => new NodeConfigFile.ChannelConfig { Name = c.Name, Secret = c.Secret.ToHex() }).ToList();
        }
        if (sections.HasFlag(ConfigSections.Contacts))
        {
            var contacts = await s.GetContactsAsync().ConfigureAwait(false);
            cfg.Contacts = contacts.Contacts.Select(c => new NodeConfigFile.ContactConfig
            {
                Type = c.TypeRawValue, Name = c.AdvertisedName, PublicKey = c.PublicKey.ToHex(), Flags = (byte)c.Flags,
                Latitude = c.Latitude.ToString(Inv), Longitude = c.Longitude.ToString(Inv),
                LastAdvert = (uint)c.LastAdvertisement.ToUnixTimeSeconds(), LastModified = (uint)c.LastModified.ToUnixTimeSeconds(),
                OutPath = c.IsFloodPath ? null : c.PathByteLength > 0 ? c.OutPath.Prefix(c.PathByteLength).ToHex() : "",
                PathHashMode = c.IsFloodPath ? null : (byte)(c.OutPathLength >> 6),
            }).ToList();
        }
        return JsonSerializer.Serialize(cfg, JsonOptions);
    }

    public static NodeConfigFile Parse(string json) =>
        JsonSerializer.Deserialize<NodeConfigFile>(json) ?? throw new InvalidDataException(L.T("Empty config file"));

    public static ConfigSections SectionsIn(NodeConfigFile c)
    {
        var s = ConfigSections.None;
        if (c.PrivateKey is not null || c.Name is not null) s |= ConfigSections.Identity;
        if (c.RadioSettings is not null) s |= ConfigSections.Radio;
        if (c.PositionSettings is not null) s |= ConfigSections.Position;
        if (c.OtherSettings is not null) s |= ConfigSections.Other;
        if (c.Channels is { Count: > 0 }) s |= ConfigSections.Channels;
        if (c.Contacts is { Count: > 0 }) s |= ConfigSections.Contacts;
        return s;
    }

    /// <summary>Applies a config file to the connected radio. Validates everything before writing.</summary>
    public async Task ImportAsync(NodeConfigFile cfg, ConfigSections sections, IProgress<string>? progress = null)
    {
        var s = app.RequireSession();
        byte[]? privateKey = null;
        if (sections.HasFlag(ConfigSections.Identity) && cfg.PrivateKey is { } pk)
        {
            privateKey = Bytes.FromHex(pk);
            if (privateKey is not { Length: 64 }) throw new InvalidDataException(L.F("Invalid private key ({0} hex chars, expected 128)", pk.Length));
        }
        var channels = new List<(string Name, byte[] Secret)>();
        if (sections.HasFlag(ConfigSections.Channels) && cfg.Channels is not null)
            foreach (var (c, i) in cfg.Channels.Select((c, i) => (c, i)))
                channels.Add((c.Name, Bytes.FromHex(c.Secret) is { Length: 16 } sec ? sec : throw new InvalidDataException(L.F("Channel {0} has an invalid secret", i))));
        var contacts = new List<MeshContact>();
        if (sections.HasFlag(ConfigSections.Contacts) && cfg.Contacts is not null)
            foreach (var c in cfg.Contacts)
            {
                contacts.Add(ToMeshContact(c));
            }

        if (sections.HasFlag(ConfigSections.Position) && cfg.PositionSettings is { } pos)
        {
            progress?.Report(L.T("Position"));
            await s.SetCoordinatesAsync(ParseDouble(pos.Latitude), ParseDouble(pos.Longitude)).ConfigureAwait(false);
        }
        if (sections.HasFlag(ConfigSections.Other) && cfg.OtherSettings is { } o)
        {
            progress?.Report(L.T("Other settings"));
            await s.MutateOtherParamsAsync(p =>
            {
                if (o.ManualAddContacts is { } m) p.ManualAddContacts = m != 0;
                if (o.AdvertLocationPolicy is { } a) p.AdvertisementLocationPolicy = a;
                if (o.TelemetryModeBase is { } tb) p.TelemetryModeBase = tb;
                if (o.TelemetryModeLocation is { } tl) p.TelemetryModeLocation = tl;
                if (o.TelemetryModeEnvironment is { } te) p.TelemetryModeEnvironment = te;
                if (o.MultiAcks is { } ma) p.MultiAcks = ma;
            }).ConfigureAwait(false);
        }
        if (privateKey is not null)
        {
            progress?.Report(L.T("Private key"));
            await s.ImportPrivateKeyAsync(privateKey).ConfigureAwait(false);
        }
        if (sections.HasFlag(ConfigSections.Identity) && cfg.Name is { Length: > 0 } name)
        {
            progress?.Report(L.T("Node name"));
            await s.SetNameAsync(name).ConfigureAwait(false);
        }
        if (sections.HasFlag(ConfigSections.Radio) && cfg.RadioSettings is { } r)
        {
            progress?.Report(L.T("Radio parameters"));
            await s.SetRadioAsync(r.Frequency / 1000.0, r.Bandwidth / 1000.0, r.SpreadingFactor, r.CodingRate).ConfigureAwait(false);
            progress?.Report(L.T("TX power"));
            await s.SetTxPowerAsync(r.TxPower).ConfigureAwait(false);
        }
        if (channels.Count > 0)
        {
            var existing = app.Channels.GetAll();
            var used = existing.Select(c => c.Idx).ToHashSet();
            var max = app.Capabilities?.MaxChannels ?? 8;
            foreach (var (n, secret) in channels)
            {
                if (existing.Any(c => c.Secret.SequenceEquals(secret))) continue;
                var slot = Enumerable.Range(0, max).FirstOrDefault(i => !used.Contains(i), -1);
                if (slot < 0) throw new InvalidOperationException(L.F("No empty channel slot available for \"{0}\"", n));
                progress?.Report(L.F("Channel {0}", n));
                await app.Channels.SetAsync(slot, n, secret).ConfigureAwait(false);
                used.Add(slot);
            }
        }
        foreach (var c in contacts)
        {
            progress?.Report(L.F("Contact {0}", c.AdvertisedName));
            await s.AddContactAsync(c).ConfigureAwait(false);
        }
        progress?.Report(L.T("Refreshing"));
        app.RefreshSelfInfo(await s.SendAppStartAsync().ConfigureAwait(false));
        if (contacts.Count > 0) await app.SyncContactsAsync(false).ConfigureAwait(false);
    }

    private static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0;

    /// <summary>A contact entry of a config file as a radio contact (validated).</summary>
    public static MeshContact ToMeshContact(NodeConfigFile.ContactConfig c)
    {
        var key = Bytes.FromHex(c.PublicKey);
        if (key is not { Length: 32 }) throw new InvalidDataException(L.F("Contact \"{0}\" has an invalid public key", c.Name));
        byte pathLen = 0xFF;
        byte[] path = [];
        if (c.OutPath is { } op)
        {
            path = Bytes.FromHex(op) ?? throw new InvalidDataException(L.F("Contact \"{0}\" has an invalid routing path", c.Name));
            var mode = c.PathHashMode ?? 0;
            if (mode > 2) throw new InvalidDataException(L.F("Contact \"{0}\" has unsupported path hash mode {1}", c.Name, mode));
            pathLen = PathEncoding.Encode(mode + 1, path.Length / (mode + 1));
        }
        var type = Enum.IsDefined(typeof(ContactType), c.Type) ? (ContactType)c.Type : ContactType.Chat;
        return new MeshContact(key, type, c.Type, (ContactFlags)c.Flags, pathLen, path, c.Name,
            DateTimeOffset.FromUnixTimeSeconds(c.LastAdvert), ParseDouble(c.Latitude), ParseDouble(c.Longitude), DateTimeOffset.FromUnixTimeSeconds(c.LastModified));
    }
}

/// <summary>Whole-app backup/restore (database + settings) as a .zip.</summary>
public sealed class BackupService(MeshApp app)
{
    public async Task ExportAsync(string zipPath)
    {
        app.Db.Checkpoint();
        var tmp = Path.Combine(Path.GetTempPath(), "mc1-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            using (var src = app.Db.Open())
            {
                var dbCopy = Path.Combine(tmp, "meshcoreone.db");
                using var dest = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbCopy};Pooling=False");
                dest.Open();
                src.BackupDatabase(dest);
            }
            await File.WriteAllTextAsync(Path.Combine(tmp, "settings.json"), app.Settings.Export()).ConfigureAwait(false);
            if (Directory.Exists(AppPaths.Backgrounds))
            {
                var bg = Directory.CreateDirectory(Path.Combine(tmp, "backgrounds"));
                foreach (var f in Directory.EnumerateFiles(AppPaths.Backgrounds)) File.Copy(f, Path.Combine(bg.FullName, Path.GetFileName(f)));
            }
            await File.WriteAllTextAsync(Path.Combine(tmp, "backup.json"), JsonSerializer.Serialize(new
            {
                format = "MeshCoreOne-Windows-Backup",
                version = 1,
                created = DateTimeOffset.Now,
                radios = app.Db.GetRadios().Select(r => new { r.Id, r.Name }),
            })).ConfigureAwait(false);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tmp, zipPath);
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* ignore */ } }
    }

    /// <summary>Restores a backup. The app must be disconnected; restart is recommended afterwards.</summary>
    public async Task ImportAsync(string zipPath)
    {
        if (app.IsConnected) await app.DisconnectAsync().ConfigureAwait(false);
        var tmp = Path.Combine(Path.GetTempPath(), "mc1-restore-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zipPath, tmp);
        try
        {
            var meta = Path.Combine(tmp, "backup.json");
            if (!File.Exists(meta) || !File.ReadAllText(meta).Contains("MeshCoreOne-Windows-Backup")) throw new InvalidDataException(L.T("This isn't a MeshCore for Windows backup."));
            var db = Path.Combine(tmp, "meshcoreone.db");
            if (File.Exists(db))
            {
                using var src = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False;Mode=ReadOnly");
                src.Open();
                using var dest = app.Db.Open();
                src.BackupDatabase(dest);
            }
            var backgrounds = Path.Combine(tmp, "backgrounds");
            if (Directory.Exists(backgrounds))
            {
                Directory.CreateDirectory(AppPaths.Backgrounds);
                foreach (var f in Directory.EnumerateFiles(backgrounds)) File.Copy(f, Path.Combine(AppPaths.Backgrounds, Path.GetFileName(f)), true);
            }
            var settings = Path.Combine(tmp, "settings.json");
            if (File.Exists(settings)) app.Settings.Import(await File.ReadAllTextAsync(settings).ConfigureAwait(false));
            app.Notify(DataKind.Radio);
            app.Notify(DataKind.Contacts);
            app.Notify(DataKind.Conversations);
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* ignore */ } }
    }
}
