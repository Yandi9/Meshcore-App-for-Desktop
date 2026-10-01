using System.Security.Cryptography;
using System.Text;
using MC1.Core.Models;
using MeshCore;

namespace MC1.Core.Services;

/// <summary>Room-server conversations (signed posts relayed by the room).</summary>
public sealed class RoomService(MeshApp app)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _ackToMessage = new();

    public IReadOnlyList<RemoteSessionRecord> Rooms() => app.Remote.Sessions().Where(s => s.IsRoom).ToList();

    public IReadOnlyList<RoomMessageRecord> Messages(string sessionId) => app.Db.GetRoomMessages(sessionId);

    public static string DedupKey(uint timestamp, byte[] authorPrefix, string text)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return $"{timestamp}-{Convert.ToHexString(authorPrefix)}-{Convert.ToHexString(h, 0, 4)}";
    }

    public async Task<RemoteSessionRecord> JoinAsync(ContactRecord room, string? password, bool remember, IProgress<int>? timeout = null, CancellationToken ct = default)
    {
        var result = await app.Remote.LoginAsync(room, password, remember, timeout, ct).ConfigureAwait(false);
        var remote = app.Remote.GetOrCreateSession(room);
        app.Log.Info("Rooms", $"Joined {room.DisplayName} as {result.Permission}");
        // Ask the room to push history since our last sync (status request doubles as a sync poke).
        _ = Task.Run(async () =>
        {
            try { await app.RequireSession().RequestStatusAsync(room.PublicKey, ContactType.Room).ConfigureAwait(false); }
            catch (Exception ex) { app.Log.Debug("Rooms", "History sync poke failed: " + ex.Message); }
        });
        app.Notify(DataKind.Rooms);
        app.Notify(DataKind.Conversations);
        return app.Db.GetRemoteSession(remote.Id) ?? remote;
    }

    public async Task LeaveAsync(RemoteSessionRecord room)
    {
        await app.Remote.LogoutAsync(room).ConfigureAwait(false);
        app.Remote.RemoveSession(room);
        app.Notify(DataKind.Rooms);
    }

    public RoomMessageRecord Post(RemoteSessionRecord room, string text)
    {
        if (!room.CanPost) throw new RemoteNodeException(L.T("You have read-only access to this room."));
        if (text.Utf8Length() > MessageService.MaxDirectMessageBytes) throw new MessageServiceException(L.T("Message is too long."));
        var session = app.RequireSession();
        var selfPrefix = session.SelfInfo?.PublicKey.Prefix(4) ?? new byte[4];
        var now = DateTimeOffset.UtcNow;
        var msg = new RoomMessageRecord
        {
            SessionId = room.Id,
            AuthorKeyPrefix = selfPrefix,
            AuthorName = app.SelfName,
            Text = text,
            Timestamp = now.ToUnixTimeSeconds(),
            IsFromSelf = true,
            Status = (int)MessageStatus.Pending,
            DedupKey = DedupKey((uint)now.ToUnixTimeSeconds(), selfPrefix, text),
        };
        app.Db.SaveRoomMessage(msg);
        app.Db.UpdateRoomActivity(room.Id);
        app.Notify(DataKind.RoomMessages, room.Id, msg.Id);
        _ = Task.Run(() => SendAsync(room, msg, now));
        return msg;
    }

    public void Retry(RoomMessageRecord msg)
    {
        var room = app.Db.GetRemoteSession(msg.SessionId);
        if (room is null) return;
        msg.RetryAttempt++;
        msg.Status = (int)MessageStatus.Pending;
        app.Db.SaveRoomMessage(msg);
        app.Notify(DataKind.RoomMessages, room.Id, msg.Id);
        _ = Task.Run(() => SendAsync(room, msg, DateTimeOffset.UtcNow));
    }

    private async Task SendAsync(RemoteSessionRecord room, RoomMessageRecord msg, DateTimeOffset timestamp)
    {
        try
        {
            var session = app.RequireSession();
            var s = app.Settings.Current;
            await app.Channels.ApplyFloodScopeAsync(null).ConfigureAwait(false);
            var sent = await session.SendMessageWithRetryAsync(room.PublicKey, msg.Text, timestamp, Math.Clamp(s.DmMaxAttempts, 1, 5), Math.Clamp(s.DmFloodAfter, 1, 5), 1).ConfigureAwait(false);
            app.Db.UpdateRoomMessageStatus(msg.Id, sent is not null ? MessageStatus.Delivered : MessageStatus.Sent, sent?.AckCodeUInt32);
            app.Db.UpdateRoomActivity(room.Id);
        }
        catch (Exception ex)
        {
            app.Log.Warn("Rooms", "Room post failed: " + ex.Message);
            app.Db.UpdateRoomMessageStatus(msg.Id, MessageStatus.Failed);
        }
        app.Notify(DataKind.RoomMessages, room.Id, msg.Id);
        app.Notify(DataKind.Conversations);
    }

    internal void HandleAck(byte[] code) { /* room posts wait for their ACK inside SendMessageWithRetryAsync */ }

    internal void HandleIncoming(ContactMessage m)
    {
        var radioId = app.RadioId;
        if (radioId is null) return;
        if (m.Signature is not { Length: 4 } author) { app.Log.Warn("Rooms", "Dropping signed message without author prefix"); return; }
        var room = app.Db.GetRemoteSessions(radioId).FirstOrDefault(s => s.IsRoom && s.PublicKey.StartsWith(m.SenderPublicKeyPrefix));
        if (room is null)
        {
            // A room we never joined in this app (e.g. joined from another client): create a session so the posts show.
            var contact = app.Db.GetContactByPrefix(radioId, m.SenderPublicKeyPrefix);
            if (contact is null || contact.ContactType != ContactType.Room) return;
            room = app.Remote.GetOrCreateSession(contact);
        }
        if (!room.IsConnected) { app.Db.SetRemoteSessionConnected(room.Id, true); app.Notify(DataKind.Sessions, room.Id); }
        var ts = (uint)m.SenderTimestamp.ToUnixTimeSeconds();
        var dedup = DedupKey(ts, author, m.Text);
        if (app.Db.RoomMessageExists(room.Id, dedup)) return;
        var selfPrefix = app.SelfInfo?.PublicKey.Prefix(4);
        var isSelf = selfPrefix is not null && selfPrefix.SequenceEquals(author);
        var authorName = isSelf ? app.SelfName : ResolveAuthor(radioId, author);
        var msg = new RoomMessageRecord
        {
            SessionId = room.Id,
            AuthorKeyPrefix = author,
            AuthorName = authorName,
            Text = m.Text,
            Timestamp = ts,
            IsFromSelf = isSelf,
            DedupKey = dedup,
        };
        app.Db.SaveRoomMessage(msg);
        var key = MessageService.RoomKey(room.Id);
        app.Db.UpdateRoomActivity(room.Id, ts, incrementUnread: !isSelf && !app.Messages.IsSeenOnArrival(key));
        var st = app.Settings.Current;
        if (!isSelf && st.NotificationsEnabled && st.NotifyRoomMessages && room.NotificationLevel != (int)NotificationLevel.Muted && !app.Messages.IsConversationOpen(key))
            app.Notifier.ShowMessage(room.Name, $"{authorName}: {m.Text}", key, allowReply: room.CanPost);
        app.Notify(DataKind.RoomMessages, room.Id, msg.Id);
        app.Notify(DataKind.Conversations);
        app.Messages.UpdateBadge();
    }

    private string ResolveAuthor(string radioId, byte[] prefix)
    {
        var c = app.Db.GetContacts(radioId).FirstOrDefault(c => c.PublicKey.StartsWith(prefix));
        return c?.DisplayName ?? Convert.ToHexString(prefix);
    }

    public void ClearHistory(RemoteSessionRecord room)
    {
        app.Db.ClearRoomMessages(room.Id);
        app.Notify(DataKind.RoomMessages, room.Id);
        app.Notify(DataKind.Conversations);
    }
}
