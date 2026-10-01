using System.Security.Cryptography;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Channel slots on the radio (public, private and #hashtag channels) and per-channel flood scope.</summary>
public sealed class ChannelService(MeshApp app)
{
    private string _lastPushedScope = "inherit";

    public IReadOnlyList<ChannelRecord> GetAll() => app.RadioId is { } id ? app.Db.GetChannels(id) : [];

    public string? DeviceDefaultFloodScope { get; private set; }

    /// <summary>Re-reads every channel slot (and the default flood scope) from the radio.</summary>
    public Task RefreshAsync() => SyncAsync(CancellationToken.None);

    internal async Task SyncAsync(CancellationToken ct)
    {
        var session = app.RequireSession();
        var radioId = app.RequireRadioId();
        var max = app.Capabilities?.MaxChannels is > 0 and var m ? m : 8;
        var indices = Enumerable.Range(0, max).Select(i => (byte)i).ToList();
        var (received, missing) = await session.GetChannelsAsync(indices, ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            app.Log.Warn("Channels", $"Retrying {missing.Count} channel slots");
            foreach (var idx in missing)
            {
                try { received = [.. received, await session.GetChannelAsync(idx, ct).ConfigureAwait(false)]; }
                catch (MeshCoreException ex) { app.Log.Warn("Channels", $"Slot {idx} unreadable: {ex.Message}"); }
            }
        }
        var existing = app.Db.GetChannels(radioId).ToDictionary(c => c.Idx);
        foreach (var info in received)
        {
            if (!info.IsConfigured)
            {
                // A restored channel waiting to be written to this empty slot stays until then.
                if (existing.ContainsKey(info.Index) && !app.PendingConfig.ProtectsChannel(radioId, info.Index))
                    app.Db.DeleteChannel(radioId, info.Index, deleteMessages: false);
                continue;
            }
            var rec = existing.GetValueOrDefault(info.Index) ?? new ChannelRecord { RadioId = radioId, Idx = info.Index };
            rec.Name = info.Name;
            rec.Secret = info.Secret;
            app.Db.UpsertChannel(rec);
        }
        app.UpdateRadioRecord(r => r.LastChannelSync = Time.Now());
        app.RxLog.RefreshChannels();
        if ((app.Capabilities?.FirmwareVersion ?? 0) >= 11)
        {
            try { DeviceDefaultFloodScope = (await session.GetDefaultFloodScopeAsync(ct).ConfigureAwait(false))?.Name; }
            catch (Exception ex) { app.Log.Debug("Channels", "Default flood scope unavailable: " + ex.Message); }
        }
        _lastPushedScope = "inherit";
        app.Log.Info("Channels", $"Channels synced: {received.Count(c => c.IsConfigured)} configured of {max} slots");
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
    }

    public int? FirstFreeSlot()
    {
        var used = GetAll().Select(c => c.Idx).ToHashSet();
        var max = app.Capabilities?.MaxChannels is > 0 and var m ? m : 8;
        for (var i = 0; i < max; i++) if (!used.Contains(i)) return i;
        return null;
    }

    public async Task<ChannelRecord> SetAsync(int index, string name, byte[] secret)
    {
        var session = app.RequireSession();
        var radioId = app.RequireRadioId();
        if (secret.Length != 16) throw new MessageServiceException(L.T("Channel secrets must be 16 bytes."));
        await session.SetChannelAsync((byte)index, name, secret).ConfigureAwait(false);
        var rec = app.Db.GetChannel(radioId, index) ?? new ChannelRecord { RadioId = radioId, Idx = index };
        var secretChanged = !rec.Secret.SequenceEquals(secret);
        rec.Name = name;
        rec.Secret = secret;
        app.Db.UpsertChannel(rec);
        if (secretChanged) app.Db.ClearChannelMessages(radioId, index);
        app.RxLog.RefreshChannels();
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
        return rec;
    }

    private int RequireFreeSlot() => FirstFreeSlot() ?? throw new MessageServiceException(L.T("All channel slots on the radio are in use. Remove a channel first."));

    public Task<ChannelRecord> AddPublicAsync() =>
        GetAll().FirstOrDefault(c => c.IsPublic) is { } existing ? Task.FromResult(existing) : SetAsync(RequireFreeSlot(), "Public", ChannelSecrets.PublicChannelSecret);

    public Task<ChannelRecord> JoinHashtagAsync(string tag)
    {
        var body = HashtagUtilities.SanitizeHashtagNameInput(tag.TrimStart('#'));
        if (!HashtagUtilities.IsValidHashtagName(body)) throw new MessageServiceException(L.T("Hashtag names may only use letters, numbers and hyphens."));
        var (name, secret) = HashtagUtilities.HashtagChannel(body);
        if (GetAll().FirstOrDefault(c => c.Secret.SequenceEquals(secret)) is { } existing) return Task.FromResult(existing);
        return SetAsync(RequireFreeSlot(), name, secret);
    }

    public Task<ChannelRecord> CreatePrivateAsync(string name) =>
        SetAsync(RequireFreeSlot(), name.Utf8Prefix(31), RandomNumberGenerator.GetBytes(16));

    public Task<ChannelRecord> JoinPrivateAsync(string name, byte[] secret) =>
        GetAll().FirstOrDefault(c => c.Secret.SequenceEquals(secret)) is { } existing ? Task.FromResult(existing) : SetAsync(RequireFreeSlot(), name.Utf8Prefix(31), secret);

    /// <summary>Joins from a meshcore://channel/add link.</summary>
    public async Task<ChannelRecord> JoinFromUriAsync(string uri)
    {
        var link = MeshCoreUrl.ParseChannel(uri.Trim()) ?? throw new MessageServiceException(L.T("That doesn't look like a MeshCore channel link."));
        var ch = await JoinPrivateAsync(link.Name, link.Secret).ConfigureAwait(false);
        if (link.RegionScope is { } region) SetFloodScope(ch, "region:" + region);
        return ch;
    }

    public async Task RemoveAsync(ChannelRecord ch, bool deleteMessages = true)
    {
        var session = app.RequireSession();
        await session.SetChannelAsync((byte)ch.Idx, "", new byte[16]).ConfigureAwait(false);
        app.Db.DeleteChannel(ch.RadioId, ch.Idx, deleteMessages);
        app.RxLog.RefreshChannels();
        app.Notify(DataKind.Channels);
        app.Notify(DataKind.Conversations);
    }

    public Task RenameAsync(ChannelRecord ch, string newName) => SetAsync(ch.Idx, newName.Utf8Prefix(31), ch.Secret);

    public void SetNotificationLevel(ChannelRecord ch, NotificationLevel level)
    {
        ch.NotificationLevel = (int)level;
        app.Db.UpsertChannel(ch);
        app.Notify(DataKind.Conversations);
    }

    public void ClearMessages(ChannelRecord ch)
    {
        app.Db.ClearChannelMessages(ch.RadioId, ch.Idx);
        app.Notify(DataKind.Messages, MessageService.ChannelKey(ch.Idx));
        app.Notify(DataKind.Conversations);
    }

    public static string ShareUri(ChannelRecord ch) => MeshCoreUrl.ChannelUri(ch.Name, ch.Secret, ch.FloodScope?.StartsWith("region:") == true ? ch.FloodScope[7..] : null);

    /// <summary>Sets the per-channel flood scope: null/"inherit", "all" or "region:NAME".</summary>
    public void SetFloodScope(ChannelRecord ch, string? scope)
    {
        ch.FloodScope = scope is null or "inherit" ? null : scope;
        app.Db.UpsertChannel(ch);
        app.Notify(DataKind.Channels);
    }

    /// <summary>Pushes the session flood scope needed before sending on <paramref name="channelIndex"/>.</summary>
    internal async Task ApplyFloodScopeAsync(int? channelIndex)
    {
        var session = app.Session;
        if (session is null || !session.IsRunning) return;
        var ch = channelIndex is { } idx ? app.Db.GetChannel(app.RequireRadioId(), idx) : null;
        var pref = ch?.FloodScope ?? "inherit";
        var fw = app.Capabilities?.FirmwareVersion ?? 0;
        string desired;
        FloodScope? scope = null;
        var unscoped = false;
        if (pref == "all") { if (fw >= 12) { desired = "unscoped"; unscoped = true; } else { desired = "disabled"; scope = new FloodScope.Disabled(); } }
        else if (pref.StartsWith("region:")) { desired = pref; scope = new FloodScope.Region(pref[7..]); }
        else desired = "inherit";
        if (desired == _lastPushedScope) return;
        try
        {
            if (desired == "inherit")
            {
                if (!string.IsNullOrEmpty(DeviceDefaultFloodScope)) await session.SetFloodScopeAsync(new FloodScope.Region(DeviceDefaultFloodScope).ScopeKey()).ConfigureAwait(false);
                else await session.SetFloodScopeAsync(new FloodScope.Disabled().ScopeKey()).ConfigureAwait(false);
            }
            else if (unscoped) await session.SetFloodScopeUnscopedAsync().ConfigureAwait(false);
            else await session.SetFloodScopeAsync(scope!.ScopeKey()).ConfigureAwait(false);
            _lastPushedScope = desired;
        }
        catch (Exception ex) { app.Log.Warn("Channels", "Could not set flood scope: " + ex.Message); }
    }
}
