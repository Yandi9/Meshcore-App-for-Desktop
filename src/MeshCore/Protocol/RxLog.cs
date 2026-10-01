using System.Security.Cryptography;

namespace MeshCore;

public enum RouteType : byte
{
    TcFlood = 0,
    Flood = 1,
    Direct = 2,
    TcDirect = 3,
}

public static class RouteTypeExtensions
{
    public static bool HasTransportCode(this RouteType r) => r is RouteType.TcFlood or RouteType.TcDirect;
    public static bool IsFlood(this RouteType r) => r is RouteType.Flood or RouteType.TcFlood;
    public static string DisplayName(this RouteType r) => r switch
    {
        RouteType.TcFlood => "TC_FLOOD",
        RouteType.Flood => "FLOOD",
        RouteType.Direct => "DIRECT",
        RouteType.TcDirect => "TC_DIRECT",
        _ => "?",
    };
}

public enum PayloadType : byte
{
    Request = 0,
    Response = 1,
    TextMessage = 2,
    Ack = 3,
    Advert = 4,
    GroupText = 5,
    GroupData = 6,
    AnonRequest = 7,
    Path = 8,
    Trace = 9,
    Multipart = 10,
    Control = 11,
    RawCustom = 15,
    Unknown = 255,
}

public static class PayloadTypeExtensions
{
    public static PayloadType FromBits(byte bits) => Enum.IsDefined(typeof(PayloadType), bits) ? (PayloadType)bits : PayloadType.Unknown;

    public static string DisplayName(this PayloadType p) => p switch
    {
        PayloadType.Request => "REQUEST",
        PayloadType.Response => "RESPONSE",
        PayloadType.TextMessage => "TEXT_MSG",
        PayloadType.Ack => "ACK",
        PayloadType.Advert => "ADVERT",
        PayloadType.GroupText => "GROUP_TEXT",
        PayloadType.GroupData => "GROUP_DATA",
        PayloadType.AnonRequest => "ANON_REQ",
        PayloadType.Path => "PATH",
        PayloadType.Trace => "TRACE",
        PayloadType.Multipart => "MULTIPART",
        PayloadType.Control => "CONTROL",
        PayloadType.RawCustom => "RAW_CUSTOM",
        _ => "UNKNOWN",
    };
}

/// <summary>A raw over-the-air packet reported by the radio's RX log.</summary>
public sealed record ParsedRxLogData(
    double? Snr,
    int? Rssi,
    byte[] RawPayload,
    RouteType RouteType,
    PayloadType PayloadType,
    byte PayloadVersion,
    byte PayloadTypeBits,
    byte[]? TransportCode,
    byte PathLength,
    byte[] PathNodes,
    byte[] PacketPayload,
    byte[]? SenderPubkeyPrefix = null,
    byte[]? RecipientPubkeyPrefix = null)
{
    public string PacketHash { get; } = ComputePacketHash(PacketPayload);

    public static string ComputePacketHash(byte[] packetPayload) =>
        Convert.ToHexString(SHA256.HashData(packetPayload), 0, 8).ToLowerInvariant();
}

public static class RxLogParser
{
    public static ParsedRxLogData? Parse(double? snr, int? rssi, byte[] payload)
    {
        if (payload.Length == 0) return null;
        var o = 0;
        var header = payload[o++];
        var routeBits = (byte)(header & 0x03);
        var typeBits = (byte)((header >> 2) & 0x0F);
        var version = (byte)((header >> 6) & 0x03);
        var route = (RouteType)routeBits;
        var ptype = PayloadTypeExtensions.FromBits(typeBits);
        byte[]? tc = null;
        if (route.HasTransportCode())
        {
            if (payload.Length < o + 4) return null;
            tc = payload.Slice(o, 4);
            o += 4;
        }
        if (payload.Length <= o) return null;
        var pathLen = payload[o++];
        if (PathEncoding.Decode(pathLen) is not { } dec) return null;
        byte[] nodes = [];
        if (dec.ByteLength > 0)
        {
            if (payload.Length < o + dec.ByteLength) return null;
            nodes = payload.Slice(o, dec.ByteLength);
            o += dec.ByteLength;
        }
        var packet = payload.Length > o ? payload.From(o) : [];
        byte[]? sender = null, recipient = null;
        if (ptype == PayloadType.TextMessage && packet.Length >= 2)
        {
            recipient = packet.Slice(0, 1);
            sender = packet.Slice(1, 1);
        }
        return new ParsedRxLogData(snr, rssi, payload, route, ptype, version, typeBits, tc, pathLen, nodes, packet, sender, recipient);
    }
}
