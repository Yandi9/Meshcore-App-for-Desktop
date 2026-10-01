using MeshCore;

namespace MC1.Core.Models;

public enum MessageStatus { Pending = 0, Sending = 1, Sent = 2, Delivered = 3, Failed = 4, Retrying = 5 }
public enum MessageDirection { Incoming = 0, Outgoing = 1 }
public enum NotificationLevel { All = 0, MentionsOnly = 1, Muted = 2 }
public enum RoomPermission { Guest = 0, ReadWrite = 1, Admin = 2 }
public enum DecryptStatus { NotApplicable = 0, Success = 1, NoMatchingKey = 2, Pending = 3, DmNoMatchingKey = 4 }

public static class Time
{
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public static long ToMs(DateTimeOffset d) => d.ToUnixTimeMilliseconds();
}

/// <summary>A companion radio this app has connected to (keyed by its public key).</summary>
public sealed class RadioRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public string Model { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string FirmwareBuild { get; set; } = "";
    public int FirmwareCode { get; set; }
    public int MaxContacts { get; set; }
    public int MaxChannels { get; set; }
    public long LastContactSync { get; set; }
    public long LastConnected { get; set; }
    public string? OcvPreset { get; set; }
    public string? CustomOcv { get; set; }
    public string? KnownRegions { get; set; }
    public string? LastConnection { get; set; }
    public long LastChannelSync { get; set; }
}

public sealed class ContactRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public string Name { get; set; } = "";
    public string? Nickname { get; set; }
    public int Type { get; set; } = 1;
    public int TypeRaw { get; set; } = 1;
    public int Flags { get; set; }
    public int OutPathLength { get; set; } = 0xFF;
    public byte[] OutPath { get; set; } = [];
    public long LastAdvert { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public long LastModified { get; set; }
    public bool IsBlocked { get; set; }
    public int NotificationLevel { get; set; }
    public long LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    public int UnreadMentions { get; set; }
    public long LastHeard { get; set; }
    public int? InboundHops { get; set; }
    public string? DraftText { get; set; }

    public ContactType ContactType => Enum.IsDefined(typeof(ContactType), (byte)Type) ? (ContactType)(byte)Type : MeshCore.ContactType.Chat;
    public ContactFlags ContactFlags => (ContactFlags)(byte)Flags;
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? (string.IsNullOrWhiteSpace(Name) ? PublicKeyHex[..8] : Name) : Nickname!;
    public string PublicKeyHex => PublicKey.ToHex();
    public string ShortKey => PublicKey.Length >= 4 ? Convert.ToHexString(PublicKey, 0, 4) : "";
    public bool IsFavorite => (Flags & (int)MeshCore.ContactFlags.Favorite) != 0;
    public bool IsFloodRouted => OutPathLength == 0xFF;
    public bool HasLocation => Latitude != 0 || Longitude != 0;
    public int HopCount => PathEncoding.Decode((byte)OutPathLength)?.HopCount ?? 0;
    public int HashSize => PathEncoding.Decode((byte)OutPathLength)?.HashSize ?? 1;
    public bool IsMuted => NotificationLevel == (int)Models.NotificationLevel.Muted;

    public string RouteDescription => IsFloodRouted ? L.T("Flood") : HopCount == 0 ? L.T("Direct") : L.Plural(HopCount, "{0} hop", "{0} hops");

    public MeshContact ToMeshContact() => new(PublicKey, ContactType, (byte)TypeRaw, ContactFlags, (byte)OutPathLength, OutPath, Name,
        DateTimeOffset.FromUnixTimeSeconds(LastAdvert), Latitude, Longitude, DateTimeOffset.FromUnixTimeSeconds(LastModified));

    public void ApplyFrom(MeshContact c)
    {
        PublicKey = c.PublicKey;
        Name = c.AdvertisedName;
        Type = (int)c.Type;
        TypeRaw = c.TypeRawValue;
        Flags = (int)c.Flags;
        OutPathLength = c.OutPathLength;
        OutPath = c.OutPath;
        LastAdvert = c.LastAdvertisement.ToUnixTimeSeconds();
        Latitude = c.Latitude;
        Longitude = c.Longitude;
        LastModified = c.LastModified.ToUnixTimeSeconds();
    }
}

/// <summary>A node heard via a new advert that is not (yet) in the radio's contacts.</summary>
public sealed class DiscoveredNodeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public string Name { get; set; } = "";
    public int Type { get; set; } = 1;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public long LastAdvert { get; set; }
    public long FirstSeen { get; set; }
    public long LastHeard { get; set; }
    public int OutPathLength { get; set; } = 0xFF;
    public byte[] OutPath { get; set; } = [];
    public ContactType ContactType => Enum.IsDefined(typeof(ContactType), (byte)Type) ? (ContactType)(byte)Type : MeshCore.ContactType.Chat;
    public bool HasLocation => Latitude != 0 || Longitude != 0;
    public string PublicKeyHex => PublicKey.ToHex();

    public MeshContact ToMeshContact() => new(PublicKey, ContactType, (byte)Type, MeshCore.ContactFlags.None, (byte)OutPathLength, OutPath, Name,
        DateTimeOffset.FromUnixTimeSeconds(LastAdvert), Latitude, Longitude, DateTimeOffset.UtcNow);
}

public sealed class ChannelRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public int Idx { get; set; }
    public string Name { get; set; } = "";
    public byte[] Secret { get; set; } = new byte[16];
    public long LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    public int UnreadMentions { get; set; }
    public int NotificationLevel { get; set; }
    public string? FloodScope { get; set; }
    public string? DraftText { get; set; }

    public bool IsPublic => Secret.AsSpan().SequenceEqual(ChannelSecrets.PublicChannelSecret);
    public bool IsHashtag => Name.StartsWith('#');
    public bool IsConfigured => !string.IsNullOrEmpty(Name) || Secret.Any(b => b != 0);
    public string DisplayName => string.IsNullOrEmpty(Name) ? L.F("Channel {0}", Idx) : Name;
    public bool IsMuted => NotificationLevel == (int)Models.NotificationLevel.Muted;
}

public sealed class MessageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public string? ContactId { get; set; }
    public int? ChannelIndex { get; set; }
    public string Text { get; set; } = "";
    public long Timestamp { get; set; }
    public long CreatedAt { get; set; } = Time.Now();
    public long SortDate { get; set; } = Time.Now();
    public int Direction { get; set; }
    public int Status { get; set; }
    public int TextType { get; set; }
    public long? AckCode { get; set; }
    public int PathLength { get; set; }
    public double? Snr { get; set; }
    public byte[]? PathNodes { get; set; }
    public byte[]? SenderKeyPrefix { get; set; }
    public string? SenderName { get; set; }
    public bool IsRead { get; set; }
    public string? ReplyToId { get; set; }
    public long? RoundTripTime { get; set; }
    public int HeardRepeats { get; set; }
    public int SendCount { get; set; } = 1;
    public int RetryAttempt { get; set; }
    public int MaxRetryAttempts { get; set; }
    public string? DedupKey { get; set; }
    public bool ContainsSelfMention { get; set; }
    public bool MentionSeen { get; set; }
    public string? ReactionSummary { get; set; }
    public int RouteType { get; set; } = -1;
    public string? RegionScope { get; set; }
    public long? SenderTimestamp { get; set; }
    public bool TimestampCorrected { get; set; }
    public string? LinkPreviewUrl { get; set; }
    public string? LinkPreviewTitle { get; set; }
    public byte[]? LinkPreviewImage { get; set; }
    public bool LinkPreviewFetched { get; set; }

    public bool IsOutgoing => Direction == (int)MessageDirection.Outgoing;
    public bool IsChannel => ChannelIndex is not null;
    public MessageStatus MessageStatus => (MessageStatus)Status;
    public DateTimeOffset Date => Time.FromMs(SortDate);
    /// <summary>Timestamp used for reaction hashes: the sender's original stamp.</summary>
    public uint WireTimestamp => (uint)(SenderTimestamp ?? Timestamp);
}

public sealed class MessageRepeatRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MessageId { get; set; } = "";
    public long ReceivedAt { get; set; } = Time.Now();
    public byte[] PathNodes { get; set; } = [];
    public int PathLength { get; set; }
    public double? Snr { get; set; }
    public int? Rssi { get; set; }
    public string? RxLogEntryId { get; set; }
}

public sealed class ReactionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MessageId { get; set; } = "";
    public string RadioId { get; set; } = "";
    public string Emoji { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string MessageHash { get; set; } = "";
    public string RawText { get; set; } = "";
    public string? ContactId { get; set; }
    public int? ChannelIndex { get; set; }
    public long ReceivedAt { get; set; } = Time.Now();
    public bool IsOutgoing { get; set; }
}

public sealed class RxLogRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public long ReceivedAt { get; set; } = Time.Now();
    public double? Snr { get; set; }
    public int? Rssi { get; set; }
    public int RouteType { get; set; }
    public int PayloadType { get; set; }
    public int PayloadVersion { get; set; }
    public int PathLength { get; set; }
    public byte[] PathNodes { get; set; } = [];
    public byte[] PacketPayload { get; set; } = [];
    public byte[] RawPayload { get; set; } = [];
    public string PacketHash { get; set; } = "";
    public int? ChannelIndex { get; set; }
    public string? ChannelName { get; set; }
    public int DecryptStatus { get; set; }
    public string? FromContactName { get; set; }
    public long? SenderTimestamp { get; set; }
    public string? DecodedText { get; set; }
    public byte[]? TransportCode { get; set; }
    public string? RegionScope { get; set; }

    public RouteType Route => (RouteType)RouteType;
    public PayloadType Payload => PayloadTypeExtensions.FromBits((byte)PayloadType);
    public DecryptStatus Decrypt => (DecryptStatus)DecryptStatus;
    public int HopCount => PathEncoding.Decode((byte)PathLength)?.HopCount ?? 0;
    public int HashSize => PathEncoding.Decode((byte)PathLength)?.HashSize ?? 1;
}

public sealed class RemoteSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public string Name { get; set; } = "";
    public bool IsRoom { get; set; }
    public bool IsConnected { get; set; }
    public int Permission { get; set; }
    public long LastSyncTimestamp { get; set; }
    public int UnreadCount { get; set; }
    public long LastActivity { get; set; }
    public int NotificationLevel { get; set; }
    public long LastLogin { get; set; }

    public bool IsAdmin => Permission == (int)RoomPermission.Admin;
    public bool CanPost => Permission >= (int)RoomPermission.ReadWrite;
    public RoomPermission PermissionLevel => (RoomPermission)Permission;
}

public sealed class RoomMessageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SessionId { get; set; } = "";
    public byte[] AuthorKeyPrefix { get; set; } = [];
    public string AuthorName { get; set; } = "";
    public string Text { get; set; } = "";
    public long Timestamp { get; set; }
    public long CreatedAt { get; set; } = Time.Now();
    public bool IsFromSelf { get; set; }
    public int Status { get; set; } = (int)MessageStatus.Delivered;
    public string? DedupKey { get; set; }
    public long? AckCode { get; set; }
    public int RetryAttempt { get; set; }
    public MessageStatus MessageStatus => (MessageStatus)Status;
}

/// <summary>A point-in-time status/telemetry capture of a remote node (for history charts).</summary>
public sealed class NodeSnapshotRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public long CapturedAt { get; set; } = Time.Now();
    public int? BatteryMv { get; set; }
    public int? NoiseFloor { get; set; }
    public int? LastRssi { get; set; }
    public double? LastSnr { get; set; }
    public long? Uptime { get; set; }
    public long? PacketsReceived { get; set; }
    public long? PacketsSent { get; set; }
    public long? Airtime { get; set; }
    public long? RxAirtime { get; set; }
    public int? TxQueue { get; set; }
    public string? TelemetryJson { get; set; }
    public string? NeighboursJson { get; set; }
}

public sealed class TracePathRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RadioId { get; set; } = "";
    public string Name { get; set; } = "";
    public byte[] Path { get; set; } = [];
    public int HashSize { get; set; } = 1;
    public long CreatedAt { get; set; } = Time.Now();
    public long? LastRunAt { get; set; }
    public string? LastResultJson { get; set; }
}

public sealed class BlockedSenderRecord
{
    public string RadioId { get; set; } = "";
    public string Name { get; set; } = "";
    public long BlockedAt { get; set; } = Time.Now();
}

/// <summary>Conversation summary used by the chat list.</summary>
public sealed record ConversationSummary(
    string Key,
    string Title,
    string? Subtitle,
    long LastActivity,
    int Unread,
    int UnreadMentions,
    bool IsChannel,
    bool IsRoom,
    ContactRecord? Contact,
    ChannelRecord? Channel,
    RemoteSessionRecord? Room,
    bool IsMuted,
    bool IsFavorite);
