using System.Security.Cryptography;
using System.Text;

namespace MeshCore;

public enum ContactType : byte
{
    Chat = 0x01,
    Repeater = 0x02,
    Room = 0x03,
    Sensor = 0x04,
}

[Flags]
public enum ContactFlags : byte
{
    None = 0,
    Favorite = 0x01,
    TelemetryBase = 0x02,
    TelemetryLocation = 0x04,
    TelemetryEnvironment = 0x08,
    TelemetryAll = TelemetryBase | TelemetryLocation | TelemetryEnvironment,
}

/// <summary>A contact record as stored on the companion radio.</summary>
public sealed record MeshContact(
    byte[] PublicKey,
    ContactType Type,
    byte TypeRawValue,
    ContactFlags Flags,
    byte OutPathLength,
    byte[] OutPath,
    string AdvertisedName,
    DateTimeOffset LastAdvertisement,
    double Latitude,
    double Longitude,
    DateTimeOffset LastModified)
{
    public string Id => PublicKey.ToHex();
    public string PublicKeyPrefix => PublicKey.Prefix(6).ToHex();
    public bool IsFloodPath => OutPathLength == PathEncoding.FloodSentinel;
    public int PathHashSize => PathEncoding.Decode(OutPathLength)?.HashSize ?? 1;
    public int PathHopCount => PathEncoding.Decode(OutPathLength)?.HopCount ?? 0;
    public int PathByteLength => PathEncoding.Decode(OutPathLength)?.ByteLength ?? 0;
}

public sealed record SelfInfo(
    byte AdvertisementType,
    sbyte TxPower,
    sbyte MaxTxPower,
    byte[] PublicKey,
    double Latitude,
    double Longitude,
    byte MultiAcks,
    byte AdvertisementLocationPolicy,
    byte TelemetryModeEnvironment,
    byte TelemetryModeLocation,
    byte TelemetryModeBase,
    bool ManualAddContacts,
    /// <summary>Radio frequency in MHz.</summary>
    double RadioFrequency,
    /// <summary>Radio bandwidth in kHz.</summary>
    double RadioBandwidth,
    byte RadioSpreadingFactor,
    byte RadioCodingRate,
    string Name);

public sealed record DeviceCapabilities(
    byte FirmwareVersion,
    int MaxContacts,
    int MaxChannels,
    uint BlePin,
    string FirmwareBuild,
    string Model,
    string Version,
    bool ClientRepeat = false,
    byte PathHashMode = 0)
{
    public int HashSize => PathHashMode + 1;
    public bool SupportsPathHashMode => FirmwareVersion >= 10;
}

public sealed record BatteryInfo(int Level, int? UsedStorageKB = null, int? TotalStorageKB = null);

public sealed record OtherParamsConfig
{
    public bool ManualAddContacts { get; set; }
    public byte TelemetryModeBase { get; set; }
    public byte TelemetryModeLocation { get; set; }
    public byte TelemetryModeEnvironment { get; set; }
    public byte AdvertisementLocationPolicy { get; set; }
    public byte MultiAcks { get; set; }

    public static OtherParamsConfig From(SelfInfo s) => new()
    {
        ManualAddContacts = s.ManualAddContacts,
        TelemetryModeBase = s.TelemetryModeBase,
        TelemetryModeLocation = s.TelemetryModeLocation,
        TelemetryModeEnvironment = s.TelemetryModeEnvironment,
        AdvertisementLocationPolicy = s.AdvertisementLocationPolicy,
        MultiAcks = s.MultiAcks,
    };
}

public sealed record DefaultFloodScope(string Name, byte[] ScopeKey);

public sealed record ChannelInfo(byte Index, string Name, byte[] Secret)
{
    public bool IsConfigured => !string.IsNullOrEmpty(Name) || Secret.Any(b => b != 0);
}

public sealed record DiscoverResponse(byte NodeType, double SnrIn, double Snr, int Rssi, byte PathLength, byte[] Tag, byte[] PublicKey);

public sealed record AdvertPathResponse(uint RecvTimestamp, byte PathLength, byte[] Path);

public sealed record OwnerInfoResponse(string FirmwareVersion, string NodeName, string OwnerInfo);

public enum StatusLayout { Repeater, RoomServer }

public sealed record StatusResponse(
    StatusLayout Layout,
    byte[] PublicKeyPrefix,
    int Battery,
    int TxQueueLength,
    int NoiseFloor,
    int LastRssi,
    uint PacketsReceived,
    uint PacketsSent,
    uint Airtime,
    uint Uptime,
    uint SentFlood,
    uint SentDirect,
    uint ReceivedFlood,
    uint ReceivedDirect,
    int FullEvents,
    double LastSnr,
    int DirectDuplicates,
    int FloodDuplicates,
    uint RxAirtime,
    uint ReceiveErrors = 0,
    ushort? RoomServerPostedCount = null,
    ushort? RoomServerPostPushCount = null);

public sealed record AutoAddConfig(byte Bitmask, byte MaxHops = 0)
{
    public const byte OverwriteOldestBit = 0x01;
    public const byte ContactsBit = 0x02;
    public const byte RepeatersBit = 0x04;
    public const byte RoomServersBit = 0x08;
    public const byte SensorsBit = 0x10;
}

public sealed record FrequencyRange(uint LowerKHz, uint UpperKHz);

public sealed record TuningParamsResponse(double RxDelayBase, double AirtimeFactor);

public sealed record TraceNode(byte[]? HashBytes, double Snr)
{
    public byte? Hash => HashBytes is { Length: > 0 } ? HashBytes[0] : null;
}

public sealed record TraceInfo(uint Tag, uint AuthCode, byte Flags, byte PathLength, IReadOnlyList<TraceNode> Path);

public sealed record PathInfo(byte[] PublicKeyPrefix, byte OutPathLength, byte[] OutPath, byte InPathLength, byte[] InPath)
{
    public int? OutHopCount => PathEncoding.Decode(OutPathLength)?.HopCount;
    public int? InHopCount => PathEncoding.Decode(InPathLength)?.HopCount;
    public bool Matches(byte[] publicKey) => PublicKeyPrefix.Length > 0 && publicKey.StartsWith(PublicKeyPrefix);
}

public sealed record RawDataInfo(double Snr, int Rssi, byte[] Payload);
public sealed record LogDataInfo(double? Snr, int? Rssi, byte[] Payload);
public sealed record ControlDataInfo(double Snr, int Rssi, byte PathLength, byte PayloadType, byte[] Payload);

public sealed record MessageSentInfo(byte Route, byte[] ExpectedAck, uint SuggestedTimeoutMs)
{
    public uint AckCodeUInt32 => ExpectedAck.ReadUInt32LE(0);
    public bool IsFlood => Route == 1;
}

public sealed record ContactMessage(
    byte[] SenderPublicKeyPrefix,
    byte PathLength,
    byte TextType,
    DateTimeOffset SenderTimestamp,
    byte[]? Signature,
    string Text,
    double? Snr);

public sealed record ChannelMessage(
    byte ChannelIndex,
    byte PathLength,
    byte TextType,
    DateTimeOffset SenderTimestamp,
    string Text,
    double? Snr);

public sealed record ChannelDatagram(byte ChannelIndex, byte PathLength, ushort DataType, byte[] Data, double Snr);

public sealed record LoginInfo(byte Permissions, bool IsAdmin, byte[] PublicKeyPrefix, DateTimeOffset? ServerTime = null);

public sealed record TelemetryResponse(byte[] PublicKeyPrefix, byte[]? Tag, byte[] RawData)
{
    public IReadOnlyList<LppDataPoint> DataPoints => LppDecoder.Decode(RawData);
}

public sealed record MmaEntry(byte Channel, string Type, double Min, double Max, double Avg);
public sealed record MmaResponse(byte[] PublicKeyPrefix, byte[] Tag, IReadOnlyList<MmaEntry> Data);
public sealed record AclEntry(byte[] KeyPrefix, byte Permissions);
public sealed record AclResponse(byte[] PublicKeyPrefix, byte[] Tag, IReadOnlyList<AclEntry> Entries);
public sealed record Neighbour(byte[] PublicKeyPrefix, int SecondsAgo, double Snr);

public sealed record NeighboursResponse(byte[] PublicKeyPrefix, byte[] Tag, int TotalCount, IReadOnlyList<Neighbour> Neighbours)
{
    public const int MaxPaginationPages = 512;
}

public sealed record CoreStats(ushort BatteryMV, uint UptimeSeconds, ushort Errors, byte QueueLength);
public sealed record RadioStats(short NoiseFloor, sbyte LastRssi, double LastSnr, uint TxAirtimeSeconds, uint RxAirtimeSeconds);
public sealed record PacketStats(uint Received, uint Sent, uint FloodTx, uint DirectTx, uint FloodRx, uint DirectRx, uint ReceiveErrors = 0);

/// <summary>Flood scope used for regional routing (transport codes).</summary>
public abstract record FloodScope
{
    public sealed record Disabled : FloodScope;
    public sealed record ChannelName(string Name) : FloodScope;
    public sealed record RawKey(byte[] Key) : FloodScope;
    public sealed record Region(string Name) : FloodScope;

    public byte[] ScopeKey() => this switch
    {
        Disabled => new byte[16],
        ChannelName c => SHA256.HashData(Encoding.UTF8.GetBytes(c.Name)).Prefix(16),
        RawKey r => r.Key.PaddedOrTruncated(16),
        Region g => SHA256.HashData(Encoding.UTF8.GetBytes(g.Name.StartsWith('#') ? g.Name : "#" + g.Name)).Prefix(16),
        _ => new byte[16],
    };
}

public static class ChannelSecrets
{
    /// <summary>The well-known secret of the default "Public" channel.</summary>
    public static readonly byte[] PublicChannelSecret =
        [0x8B, 0x33, 0x87, 0xE9, 0xC5, 0xCD, 0xEA, 0x6A, 0xC9, 0xE5, 0xED, 0xBA, 0xA1, 0x15, 0xCD, 0x72];

    /// <summary>SHA-256(passphrase)[0..16]; an empty passphrase yields an all-zero secret.</summary>
    public static byte[] HashSecret(string passphrase) =>
        string.IsNullOrEmpty(passphrase) ? new byte[16] : SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)).Prefix(16);
}
