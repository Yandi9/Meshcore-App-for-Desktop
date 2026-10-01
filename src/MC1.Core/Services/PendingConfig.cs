using System.Text.Json;
using System.Text.Json.Serialization;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>A channel restored into a slot, to be written to the radio.</summary>
public sealed class PendingChannel
{
    public int Idx { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
}

/// <summary>What a backup restored while disconnected still has to write to the radio itself.</summary>
public sealed class PendingRadioConfig
{
    public string RadioId { get; set; } = "";
    public string RadioName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Identity (the name only), Radio, Position and Other.</summary>
    public ConfigSections Sections { get; set; }
    public NodeConfigFile Config { get; set; } = new();
    public List<PendingChannel> Channels { get; set; } = [];
    public List<NodeConfigFile.ContactConfig> Contacts { get; set; } = [];

    /// <summary>A list of what's waiting ("radio settings, 2 channels"), in the app's language (for display only).</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Sections.HasFlag(ConfigSections.Identity) && Config.Name is { } n) parts.Add(L.F("name \"{0}\"", n));
            if (Sections.HasFlag(ConfigSections.Radio)) parts.Add(L.T("radio settings"));
            if (Sections.HasFlag(ConfigSections.Position)) parts.Add(L.T("position"));
            if (Sections.HasFlag(ConfigSections.Other)) parts.Add(L.T("other settings"));
            if (Channels.Count > 0) parts.Add(L.Plural(Channels.Count, "{0} channel", "{0} channels"));
            if (Contacts.Count > 0) parts.Add(L.Plural(Contacts.Count, "{0} contact", "{0} contacts"));
            return parts.Count == 0 ? L.T("nothing") : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Keeps a restored configuration until its radio connects, then writes it (after the user agrees). While it waits,
/// the restored contacts and channels are kept in the app even though the radio doesn't have them yet.
/// </summary>
public sealed class PendingConfigService(MeshApp app)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private PendingRadioConfig? _current;
    private bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.Root, "pending-radio-config.json");

    public PendingRadioConfig? Current
    {
        get
        {
            if (_loaded) return _current;
            _loaded = true;
            try
            {
                if (File.Exists(FilePath)) _current = JsonSerializer.Deserialize<PendingRadioConfig>(File.ReadAllText(FilePath), Json);
            }
            catch (Exception ex) { app.Log.Warn("Restore", "Pending radio configuration unreadable: " + ex.Message); }
            return _current;
        }
    }

    public bool IsFor(string? radioId) => radioId is not null && Current?.RadioId == radioId;
    public bool ProtectsContacts(string radioId) => IsFor(radioId) && Current!.Contacts.Count > 0;
    public bool ProtectsChannel(string radioId, int idx) => IsFor(radioId) && Current!.Channels.Any(c => c.Idx == idx);

    public void Save(PendingRadioConfig config)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Json));
        _current = config;
        _loaded = true;
        app.Notify(DataKind.Radio);
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* retried next time */ }
        _current = null;
        _loaded = true;
        app.Notify(DataKind.Radio);
    }

    /// <summary>Writes the waiting configuration to the connected radio. Returns a short description of what was done.</summary>
    public async Task<string> ApplyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p = Current ?? throw new InvalidOperationException(L.T("Nothing is waiting to be written."));
        var radioId = app.RequireRadioId();
        if (p.RadioId != radioId) throw new InvalidOperationException(L.F("The restored configuration is for {0}, not the connected radio.", p.RadioName));
        var session = app.RequireSession();

        var settings = p.Sections & (ConfigSections.Identity | ConfigSections.Radio | ConfigSections.Position | ConfigSections.Other);
        if (settings != ConfigSections.None)
        {
            var cfg = p.Config;
            cfg.PrivateKey = null;
            cfg.Channels = null;
            cfg.Contacts = null;
            await app.NodeConfig.ImportAsync(cfg, settings, progress).ConfigureAwait(false);
        }

        var channelsWritten = 0;
        if (p.Channels.Count > 0)
        {
            var max = app.Capabilities?.MaxChannels is > 0 and var m ? m : 8;
            var (onRadio, _) = await session.GetChannelsAsync(Enumerable.Range(0, max).Select(i => (byte)i).ToList(), ct).ConfigureAwait(false);
            var slots = onRadio.ToDictionary(c => (int)c.Index);
            var claimed = p.Channels.Select(c => c.Idx).ToHashSet();
            foreach (var ch in p.Channels)
            {
                var secret = Bytes.FromHex(ch.Secret);
                if (secret is not { Length: 16 }) continue;
                progress?.Report(L.F("Channel {0}", ch.Name));
                var elsewhere = slots.Values.FirstOrDefault(s => s.IsConfigured && s.Secret.SequenceEquals(secret));
                var own = slots.GetValueOrDefault(ch.Idx);
                if (elsewhere is not null)
                {
                    // The radio already has this channel (maybe in another slot): keep the restored messages with it.
                    if (elsewhere.Index != ch.Idx && own is not { IsConfigured: true }) app.Db.MoveChannelMessages(radioId, ch.Idx, elsewhere.Index);
                    continue;
                }
                var slot = ch.Idx;
                if (own is { IsConfigured: true })
                {
                    slot = Enumerable.Range(0, max).FirstOrDefault(i => !(slots.GetValueOrDefault(i)?.IsConfigured ?? false) && !claimed.Contains(i), -1);
                    if (slot < 0) { app.Log.Warn("Restore", $"No free channel slot for \"{ch.Name}\""); continue; }
                    claimed.Add(slot);
                    app.Log.Warn("Restore", $"Slot {ch.Idx} is now used by another channel; \"{ch.Name}\" goes to slot {slot}");
                }
                await app.Channels.SetAsync(slot, ch.Name, secret).ConfigureAwait(false);
                channelsWritten++;
            }
        }

        var contactsWritten = 0;
        var contactsFailed = 0;
        foreach (var c in p.Contacts)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(L.F("Contact {0}", c.Name));
            try
            {
                await session.AddContactAsync(NodeConfigService.ToMeshContact(c)).ConfigureAwait(false);
                contactsWritten++;
            }
            catch (Exception ex) when (ex is MeshCoreException or InvalidDataException)
            {
                contactsFailed++;
                app.Log.Warn("Restore", $"Contact \"{c.Name}\" not written: {ex.Message}");
            }
        }

        Clear();
        progress?.Report(L.T("Refreshing"));
        try { await app.Channels.RefreshAsync().ConfigureAwait(false); } catch (Exception ex) { app.Log.Warn("Restore", "Channel refresh failed: " + ex.Message); }
        try { await app.SyncContactsAsync(force: true, ct).ConfigureAwait(false); } catch (Exception ex) { app.Log.Warn("Restore", "Contact refresh failed: " + ex.Message); }
        var parts = new List<string>();
        if (settings != ConfigSections.None) parts.Add(L.T("settings"));
        if (channelsWritten > 0) parts.Add(L.Plural(channelsWritten, "{0} channel", "{0} channels"));
        if (contactsWritten > 0) parts.Add(L.Plural(contactsWritten, "{0} contact", "{0} contacts"));
        var text = parts.Count == 0 ? L.T("Nothing needed writing.") : L.F("Wrote {0} to the radio.", string.Join(", ", parts));
        if (contactsFailed > 0)
            // The English keeps its "contact(s)" wording; a plural so other languages can use their own forms.
            text += " " + L.Plural(contactsFailed, "{0} contact(s) didn't fit — the radio's contact list may be full.",
                "{0} contact(s) didn't fit — the radio's contact list may be full.");
        return text;
    }

    /// <summary>Forgets the waiting configuration; restored contacts/channels the radio doesn't have are removed at the next sync.</summary>
    public async Task DiscardAsync()
    {
        var p = Current;
        Clear();
        if (p is null || !app.IsConnected || app.RadioId != p.RadioId) return;
        try { await app.Channels.RefreshAsync().ConfigureAwait(false); } catch (Exception ex) { app.Log.Warn("Restore", "Channel refresh failed: " + ex.Message); }
        try { await app.SyncContactsAsync(force: true).ConfigureAwait(false); } catch (Exception ex) { app.Log.Warn("Restore", "Contact refresh failed: " + ex.Message); }
    }
}
