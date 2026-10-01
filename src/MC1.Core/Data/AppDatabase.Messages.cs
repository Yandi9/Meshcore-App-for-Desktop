using Dapper;
using MC1.Core.Models;

namespace MC1.Core.Data;

public sealed partial class AppDatabase
{
    private const string MessageColumns = """
        Id,RadioId,ContactId,ChannelIndex,Text,Timestamp,CreatedAt,SortDate,Direction,Status,TextType,AckCode,PathLength,Snr,PathNodes,
        SenderKeyPrefix,SenderName,IsRead,ReplyToId,RoundTripTime,HeardRepeats,SendCount,RetryAttempt,MaxRetryAttempts,DedupKey,
        ContainsSelfMention,MentionSeen,ReactionSummary,RouteType,RegionScope,SenderTimestamp,TimestampCorrected,LinkPreviewUrl,
        LinkPreviewTitle,LinkPreviewImage,LinkPreviewFetched
        """;

    private static readonly string MessageUpsertSql = BuildMessageUpsert();

    private static string BuildMessageUpsert()
    {
        var cols = MessageColumns.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var values = string.Join(",", cols.Select(s => "@" + s));
        var sets = string.Join(",", cols.Where(s => s != "Id").Select(s => $"{s}=excluded.{s}"));
        return $"INSERT INTO Messages({string.Join(",", cols)}) VALUES({values}) ON CONFLICT(Id) DO UPDATE SET {sets}";
    }

    public void SaveMessage(MessageRecord m)
    {
        using var c = Open();
        c.Execute(MessageUpsertSql, m);
    }

    public MessageRecord? GetMessage(string id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<MessageRecord>("SELECT * FROM Messages WHERE Id=@id", new { id });
    }

    /// <summary>Moves a channel's messages to another slot number (the channel ended up in a different slot).</summary>
    public void MoveChannelMessages(string radioId, int fromIdx, int toIdx)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET ChannelIndex=@toIdx WHERE RadioId=@radioId AND ChannelIndex=@fromIdx", new { radioId, fromIdx, toIdx });
    }

    public MessageRecord? GetMessageByDedup(string radioId, string dedupKey)
    {
        using var c = Open();
        return c.Query<MessageRecord>("SELECT * FROM Messages WHERE RadioId=@radioId AND DedupKey=@dedupKey LIMIT 1", new { radioId, dedupKey }).FirstOrDefault();
    }

    public IReadOnlyList<MessageRecord> GetDirectMessages(string contactId, int limit = 500, long? beforeSortDate = null)
    {
        using var c = Open();
        var rows = c.Query<MessageRecord>("""
            SELECT * FROM Messages WHERE ContactId=@contactId AND (@before IS NULL OR SortDate < @before)
            ORDER BY SortDate DESC LIMIT @limit
            """, new { contactId, limit, before = beforeSortDate }).ToList();
        rows.Reverse();
        return rows;
    }

    public IReadOnlyList<MessageRecord> GetChannelMessages(string radioId, int channelIndex, int limit = 500, long? beforeSortDate = null)
    {
        using var c = Open();
        var rows = c.Query<MessageRecord>("""
            SELECT * FROM Messages WHERE RadioId=@radioId AND ChannelIndex=@channelIndex AND (@before IS NULL OR SortDate < @before)
            ORDER BY SortDate DESC LIMIT @limit
            """, new { radioId, channelIndex, limit, before = beforeSortDate }).ToList();
        rows.Reverse();
        return rows;
    }

    public MessageRecord? GetLastDirectMessage(string contactId)
    {
        using var c = Open();
        return c.Query<MessageRecord>("SELECT * FROM Messages WHERE ContactId=@contactId ORDER BY SortDate DESC LIMIT 1", new { contactId }).FirstOrDefault();
    }

    public MessageRecord? GetLastChannelMessage(string radioId, int channelIndex)
    {
        using var c = Open();
        return c.Query<MessageRecord>("SELECT * FROM Messages WHERE RadioId=@radioId AND ChannelIndex=@channelIndex ORDER BY SortDate DESC LIMIT 1",
            new { radioId, channelIndex }).FirstOrDefault();
    }

    /// <summary>Latest message per DM conversation and per channel (for the chat list).</summary>
    public IReadOnlyDictionary<string, MessageRecord> GetLastMessages(string radioId)
    {
        using var c = Open();
        var rows = c.Query<MessageRecord>("""
            SELECT m.* FROM Messages m
            JOIN (SELECT COALESCE(ContactId, 'ch:' || ChannelIndex) AS K, MAX(SortDate) AS S FROM Messages WHERE RadioId=@radioId GROUP BY K) x
              ON COALESCE(m.ContactId, 'ch:' || m.ChannelIndex) = x.K AND m.SortDate = x.S
            WHERE m.RadioId=@radioId
            """, new { radioId });
        var map = new Dictionary<string, MessageRecord>();
        foreach (var r in rows) map[r.ContactId ?? "ch:" + r.ChannelIndex] = r;
        return map;
    }

    public void UpdateMessageStatus(string id, MessageStatus status)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET Status=@s WHERE Id=@id", new { id, s = (int)status });
    }

    /// <returns>true if the row changed (was not already delivered).</returns>
    public bool UpdateMessageStatusUnlessDelivered(string id, MessageStatus status)
    {
        using var c = Open();
        return c.Execute("UPDATE Messages SET Status=@s WHERE Id=@id AND Status<>@d", new { id, s = (int)status, d = (int)MessageStatus.Delivered }) > 0;
    }

    public void UpdateMessageAck(string id, long? ackCode, MessageStatus status, long? roundTrip = null)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET AckCode=@ackCode, Status=@s, RoundTripTime=COALESCE(@roundTrip, RoundTripTime) WHERE Id=@id",
            new { id, ackCode, s = (int)status, roundTrip });
    }

    public void UpdateMessageRetry(string id, MessageStatus status, int attempt, int max)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET Status=@s, RetryAttempt=@attempt, MaxRetryAttempts=@max WHERE Id=@id", new { id, s = (int)status, attempt, max });
    }

    public void UpdateMessageTimestamp(string id, long timestamp)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET Timestamp=@timestamp WHERE Id=@id", new { id, timestamp });
    }

    public void IncrementSendCount(string id)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET SendCount=SendCount+1 WHERE Id=@id", new { id });
    }

    public int IncrementHeardRepeats(string id)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET HeardRepeats=HeardRepeats+1 WHERE Id=@id", new { id });
        return c.ExecuteScalar<int>("SELECT HeardRepeats FROM Messages WHERE Id=@id", new { id });
    }

    public void ResetHeardRepeats(string id)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET HeardRepeats=0 WHERE Id=@id; DELETE FROM MessageRepeats WHERE MessageId=@id", new { id });
    }

    public void AdoptIncomingPath(string id, byte[] pathNodes, int pathLength)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET PathNodes=@pathNodes, PathLength=@pathLength WHERE Id=@id AND PathNodes IS NULL", new { id, pathNodes, pathLength });
    }

    public void SetReactionSummary(string id, string? summary)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET ReactionSummary=@summary WHERE Id=@id", new { id, summary });
    }

    public void SetLinkPreview(string id, string? url, string? title, byte[]? image)
    {
        using var c = Open();
        c.Execute("UPDATE Messages SET LinkPreviewUrl=@url, LinkPreviewTitle=@title, LinkPreviewImage=@image, LinkPreviewFetched=1 WHERE Id=@id",
            new { id, url, title, image });
    }

    public void DeleteMessage(string id)
    {
        using var c = Open();
        c.Execute("DELETE FROM Messages WHERE Id=@id", new { id });
    }

    public void ClearDirectMessages(string contactId)
    {
        using var c = Open();
        c.Execute("DELETE FROM Messages WHERE ContactId=@contactId", new { contactId });
    }

    public void ClearChannelMessages(string radioId, int channelIndex)
    {
        using var c = Open();
        c.Execute("DELETE FROM Messages WHERE RadioId=@radioId AND ChannelIndex=@channelIndex", new { radioId, channelIndex });
    }

    /// <summary>Finds our own sent channel message matching an over-the-air echo (heard repeat).</summary>
    public MessageRecord? FindSentChannelMessage(string radioId, int channelIndex, long timestamp, string text)
    {
        using var c = Open();
        return c.Query<MessageRecord>("""
            SELECT * FROM Messages WHERE RadioId=@radioId AND ChannelIndex=@channelIndex AND Direction=1 AND Timestamp=@timestamp AND Text=@text
            ORDER BY SortDate DESC LIMIT 1
            """, new { radioId, channelIndex, timestamp, text }).FirstOrDefault();
    }

    public IReadOnlyList<MessageRecord> SearchMessages(string radioId, string query, int limit = 200)
    {
        using var c = Open();
        return c.Query<MessageRecord>("""
            SELECT * FROM Messages WHERE RadioId=@radioId AND Text LIKE @q ESCAPE '\' ORDER BY SortDate DESC LIMIT @limit
            """, new { radioId, q = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%", limit }).ToList();
    }

    public IReadOnlyList<MessageRecord> GetPendingOutgoing(string radioId)
    {
        using var c = Open();
        return c.Query<MessageRecord>("SELECT * FROM Messages WHERE RadioId=@radioId AND Direction=1 AND Status IN (0,1,5)", new { radioId }).ToList();
    }

    public int FailStuckOutgoing(string radioId)
    {
        using var c = Open();
        return c.Execute("UPDATE Messages SET Status=4 WHERE RadioId=@radioId AND Direction=1 AND Status IN (0,1,5)", new { radioId });
    }

    // MARK: Repeats

    public void SaveRepeat(MessageRepeatRecord r)
    {
        using var c = Open();
        c.Execute("INSERT INTO MessageRepeats(Id,MessageId,ReceivedAt,PathNodes,PathLength,Snr,Rssi,RxLogEntryId) VALUES(@Id,@MessageId,@ReceivedAt,@PathNodes,@PathLength,@Snr,@Rssi,@RxLogEntryId)", r);
    }

    public IReadOnlyList<MessageRepeatRecord> GetRepeats(string messageId)
    {
        using var c = Open();
        return c.Query<MessageRepeatRecord>("SELECT * FROM MessageRepeats WHERE MessageId=@messageId ORDER BY ReceivedAt", new { messageId }).ToList();
    }

    public bool RepeatExistsForRxEntry(string rxLogEntryId)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM MessageRepeats WHERE RxLogEntryId=@rxLogEntryId", new { rxLogEntryId }) > 0;
    }

    // MARK: Reactions

    public bool SaveReactionIfNew(ReactionRecord r)
    {
        using var c = Open();
        var exists = c.ExecuteScalar<long>("SELECT COUNT(*) FROM Reactions WHERE MessageId=@MessageId AND SenderName=@SenderName AND Emoji=@Emoji", r) > 0;
        if (exists) return false;
        c.Execute("""
            INSERT INTO Reactions(Id,MessageId,RadioId,Emoji,SenderName,MessageHash,RawText,ContactId,ChannelIndex,ReceivedAt,IsOutgoing)
            VALUES(@Id,@MessageId,@RadioId,@Emoji,@SenderName,@MessageHash,@RawText,@ContactId,@ChannelIndex,@ReceivedAt,@IsOutgoing)
            """, r);
        return true;
    }

    public IReadOnlyList<ReactionRecord> GetReactions(string messageId)
    {
        using var c = Open();
        return c.Query<ReactionRecord>("SELECT * FROM Reactions WHERE MessageId=@messageId ORDER BY ReceivedAt", new { messageId }).ToList();
    }

    public void DeleteReaction(string messageId, string senderName, string emoji)
    {
        using var c = Open();
        c.Execute("DELETE FROM Reactions WHERE MessageId=@messageId AND SenderName=@senderName AND Emoji=@emoji", new { messageId, senderName, emoji });
    }

    // MARK: RX log

    public void SaveRxLog(RxLogRecord r)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO RxLog(Id,RadioId,ReceivedAt,Snr,Rssi,RouteType,PayloadType,PayloadVersion,PathLength,PathNodes,PacketPayload,RawPayload,
                PacketHash,ChannelIndex,ChannelName,DecryptStatus,FromContactName,SenderTimestamp,DecodedText,TransportCode,RegionScope)
            VALUES(@Id,@RadioId,@ReceivedAt,@Snr,@Rssi,@RouteType,@PayloadType,@PayloadVersion,@PathLength,@PathNodes,@PacketPayload,@RawPayload,
                @PacketHash,@ChannelIndex,@ChannelName,@DecryptStatus,@FromContactName,@SenderTimestamp,@DecodedText,@TransportCode,@RegionScope)
            """, r);
    }

    public IReadOnlyList<RxLogRecord> GetRxLog(string radioId, int limit = 1000)
    {
        using var c = Open();
        var rows = c.Query<RxLogRecord>("SELECT * FROM RxLog WHERE RadioId=@radioId ORDER BY ReceivedAt DESC LIMIT @limit", new { radioId, limit }).ToList();
        return rows;
    }

    public IReadOnlyList<RxLogRecord> GetRxLogForChannel(string radioId, int channelIndex, long senderTimestamp)
    {
        using var c = Open();
        return c.Query<RxLogRecord>("SELECT * FROM RxLog WHERE RadioId=@radioId AND ChannelIndex=@channelIndex AND SenderTimestamp=@senderTimestamp ORDER BY ReceivedAt",
            new { radioId, channelIndex, senderTimestamp }).ToList();
    }

    public IReadOnlyList<RxLogRecord> GetRecentRxLogByStatus(string radioId, DecryptStatus status, long sinceMs)
    {
        using var c = Open();
        return c.Query<RxLogRecord>("SELECT * FROM RxLog WHERE RadioId=@radioId AND DecryptStatus=@s AND ReceivedAt>=@sinceMs",
            new { radioId, s = (int)status, sinceMs }).ToList();
    }

    public void UpdateRxLogDecryption(string id, int? channelIndex, string? channelName, long? senderTimestamp, string? decodedText, DecryptStatus status)
    {
        using var c = Open();
        c.Execute("UPDATE RxLog SET ChannelIndex=@channelIndex, ChannelName=@channelName, SenderTimestamp=@senderTimestamp, DecodedText=@decodedText, DecryptStatus=@s WHERE Id=@id",
            new { id, channelIndex, channelName, senderTimestamp, decodedText, s = (int)status });
    }

    public void ClearRxLog(string radioId)
    {
        using var c = Open();
        c.Execute("DELETE FROM RxLog WHERE RadioId=@radioId", new { radioId });
    }

    public void TrimRxLog(string radioId, int keep)
    {
        using var c = Open();
        c.Execute("DELETE FROM RxLog WHERE RadioId=@radioId AND Id NOT IN (SELECT Id FROM RxLog WHERE RadioId=@radioId ORDER BY ReceivedAt DESC LIMIT @keep)",
            new { radioId, keep });
    }

    // MARK: Remote sessions & rooms

    public IReadOnlyList<RemoteSessionRecord> GetRemoteSessions(string radioId)
    {
        using var c = Open();
        return c.Query<RemoteSessionRecord>("SELECT * FROM RemoteSessions WHERE RadioId=@radioId ORDER BY LastActivity DESC", new { radioId }).ToList();
    }

    public RemoteSessionRecord? GetRemoteSession(string id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<RemoteSessionRecord>("SELECT * FROM RemoteSessions WHERE Id=@id", new { id });
    }

    public RemoteSessionRecord? GetRemoteSessionByKey(string radioId, byte[] publicKey)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<RemoteSessionRecord>("SELECT * FROM RemoteSessions WHERE RadioId=@radioId AND PublicKey=@publicKey", new { radioId, publicKey });
    }

    public void UpsertRemoteSession(RemoteSessionRecord s)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO RemoteSessions(Id,RadioId,PublicKey,Name,IsRoom,IsConnected,Permission,LastSyncTimestamp,UnreadCount,LastActivity,NotificationLevel,LastLogin)
            VALUES(@Id,@RadioId,@PublicKey,@Name,@IsRoom,@IsConnected,@Permission,@LastSyncTimestamp,@UnreadCount,@LastActivity,@NotificationLevel,@LastLogin)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, IsRoom=excluded.IsRoom, IsConnected=excluded.IsConnected, Permission=excluded.Permission,
                LastSyncTimestamp=excluded.LastSyncTimestamp, UnreadCount=excluded.UnreadCount, LastActivity=excluded.LastActivity,
                NotificationLevel=excluded.NotificationLevel, LastLogin=excluded.LastLogin
            """, s);
    }

    public void SetRemoteSessionConnected(string id, bool connected, RoomPermission? permission = null)
    {
        using var c = Open();
        c.Execute("UPDATE RemoteSessions SET IsConnected=@connected, Permission=COALESCE(@p, Permission), LastLogin=CASE WHEN @connected THEN @now ELSE LastLogin END WHERE Id=@id",
            new { id, connected, p = (int?)permission, now = Time.Now() });
    }

    public void MarkAllSessionsDisconnected(string radioId)
    {
        using var c = Open();
        c.Execute("UPDATE RemoteSessions SET IsConnected=0 WHERE RadioId=@radioId", new { radioId });
    }

    public void DeleteRemoteSession(string id)
    {
        using var c = Open();
        c.Execute("DELETE FROM RemoteSessions WHERE Id=@id", new { id });
    }

    public void SaveRoomMessage(RoomMessageRecord m)
    {
        using var c = Open();
        c.Execute("""
            INSERT OR REPLACE INTO RoomMessages(Id,SessionId,AuthorKeyPrefix,AuthorName,Text,Timestamp,CreatedAt,IsFromSelf,Status,DedupKey,AckCode,RetryAttempt)
            VALUES(@Id,@SessionId,@AuthorKeyPrefix,@AuthorName,@Text,@Timestamp,@CreatedAt,@IsFromSelf,@Status,@DedupKey,@AckCode,@RetryAttempt)
            """, m);
    }

    public bool RoomMessageExists(string sessionId, string dedupKey)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM RoomMessages WHERE SessionId=@sessionId AND DedupKey=@dedupKey", new { sessionId, dedupKey }) > 0;
    }

    public IReadOnlyList<RoomMessageRecord> GetRoomMessages(string sessionId, int limit = 500)
    {
        using var c = Open();
        var rows = c.Query<RoomMessageRecord>("SELECT * FROM RoomMessages WHERE SessionId=@sessionId ORDER BY Timestamp DESC, CreatedAt DESC LIMIT @limit", new { sessionId, limit }).ToList();
        rows.Reverse();
        return rows;
    }

    public RoomMessageRecord? GetRoomMessage(string id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<RoomMessageRecord>("SELECT * FROM RoomMessages WHERE Id=@id", new { id });
    }

    public void UpdateRoomMessageStatus(string id, MessageStatus status, long? ackCode = null)
    {
        using var c = Open();
        c.Execute("UPDATE RoomMessages SET Status=@s, AckCode=COALESCE(@ackCode, AckCode) WHERE Id=@id", new { id, s = (int)status, ackCode });
    }

    public void UpdateRoomActivity(string sessionId, long? syncTimestamp = null, bool incrementUnread = false)
    {
        using var c = Open();
        c.Execute("""
            UPDATE RemoteSessions SET LastActivity=@now, LastSyncTimestamp=MAX(LastSyncTimestamp, COALESCE(@syncTimestamp, 0)),
                UnreadCount=UnreadCount + @inc WHERE Id=@sessionId
            """, new { sessionId, now = Time.Now(), syncTimestamp, inc = incrementUnread ? 1 : 0 });
    }

    public void ClearRoomUnread(string sessionId)
    {
        using var c = Open();
        c.Execute("UPDATE RemoteSessions SET UnreadCount=0 WHERE Id=@sessionId", new { sessionId });
    }

    public void ClearRoomMessages(string sessionId)
    {
        using var c = Open();
        c.Execute("DELETE FROM RoomMessages WHERE SessionId=@sessionId", new { sessionId });
    }

    // MARK: Node snapshots

    public void SaveSnapshot(NodeSnapshotRecord s)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO NodeSnapshots(Id,RadioId,PublicKey,CapturedAt,BatteryMv,NoiseFloor,LastRssi,LastSnr,Uptime,PacketsReceived,PacketsSent,
                Airtime,RxAirtime,TxQueue,TelemetryJson,NeighboursJson)
            VALUES(@Id,@RadioId,@PublicKey,@CapturedAt,@BatteryMv,@NoiseFloor,@LastRssi,@LastSnr,@Uptime,@PacketsReceived,@PacketsSent,
                @Airtime,@RxAirtime,@TxQueue,@TelemetryJson,@NeighboursJson)
            """, s);
    }

    public IReadOnlyList<NodeSnapshotRecord> GetSnapshots(string radioId, byte[] publicKey, long sinceMs = 0)
    {
        using var c = Open();
        return c.Query<NodeSnapshotRecord>("SELECT * FROM NodeSnapshots WHERE RadioId=@radioId AND PublicKey=@publicKey AND CapturedAt>=@sinceMs ORDER BY CapturedAt",
            new { radioId, publicKey, sinceMs }).ToList();
    }

    public void DeleteSnapshots(string radioId, byte[] publicKey)
    {
        using var c = Open();
        c.Execute("DELETE FROM NodeSnapshots WHERE RadioId=@radioId AND PublicKey=@publicKey", new { radioId, publicKey });
    }

    // MARK: Trace paths

    public IReadOnlyList<TracePathRecord> GetTracePaths(string radioId)
    {
        using var c = Open();
        return c.Query<TracePathRecord>("SELECT * FROM TracePaths WHERE RadioId=@radioId ORDER BY CreatedAt DESC", new { radioId }).ToList();
    }

    public void SaveTracePath(TracePathRecord t)
    {
        using var c = Open();
        c.Execute("""
            INSERT OR REPLACE INTO TracePaths(Id,RadioId,Name,Path,HashSize,CreatedAt,LastRunAt,LastResultJson)
            VALUES(@Id,@RadioId,@Name,@Path,@HashSize,@CreatedAt,@LastRunAt,@LastResultJson)
            """, t);
    }

    public void DeleteTracePath(string id)
    {
        using var c = Open();
        c.Execute("DELETE FROM TracePaths WHERE Id=@id", new { id });
    }
}
