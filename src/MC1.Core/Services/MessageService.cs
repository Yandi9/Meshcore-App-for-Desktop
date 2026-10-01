using System.Collections.Concurrent;
using MC1.Core.Models;
using MC1.Core.Utilities;
using MeshCore;

namespace MC1.Core.Services;

public sealed class MessageServiceException(string message) : Exception(message);

/// <summary>Sending, receiving, ACK tracking, retries and reactions for DMs and channels.</summary>
public sealed class MessageService
{
    public const int MaxDirectMessageBytes = 150;
    public const int MaxChannelMessageTotalBytes = 139;
    private static readonly TimeSpan AckGiveUpWindow = TimeSpan.FromSeconds(30);
    private const uint ReactionWindowSeconds = 300;

    private readonly MeshApp _app;
    private readonly ConcurrentDictionary<string, PendingAck> _pendingAcks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly List<(string Key, ParsedReaction? Channel, ParsedDmReaction? Dm, string Sender, string Raw, string RadioId, int? ChannelIndex, string? ContactId)> _pendingReactions = new();
    private Timer? _ackTimer;

    private sealed class PendingAck
    {
        public required string MessageId { get; init; }
        public required string ContactId { get; init; }
        public HashSet<string> Codes { get; } = new();
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
        public bool Delivered { get; set; }
        public bool LoopFinished { get; set; }
    }

    public MessageService(MeshApp app) => _app = app;

    /// <summary>Raised when a message row changes (status, repeats, reactions).</summary>
    public event Action<string>? MessageUpdated;

    private void Updated(string messageId, string conversationKey)
    {
        try { MessageUpdated?.Invoke(messageId); } catch { /* ignore */ }
        _app.Notify(DataKind.Messages, conversationKey, messageId);
    }

    public static string DmKey(string contactId) => "dm:" + contactId;
    public static string ChannelKey(int idx) => "ch:" + idx;
    public static string RoomKey(string sessionId) => "room:" + sessionId;

    public int MaxChannelMessageBytes => Math.Max(0, MaxChannelMessageTotalBytes - Math.Max(1, _app.SelfName.Utf8Length()) - 2);

    // MARK: Incoming

    private static (long Timestamp, bool Corrected) CorrectTimestamp(uint ts, DateTimeOffset now)
    {
        var s = now.ToUnixTimeSeconds();
        if (ts > s + 5 * 60 || ts < s - 180L * 24 * 3600) return (s, true);
        return (ts, false);
    }

    internal async Task IngestDirectAsync(ContactMessage m, bool fromInitialSync)
    {
        var radioId = _app.RequireRadioId();
        var contact = _app.Db.GetContactByPrefix(radioId, m.SenderPublicKeyPrefix);
        if (contact is null && _app.IsConnected)
        {
            // The radio only decrypts DMs from contacts it knows; our copy is just stale.
            try { await _app.SyncContactsAsync(false).ConfigureAwait(false); } catch (Exception ex) { _app.Log.Warn("Messages", "Contact refresh for unknown sender failed: " + ex.Message); }
            contact = _app.Db.GetContactByPrefix(radioId, m.SenderPublicKeyPrefix);
        }

        var now = DateTimeOffset.UtcNow;
        var ts = (uint)m.SenderTimestamp.ToUnixTimeSeconds();
        var (finalTs, corrected) = CorrectTimestamp(ts, now);
        var text = m.Text;
        var dedup = DeduplicationKey.ContentBased(contact?.Id, null, null, ts, text);
        if (_app.Db.GetMessageByDedup(radioId, dedup) is not null) return;

        if (contact is not null)
        {
            _app.Db.TouchContactHeard(radioId, contact.PublicKey, Time.Now());
            if (HandleDmReaction(text, contact, radioId)) return;
        }

        var selfName = _app.SelfName;
        var mention = MentionUtilities.ContainsSelfMention(text, selfName);
        var msg = new MessageRecord
        {
            RadioId = radioId,
            ContactId = contact?.Id,
            Text = text,
            Timestamp = finalTs,
            SenderTimestamp = corrected ? ts : null,
            TimestampCorrected = corrected,
            SortDate = fromInitialSync && !corrected ? Math.Min(Time.Now(), finalTs * 1000) : Time.Now(),
            Direction = (int)MessageDirection.Incoming,
            Status = (int)MessageStatus.Delivered,
            TextType = m.TextType,
            PathLength = m.PathLength,
            Snr = m.Snr,
            SenderKeyPrefix = m.SenderPublicKeyPrefix,
            DedupKey = dedup,
            ContainsSelfMention = mention,
        };
        _app.Db.SaveMessage(msg);
        if (contact is null)
        {
            _app.Log.Warn("Messages", $"Stored DM from unknown sender {Convert.ToHexString(m.SenderPublicKeyPrefix)}");
            _app.Notify(DataKind.Conversations);
            return;
        }
        ApplyPendingReactions(DmKey(contact.Id), msg, null);
        _app.Db.UpdateContactLastMessage(contact.Id, Time.Now());
        if (!contact.IsBlocked)
        {
            if (!IsSeenOnArrival(DmKey(contact.Id))) _app.Db.IncrementContactUnread(contact.Id, mention);
            else _app.Db.ClearContactUnread(contact.Id);
            var s = _app.Settings.Current;
            if (s.NotificationsEnabled && s.NotifyDirectMessages && !contact.IsMuted && !IsConversationOpen(DmKey(contact.Id)))
                _app.Notifier.ShowMessage(contact.DisplayName, text, DmKey(contact.Id), allowReply: true);
        }
        _app.Notify(DataKind.Messages, DmKey(contact.Id), msg.Id);
        _app.Notify(DataKind.Conversations);
        UpdateBadge();
    }

    internal void IngestChannel(ChannelMessage m, bool fromInitialSync)
    {
        var radioId = _app.RequireRadioId();
        var channel = _app.Db.GetChannel(radioId, m.ChannelIndex);
        var (sender, text) = ChannelMessageFormat.Parse(m.Text);
        var now = DateTimeOffset.UtcNow;
        var ts = (uint)m.SenderTimestamp.ToUnixTimeSeconds();
        var (finalTs, corrected) = CorrectTimestamp(ts, now);
        var dedup = DeduplicationKey.ContentBased(null, m.ChannelIndex, sender, ts, text);
        var existing = _app.Db.GetMessageByDedup(radioId, dedup);
        if (existing is not null) return;
        if (sender is not null && IsBlockedSender(radioId, sender)) return;
        if (HandleChannelReaction(text, m.ChannelIndex, sender, radioId)) return;

        var selfName = _app.SelfName;
        var mention = sender != selfName && MentionUtilities.ContainsSelfMention(text, selfName);
        var rx = _app.RxLog.LookupPathForChannel(radioId, m.ChannelIndex, ts, dedup);
        var msg = new MessageRecord
        {
            RadioId = radioId,
            ChannelIndex = m.ChannelIndex,
            Text = text,
            Timestamp = finalTs,
            SenderTimestamp = corrected ? ts : null,
            TimestampCorrected = corrected,
            SortDate = fromInitialSync && !corrected ? Math.Min(Time.Now(), finalTs * 1000) : Time.Now(),
            Direction = (int)MessageDirection.Incoming,
            Status = (int)MessageStatus.Delivered,
            TextType = m.TextType,
            PathLength = rx?.PathLength ?? m.PathLength,
            PathNodes = rx?.PathNodes,
            RouteType = rx?.RouteType ?? -1,
            RegionScope = rx?.RegionScope,
            Snr = m.Snr,
            SenderName = sender,
            DedupKey = dedup,
            ContainsSelfMention = mention,
        };
        _app.Db.SaveMessage(msg);
        ApplyPendingReactions(ChannelKey(m.ChannelIndex), msg, sender);
        _app.Db.UpdateChannelLastMessage(radioId, m.ChannelIndex, Time.Now());
        var key = ChannelKey(m.ChannelIndex);
        if (!IsSeenOnArrival(key)) _app.Db.IncrementChannelUnread(radioId, m.ChannelIndex, mention);
        var s = _app.Settings.Current;
        var level = (NotificationLevel)(channel?.NotificationLevel ?? 0);
        if (channel is not null && s.NotificationsEnabled && s.NotifyChannelMessages && !IsConversationOpen(key) &&
            (level == NotificationLevel.All || (level == NotificationLevel.MentionsOnly && mention)))
            _app.Notifier.ShowMessage($"{channel.DisplayName}", sender is null ? text : $"{sender}: {text}", key, allowReply: true);
        _app.Notify(DataKind.Messages, key, msg.Id);
        _app.Notify(DataKind.Conversations);
        UpdateBadge();
    }

    private readonly ConcurrentDictionary<string, HashSet<string>> _blockedCache = new();

    internal void InvalidateBlockedCache() => _blockedCache.Clear();

    private bool IsBlockedSender(string radioId, string name)
    {
        var set = _blockedCache.GetOrAdd(radioId, id =>
        {
            var names = _app.Db.GetBlockedSenders(id).Select(b => b.Name).ToHashSet();
            foreach (var c in _app.Db.GetBlockedContacts(id)) names.Add(c.Name);
            return names;
        });
        return set.Contains(name);
    }

    // MARK: Open conversation tracking (for unread counts)

    private readonly ConcurrentDictionary<string, byte> _openConversations = new();

    public void SetConversationOpen(string key, bool open)
    {
        if (open) _openConversations[key] = 0; else _openConversations.TryRemove(key, out _);
    }

    public bool AppIsForeground { get; set; } = true;

    /// <summary>The PC is locked: nothing is on screen, so arriving messages stay unread (and show on the lock screen).</summary>
    public bool ScreenLocked { get; set; }

    public bool IsConversationOpen(string key) => AppIsForeground && !ScreenLocked && _openConversations.ContainsKey(key);

    /// <summary>
    /// True when a message arriving now is seen immediately: its chat is on screen and the window is in front.
    /// Messages the radio queued while the app was closed or disconnected (initial sync) always count as unread,
    /// so the chat shows a "New messages" line above them.
    /// </summary>
    public bool IsSeenOnArrival(string key) => IsConversationOpen(key) && !_app.IsInitialSyncRunning;

    /// <summary>Unread count stored for a conversation.</summary>
    public int UnreadCount(string key)
    {
        var radioId = _app.RadioId;
        if (radioId is null) return 0;
        if (key.StartsWith("dm:")) return _app.Db.GetContact(key[3..])?.UnreadCount ?? 0;
        if (key.StartsWith("ch:") && int.TryParse(key[3..], out var idx)) return _app.Db.GetChannel(radioId, idx)?.UnreadCount ?? 0;
        if (key.StartsWith("room:")) return _app.Db.GetRemoteSession(key[5..])?.UnreadCount ?? 0;
        return 0;
    }

    public void MarkRead(string key)
    {
        var radioId = _app.RadioId;
        if (radioId is null) return;
        if (key.StartsWith("dm:")) _app.Db.ClearContactUnread(key[3..]);
        else if (key.StartsWith("ch:")) _app.Db.ClearChannelUnread(radioId, int.Parse(key[3..]));
        else if (key.StartsWith("room:")) _app.Db.ClearRoomUnread(key[5..]);
        _app.Notify(DataKind.Conversations);
        UpdateBadge();
    }

    public int TotalUnread()
    {
        var radioId = _app.RadioId;
        if (radioId is null) return 0;
        var contacts = _app.Db.GetContacts(radioId).Where(c => !c.IsBlocked && !c.IsMuted).Sum(c => c.UnreadCount);
        var channels = _app.Db.GetChannels(radioId).Where(c => !c.IsMuted).Sum(c => c.UnreadCount);
        var rooms = _app.Db.GetRemoteSessions(radioId).Where(r => r.IsRoom && r.NotificationLevel != (int)NotificationLevel.Muted).Sum(r => r.UnreadCount);
        return contacts + channels + rooms;
    }

    internal void UpdateBadge()
    {
        try { _app.Notifier.UpdateBadge(TotalUnread()); } catch { /* ignore */ }
    }

    // MARK: Sending direct messages

    public MessageRecord QueueDirectMessage(ContactRecord contact, string text, string? replyToId = null)
    {
        if (contact.ContactType == ContactType.Repeater) throw new MessageServiceException(L.T("Repeaters can't receive messages."));
        if (text.Utf8Length() > MaxDirectMessageBytes) throw new MessageServiceException(L.F("Message is too long (max {0} bytes).", MaxDirectMessageBytes));
        var radioId = _app.RequireRadioId();
        var msg = new MessageRecord
        {
            RadioId = radioId,
            ContactId = contact.Id,
            Text = text,
            Timestamp = Bytes.NowEpoch(),
            Direction = (int)MessageDirection.Outgoing,
            Status = (int)MessageStatus.Pending,
            ReplyToId = replyToId,
            MaxRetryAttempts = _app.Settings.Current.DmMaxAttempts - 1,
        };
        _app.Db.SaveMessage(msg);
        _app.Db.UpdateContactLastMessage(contact.Id, Time.Now());
        _app.Notify(DataKind.Messages, DmKey(contact.Id), msg.Id);
        _app.Notify(DataKind.Conversations);
        _ = Task.Run(() => SendDirectLoopAsync(msg.Id, contact.Id, preserveTimestamp: true));
        return msg;
    }

    public void ResendDirect(string messageId)
    {
        var m = _app.Db.GetMessage(messageId);
        if (m?.ContactId is null) return;
        _app.Db.UpdateMessageStatus(messageId, MessageStatus.Pending);
        Updated(messageId, DmKey(m.ContactId));
        _ = Task.Run(() => SendDirectLoopAsync(messageId, m.ContactId, preserveTimestamp: false, isResend: true));
    }

    private SemaphoreSlim LockFor(string key) => _conversationLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private async Task SendDirectLoopAsync(string messageId, string contactId, bool preserveTimestamp, bool isResend = false)
    {
        if (!_inFlight.TryAdd(messageId, 0)) return;
        var key = DmKey(contactId);
        var gate = LockFor(key);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _app.Session;
            var contact = _app.Db.GetContact(contactId);
            var msg = _app.Db.GetMessage(messageId);
            if (contact is null || msg is null) return;
            if (session is null || !session.IsRunning || session.SelfInfo is null)
            {
                _app.Db.UpdateMessageStatus(messageId, MessageStatus.Failed);
                Updated(messageId, key);
                return;
            }
            uint ts;
            if (preserveTimestamp) ts = (uint)msg.Timestamp;
            else { ts = Bytes.NowEpoch(); _app.Db.UpdateMessageTimestamp(messageId, ts); }
            var wireTime = DateTimeOffset.FromUnixTimeSeconds(ts);
            var settings = _app.Settings.Current;
            var maxAttempts = Math.Clamp(settings.DmMaxAttempts, 1, 5);
            var floodAfter = Math.Clamp(settings.DmFloodAfter, 1, maxAttempts);
            var pending = new PendingAck { MessageId = messageId, ContactId = contactId };
            _pendingAcks[messageId] = pending;
            var attempts = 0;
            var floodAttempts = 0;
            var isFlood = false;
            MessageSentInfo? lastSent = null;
            var initialPathLen = contact.OutPathLength;

            _app.Db.UpdateMessageStatus(messageId, MessageStatus.Sending);
            Updated(messageId, key);
            await _app.Channels.ApplyFloodScopeAsync(null).ConfigureAwait(false);

            while (attempts < maxAttempts && (!isFlood || floodAttempts < 1))
            {
                if (pending.Delivered) break;
                if (attempts > 0)
                {
                    _app.Db.UpdateMessageRetry(messageId, MessageStatus.Retrying, attempts - 1, maxAttempts - 1);
                    Updated(messageId, key);
                }
                if (attempts == floodAfter && !isFlood && contact.IsFloodRouted) isFlood = true;
                if (attempts == floodAfter && !isFlood)
                {
                    try
                    {
                        await session.ResetPathAsync(contact.PublicKey).ConfigureAwait(false);
                        _app.Log.Info("Messages", $"Reset path to flood for {contact.DisplayName} after {attempts} attempts");
                        if (await session.GetContactAsync(contact.PublicKey).ConfigureAwait(false) is { } updated)
                            _app.Db.SaveContactFrame(contact.RadioId, updated);
                        _app.Notify(DataKind.Contacts);
                    }
                    catch (Exception ex) { _app.Log.Warn("Messages", "Path reset failed: " + ex.Message); }
                    isFlood = true;
                }
                var predicted = AckCodeBuilder.ExpectedAck(ts, (byte)attempts, msg.Text, session.SelfInfo!.PublicKey);
                pending.Codes.Add(predicted.ToHex());
                MessageSentInfo sent;
                try
                {
                    sent = await SendWithPoolBackoffAsync(() => session.SendMessageAsync(contact.PublicKey.Prefix(6), msg.Text, wireTime, (byte)attempts)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _app.Log.Warn("Messages", $"DM send failed: {ex.Message}");
                    _pendingAcks.TryRemove(messageId, out _);
                    _app.Db.UpdateMessageStatus(messageId, MessageStatus.Failed);
                    Updated(messageId, key);
                    return;
                }
                lastSent = sent;
                pending.Codes.Add(sent.ExpectedAck.ToHex());
                pending.SentAt = DateTimeOffset.UtcNow;
                var ackTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, sent.SuggestedTimeoutMs * 1.2));
                pending.Timeout = ackTimeout;
                if (attempts == 0) { _app.Db.UpdateMessageAck(messageId, sent.AckCodeUInt32, MessageStatus.Sent); Updated(messageId, key); }
                if (isResend && attempts == 0) _app.Db.IncrementSendCount(messageId);
                var deadline = DateTimeOffset.UtcNow + ackTimeout;
                while (!pending.Delivered && DateTimeOffset.UtcNow < deadline && session.IsRunning)
                    await Task.Delay(100).ConfigureAwait(false);
                if (pending.Delivered) break;
                attempts++;
                if (isFlood) floodAttempts++;
                if (!session.IsRunning) break;
            }

            pending.LoopFinished = true;
            if (pending.Delivered)
            {
                _pendingAcks.TryRemove(messageId, out _);
            }
            else if (lastSent is not null)
            {
                // Keep listening for a late ACK; the expiry timer fails it afterwards.
                _app.Db.UpdateMessageStatus(messageId, MessageStatus.Sent);
                Updated(messageId, key);
                EnsureAckTimer();
            }
            if (_app.Session is { IsRunning: true } s2)
            {
                try
                {
                    if (await s2.GetContactAsync(contact.PublicKey).ConfigureAwait(false) is { } updated && updated.OutPathLength != initialPathLen)
                    {
                        _app.Db.SaveContactFrame(contact.RadioId, updated);
                        _app.Notify(DataKind.Contacts);
                    }
                }
                catch { /* informational */ }
            }
        }
        finally
        {
            gate.Release();
            _inFlight.TryRemove(messageId, out _);
        }
    }

    private static async Task<T> SendWithPoolBackoffAsync<T>(Func<Task<T>> send)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await send().ConfigureAwait(false); }
            catch (MeshCoreException ex) when (ex.Kind == MeshCoreErrorKind.DeviceError && ex.FirmwareError is ErrorCode.TableFull or ErrorCode.NotFound && attempt < 3)
            {
                var delay = 500 * Math.Pow(2, attempt) * (0.8 + Random.Shared.NextDouble() * 0.4);
                await Task.Delay(TimeSpan.FromMilliseconds(delay)).ConfigureAwait(false);
            }
        }
    }

    private async Task SendWithPoolBackoffAsync(Func<Task> send) =>
        await SendWithPoolBackoffAsync<object>(async () => { await send().ConfigureAwait(false); return true; }).ConfigureAwait(false);

    internal void HandleAck(byte[] code, uint? tripTime)
    {
        var hex = code.ToHex();
        foreach (var p in _pendingAcks.Values)
        {
            if (p.Delivered || !p.Codes.Contains(hex)) continue;
            p.Delivered = true;
            var rtt = tripTime ?? (uint)(DateTimeOffset.UtcNow - p.SentAt).TotalMilliseconds;
            _app.Db.UpdateMessageAck(p.MessageId, code.ReadUInt32LE(0), MessageStatus.Delivered, rtt);
            _app.Db.UpdateContactLastMessage(p.ContactId, Time.Now());
            if (p.LoopFinished) _pendingAcks.TryRemove(p.MessageId, out _);
            Updated(p.MessageId, DmKey(p.ContactId));
            return;
        }
        // Also covers room posts, which use the session retry helper.
        _app.Rooms.HandleAck(code);
    }

    private void EnsureAckTimer()
    {
        _ackTimer ??= new Timer(_ => CheckExpiredAcks(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void CheckExpiredAcks()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var p in _pendingAcks.Values.ToList())
        {
            if (p.Delivered || !p.LoopFinished) continue;
            var deadline = p.Timeout > AckGiveUpWindow ? p.Timeout : AckGiveUpWindow;
            if (now - p.SentAt <= deadline) continue;
            if (_pendingAcks.TryRemove(p.MessageId, out _) && _app.Db.UpdateMessageStatusUnlessDelivered(p.MessageId, MessageStatus.Failed))
                Updated(p.MessageId, DmKey(p.ContactId));
        }
    }

    internal void OnDisconnected()
    {
        foreach (var p in _pendingAcks.Values.ToList())
        {
            _pendingAcks.TryRemove(p.MessageId, out _);
            if (!p.Delivered && _app.Db.UpdateMessageStatusUnlessDelivered(p.MessageId, MessageStatus.Failed))
                Updated(p.MessageId, DmKey(p.ContactId));
        }
        lock (_pendingReactions) _pendingReactions.Clear();
    }

    /// <summary>Messages left pending by a previous run are marked failed so the user can retry them.</summary>
    internal void ResumePendingAfterConnect()
    {
        var radioId = _app.RadioId;
        if (radioId is null) return;
        var stuck = _app.Db.GetPendingOutgoing(radioId).Where(m => !_inFlight.ContainsKey(m.Id)).ToList();
        foreach (var m in stuck)
        {
            _app.Db.UpdateMessageStatus(m.Id, MessageStatus.Failed);
            Updated(m.Id, m.ContactId is { } c ? DmKey(c) : ChannelKey(m.ChannelIndex ?? 0));
        }
    }

    // MARK: Channels

    public MessageRecord QueueChannelMessage(int channelIndex, string text)
    {
        if (text.Utf8Length() > MaxChannelMessageBytes) throw new MessageServiceException(L.F("Message is too long (max {0} bytes on channels).", MaxChannelMessageBytes));
        var radioId = _app.RequireRadioId();
        var msg = new MessageRecord
        {
            RadioId = radioId,
            ChannelIndex = channelIndex,
            Text = text,
            Timestamp = Bytes.NowEpoch(),
            Direction = (int)MessageDirection.Outgoing,
            Status = (int)MessageStatus.Pending,
            SenderName = _app.SelfName,
        };
        _app.Db.SaveMessage(msg);
        _app.Db.UpdateChannelLastMessage(radioId, channelIndex, Time.Now());
        _app.Notify(DataKind.Messages, ChannelKey(channelIndex), msg.Id);
        _app.Notify(DataKind.Conversations);
        _ = Task.Run(() => SendChannelAsync(msg.Id, channelIndex, preserveTimestamp: true));
        return msg;
    }

    public void ResendChannel(string messageId)
    {
        var m = _app.Db.GetMessage(messageId);
        if (m?.ChannelIndex is not { } idx) return;
        _app.Db.ResetHeardRepeats(messageId);
        _ = Task.Run(() => SendChannelAsync(messageId, idx, preserveTimestamp: false, isResend: true));
    }

    private async Task SendChannelAsync(string messageId, int channelIndex, bool preserveTimestamp, bool isResend = false)
    {
        var key = ChannelKey(channelIndex);
        var gate = LockFor(key);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var msg = _app.Db.GetMessage(messageId);
            if (msg is null) return;
            var session = _app.Session;
            if (session is null || !session.IsRunning)
            {
                _app.Db.UpdateMessageStatus(messageId, MessageStatus.Failed);
                Updated(messageId, key);
                return;
            }
            uint ts = preserveTimestamp ? (uint)msg.Timestamp : Bytes.NowEpoch();
            if (!preserveTimestamp) _app.Db.UpdateMessageTimestamp(messageId, ts);
            _app.Db.UpdateMessageStatus(messageId, MessageStatus.Sending);
            Updated(messageId, key);
            try
            {
                await _app.Channels.ApplyFloodScopeAsync(channelIndex).ConfigureAwait(false);
                await SendWithPoolBackoffAsync(() => session.SendChannelMessageAsync((byte)channelIndex, msg.Text, DateTimeOffset.FromUnixTimeSeconds(ts))).ConfigureAwait(false);
                _app.Db.UpdateMessageStatus(messageId, MessageStatus.Sent);
                if (isResend) _app.Db.IncrementSendCount(messageId);
            }
            catch (Exception ex)
            {
                _app.Log.Warn("Messages", "Channel send failed: " + ex.Message);
                _app.Db.UpdateMessageStatus(messageId, MessageStatus.Failed);
            }
            Updated(messageId, key);
        }
        finally { gate.Release(); }
    }

    public void DeleteMessage(MessageRecord m)
    {
        _app.Db.DeleteMessage(m.Id);
        var key = m.ContactId is { } c ? DmKey(c) : ChannelKey(m.ChannelIndex ?? 0);
        _app.Notify(DataKind.Messages, key);
        _app.Notify(DataKind.Conversations);
    }

    // MARK: Reactions

    public async Task SendReactionAsync(MessageRecord target, string emoji)
    {
        var radioId = _app.RequireRadioId();
        var session = _app.RequireSession();
        string text;
        if (target.ChannelIndex is { } idx)
        {
            var targetSender = target.IsOutgoing ? _app.SelfName : target.SenderName ?? "";
            text = ReactionParser.BuildChannelReaction(emoji, targetSender, target.Text, target.WireTimestamp);
            await _app.Channels.ApplyFloodScopeAsync(idx).ConfigureAwait(false);
            await SendWithPoolBackoffAsync(() => session.SendChannelMessageAsync((byte)idx, text, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        else if (target.ContactId is { } cid)
        {
            var contact = _app.Db.GetContact(cid) ?? throw new MessageServiceException(L.T("Contact not found"));
            text = ReactionParser.BuildDmReaction(emoji, target.Text, target.WireTimestamp);
            var sent = await session.SendMessageAsync(contact.PublicKey.Prefix(6), text, DateTimeOffset.UtcNow).ConfigureAwait(false);
            _ = sent;
        }
        else return;
        _app.Db.SaveReactionIfNew(new ReactionRecord
        {
            MessageId = target.Id,
            RadioId = radioId,
            Emoji = emoji,
            SenderName = _app.SelfName,
            MessageHash = ReactionParser.GenerateMessageHash(target.Text, target.WireTimestamp),
            RawText = text,
            ContactId = target.ContactId,
            ChannelIndex = target.ChannelIndex,
            IsOutgoing = true,
        });
        RefreshReactionSummary(target.Id, target.ContactId is { } c2 ? DmKey(c2) : ChannelKey(target.ChannelIndex!.Value));
        _app.Settings.Update(s =>
        {
            s.RecentEmoji.Remove(emoji);
            s.RecentEmoji.Insert(0, emoji);
            if (s.RecentEmoji.Count > 12) s.RecentEmoji.RemoveRange(12, s.RecentEmoji.Count - 12);
        });
    }

    private void RefreshReactionSummary(string messageId, string key)
    {
        var all = _app.Db.GetReactions(messageId);
        var summary = ReactionParser.BuildSummary(all.Select(r => (r.Emoji, r.ReceivedAt)));
        _app.Db.SetReactionSummary(messageId, summary.Length == 0 ? null : summary);
        Updated(messageId, key);
    }

    private bool PersistReaction(ReactionRecord r, string key)
    {
        if (!_app.Db.SaveReactionIfNew(r)) return false;
        RefreshReactionSummary(r.MessageId, key);
        return true;
    }

    private bool HandleDmReaction(string text, ContactRecord contact, string radioId)
    {
        var key = DmKey(contact.Id);
        var now = Bytes.NowEpoch();
        if (MeshCoreOpenReactionParser.Parse(text) is { } mco)
        {
            foreach (var c in DmCandidates(contact.Id, now))
                if (MeshCoreOpenReactionParser.ComputeReactionHash(c.WireTimestamp, null, c.Text) == mco.DartHash)
                {
                    PersistReaction(new ReactionRecord { MessageId = c.Id, RadioId = radioId, Emoji = mco.Emoji, SenderName = contact.DisplayName, MessageHash = mco.DartHash, RawText = text, ContactId = contact.Id }, key);
                    break;
                }
            return true;
        }
        if (MeshCoreOpenReactionParser.ParseV1(text) is { } v1)
        {
            foreach (var c in DmCandidates(contact.Id, v1.TimestampSeconds))
                if (MeshCoreOpenReactionParser.DartStringHash(c.Text) == v1.TextHash)
                {
                    PersistReaction(new ReactionRecord { MessageId = c.Id, RadioId = radioId, Emoji = v1.Emoji, SenderName = contact.DisplayName, MessageHash = $"{v1.TimestampSeconds}", RawText = text, ContactId = contact.Id }, key);
                    break;
                }
            return true;
        }
        if (ReactionParser.ParseDm(text) is not { } parsed) return false;
        var target = _app.Db.GetDirectMessages(contact.Id, 500).LastOrDefault(m =>
            !ReactionParser.IsReactionText(m.Text, true) && ReactionParser.GenerateMessageHash(m.Text, m.WireTimestamp) == parsed.MessageHash);
        if (target is not null)
            PersistReaction(new ReactionRecord { MessageId = target.Id, RadioId = radioId, Emoji = parsed.Emoji, SenderName = contact.DisplayName, MessageHash = parsed.MessageHash, RawText = text, ContactId = contact.Id }, key);
        else
            QueuePending(key, null, parsed, contact.DisplayName, text, radioId, null, contact.Id);
        return true;
    }

    private IEnumerable<MessageRecord> DmCandidates(string contactId, uint anchor) =>
        _app.Db.GetDirectMessages(contactId, 200).Where(m => Math.Abs((long)m.WireTimestamp - anchor) <= ReactionWindowSeconds && !ReactionParser.IsReactionText(m.Text, true)).Reverse();

    private IEnumerable<MessageRecord> ChannelCandidates(string radioId, int idx, uint anchor) =>
        _app.Db.GetChannelMessages(radioId, idx, 200).Where(m => Math.Abs((long)m.WireTimestamp - anchor) <= ReactionWindowSeconds && !ReactionParser.IsReactionText(m.Text, false)).Reverse();

    private bool HandleChannelReaction(string text, byte channelIndex, string? sender, string radioId)
    {
        var key = ChannelKey(channelIndex);
        var senderName = sender ?? "Unknown";
        var selfName = _app.SelfName;
        string? CandidateSender(MessageRecord m) => m.IsOutgoing ? (selfName.Length == 0 ? null : selfName) : m.SenderName;
        if (MeshCoreOpenReactionParser.Parse(text) is { } mco)
        {
            foreach (var c in ChannelCandidates(radioId, channelIndex, Bytes.NowEpoch()))
                if (MeshCoreOpenReactionParser.ComputeReactionHash(c.WireTimestamp, CandidateSender(c), c.Text) == mco.DartHash)
                {
                    PersistReaction(new ReactionRecord { MessageId = c.Id, RadioId = radioId, Emoji = mco.Emoji, SenderName = senderName, MessageHash = mco.DartHash, RawText = text, ChannelIndex = channelIndex }, key);
                    break;
                }
            return true;
        }
        if (MeshCoreOpenReactionParser.ParseV1(text) is { } v1)
        {
            foreach (var c in ChannelCandidates(radioId, channelIndex, v1.TimestampSeconds))
            {
                if (CandidateSender(c) is { } n && MeshCoreOpenReactionParser.DartStringHash(n) != v1.SenderNameHash) continue;
                if (MeshCoreOpenReactionParser.DartStringHash(c.Text) != v1.TextHash) continue;
                PersistReaction(new ReactionRecord { MessageId = c.Id, RadioId = radioId, Emoji = v1.Emoji, SenderName = senderName, MessageHash = $"{v1.TimestampSeconds}", RawText = text, ChannelIndex = channelIndex }, key);
                break;
            }
            return true;
        }
        if (ReactionParser.Parse(text) is not { } parsed) return false;
        var target = _app.Db.GetChannelMessages(radioId, channelIndex, 500).LastOrDefault(m =>
            string.Equals(CandidateSender(m), parsed.TargetSender, StringComparison.Ordinal) &&
            ReactionParser.GenerateMessageHash(m.Text, m.WireTimestamp) == parsed.MessageHash);
        if (target is not null)
            PersistReaction(new ReactionRecord { MessageId = target.Id, RadioId = radioId, Emoji = parsed.Emoji, SenderName = senderName, MessageHash = parsed.MessageHash, RawText = text, ChannelIndex = channelIndex }, key);
        else
            QueuePending(key, parsed, null, senderName, text, radioId, channelIndex, null);
        return true;
    }

    private void QueuePending(string key, ParsedReaction? ch, ParsedDmReaction? dm, string sender, string raw, string radioId, int? channelIndex, string? contactId)
    {
        lock (_pendingReactions)
        {
            _pendingReactions.Add((key, ch, dm, sender, raw, radioId, channelIndex, contactId));
            if (_pendingReactions.Count > 100) _pendingReactions.RemoveAt(0);
        }
    }

    private void ApplyPendingReactions(string key, MessageRecord msg, string? senderName)
    {
        List<(string Key, ParsedReaction? Channel, ParsedDmReaction? Dm, string Sender, string Raw, string RadioId, int? ChannelIndex, string? ContactId)> matches;
        var hash = ReactionParser.GenerateMessageHash(msg.Text, msg.WireTimestamp);
        lock (_pendingReactions)
        {
            matches = _pendingReactions.Where(p => p.Key == key &&
                ((p.Dm is not null && p.Dm.MessageHash == hash) ||
                 (p.Channel is not null && p.Channel.MessageHash == hash && p.Channel.TargetSender == senderName))).ToList();
            foreach (var m in matches) _pendingReactions.Remove(m);
        }
        foreach (var p in matches)
            PersistReaction(new ReactionRecord
            {
                MessageId = msg.Id, RadioId = p.RadioId, Emoji = p.Channel?.Emoji ?? p.Dm!.Emoji, SenderName = p.Sender,
                MessageHash = hash, RawText = p.Raw, ContactId = p.ContactId, ChannelIndex = p.ChannelIndex,
            }, key);
    }
}
