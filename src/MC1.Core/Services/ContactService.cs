using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

public sealed record PingResult(bool Success, int LatencyMs, double? SnrThere, double? SnrBack, string? Error);

/// <summary>Contact management (adds, removals, sharing, paths, favourites, blocking).</summary>
public sealed class ContactService(MeshApp app)
{
    public IReadOnlyList<ContactRecord> GetAll() => app.RadioId is { } id ? app.Db.GetContacts(id) : [];

    public ContactRecord? Get(string id) => app.Db.GetContact(id);

    internal void HandleNewAdvert(MeshContact frame)
    {
        var radioId = app.RequireRadioId();
        var (node, isNew) = app.Db.UpsertDiscoveredNode(radioId, frame);
        app.Notify(DataKind.Discovered);
        if (isNew)
        {
            app.Log.Info("Contacts", $"Discovered new node: {node.Name} ({node.ContactType})");
            var s = app.Settings.Current;
            if (s.NotificationsEnabled && s.NotifyNewContacts)
                app.Notifier.ShowInfo(L.T("New node discovered"), $"{node.Name} ({TypeName(node.ContactType)})", "discover");
        }
    }

    /// <summary>The contact type's name to show (in the app's language; display only, never stored or compared).</summary>
    public static string TypeName(ContactType t) => t switch
    {
        ContactType.Repeater => L.T("Repeater"),
        ContactType.Room => L.T("Room server"),
        ContactType.Sensor => L.T("Sensor"),
        _ => L.T("Companion"),
    };

    internal void HandlePathDiscovery(PathInfo p)
    {
        var radioId = app.RadioId;
        if (radioId is null) return;
        var c = app.Db.GetContactByPrefix(radioId, p.PublicKeyPrefix);
        if (c is null) return;
        c.OutPathLength = p.OutPathLength;
        c.OutPath = p.OutPath;
        c.LastHeard = Time.Now();
        app.Db.UpsertContact(c);
        app.Log.Info("Contacts", $"Path discovery for {c.DisplayName}: {(p.OutPath.Length == 0 ? "direct" : string.Join("→", PathEncoding.HopHexes(p.OutPath, PathEncoding.Decode(p.OutPathLength)?.HashSize ?? 1)))}");
        app.Notify(DataKind.Contacts, c.Id);
        PathDiscovered?.Invoke(c, p);
    }

    public event Action<ContactRecord, PathInfo>? PathDiscovered;

    /// <summary>Adds a node to the radio's contact table (from discovery, QR/URI or a shared token).</summary>
    public async Task<ContactRecord> AddAsync(byte[] publicKey, string name, ContactType type, double lat = 0, double lon = 0, byte outPathLength = 0xFF, byte[]? outPath = null)
    {
        var session = app.RequireSession();
        var radioId = app.RequireRadioId();
        var frame = new MeshContact(publicKey, type, (byte)type, ContactFlags.None, outPathLength, outPath ?? [], name,
            DateTimeOffset.UtcNow, lat, lon, DateTimeOffset.UtcNow);
        try { await session.AddContactAsync(frame).ConfigureAwait(false); }
        catch (MeshCoreException ex) when (ex.FirmwareError == ErrorCode.TableFull)
        {
            throw new MessageServiceException(L.T("The radio's contact list is full. Remove a contact first."));
        }
        var fetched = await session.GetContactAsync(publicKey).ConfigureAwait(false) ?? frame;
        var rec = app.Db.SaveContactFrame(radioId, fetched);
        foreach (var d in app.Db.GetDiscoveredNodes(radioId).Where(d => d.PublicKey.SequenceEquals(publicKey)))
            app.Db.DeleteDiscoveredNode(d.Id);
        app.RxLog.RefreshContactKeys();
        app.Notify(DataKind.Contacts);
        app.Notify(DataKind.Discovered);
        return rec;
    }

    public Task<ContactRecord> AddDiscoveredAsync(DiscoveredNodeRecord node) =>
        AddAsync(node.PublicKey, node.Name, node.ContactType, node.Latitude, node.Longitude, (byte)node.OutPathLength, node.OutPath);

    /// <summary>Imports a node from a meshcore:// URI or a raw exported contact card (hex).</summary>
    public async Task<ContactRecord?> ImportAsync(string input)
    {
        input = input.Trim();
        if (MeshCoreUrl.ParseContact(input) is { } link) return await AddAsync(link.PublicKey, link.Name, link.Type).ConfigureAwait(false);
        if (ContactShareUtilities.Parse(input) is { } share) return await AddAsync(share.PublicKey, share.Name, share.Type).ConfigureAwait(false);
        var hex = input.StartsWith("meshcore://", StringComparison.OrdinalIgnoreCase) ? input["meshcore://".Length..] : input;
        var card = Bytes.FromHex(hex) ?? throw new MessageServiceException(L.T("That doesn't look like a MeshCore contact link."));
        var session = app.RequireSession();
        await session.ImportContactAsync(card).ConfigureAwait(false);
        await app.SyncContactsAsync(false).ConfigureAwait(false);
        return null;
    }

    public async Task RemoveAsync(ContactRecord c, bool deleteMessages)
    {
        if (app.Session is { IsRunning: true } session)
        {
            try { await session.RemoveContactAsync(c.PublicKey).ConfigureAwait(false); }
            catch (MeshCoreException ex) when (ex.FirmwareError == ErrorCode.NotFound) { /* already gone */ }
        }
        app.Db.DeleteContact(c.Id, deleteMessages);
        app.Notify(DataKind.Contacts);
        app.Notify(DataKind.Conversations);
    }

    public async Task SetFavoriteAsync(ContactRecord c, bool favorite)
    {
        var flags = favorite ? c.ContactFlags | ContactFlags.Favorite : c.ContactFlags & ~ContactFlags.Favorite;
        var session = app.RequireSession();
        await session.ChangeContactFlagsAsync(c.ToMeshContact(), flags).ConfigureAwait(false);
        c.Flags = (int)flags;
        app.Db.UpsertContact(c);
        app.Notify(DataKind.Contacts, c.Id);
    }

    public async Task SetTelemetryPermissionsAsync(ContactRecord c, ContactFlags telemetryFlags)
    {
        var flags = (c.ContactFlags & ~ContactFlags.TelemetryAll) | (telemetryFlags & ContactFlags.TelemetryAll);
        await app.RequireSession().ChangeContactFlagsAsync(c.ToMeshContact(), flags).ConfigureAwait(false);
        c.Flags = (int)flags;
        app.Db.UpsertContact(c);
        app.Notify(DataKind.Contacts, c.Id);
    }

    public void SetNickname(ContactRecord c, string? nickname)
    {
        c.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        app.Db.UpsertContact(c);
        app.Notify(DataKind.Contacts, c.Id);
        app.Notify(DataKind.Conversations);
    }

    public void SetBlocked(ContactRecord c, bool blocked)
    {
        c.IsBlocked = blocked;
        app.Db.UpsertContact(c);
        app.Messages.InvalidateBlockedCache();
        app.Notify(DataKind.Contacts, c.Id);
        app.Notify(DataKind.Conversations);
    }

    public void SetNotificationLevel(ContactRecord c, NotificationLevel level)
    {
        c.NotificationLevel = (int)level;
        app.Db.UpsertContact(c);
        app.Notify(DataKind.Conversations);
    }

    public async Task ResetPathAsync(ContactRecord c)
    {
        var session = app.RequireSession();
        await session.ResetPathAsync(c.PublicKey).ConfigureAwait(false);
        await RefreshFromRadioAsync(c).ConfigureAwait(false);
    }

    /// <summary>Sets an explicit outbound path (list of repeater hash prefixes).</summary>
    public async Task SetPathAsync(ContactRecord c, byte[] path, int hashSize)
    {
        var session = app.RequireSession();
        await session.ChangeContactPathAsync(c.ToMeshContact(), path, (byte)hashSize).ConfigureAwait(false);
        await RefreshFromRadioAsync(c).ConfigureAwait(false);
    }

    public async Task<ContactRecord> RefreshFromRadioAsync(ContactRecord c)
    {
        var session = app.RequireSession();
        if (await session.GetContactAsync(c.PublicKey).ConfigureAwait(false) is { } fresh)
        {
            c.ApplyFrom(fresh);
            app.Db.UpsertContact(c);
            app.Notify(DataKind.Contacts, c.Id);
        }
        return c;
    }

    public async Task DiscoverPathAsync(ContactRecord c)
    {
        var session = app.RequireSession();
        await session.SendPathDiscoveryAsync(c.PublicKey).ConfigureAwait(false);
    }

    /// <summary>Asks the radio to re-broadcast this contact's advert (zero-hop share).</summary>
    public Task ShareOverMeshAsync(ContactRecord c) => app.RequireSession().ShareContactAsync(c.PublicKey);

    public Task<string> ExportCardAsync(ContactRecord? c) => app.RequireSession().ExportContactAsync(c?.PublicKey);

    public static string ContactUri(ContactRecord c) => MeshCoreUrl.ContactUri(c.Name, c.PublicKey, c.ContactType);

    public string SelfContactUri() => app.SelfInfo is { } s ? MeshCoreUrl.ContactUri(s.Name, s.PublicKey, ContactType.Chat) : "";

    /// <summary>Zero-hop trace "ping" (same technique as the iOS app).</summary>
    public async Task<PingResult> PingAsync(ContactRecord c, CancellationToken ct = default)
    {
        var session = app.RequireSession();
        var mode = app.Capabilities?.PathHashMode ?? 0;
        var hashSize = 1 << mode;
        var tag = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var tcs = new TaskCompletionSource<(double There, double Back)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(MeshEvent e)
        {
            if (e is MeshEvent.RxLogData { Info: { PayloadType: PayloadType.Trace } rx } && rx.PacketPayload.Length >= 4 && rx.PacketPayload.ReadUInt32LE(0) == tag)
            {
                double? remote = rx.PathNodes.Length > 0 ? (sbyte)rx.PathNodes[^1] / 4.0 : null;
                tcs.TrySetResult((remote ?? 0, rx.Snr ?? 0));
            }
            else if (e is MeshEvent.TraceData { Info: var t } && t.Tag == tag)
            {
                tcs.TrySetResult((t.Path.Count > 0 ? t.Path[0].Snr : 0, t.Path.Count > 0 ? t.Path[^1].Snr : 0));
            }
        }
        app.RadioEvent += OnEvent;
        var started = DateTime.UtcNow;
        try
        {
            var sent = await session.SendTraceAsync(tag, 0, (byte)mode, c.PublicKey.Prefix(hashSize), ct).ConfigureAwait(false);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(sent.SuggestedTimeoutMs / 1000.0 * 1.2, 1, 30));
            if (sent.SuggestedTimeoutMs == 0) timeout = TimeSpan.FromSeconds(5);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (done != tcs.Task) return new PingResult(false, 0, null, null, L.T("No response"));
            var (there, back) = await tcs.Task.ConfigureAwait(false);
            app.Db.TouchContactHeard(c.RadioId, c.PublicKey, Time.Now());
            app.Notify(DataKind.Contacts, c.Id);
            return new PingResult(true, (int)(DateTime.UtcNow - started).TotalMilliseconds, there, back, null);
        }
        catch (Exception ex) { return new PingResult(false, 0, null, null, ex.Message); }
        finally { app.RadioEvent -= OnEvent; }
    }

    // MARK: Discovered nodes

    public IReadOnlyList<DiscoveredNodeRecord> GetDiscovered() => app.RadioId is { } id ? app.Db.GetDiscoveredNodes(id) : [];

    public void DeleteDiscovered(DiscoveredNodeRecord n) { app.Db.DeleteDiscoveredNode(n.Id); app.Notify(DataKind.Discovered); }

    public void ClearDiscovered() { if (app.RadioId is { } id) { app.Db.ClearDiscoveredNodes(id); app.Notify(DataKind.Discovered); } }

    // MARK: Channel sender blocking

    public void BlockChannelSender(string name)
    {
        app.Db.BlockSender(app.RequireRadioId(), name);
        app.Messages.InvalidateBlockedCache();
        app.Notify(DataKind.Conversations);
        app.Notify(DataKind.Messages);
    }

    public void UnblockChannelSender(string name)
    {
        app.Db.UnblockSender(app.RequireRadioId(), name);
        app.Messages.InvalidateBlockedCache();
        app.Notify(DataKind.Conversations);
    }

    public IReadOnlyList<BlockedSenderRecord> BlockedSenders() => app.RadioId is { } id ? app.Db.GetBlockedSenders(id) : [];

    /// <summary>Resolves a path hash (1–3 bytes) to a known repeater/contact name.</summary>
    public string? ResolveHopName(byte[] hash)
    {
        if (app.RadioId is not { } id) return null;
        var contacts = app.Db.GetContacts(id);
        var matches = contacts.Where(c => c.PublicKey.StartsWith(hash)).ToList();
        var repeater = matches.FirstOrDefault(c => c.ContactType == ContactType.Repeater) ?? matches.FirstOrDefault();
        if (repeater is not null) return repeater.DisplayName;
        var d = app.Db.GetDiscoveredNodes(id).FirstOrDefault(n => n.PublicKey.StartsWith(hash));
        return d?.Name;
    }

    public (double Lat, double Lon)? ResolveHopLocation(byte[] hash)
    {
        if (app.RadioId is not { } id) return null;
        var c = app.Db.GetContacts(id).Where(c => c.PublicKey.StartsWith(hash) && c.HasLocation)
            .OrderByDescending(c => c.ContactType == ContactType.Repeater).FirstOrDefault();
        if (c is not null) return (c.Latitude, c.Longitude);
        var d = app.Db.GetDiscoveredNodes(id).FirstOrDefault(n => n.PublicKey.StartsWith(hash) && n.HasLocation);
        return d is null ? null : (d.Latitude, d.Longitude);
    }
}
