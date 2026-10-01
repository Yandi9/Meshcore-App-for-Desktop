using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Services;

public sealed partial class MeshApp
{
    private EventSubscription? _loopSub;
    private volatile bool _processingEvent;

    /// <summary>Raised for every radio event after built-in handling (tools subscribe to this).</summary>
    public event Action<MeshEvent>? RadioEvent;

    private async Task EventLoopAsync(MeshCoreSession session, CancellationToken ct)
    {
        using var sub = session.Subscribe();
        _loopSub = sub;
        try
        {
            await foreach (var e in sub.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _processingEvent = true;
                try { await HandleEventAsync(session, e).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("Events", $"Handling {e.CaseName} failed: {ex}"); }
                finally { _processingEvent = false; }
                try { RadioEvent?.Invoke(e); } catch (Exception ex) { Log.Error("Events", "RadioEvent handler: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
        finally { _loopSub = null; }
    }

    internal async Task WaitForEventLoopIdleAsync(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        await Task.Delay(50).ConfigureAwait(false);
        while (DateTime.UtcNow < until)
        {
            var sub = _loopSub;
            if (sub is null || (sub.Reader.Count == 0 && !_processingEvent)) return;
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private async Task HandleEventAsync(MeshCoreSession session, MeshEvent e)
    {
        var radioId = Radio?.Id;
        if (radioId is null) return;
        switch (e)
        {
            case MeshEvent.ContactMessageReceived m:
                switch ((TextType)m.Message.TextType)
                {
                    case TextType.CliData: Remote.HandleCliResponse(m.Message); break;
                    case TextType.Signed: Rooms.HandleIncoming(m.Message); break;
                    default: await Messages.IngestDirectAsync(m.Message, _initialSyncRunning).ConfigureAwait(false); break;
                }
                break;

            case MeshEvent.ChannelMessageReceived m:
                Messages.IngestChannel(m.Message, _initialSyncRunning);
                break;

            case MeshEvent.Acknowledgement a:
                Messages.HandleAck(a.Code, a.TripTime);
                break;

            case MeshEvent.Advertisement a:
                var known = Db.TouchContactHeard(radioId, a.PublicKey, Time.Now());
                if (known) Notify(DataKind.Contacts);
                ScheduleDeltaContactSync();
                break;

            case MeshEvent.PathUpdate:
                ScheduleDeltaContactSync(TimeSpan.FromSeconds(2));
                break;

            case MeshEvent.NewContact n:
                Contacts.HandleNewAdvert(n.Value);
                ScheduleDeltaContactSync();
                break;

            case MeshEvent.ContactDeleted d:
                Db.DeleteContactByKey(radioId, d.PublicKey);
                ContactsFull = false;
                Log.Info("Contacts", $"Radio removed contact {Convert.ToHexString(d.PublicKey, 0, 4)} to make room (overwrite oldest)");
                Notify(DataKind.Contacts);
                Notify(DataKind.Conversations);
                break;

            case MeshEvent.ContactsFull:
                if (!ContactsFull)
                {
                    ContactsFull = true;
                    Notifier.ShowInfo(L.T("Radio contact storage is full"), L.T("New nodes can't be added until you remove some contacts or enable \"overwrite oldest\"."));
                    try { StatusChanged?.Invoke(); } catch { /* ignore */ }
                }
                break;

            case MeshEvent.LoginSuccess l:
                Remote.HandleLoginResult(l.Info.PublicKeyPrefix, true, l.Info);
                break;

            case MeshEvent.LoginFailed l:
                if (l.PublicKeyPrefix is { } p) Remote.HandleLoginResult(p, false, null);
                else Remote.HandleLoginFailedUnknown();
                break;

            case MeshEvent.RxLogData r:
                await RxLog.ProcessAsync(r.Info).ConfigureAwait(false);
                break;

            case MeshEvent.PathResponse pr:
                Contacts.HandlePathDiscovery(pr.Info);
                break;

            case MeshEvent.SelfInfoEvent s:
                RefreshSelfInfo(s.Info);
                break;

            case MeshEvent.StatusResponseEvent s:
                Remote.RecordStatus(s.Response);
                break;
        }
    }
}
