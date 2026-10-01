using Dapper;
using MC1.Core.Models;
using Microsoft.Data.Sqlite;

namespace MC1.Core.Data;

/// <summary>SQLite persistence (replaces SwiftData on Windows).</summary>
public sealed partial class AppDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public AppDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    public string Path { get; }

    internal SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        if (version < 1)
        {
            c.Execute(Schema);
            c.Execute($"PRAGMA user_version={SchemaVersion};");
        }
    }

    public void Checkpoint()
    {
        using var c = Open();
        c.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private const string Schema = """
    CREATE TABLE IF NOT EXISTS Radios(
        Id TEXT PRIMARY KEY, Name TEXT NOT NULL DEFAULT '', PublicKey BLOB, Model TEXT DEFAULT '', FirmwareVersion TEXT DEFAULT '',
        FirmwareBuild TEXT DEFAULT '', FirmwareCode INTEGER DEFAULT 0, MaxContacts INTEGER DEFAULT 0, MaxChannels INTEGER DEFAULT 0,
        LastContactSync INTEGER DEFAULT 0, LastConnected INTEGER DEFAULT 0, OcvPreset TEXT, CustomOcv TEXT, KnownRegions TEXT,
        LastConnection TEXT, LastChannelSync INTEGER DEFAULT 0);

    CREATE TABLE IF NOT EXISTS Contacts(
        Id TEXT PRIMARY KEY, RadioId TEXT NOT NULL, PublicKey BLOB NOT NULL, Name TEXT NOT NULL DEFAULT '', Nickname TEXT,
        Type INTEGER DEFAULT 1, TypeRaw INTEGER DEFAULT 1, Flags INTEGER DEFAULT 0, OutPathLength INTEGER DEFAULT 255, OutPath BLOB,
        LastAdvert INTEGER DEFAULT 0, Latitude REAL DEFAULT 0, Longitude REAL DEFAULT 0, LastModified INTEGER DEFAULT 0,
        IsBlocked INTEGER DEFAULT 0, NotificationLevel INTEGER DEFAULT 0, LastMessageAt INTEGER DEFAULT 0, UnreadCount INTEGER DEFAULT 0,
        UnreadMentions INTEGER DEFAULT 0, LastHeard INTEGER DEFAULT 0, InboundHops INTEGER, DraftText TEXT,
        UNIQUE(RadioId, PublicKey));
    CREATE INDEX IF NOT EXISTS IX_Contacts_Radio ON Contacts(RadioId);

    CREATE TABLE IF NOT EXISTS DiscoveredNodes(
        Id TEXT PRIMARY KEY, RadioId TEXT NOT NULL, PublicKey BLOB NOT NULL, Name TEXT DEFAULT '', Type INTEGER DEFAULT 1,
        Latitude REAL DEFAULT 0, Longitude REAL DEFAULT 0, LastAdvert INTEGER DEFAULT 0, FirstSeen INTEGER DEFAULT 0,
        LastHeard INTEGER DEFAULT 0, OutPathLength INTEGER DEFAULT 255, OutPath BLOB, UNIQUE(RadioId, PublicKey));

    CREATE TABLE IF NOT EXISTS Channels(
        Id TEXT PRIMARY KEY, RadioId TEXT NOT NULL, Idx INTEGER NOT NULL, Name TEXT DEFAULT '', Secret BLOB,
        LastMessageAt INTEGER DEFAULT 0, UnreadCount INTEGER DEFAULT 0, UnreadMentions INTEGER DEFAULT 0,
        NotificationLevel INTEGER DEFAULT 0, FloodScope TEXT, DraftText TEXT, UNIQUE(RadioId, Idx));

    CREATE TABLE IF NOT EXISTS Messages(
        Id TEXT PRIMARY KEY, RadioId TEXT NOT NULL, ContactId TEXT, ChannelIndex INTEGER, Text TEXT NOT NULL DEFAULT '',
        Timestamp INTEGER DEFAULT 0, CreatedAt INTEGER DEFAULT 0, SortDate INTEGER DEFAULT 0, Direction INTEGER DEFAULT 0,
        Status INTEGER DEFAULT 0, TextType INTEGER DEFAULT 0, AckCode INTEGER, PathLength INTEGER DEFAULT 0, Snr REAL,
        PathNodes BLOB, SenderKeyPrefix BLOB, SenderName TEXT, IsRead INTEGER DEFAULT 0, ReplyToId TEXT, RoundTripTime INTEGER,
        HeardRepeats INTEGER DEFAULT 0, SendCount INTEGER DEFAULT 1, RetryAttempt INTEGER DEFAULT 0, MaxRetryAttempts INTEGER DEFAULT 0,
        DedupKey TEXT, ContainsSelfMention INTEGER DEFAULT 0, MentionSeen INTEGER DEFAULT 0, ReactionSummary TEXT,
        RouteType INTEGER DEFAULT -1, RegionScope TEXT, SenderTimestamp INTEGER, TimestampCorrected INTEGER DEFAULT 0,
        LinkPreviewUrl TEXT, LinkPreviewTitle TEXT, LinkPreviewImage BLOB, LinkPreviewFetched INTEGER DEFAULT 0);
    CREATE INDEX IF NOT EXISTS IX_Messages_Contact ON Messages(ContactId, SortDate);
    CREATE INDEX IF NOT EXISTS IX_Messages_Channel ON Messages(RadioId, ChannelIndex, SortDate);
    CREATE INDEX IF NOT EXISTS IX_Messages_Dedup ON Messages(RadioId, DedupKey);
    CREATE INDEX IF NOT EXISTS IX_Messages_Ack ON Messages(AckCode);

    CREATE TABLE IF NOT EXISTS MessageRepeats(
        Id TEXT PRIMARY KEY, MessageId TEXT NOT NULL REFERENCES Messages(Id) ON DELETE CASCADE, ReceivedAt INTEGER,
        PathNodes BLOB, PathLength INTEGER, Snr REAL, Rssi INTEGER, RxLogEntryId TEXT);
    CREATE INDEX IF NOT EXISTS IX_Repeats_Message ON MessageRepeats(MessageId);

    CREATE TABLE IF NOT EXISTS Reactions(
        Id TEXT PRIMARY KEY, MessageId TEXT NOT NULL REFERENCES Messages(Id) ON DELETE CASCADE, RadioId TEXT, Emoji TEXT,
        SenderName TEXT, MessageHash TEXT, RawText TEXT, ContactId TEXT, ChannelIndex INTEGER, ReceivedAt INTEGER, IsOutgoing INTEGER DEFAULT 0);
    CREATE INDEX IF NOT EXISTS IX_Reactions_Message ON Reactions(MessageId);

    CREATE TABLE IF NOT EXISTS RxLog(
        Id TEXT PRIMARY KEY, RadioId TEXT NOT NULL, ReceivedAt INTEGER, Snr REAL, Rssi INTEGER, RouteType INTEGER, PayloadType INTEGER,
        PayloadVersion INTEGER, PathLength INTEGER, PathNodes BLOB, PacketPayload BLOB, RawPayload BLOB, PacketHash TEXT,
        ChannelIndex INTEGER, ChannelName TEXT, DecryptStatus INTEGER, FromContactName TEXT, SenderTimestamp INTEGER,
        DecodedText TEXT, TransportCode BLOB, RegionScope TEXT);
    CREATE INDEX IF NOT EXISTS IX_RxLog_Radio ON RxLog(RadioId, ReceivedAt);
    CREATE INDEX IF NOT EXISTS IX_RxLog_Channel ON RxLog(RadioId, ChannelIndex, SenderTimestamp);

    CREATE TABLE IF NOT EXISTS RemoteSessions(
        Id TEXT PRIMARY KEY, RadioId TEXT NOT NULL, PublicKey BLOB NOT NULL, Name TEXT DEFAULT '', IsRoom INTEGER DEFAULT 0,
        IsConnected INTEGER DEFAULT 0, Permission INTEGER DEFAULT 0, LastSyncTimestamp INTEGER DEFAULT 0, UnreadCount INTEGER DEFAULT 0,
        LastActivity INTEGER DEFAULT 0, NotificationLevel INTEGER DEFAULT 0, LastLogin INTEGER DEFAULT 0, UNIQUE(RadioId, PublicKey));

    CREATE TABLE IF NOT EXISTS RoomMessages(
        Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES RemoteSessions(Id) ON DELETE CASCADE, AuthorKeyPrefix BLOB,
        AuthorName TEXT, Text TEXT, Timestamp INTEGER, CreatedAt INTEGER, IsFromSelf INTEGER DEFAULT 0, Status INTEGER DEFAULT 3,
        DedupKey TEXT, AckCode INTEGER, RetryAttempt INTEGER DEFAULT 0);
    CREATE INDEX IF NOT EXISTS IX_RoomMessages_Session ON RoomMessages(SessionId, Timestamp);

    CREATE TABLE IF NOT EXISTS NodeSnapshots(
        Id TEXT PRIMARY KEY, RadioId TEXT, PublicKey BLOB, CapturedAt INTEGER, BatteryMv INTEGER, NoiseFloor INTEGER, LastRssi INTEGER,
        LastSnr REAL, Uptime INTEGER, PacketsReceived INTEGER, PacketsSent INTEGER, Airtime INTEGER, RxAirtime INTEGER, TxQueue INTEGER,
        TelemetryJson TEXT, NeighboursJson TEXT);
    CREATE INDEX IF NOT EXISTS IX_Snapshots_Node ON NodeSnapshots(RadioId, PublicKey, CapturedAt);

    CREATE TABLE IF NOT EXISTS TracePaths(
        Id TEXT PRIMARY KEY, RadioId TEXT, Name TEXT, Path BLOB, HashSize INTEGER DEFAULT 1, CreatedAt INTEGER, LastRunAt INTEGER, LastResultJson TEXT);

    CREATE TABLE IF NOT EXISTS BlockedSenders(RadioId TEXT NOT NULL, Name TEXT NOT NULL, BlockedAt INTEGER, PRIMARY KEY(RadioId, Name));
    """;

    // MARK: Radios

    public RadioRecord? GetRadio(string id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<RadioRecord>("SELECT * FROM Radios WHERE Id=@id", new { id });
    }

    public IReadOnlyList<RadioRecord> GetRadios()
    {
        using var c = Open();
        return c.Query<RadioRecord>("SELECT * FROM Radios ORDER BY LastConnected DESC").ToList();
    }

    public void UpsertRadio(RadioRecord r)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO Radios(Id,Name,PublicKey,Model,FirmwareVersion,FirmwareBuild,FirmwareCode,MaxContacts,MaxChannels,LastContactSync,
                LastConnected,OcvPreset,CustomOcv,KnownRegions,LastConnection,LastChannelSync)
            VALUES(@Id,@Name,@PublicKey,@Model,@FirmwareVersion,@FirmwareBuild,@FirmwareCode,@MaxContacts,@MaxChannels,@LastContactSync,
                @LastConnected,@OcvPreset,@CustomOcv,@KnownRegions,@LastConnection,@LastChannelSync)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, PublicKey=excluded.PublicKey, Model=excluded.Model,
                FirmwareVersion=excluded.FirmwareVersion, FirmwareBuild=excluded.FirmwareBuild, FirmwareCode=excluded.FirmwareCode,
                MaxContacts=excluded.MaxContacts, MaxChannels=excluded.MaxChannels, LastContactSync=excluded.LastContactSync,
                LastConnected=excluded.LastConnected, OcvPreset=excluded.OcvPreset, CustomOcv=excluded.CustomOcv,
                KnownRegions=excluded.KnownRegions, LastConnection=excluded.LastConnection, LastChannelSync=excluded.LastChannelSync
            """, r);
    }

    public void SetContactWatermark(string radioId, long watermark)
    {
        using var c = Open();
        c.Execute("UPDATE Radios SET LastContactSync=@watermark WHERE Id=@radioId", new { radioId, watermark });
    }

    public void DeleteRadioData(string radioId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var table in new[] { "Reactions", "Messages", "Contacts", "DiscoveredNodes", "Channels", "RxLog", "RemoteSessions", "NodeSnapshots", "TracePaths", "BlockedSenders" })
            c.Execute($"DELETE FROM {table} WHERE RadioId=@radioId", new { radioId }, tx);
        c.Execute("DELETE FROM Radios WHERE Id=@radioId", new { radioId }, tx);
        tx.Commit();
    }

    // MARK: Contacts

    public IReadOnlyList<ContactRecord> GetContacts(string radioId)
    {
        using var c = Open();
        return c.Query<ContactRecord>("SELECT * FROM Contacts WHERE RadioId=@radioId ORDER BY Name COLLATE NOCASE", new { radioId }).ToList();
    }

    public ContactRecord? GetContact(string id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<ContactRecord>("SELECT * FROM Contacts WHERE Id=@id", new { id });
    }

    public ContactRecord? GetContactByKey(string radioId, byte[] publicKey)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<ContactRecord>("SELECT * FROM Contacts WHERE RadioId=@radioId AND PublicKey=@publicKey", new { radioId, publicKey });
    }

    public ContactRecord? GetContactByPrefix(string radioId, byte[] prefix)
    {
        if (prefix.Length == 0) return null;
        using var c = Open();
        return c.Query<ContactRecord>("SELECT * FROM Contacts WHERE RadioId=@radioId AND substr(PublicKey,1,@len)=@prefix LIMIT 2",
            new { radioId, prefix, len = prefix.Length }).FirstOrDefault();
    }

    /// <summary>Inserts or updates a contact from a radio frame, preserving local-only fields.</summary>
    public ContactRecord SaveContactFrame(string radioId, MeshCore.MeshContact frame)
    {
        using var c = Open();
        var existing = c.QuerySingleOrDefault<ContactRecord>("SELECT * FROM Contacts WHERE RadioId=@radioId AND PublicKey=@pk",
            new { radioId, pk = frame.PublicKey });
        var rec = existing ?? new ContactRecord { RadioId = radioId };
        rec.ApplyFrom(frame);
        UpsertContact(c, rec);
        return rec;
    }

    public void UpsertContact(ContactRecord r)
    {
        using var c = Open();
        UpsertContact(c, r);
    }

    private static void UpsertContact(SqliteConnection c, ContactRecord r, SqliteTransaction? tx = null) =>
        c.Execute("""
            INSERT INTO Contacts(Id,RadioId,PublicKey,Name,Nickname,Type,TypeRaw,Flags,OutPathLength,OutPath,LastAdvert,Latitude,Longitude,
                LastModified,IsBlocked,NotificationLevel,LastMessageAt,UnreadCount,UnreadMentions,LastHeard,InboundHops,DraftText)
            VALUES(@Id,@RadioId,@PublicKey,@Name,@Nickname,@Type,@TypeRaw,@Flags,@OutPathLength,@OutPath,@LastAdvert,@Latitude,@Longitude,
                @LastModified,@IsBlocked,@NotificationLevel,@LastMessageAt,@UnreadCount,@UnreadMentions,@LastHeard,@InboundHops,@DraftText)
            ON CONFLICT(Id) DO UPDATE SET PublicKey=excluded.PublicKey, Name=excluded.Name, Nickname=excluded.Nickname, Type=excluded.Type,
                TypeRaw=excluded.TypeRaw, Flags=excluded.Flags, OutPathLength=excluded.OutPathLength, OutPath=excluded.OutPath,
                LastAdvert=excluded.LastAdvert, Latitude=excluded.Latitude, Longitude=excluded.Longitude, LastModified=excluded.LastModified,
                IsBlocked=excluded.IsBlocked, NotificationLevel=excluded.NotificationLevel, LastMessageAt=excluded.LastMessageAt,
                UnreadCount=excluded.UnreadCount, UnreadMentions=excluded.UnreadMentions, LastHeard=excluded.LastHeard,
                InboundHops=excluded.InboundHops, DraftText=excluded.DraftText
            """, r, tx);

    /// <summary>Deletes local contacts that are no longer on the radio (full sync pruning). Keeps contacts with message history
    /// unless <paramref name="includeWithHistory"/> is set.</summary>
    public IReadOnlyList<string> PruneContacts(string radioId, IReadOnlyCollection<byte[]> keepKeys)
    {
        using var c = Open();
        var keep = new HashSet<string>(keepKeys.Select(k => Convert.ToHexString(k)));
        var all = c.Query<ContactRecord>("SELECT Id, PublicKey FROM Contacts WHERE RadioId=@radioId", new { radioId });
        var remove = all.Where(x => !keep.Contains(Convert.ToHexString(x.PublicKey))).Select(x => x.Id).ToList();
        foreach (var id in remove) c.Execute("DELETE FROM Contacts WHERE Id=@id", new { id });
        return remove;
    }

    public void DeleteContact(string id, bool deleteMessages)
    {
        using var c = Open();
        if (deleteMessages) c.Execute("DELETE FROM Messages WHERE ContactId=@id", new { id });
        c.Execute("DELETE FROM Contacts WHERE Id=@id", new { id });
    }

    public void DeleteContactByKey(string radioId, byte[] publicKey)
    {
        using var c = Open();
        c.Execute("DELETE FROM Contacts WHERE RadioId=@radioId AND PublicKey=@publicKey", new { radioId, publicKey });
    }

    public bool TouchContactHeard(string radioId, byte[] publicKey, long atMs)
    {
        using var c = Open();
        return c.Execute("UPDATE Contacts SET LastHeard=MAX(LastHeard,@atMs) WHERE RadioId=@radioId AND PublicKey=@publicKey", new { radioId, publicKey, atMs }) > 0;
    }

    public void SetInboundHops(string radioId, byte[] publicKey, int hops)
    {
        using var c = Open();
        c.Execute("UPDATE Contacts SET InboundHops=@hops WHERE RadioId=@radioId AND PublicKey=@publicKey", new { radioId, publicKey, hops });
    }

    public void UpdateContactLastMessage(string contactId, long atMs)
    {
        using var c = Open();
        c.Execute("UPDATE Contacts SET LastMessageAt=MAX(LastMessageAt,@atMs) WHERE Id=@contactId", new { contactId, atMs });
    }

    public void IncrementContactUnread(string contactId, bool mention)
    {
        using var c = Open();
        c.Execute("UPDATE Contacts SET UnreadCount=UnreadCount+1, UnreadMentions=UnreadMentions+@m WHERE Id=@contactId", new { contactId, m = mention ? 1 : 0 });
    }

    public void ClearContactUnread(string contactId)
    {
        using var c = Open();
        c.Execute("UPDATE Contacts SET UnreadCount=0, UnreadMentions=0 WHERE Id=@contactId", new { contactId });
        c.Execute("UPDATE Messages SET IsRead=1, MentionSeen=1 WHERE ContactId=@contactId AND IsRead=0", new { contactId });
    }

    public void SetContactDraft(string contactId, string? draft)
    {
        using var c = Open();
        c.Execute("UPDATE Contacts SET DraftText=@draft WHERE Id=@contactId", new { contactId, draft });
    }

    public IReadOnlyList<ContactRecord> GetBlockedContacts(string radioId)
    {
        using var c = Open();
        return c.Query<ContactRecord>("SELECT * FROM Contacts WHERE RadioId=@radioId AND IsBlocked=1", new { radioId }).ToList();
    }

    // MARK: Discovered nodes

    public IReadOnlyList<DiscoveredNodeRecord> GetDiscoveredNodes(string radioId)
    {
        using var c = Open();
        return c.Query<DiscoveredNodeRecord>("SELECT * FROM DiscoveredNodes WHERE RadioId=@radioId ORDER BY LastHeard DESC", new { radioId }).ToList();
    }

    /// <returns>(record, isNew)</returns>
    public (DiscoveredNodeRecord, bool) UpsertDiscoveredNode(string radioId, MeshCore.MeshContact frame)
    {
        using var c = Open();
        var existing = c.QuerySingleOrDefault<DiscoveredNodeRecord>("SELECT * FROM DiscoveredNodes WHERE RadioId=@radioId AND PublicKey=@pk", new { radioId, pk = frame.PublicKey });
        var now = Time.Now();
        var r = existing ?? new DiscoveredNodeRecord { RadioId = radioId, PublicKey = frame.PublicKey, FirstSeen = now };
        r.Name = frame.AdvertisedName;
        r.Type = (int)frame.Type;
        r.Latitude = frame.Latitude;
        r.Longitude = frame.Longitude;
        r.LastAdvert = frame.LastAdvertisement.ToUnixTimeSeconds();
        r.LastHeard = now;
        r.OutPathLength = frame.OutPathLength;
        r.OutPath = frame.OutPath;
        c.Execute("""
            INSERT INTO DiscoveredNodes(Id,RadioId,PublicKey,Name,Type,Latitude,Longitude,LastAdvert,FirstSeen,LastHeard,OutPathLength,OutPath)
            VALUES(@Id,@RadioId,@PublicKey,@Name,@Type,@Latitude,@Longitude,@LastAdvert,@FirstSeen,@LastHeard,@OutPathLength,@OutPath)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Type=excluded.Type, Latitude=excluded.Latitude, Longitude=excluded.Longitude,
                LastAdvert=excluded.LastAdvert, LastHeard=excluded.LastHeard, OutPathLength=excluded.OutPathLength, OutPath=excluded.OutPath
            """, r);
        return (r, existing is null);
    }

    public void DeleteDiscoveredNode(string id)
    {
        using var c = Open();
        c.Execute("DELETE FROM DiscoveredNodes WHERE Id=@id", new { id });
    }

    public void ClearDiscoveredNodes(string radioId)
    {
        using var c = Open();
        c.Execute("DELETE FROM DiscoveredNodes WHERE RadioId=@radioId", new { radioId });
    }

    // MARK: Channels

    public IReadOnlyList<ChannelRecord> GetChannels(string radioId)
    {
        using var c = Open();
        return c.Query<ChannelRecord>("SELECT * FROM Channels WHERE RadioId=@radioId ORDER BY Idx", new { radioId }).ToList();
    }

    public ChannelRecord? GetChannel(string radioId, int idx)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<ChannelRecord>("SELECT * FROM Channels WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx });
    }

    public void UpsertChannel(ChannelRecord r)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO Channels(Id,RadioId,Idx,Name,Secret,LastMessageAt,UnreadCount,UnreadMentions,NotificationLevel,FloodScope,DraftText)
            VALUES(@Id,@RadioId,@Idx,@Name,@Secret,@LastMessageAt,@UnreadCount,@UnreadMentions,@NotificationLevel,@FloodScope,@DraftText)
            ON CONFLICT(RadioId, Idx) DO UPDATE SET Name=excluded.Name, Secret=excluded.Secret, LastMessageAt=excluded.LastMessageAt,
                UnreadCount=excluded.UnreadCount, UnreadMentions=excluded.UnreadMentions, NotificationLevel=excluded.NotificationLevel,
                FloodScope=excluded.FloodScope, DraftText=excluded.DraftText
            """, r);
    }

    public void DeleteChannel(string radioId, int idx, bool deleteMessages)
    {
        using var c = Open();
        if (deleteMessages) c.Execute("DELETE FROM Messages WHERE RadioId=@radioId AND ChannelIndex=@idx", new { radioId, idx });
        c.Execute("DELETE FROM Channels WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx });
    }

    public void UpdateChannelLastMessage(string radioId, int idx, long atMs)
    {
        using var c = Open();
        c.Execute("UPDATE Channels SET LastMessageAt=MAX(LastMessageAt,@atMs) WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx, atMs });
    }

    public void IncrementChannelUnread(string radioId, int idx, bool mention)
    {
        using var c = Open();
        c.Execute("UPDATE Channels SET UnreadCount=UnreadCount+1, UnreadMentions=UnreadMentions+@m WHERE RadioId=@radioId AND Idx=@idx",
            new { radioId, idx, m = mention ? 1 : 0 });
    }

    public void ClearChannelUnread(string radioId, int idx)
    {
        using var c = Open();
        c.Execute("UPDATE Channels SET UnreadCount=0, UnreadMentions=0 WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx });
        c.Execute("UPDATE Messages SET IsRead=1, MentionSeen=1 WHERE RadioId=@radioId AND ChannelIndex=@idx AND IsRead=0", new { radioId, idx });
    }

    public void SetChannelDraft(string radioId, int idx, string? draft)
    {
        using var c = Open();
        c.Execute("UPDATE Channels SET DraftText=@draft WHERE RadioId=@radioId AND Idx=@idx", new { radioId, idx, draft });
    }

    // MARK: Blocked channel senders

    public IReadOnlyList<BlockedSenderRecord> GetBlockedSenders(string radioId)
    {
        using var c = Open();
        return c.Query<BlockedSenderRecord>("SELECT * FROM BlockedSenders WHERE RadioId=@radioId ORDER BY Name", new { radioId }).ToList();
    }

    public void BlockSender(string radioId, string name)
    {
        using var c = Open();
        c.Execute("INSERT OR IGNORE INTO BlockedSenders(RadioId,Name,BlockedAt) VALUES(@radioId,@name,@now)", new { radioId, name, now = Time.Now() });
        c.Execute("DELETE FROM Messages WHERE RadioId=@radioId AND ChannelIndex IS NOT NULL AND SenderName=@name", new { radioId, name });
    }

    public void UnblockSender(string radioId, string name)
    {
        using var c = Open();
        c.Execute("DELETE FROM BlockedSenders WHERE RadioId=@radioId AND Name=@name", new { radioId, name });
    }
}
