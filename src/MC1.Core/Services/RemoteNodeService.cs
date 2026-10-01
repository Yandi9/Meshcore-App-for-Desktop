using System.Collections.Concurrent;
using System.Text.Json;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

public sealed class RemoteNodeException(string message) : Exception(message);

public sealed record LoginResult(bool Success, bool IsAdmin, RoomPermission Permission, DateTimeOffset? ServerTime);

/// <summary>Repeater and room-server administration: login/keep-alive, status, telemetry, neighbours, CLI.</summary>
public sealed class RemoteNodeService(MeshApp app)
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LoginResult>> _pendingLogins = new();
    private readonly ConcurrentDictionary<string, PendingCli> _pendingCli = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _keepAlives = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cliSlots = new();
    private int _cliCounter;

    private sealed record PendingCli(string Id, string Command, string WirePrefix, bool AcceptAny, TaskCompletionSource<string> Tcs);

    public event Action<string, bool>? SessionStateChanged;

    private static string Prefix(byte[] key) => Convert.ToHexString(key, 0, Math.Min(6, key.Length));

    public IReadOnlyList<RemoteSessionRecord> Sessions() => app.RadioId is { } id ? app.Db.GetRemoteSessions(id) : [];

    public RemoteSessionRecord GetOrCreateSession(ContactRecord contact)
    {
        var radioId = app.RequireRadioId();
        var s = app.Db.GetRemoteSessionByKey(radioId, contact.PublicKey);
        if (s is null)
        {
            s = new RemoteSessionRecord { RadioId = radioId, PublicKey = contact.PublicKey, Name = contact.DisplayName, IsRoom = contact.ContactType == ContactType.Room, LastActivity = Time.Now() };
            app.Db.UpsertRemoteSession(s);
        }
        else if (s.Name != contact.DisplayName)
        {
            s.Name = contact.DisplayName;
            app.Db.UpsertRemoteSession(s);
        }
        return s;
    }

    public string? StoredPassword(byte[] key) => app.Secrets.GetPassword(key);
    public void ForgetPassword(byte[] key) => app.Secrets.DeletePassword(key);

    /// <summary>Logs into a repeater or room server. An empty password logs in as guest.</summary>
    public async Task<LoginResult> LoginAsync(ContactRecord contact, string? password, bool remember, IProgress<int>? timeoutSeconds = null, CancellationToken ct = default)
    {
        var session = app.RequireSession();
        var remote = GetOrCreateSession(contact);
        var pwd = password ?? app.Secrets.GetPassword(contact.PublicKey) ?? "";
        var prefix = Prefix(contact.PublicKey);
        var tcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pendingLogins.TryRemove(prefix, out var old)) old.TrySetCanceled();
        _pendingLogins[prefix] = tcs;
        try
        {
            MessageSentInfo sent;
            try { sent = await session.SendLoginAsync(contact.PublicKey, pwd, ct).ConfigureAwait(false); }
            catch (MeshCoreException ex) when (ex.FirmwareError == ErrorCode.NotFound)
            {
                // The radio lost this contact; re-add it (flood routed) and retry once.
                await session.AddContactAsync(contact.ToMeshContact() with { OutPathLength = 0xFF, OutPath = [] }, ct).ConfigureAwait(false);
                sent = await session.SendLoginAsync(contact.PublicKey, pwd, ct).ConfigureAwait(false);
            }
            var byPath = TimeSpan.FromSeconds(Math.Min(60, 5 + 10 * contact.HopCount + (contact.IsFloodRouted ? 10 : 0)));
            var firmware = TimeSpan.FromMilliseconds(sent.SuggestedTimeoutMs * 2.0);
            var timeout = TimeSpan.FromSeconds(Math.Min(20, Math.Max(firmware.TotalSeconds, byPath.TotalSeconds)));
            timeoutSeconds?.Report((int)Math.Ceiling(timeout.TotalSeconds));
            var retransmit = TimeSpan.FromMilliseconds(Math.Max(1000, sent.SuggestedTimeoutMs));
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var wait = deadline - DateTime.UtcNow < retransmit ? deadline - DateTime.UtcNow : retransmit;
                var done = await Task.WhenAny(tcs.Task, Task.Delay(wait, ct)).ConfigureAwait(false);
                if (done == tcs.Task) break;
                if (DateTime.UtcNow >= deadline) break;
                try { await session.SendLoginAsync(contact.PublicKey, pwd, ct).ConfigureAwait(false); } catch (Exception ex) { app.Log.Debug("Remote", "Login retransmit failed: " + ex.Message); }
            }
            if (!tcs.Task.IsCompleted) throw new RemoteNodeException(L.T("No response from the node (timed out)."));
            var result = await tcs.Task.ConfigureAwait(false);
            if (!result.Success) throw new RemoteNodeException(L.T("Login failed — wrong password?"));
            app.Db.SetRemoteSessionConnected(remote.Id, true, result.Permission);
            if (remember && password is not null) app.Secrets.SetPassword(contact.PublicKey, password);
            StartKeepAlive(remote.Id);
            app.Notify(DataKind.Sessions, remote.Id);
            SessionStateChanged?.Invoke(remote.Id, true);
            return result;
        }
        finally { _pendingLogins.TryRemove(new KeyValuePair<string, TaskCompletionSource<LoginResult>>(prefix, tcs)); }
    }

    internal void HandleLoginResult(byte[] prefix, bool success, LoginInfo? info)
    {
        var key = Prefix(prefix);
        if (!_pendingLogins.TryRemove(key, out var tcs))
        {
            app.Log.Debug("Remote", $"Unmatched login result for {key}");
            return;
        }
        tcs.TrySetResult(success && info is not null
            ? new LoginResult(true, info.IsAdmin, (RoomPermission)info.Permissions, info.ServerTime)
            : new LoginResult(false, false, RoomPermission.Guest, null));
    }

    internal void HandleLoginFailedUnknown()
    {
        if (_pendingLogins.Count == 1 && _pendingLogins.Keys.First() is var k && _pendingLogins.TryRemove(k, out var tcs))
            tcs.TrySetResult(new LoginResult(false, false, RoomPermission.Guest, null));
    }

    public async Task LogoutAsync(RemoteSessionRecord remote)
    {
        StopKeepAlive(remote.Id);
        if (app.Session is { IsRunning: true } s)
        {
            try { await s.SendLogoutAsync(remote.PublicKey).ConfigureAwait(false); } catch (Exception ex) { app.Log.Debug("Remote", "Logout: " + ex.Message); }
        }
        app.Db.SetRemoteSessionConnected(remote.Id, false, RoomPermission.Guest);
        app.Notify(DataKind.Sessions, remote.Id);
        SessionStateChanged?.Invoke(remote.Id, false);
    }

    public void RemoveSession(RemoteSessionRecord remote)
    {
        StopKeepAlive(remote.Id);
        app.Db.DeleteRemoteSession(remote.Id);
        app.Notify(DataKind.Sessions);
        app.Notify(DataKind.Conversations);
    }

    private void StartKeepAlive(string sessionId)
    {
        StopKeepAlive(sessionId);
        var cts = new CancellationTokenSource();
        _keepAlives[sessionId] = cts;
        _ = Task.Run(async () =>
        {
            var failures = 0;
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(KeepAliveInterval, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                var remote = app.Db.GetRemoteSession(sessionId);
                var session = app.Session;
                if (remote is null || session is null || !session.IsRunning) return;
                var contact = app.Db.GetContactByKey(remote.RadioId, remote.PublicKey);
                if (contact is null) { MarkDisconnected(sessionId); return; }
                if (contact.IsFloodRouted) continue; // keep-alives only make sense over a direct path
                try
                {
                    await session.SendKeepAliveAsync(remote.PublicKey, (uint)remote.LastSyncTimestamp, cts.Token).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    failures++;
                    app.Log.Warn("Remote", $"Keep-alive {failures}/2 failed for {remote.Name}: {ex.Message}");
                    if (failures >= 2) { MarkDisconnected(sessionId); return; }
                }
            }
        });
    }

    private void MarkDisconnected(string sessionId)
    {
        StopKeepAlive(sessionId);
        app.Db.SetRemoteSessionConnected(sessionId, false);
        app.Notify(DataKind.Sessions, sessionId);
        SessionStateChanged?.Invoke(sessionId, false);
    }

    private void StopKeepAlive(string sessionId)
    {
        if (_keepAlives.TryRemove(sessionId, out var cts)) cts.Cancel();
    }

    internal void OnDisconnected()
    {
        foreach (var k in _keepAlives.Keys.ToList()) StopKeepAlive(k);
        foreach (var p in _pendingLogins.Values) p.TrySetCanceled();
        _pendingLogins.Clear();
        foreach (var c in _pendingCli.Values) c.Tcs.TrySetException(new RemoteNodeException(L.T("Disconnected from radio")));
        _pendingCli.Clear();
    }

    // MARK: Status / telemetry / neighbours

    public async Task<StatusResponse> RequestStatusAsync(ContactRecord contact, CancellationToken ct = default)
    {
        var s = await WithFloodRecovery(contact, () => app.RequireSession().RequestStatusAsync(contact.PublicKey, contact.ContactType, ct)).ConfigureAwait(false);
        RecordStatus(s, contact.PublicKey);
        return s;
    }

    internal void RecordStatus(StatusResponse s, byte[]? fullKey = null)
    {
        var radioId = app.RadioId;
        if (radioId is null) return;
        var key = fullKey ?? app.Db.GetContactByPrefix(radioId, s.PublicKeyPrefix)?.PublicKey;
        if (key is null) return;
        // Avoid duplicate snapshots when both routed and binary replies arrive.
        var last = app.Db.GetSnapshots(radioId, key, Time.Now() - 5000).LastOrDefault();
        if (last is not null && last.Uptime == s.Uptime) return;
        app.Db.SaveSnapshot(new NodeSnapshotRecord
        {
            RadioId = radioId, PublicKey = key, BatteryMv = s.Battery, NoiseFloor = s.NoiseFloor, LastRssi = s.LastRssi, LastSnr = s.LastSnr,
            Uptime = s.Uptime, PacketsReceived = s.PacketsReceived, PacketsSent = s.PacketsSent, Airtime = s.Airtime, RxAirtime = s.RxAirtime, TxQueue = s.TxQueueLength,
        });
        app.Db.TouchContactHeard(radioId, key, Time.Now());
        app.Notify(DataKind.Snapshots, Convert.ToHexString(key));
    }

    public async Task<TelemetryResponse> RequestTelemetryAsync(ContactRecord contact, CancellationToken ct = default)
    {
        var t = await WithFloodRecovery(contact, () => app.RequireSession().RequestTelemetryAsync(contact.PublicKey, ct)).ConfigureAwait(false);
        var radioId = app.RequireRadioId();
        var points = t.DataPoints.Select(p => new { ch = p.Channel, type = p.Type.ToString(), name = p.Type.DisplayName(), value = p.Value.NumericValue, text = p.FormattedValue }).ToList();
        app.Db.SaveSnapshot(new NodeSnapshotRecord
        {
            RadioId = radioId, PublicKey = contact.PublicKey,
            BatteryMv = t.DataPoints.FirstOrDefault(p => p.Type == LppSensorType.Voltage)?.Value.NumericValue is { } v ? (int)(v * 1000) : null,
            TelemetryJson = JsonSerializer.Serialize(points),
        });
        foreach (var gps in t.DataPoints.Select(p => p.Value).OfType<LppValue.Gps>())
        {
            if (gps.Latitude != 0 || gps.Longitude != 0)
            {
                contact.Latitude = gps.Latitude;
                contact.Longitude = gps.Longitude;
                app.Db.UpsertContact(contact);
                app.Notify(DataKind.Contacts, contact.Id);
            }
        }
        app.Db.TouchContactHeard(radioId, contact.PublicKey, Time.Now());
        app.Notify(DataKind.Snapshots, contact.PublicKeyHex);
        return t;
    }

    public async Task<NeighboursResponse> RequestNeighboursAsync(ContactRecord contact, CancellationToken ct = default)
    {
        var n = await WithFloodRecovery(contact, () => app.RequireSession().FetchAllNeighboursAsync(contact.PublicKey, ct: ct)).ConfigureAwait(false);
        var radioId = app.RequireRadioId();
        app.Db.SaveSnapshot(new NodeSnapshotRecord
        {
            RadioId = radioId, PublicKey = contact.PublicKey,
            NeighboursJson = JsonSerializer.Serialize(n.Neighbours.Select(x => new { key = Convert.ToHexString(x.PublicKeyPrefix), secs = x.SecondsAgo, snr = x.Snr })),
        });
        app.Notify(DataKind.Snapshots, contact.PublicKeyHex);
        return n;
    }

    public Task<OwnerInfoResponse> RequestOwnerInfoAsync(ContactRecord contact, CancellationToken ct = default) =>
        WithFloodRecovery(contact, () => app.RequireSession().RequestOwnerInfoAsync(contact.PublicKey, ct));

    public Task<AclResponse> RequestAclAsync(ContactRecord contact, CancellationToken ct = default) =>
        WithFloodRecovery(contact, () => app.RequireSession().RequestAclAsync(contact.PublicKey, ct));

    public Task<MmaResponse> RequestMmaAsync(ContactRecord contact, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        WithFloodRecovery(contact, () => app.RequireSession().RequestMmaAsync(contact.PublicKey, from, to, ct));

    public Task<IReadOnlyList<string>> RequestRegionsAsync(ContactRecord contact, CancellationToken ct = default) =>
        app.RequireSession().RequestRegionsAsync(contact.ToMeshContact(), ct);

    /// <summary>On timeout over a stored direct path, resets to flood once and retries (path may be stale).</summary>
    private async Task<T> WithFloodRecovery<T>(ContactRecord contact, Func<Task<T>> op)
    {
        try { return await op().ConfigureAwait(false); }
        catch (MeshCoreException ex) when (ex.Kind == MeshCoreErrorKind.Timeout && !contact.IsFloodRouted)
        {
            app.Log.Info("Remote", $"Timeout over direct path to {contact.DisplayName}; retrying via flood");
            await app.RequireSession().ResetPathAsync(contact.PublicKey).ConfigureAwait(false);
            await app.Contacts.RefreshFromRadioAsync(contact).ConfigureAwait(false);
            return await op().ConfigureAwait(false);
        }
    }

    public IReadOnlyList<NodeSnapshotRecord> History(ContactRecord contact, TimeSpan window) =>
        app.RadioId is { } id ? app.Db.GetSnapshots(id, contact.PublicKey, Time.Now() - (long)window.TotalMilliseconds) : [];

    // MARK: CLI

    /// <summary>Sends a CLI command to an admin session and waits for the textual reply.</summary>
    public async Task<string> SendCliAsync(ContactRecord contact, string command, TimeSpan? timeout = null, bool acceptAny = true, CancellationToken ct = default)
    {
        var session = app.RequireSession();
        command = CliResponse.RewriteCommand(command);
        var prefix = Prefix(contact.PublicKey);
        var slot = _cliSlots.GetOrAdd(prefix, _ => new SemaphoreSlim(1, 1));
        await slot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wirePrefix = $"{(byte)Interlocked.Increment(ref _cliCounter):X2}|";
            var pending = new PendingCli(Guid.NewGuid().ToString(), command, wirePrefix, acceptAny, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingCli[prefix] = pending;
            try
            {
                var sent = await session.SendCommandAsync(contact.PublicKey, wirePrefix + command, ct: ct).ConfigureAwait(false);
                var lower = command.Trim().ToLowerInvariant();
                // The node doesn't answer a reboot; this stands in for its reply (shown to the user, never parsed for data).
                if (lower == "reboot" || lower.StartsWith("reboot ")) return L.T("Reboot command sent.");
                var requested = timeout ?? TimeSpan.FromSeconds(10);
                var fw = TimeSpan.FromMilliseconds(sent.SuggestedTimeoutMs * 2.0);
                var effective = TimeSpan.FromSeconds(Math.Min(15, Math.Max(requested.TotalSeconds, fw.TotalSeconds)));
                var deadline = DateTime.UtcNow + effective;
                while (!pending.Tcs.Task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    // Replies arrive as queued messages; nudge the radio in case no "messages waiting" push came.
                    try { await session.GetMessageAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); } catch { /* ignore */ }
                    await Task.WhenAny(pending.Tcs.Task, Task.Delay(300, ct)).ConfigureAwait(false);
                }
                if (!pending.Tcs.Task.IsCompleted) throw new RemoteNodeException(L.T("No reply from the node (timed out)."));
                var reply = await pending.Tcs.Task.ConfigureAwait(false);
                HandlePasswordChange(contact, command, reply);
                return reply;
            }
            finally { _pendingCli.TryRemove(new KeyValuePair<string, PendingCli>(prefix, pending)); }
        }
        finally { slot.Release(); }
    }

    private void HandlePasswordChange(ContactRecord contact, string command, string reply)
    {
        var lower = command.Trim().ToLowerInvariant();
        if (!lower.StartsWith("password ") || lower.Contains("guest.password")) return;
        if (CliResponse.Parse(reply) is CliResponse.Ok || reply.Trim().StartsWith("password now:", StringComparison.OrdinalIgnoreCase))
            app.Secrets.DeletePassword(contact.PublicKey);
    }

    internal void HandleCliResponse(ContactMessage m)
    {
        var prefix = Prefix(m.SenderPublicKeyPrefix);
        var text = m.Text;
        string? echo = null;
        if (CliResponse.SplitEchoedPrefix(text) is { } split) { echo = split.Prefix; text = split.Body; }
        if (!_pendingCli.TryGetValue(prefix, out var pending))
        {
            app.Log.Debug("Remote", $"Unmatched CLI reply from {prefix}: {text}");
            CliOutput?.Invoke(prefix, text);
            return;
        }
        if (echo is not null && echo != pending.WirePrefix)
        {
            app.Log.Debug("Remote", $"Stale CLI reply ({echo}) ignored");
            return;
        }
        pending.Tcs.TrySetResult(text);
        CliOutput?.Invoke(prefix, text);
    }

    /// <summary>Every CLI reply (matched or not), keyed by 6-byte hex prefix.</summary>
    public event Action<string, string>? CliOutput;

    /// <summary>Reads the common repeater/room settings via CLI "get" commands.</summary>
    public async Task<Dictionary<string, CliResponse>> ReadNodeSettingsAsync(ContactRecord contact, IEnumerable<string> queries, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new Dictionary<string, CliResponse>();
        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(q);
            try
            {
                var reply = await SendCliAsync(contact, q, TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
                results[q] = CliResponse.Parse(reply, q);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[q] = new CliResponse.Error(ex.Message);
            }
        }
        return results;
    }

    public static readonly string[] RepeaterSettingQueries =
        ["ver", "get name", "get radio", "get tx", "get repeat", "get advert.interval", "get flood.advert.interval", "get flood.max", "get lat", "get lon", "get owner.info", "clock"];

    public static readonly string[] RoomSettingQueries =
        ["ver", "get name", "get radio", "get tx", "get advert.interval", "get flood.advert.interval", "get lat", "get lon", "get owner.info", "get guest.password", "get allow.read.only", "clock"];
}
