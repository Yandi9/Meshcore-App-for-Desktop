using System.Security.Cryptography;
using System.Text;
using MeshCore;
using Xunit;

namespace MeshCore.Tests;

public class ParserTests
{
    internal static byte[] SelfInfoFrame(string name = "MyNode", double freq = 910.525, double bw = 62.5)
    {
        var w = new ByteWriter((byte)ResponseCode.SelfInfo)
            .U8(1).I8(20).I8(22)
            .Raw(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
            .I32(37_774_900).I32(-122_419_400)
            .U8(1).U8(2)
            .U8((byte)((1 << 4) | (2 << 2) | 3))
            .U8(1)
            .U32((uint)Math.Round(freq * 1000)).U32((uint)Math.Round(bw * 1000))
            .U8(7).U8(5)
            .Utf8(name);
        return w.ToArray();
    }

    [Fact]
    public void ParsesSelfInfo()
    {
        var e = Assert.IsType<MeshEvent.SelfInfoEvent>(PacketParser.Parse(SelfInfoFrame()));
        var s = e.Info;
        Assert.Equal("MyNode", s.Name);
        Assert.Equal(20, s.TxPower);
        Assert.Equal(22, s.MaxTxPower);
        Assert.Equal(37.7749, s.Latitude, 6);
        Assert.Equal(-122.4194, s.Longitude, 6);
        Assert.Equal(910.525, s.RadioFrequency, 3);
        Assert.Equal(62.5, s.RadioBandwidth, 3);
        Assert.Equal(7, s.RadioSpreadingFactor);
        Assert.Equal(5, s.RadioCodingRate);
        Assert.Equal(1, s.TelemetryModeEnvironment);
        Assert.Equal(2, s.TelemetryModeLocation);
        Assert.Equal(3, s.TelemetryModeBase);
        Assert.True(s.ManualAddContacts);
    }

    internal static byte[] DeviceInfoFrame(byte fw = 10, bool clientRepeat = true, byte hashMode = 1)
    {
        var w = new ByteWriter((byte)ResponseCode.DeviceInfo, fw)
            .U8(175).U8(40).U32(123456)
            .Raw("12 Sep 2026"u8.ToArray().PaddedOrTruncated(12))
            .Raw("Heltec V3"u8.ToArray().PaddedOrTruncated(40))
            .Raw("v1.15.0"u8.ToArray().PaddedOrTruncated(20))
            .U8(clientRepeat ? (byte)1 : (byte)0)
            .U8(hashMode);
        return w.ToArray();
    }

    [Fact]
    public void ParsesDeviceInfoV10()
    {
        var d = Assert.IsType<MeshEvent.DeviceInfo>(PacketParser.Parse(DeviceInfoFrame())).Info;
        Assert.Equal(350, d.MaxContacts);
        Assert.Equal(40, d.MaxChannels);
        Assert.Equal(123456u, d.BlePin);
        Assert.Equal("Heltec V3", d.Model);
        Assert.Equal("v1.15.0", d.Version);
        Assert.True(d.ClientRepeat);
        Assert.Equal(1, d.PathHashMode);
        Assert.Equal(2, d.HashSize);
    }

    internal static byte[] ContactPayload(byte[] key, string name, ContactType type = ContactType.Chat, byte pathLen = 0xFF, byte[]? path = null, uint lastAdvert = 1_700_000_000, double lat = 0, double lon = 0, uint lastMod = 1_700_000_100)
    {
        return new ByteWriter()
            .Raw(key.PaddedOrTruncated(32)).U8((byte)type).U8(0).U8(pathLen)
            .Raw((path ?? []).PaddedOrTruncated(64))
            .Raw(Encoding.UTF8.GetBytes(name).PaddedOrTruncated(32))
            .U32(lastAdvert).I32((int)(lat * 1e6)).I32((int)(lon * 1e6)).U32(lastMod)
            .ToArray();
    }

    [Fact]
    public void ParsesContact()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var frame = new byte[] { (byte)ResponseCode.Contact }.Concat(ContactPayload(key, "Bob", ContactType.Repeater, 0x02, [0xA1, 0xB2], lat: 40.5, lon: -74.25)).ToArray();
        var c = Assert.IsType<MeshEvent.Contact>(PacketParser.Parse(frame)).Value;
        Assert.Equal(key, c.PublicKey);
        Assert.Equal("Bob", c.AdvertisedName);
        Assert.Equal(ContactType.Repeater, c.Type);
        Assert.Equal(new byte[] { 0xA1, 0xB2 }, c.OutPath);
        Assert.Equal(2, c.PathHopCount);
        Assert.Equal(40.5, c.Latitude, 5);
        Assert.Equal(-74.25, c.Longitude, 5);
    }

    [Fact]
    public void ContactWithReservedPathEncodingFails()
    {
        var frame = new byte[] { (byte)ResponseCode.Contact }.Concat(ContactPayload(new byte[32], "X", pathLen: 0xC1)).ToArray();
        Assert.IsType<MeshEvent.ParseFailure>(PacketParser.Parse(frame));
    }

    [Fact]
    public void ContactNameTruncatedMidCodepointDecodesPrefix()
    {
        var name = Encoding.UTF8.GetBytes(new string('x', 30) + "é").Take(31).ToArray(); // cut the 2-byte é in half
        var payload = ContactPayload(new byte[32], "");
        Array.Copy(name, 0, payload, 99, name.Length);
        var c = Assert.IsType<MeshEvent.Contact>(PacketParser.Parse(new byte[] { 0x03 }.Concat(payload).ToArray())).Value;
        Assert.Equal(new string('x', 30), c.AdvertisedName);
    }

    [Fact]
    public void ParsesMessageSent()
    {
        var e = Assert.IsType<MeshEvent.MessageSent>(PacketParser.Parse([0x06, 0x01, 0xAA, 0xBB, 0xCC, 0xDD, 0x10, 0x27, 0x00, 0x00]));
        Assert.Equal(1, e.Info.Route);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, e.Info.ExpectedAck);
        Assert.Equal(10000u, e.Info.SuggestedTimeoutMs);
    }

    [Fact]
    public void ParsesContactMessageV3()
    {
        var frame = new ByteWriter(0x10).I8(-20).U8(0).U8(0)
            .Raw([1, 2, 3, 4, 5, 6]).U8(2).U8(0).U32(1_700_000_000).Utf8("hello 👋").ToArray();
        var m = Assert.IsType<MeshEvent.ContactMessageReceived>(PacketParser.Parse(frame)).Message;
        Assert.Equal(-5.0, m.Snr);
        Assert.Equal("hello 👋", m.Text);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, m.SenderPublicKeyPrefix);
        Assert.Null(m.Signature);
    }

    [Fact]
    public void ParsesSignedRoomMessage()
    {
        var frame = new ByteWriter(0x10).I8(8).U8(0).U8(0)
            .Raw([1, 2, 3, 4, 5, 6]).U8(0).U8(2).U32(1_700_000_000).Raw([9, 8, 7, 6]).Utf8("room post").ToArray();
        var m = Assert.IsType<MeshEvent.ContactMessageReceived>(PacketParser.Parse(frame)).Message;
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, m.Signature);
        Assert.Equal("room post", m.Text);
    }

    [Fact]
    public void ParsesChannelMessageV3()
    {
        var frame = new ByteWriter(0x11).I8(12).U8(0).U8(0).U8(3).U8(1).U8(0).U32(1_700_000_000).Utf8("Alice: hi all").ToArray();
        var m = Assert.IsType<MeshEvent.ChannelMessageReceived>(PacketParser.Parse(frame)).Message;
        Assert.Equal(3, m.ChannelIndex);
        Assert.Equal(3.0, m.Snr);
        Assert.Equal("Alice: hi all", m.Text);
    }

    [Fact]
    public void ParsesAckWithTripTime()
    {
        var e = Assert.IsType<MeshEvent.Acknowledgement>(PacketParser.Parse([0x82, 1, 2, 3, 4, 0xE8, 0x03, 0, 0]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, e.Code);
        Assert.Equal(1000u, e.TripTime);
    }

    [Fact]
    public void ParsesBattery()
    {
        var e = Assert.IsType<MeshEvent.Battery>(PacketParser.Parse(new ByteWriter(0x0C).U16(4012).U32(100).U32(2000).ToArray()));
        Assert.Equal(4012, e.Info.Level);
        Assert.Equal(100, e.Info.UsedStorageKB);
        Assert.Equal(2000, e.Info.TotalStorageKB);
        Assert.IsType<MeshEvent.ParseFailure>(PacketParser.Parse([0x0C, 1, 2, 3]));
    }

    [Fact]
    public void ParsesChannelInfo()
    {
        var frame = new ByteWriter(0x12, 2).Raw("#test"u8.ToArray().PaddedOrTruncated(32)).Raw(ChannelSecrets.HashSecret("#test")).ToArray();
        var ci = Assert.IsType<MeshEvent.ChannelInfoEvent>(PacketParser.Parse(frame)).Info;
        Assert.Equal(2, ci.Index);
        Assert.Equal("#test", ci.Name);
        Assert.Equal(ChannelSecrets.HashSecret("#test"), ci.Secret);
    }

    [Fact]
    public void ParsesStatusResponse()
    {
        var w = new ByteWriter(0x87, 0).Raw([1, 2, 3, 4, 5, 6]).U16(4100).U16(0).U16(unchecked((ushort)-110)).U16(unchecked((ushort)-80))
            .U32(10).U32(20).U32(30).U32(3600).U32(1).U32(2).U32(3).U32(4).U16(0).U16(unchecked((ushort)40)).U16(5).U16(6).U32(99).U32(7);
        var s = Assert.IsType<MeshEvent.StatusResponseEvent>(PacketParser.Parse(w.ToArray())).Response;
        Assert.Equal(4100, s.Battery);
        Assert.Equal(-110, s.NoiseFloor);
        Assert.Equal(-80, s.LastRssi);
        Assert.Equal(3600u, s.Uptime);
        Assert.Equal(10.0, s.LastSnr);
        Assert.Equal(99u, s.RxAirtime);
        Assert.Equal(7u, s.ReceiveErrors);
    }

    [Fact]
    public void ParsesTraceData()
    {
        // reserved, pathLen=2, flags=0 (1-byte hashes), tag, auth, hashes[2], snrs[2], final snr
        var w = new ByteWriter(0x89, 0, 2, 0).U32(77).U32(88).Raw([0xA1, 0xB2]).I8(40).I8(-8).I8(20);
        var t = Assert.IsType<MeshEvent.TraceData>(PacketParser.Parse(w.ToArray())).Info;
        Assert.Equal(77u, t.Tag);
        Assert.Equal(3, t.Path.Count);
        Assert.Equal((byte)0xA1, t.Path[0].Hash);
        Assert.Equal(10.0, t.Path[0].Snr);
        Assert.Equal(-2.0, t.Path[1].Snr);
        Assert.Null(t.Path[2].Hash);
        Assert.Equal(5.0, t.Path[2].Snr);
    }

    [Fact]
    public void ParsesPathDiscovery()
    {
        var w = new ByteWriter(0x8D, 0).Raw([1, 2, 3, 4, 5, 6]).U8(0x42).Raw([0xAA, 0xBB, 0xCC, 0xDD]).U8(0x01).Raw([0xEE]);
        var p = Assert.IsType<MeshEvent.PathResponse>(PacketParser.Parse(w.ToArray())).Info;
        Assert.Equal(2, p.OutHopCount);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, p.OutPath);
        Assert.Equal(new byte[] { 0xEE }, p.InPath);
    }

    [Fact]
    public void ParsesLoginSuccessExtended()
    {
        var w = new ByteWriter(0x85, 1).Raw([1, 2, 3, 4, 5, 6]).U32(1_700_000_000).U8(3).U8(0);
        var l = Assert.IsType<MeshEvent.LoginSuccess>(PacketParser.Parse(w.ToArray())).Info;
        Assert.True(l.IsAdmin);
        Assert.Equal(2, l.Permissions);
        Assert.NotNull(l.ServerTime);
    }

    [Fact]
    public void ParsesDiscoverResponse()
    {
        var pk = RandomNumberGenerator.GetBytes(32);
        var w = new ByteWriter(0x8E).I8(20).I8(-90).U8(0).U8(0x92).I8(16).U32(55).Raw(pk);
        var d = Assert.IsType<MeshEvent.DiscoverResponseEvent>(PacketParser.Parse(w.ToArray())).Response;
        Assert.Equal(2, d.NodeType);
        Assert.Equal(4.0, d.SnrIn);
        Assert.Equal(5.0, d.Snr);
        Assert.Equal(-90, d.Rssi);
        Assert.Equal(pk, d.PublicKey);
    }

    [Fact]
    public void ParsesRxLogFrame()
    {
        // header: route=FLOOD(1), payload type GROUP_TEXT(5) => (5<<2)|1 = 0x15
        var raw = new ByteWriter(0x15, 0x02, 0xAA, 0xBB).Raw([0x11, 0x22, 0x33]).ToArray();
        var w = new ByteWriter(0x88).I8(24).I8(-70).Raw(raw);
        var r = Assert.IsType<MeshEvent.RxLogData>(PacketParser.Parse(w.ToArray())).Info;
        Assert.Equal(RouteType.Flood, r.RouteType);
        Assert.Equal(PayloadType.GroupText, r.PayloadType);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, r.PathNodes);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, r.PacketPayload);
        Assert.Equal(6.0, r.Snr);
        Assert.Equal(-70, r.Rssi);
        Assert.Equal(16, r.PacketHash.Length);
    }

    [Fact]
    public void UnknownCodeIsParseFailure() => Assert.IsType<MeshEvent.ParseFailure>(PacketParser.Parse([0x7F]));

    [Theory]
    [InlineData(0x00, 1, 0)]
    [InlineData(0x03, 1, 3)]
    [InlineData(0x45, 2, 5)]
    [InlineData(0x82, 3, 2)]
    public void DecodesPathLen(byte encoded, int hashSize, int hops)
    {
        var d = PathEncoding.Decode(encoded)!.Value;
        Assert.Equal(hashSize, d.HashSize);
        Assert.Equal(hops, d.HopCount);
        Assert.Equal(encoded, PathEncoding.Encode(hashSize, hops));
    }

    [Fact] public void ReservedPathModeIsNull() => Assert.Null(PathEncoding.Decode(0xC0));
}

public class LppTests
{
    [Fact] public void Temperature() => Assert.Equal(25.5, ((LppValue.Float)LppDecoder.Decode([0x01, 0x67, 0x00, 0xFF])[0].Value).Value, 3);
    [Fact] public void Humidity() => Assert.Equal(65.0, ((LppValue.Float)LppDecoder.Decode([0x02, 0x68, 0x82])[0].Value).Value, 3);
    [Fact] public void Analog() => Assert.Equal(3.3, ((LppValue.Float)LppDecoder.Decode([0x03, 0x02, 0x01, 0x4A])[0].Value).Value, 3);
    [Fact] public void Barometer() => Assert.Equal(1013.2, ((LppValue.Float)LppDecoder.Decode([0x05, 0x73, 0x27, 0x94])[0].Value).Value, 3);

    [Fact]
    public void Gps()
    {
        var g = (LppValue.Gps)LppDecoder.Decode([0x04, 0x88, 0x05, 0xC3, 0x95, 0xED, 0x51, 0xFE, 0x00, 0x03, 0xE8])[0].Value;
        Assert.Equal(37.7749, g.Latitude, 4);
        Assert.Equal(-122.4194, g.Longitude, 4);
        Assert.Equal(10.0, g.Altitude, 2);
    }

    [Fact]
    public void RoundTrip()
    {
        var enc = new LppEncoder().AddVoltage(1, 4.12).AddTemperature(2, -3.4).AddGps(3, 51.5, -0.12, 35);
        var pts = LppDecoder.Decode(enc.Encode());
        Assert.Equal(3, pts.Count);
        Assert.Equal(4.12, ((LppValue.Float)pts[0].Value).Value, 2);
        Assert.Equal(-3.4, ((LppValue.Float)pts[1].Value).Value, 2);
        Assert.Equal("4.12 V", pts[0].FormattedValue);
    }
}
