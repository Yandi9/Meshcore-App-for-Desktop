using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Services;

public sealed partial class MeshApp
{
    private readonly SemaphoreSlim _contactSyncLock = new(1, 1);
    private CancellationTokenSource? _deltaSyncCts;
    private volatile bool _initialSyncRunning;
    private static readonly TimeSpan WatermarkSkew = TimeSpan.FromDays(2);

    public bool IsInitialSyncRunning => _initialSyncRunning;

    private async Task RunInitialSyncAsync(MeshCoreSession session, CancellationToken ct)
    {
        _initialSyncRunning = true;
        try
        {
            RxLog.Configure(RequireRadioId());
            try
            {
                var key = await session.ExportPrivateKeyAsync(ct).ConfigureAwait(false);
                RxLog.SetPrivateKey(key);
            }
            catch (Exception ex) { Log.Info("Sync", "Private key export unavailable (DM decoding in RX log disabled): " + ex.Message); }

            SetSync(new SyncProgress("Contacts", 0, 0));
            await SyncContactsAsync(force: false, ct).ConfigureAwait(false);

            SetSync(new SyncProgress("Channels", 0, Capabilities?.MaxChannels ?? 0));
            try { await Channels.SyncAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Sync", "Channel sync incomplete: " + ex.Message); }

            SetSync(new SyncProgress("Messages", 0, 0));
            var count = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                MessageResult r;
                try { r = await session.GetMessageAsync(ct: ct).ConfigureAwait(false); }
                catch (MeshCoreException ex) when (ex.Kind == MeshCoreErrorKind.Timeout) { Log.Warn("Sync", "Message poll timed out"); break; }
                if (r is MessageResult.NoMoreMessages) break;
                count++;
                SetSync(new SyncProgress("Messages", count, 0));
            }
            // Let the event loop persist everything that was fetched.
            await WaitForEventLoopIdleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Log.Info("Sync", $"Initial sync complete ({count} queued messages)");
        }
        finally
        {
            _initialSyncRunning = false;
            // Open chats re-check their unread count now that the backlog is in (and place their "New messages" line).
            Notify(DataKind.Conversations);
        }
    }

    /// <summary>Synchronises the local contact list with the radio's contact table.</summary>
    /// <param name="force">Full re-fetch (prunes local contacts that are no longer on the radio).</param>
    public async Task SyncContactsAsync(bool force, CancellationToken ct = default)
    {
        var session = RequireSession();
        var radioId = RequireRadioId();
        await _contactSyncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var radio = Db.GetRadio(radioId);
            var local = Db.GetContacts(radioId);
            var atCapacity = radio is { MaxContacts: > 0 } && local.Count >= radio.MaxContacts;
            DateTimeOffset? since = null;
            var watermark = radio?.LastContactSync ?? 0;
            if (!force && !atCapacity && watermark > 0)
            {
                var wm = DateTimeOffset.FromUnixTimeSeconds(watermark);
                if (wm <= DateTimeOffset.UtcNow + WatermarkSkew) since = wm.AddSeconds(-1);
            }
            var result = await session.GetContactsAsync(since, ct).ConfigureAwait(false);
            foreach (var c in result.Contacts) Db.SaveContactFrame(radioId, c);
            if (since is null && !PendingConfig.ProtectsContacts(radioId))
            {
                var removed = Db.PruneContacts(radioId, result.Contacts.Select(c => c.PublicKey).ToList());
                if (removed.Count > 0) Log.Info("Sync", $"Pruned {removed.Count} contacts no longer on the radio");
            }
            if (result.LastModified is { } lm) Db.SetContactWatermark(radioId, lm.ToUnixTimeSeconds());
            if (Radio is not null && result.LastModified is { } lm2) Radio.LastContactSync = lm2.ToUnixTimeSeconds();
            Log.Info("Sync", $"Contacts synced: {result.Contacts.Count} {(since is null ? "(full)" : "(incremental)")}");
            RxLog.RefreshContactKeys();
            Notify(DataKind.Contacts);
            Notify(DataKind.Conversations);
        }
        finally { _contactSyncLock.Release(); }
    }

    /// <summary>Debounced incremental contact sync triggered by adverts and path updates.</summary>
    internal void ScheduleDeltaContactSync(TimeSpan? delay = null)
    {
        if (_initialSyncRunning) return;
        _deltaSyncCts?.Cancel();
        var cts = new CancellationTokenSource();
        _deltaSyncCts = cts;
        var wait = delay ?? TimeSpan.FromSeconds(5);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(wait, cts.Token).ConfigureAwait(false);
                if (!IsConnected) return;
                await SyncContactsAsync(force: false, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("Sync", "Delta contact sync failed: " + ex.Message); }
        });
    }

    /// <summary>Full manual resync (contacts, channels and queued messages).</summary>
    public async Task ResyncAsync(CancellationToken ct = default)
    {
        var session = RequireSession();
        SetStatus(LinkStatus.Syncing, L.T("Resyncing…"));
        try
        {
            SetSync(new SyncProgress("Contacts", 0, 0));
            await SyncContactsAsync(force: true, ct).ConfigureAwait(false);
            SetSync(new SyncProgress("Channels", 0, 0));
            await Channels.SyncAsync(ct).ConfigureAwait(false);
            session.RequestMessageDrain();
            LastSyncAt = DateTimeOffset.Now;
        }
        finally
        {
            SetSync(null);
            SetStatus(Session is { IsRunning: true } ? LinkStatus.Ready : LinkStatus.Disconnected, CurrentTarget?.Describe());
        }
    }
}
