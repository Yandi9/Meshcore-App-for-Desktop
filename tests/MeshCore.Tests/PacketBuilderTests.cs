using MeshCore;
using Xunit;

namespace MeshCore.Tests;

/// <summary>Byte-for-byte checks against the Python reference (meshcore_py) frames used by the Swift test-suite.</summary>
public class PacketBuilderTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1704067200);
    private static readonly byte[] Dest = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB];
    private static byte[] Key32 => Dest.PaddedOrTruncated(32);

    [Fact] public void AppStart() => Assert.Equal(new byte[] { 0x01, 0x03, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x4D, 0x43, 0x6F, 0x72, 0x65 }, PacketBuilder.AppStart("MCore"));
    [Fact] public void DeviceQuery() => Assert.Equal(new byte[] { 0x16, 0x03 }, PacketBuilder.DeviceQuery());
    [Fact] public void GetBattery() => Assert.Equal(new byte[] { 0x14 }, PacketBuilder.GetBattery());
    [Fact] public void SetTime() => Assert.Equal(new byte[] { 0x06, 0x80, 0x00, 0x92, 0x65 }, PacketBuilder.SetTime(T));
    [Fact] public void SetName() => Assert.Equal(new byte[] { 0x08, 0x54, 0x65, 0x73, 0x74, 0x4E, 0x6F, 0x64, 0x65 }, PacketBuilder.SetName("TestNode"));
    [Fact] public void SetCoords() => Assert.Equal(new byte[] { 0x0E, 0x34, 0x66, 0x40, 0x02, 0x38, 0x07, 0xB4, 0xF8, 0, 0, 0, 0 }, PacketBuilder.SetCoordinates(37.7749, -122.4194));
    [Fact] public void SetTxPower() => Assert.Equal(new byte[] { 0x0C, 0x14 }, PacketBuilder.SetTxPower(20));
    [Fact] public void SetRadio() => Assert.Equal(new byte[] { 0x0B, 0x7B, 0xD6, 0x0D, 0x00, 0x90, 0xD0, 0x03, 0x00, 0x0B, 0x08 }, PacketBuilder.SetRadio(906.875, 250, 11, 8));
    [Fact] public void Advert() => Assert.Equal(new byte[] { 0x07 }, PacketBuilder.SendAdvertisement());
    [Fact] public void AdvertFlood() => Assert.Equal(new byte[] { 0x07, 0x01 }, PacketBuilder.SendAdvertisement(true));
    [Fact] public void Reboot() => Assert.Equal(new byte[] { 0x13, 0x72, 0x65, 0x62, 0x6F, 0x6F, 0x74 }, PacketBuilder.Reboot());
    [Fact] public void GetContacts() => Assert.Equal(new byte[] { 0x04 }, PacketBuilder.GetContacts());
    [Fact] public void GetMessage() => Assert.Equal(new byte[] { 0x0A }, PacketBuilder.GetMessage());

    [Fact]
    public void SendMessageHello() => Assert.Equal(
        new byte[] { 0x02, 0x00, 0x00, 0x80, 0x00, 0x92, 0x65, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0x48, 0x65, 0x6C, 0x6C, 0x6F },
        PacketBuilder.SendMessage(Dest, "Hello", T));

    [Fact]
    public void SendCommandStatus() => Assert.Equal(
        new byte[] { 0x02, 0x01, 0x00, 0x80, 0x00, 0x92, 0x65, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0x73, 0x74, 0x61, 0x74, 0x75, 0x73 },
        PacketBuilder.SendCommand(Dest, "status", T));

    [Fact]
    public void SendChannelMessage() => Assert.Equal(
        new byte[] { 0x03, 0x00, 0x00, 0x80, 0x00, 0x92, 0x65, 0x48, 0x69 },
        PacketBuilder.SendChannelMessage(0, "Hi", T));

    [Fact]
    public void SendLogin()
    {
        var expected = new List<byte> { 0x1A };
        expected.AddRange(Key32);
        expected.AddRange("secret"u8.ToArray());
        Assert.Equal(expected.ToArray(), PacketBuilder.SendLogin(Key32, "secret"));
    }

    [Fact] public void SendLogout() => Assert.Equal(new byte[] { 0x1D }.Concat(Key32).ToArray(), PacketBuilder.SendLogout(Key32));
    [Fact] public void StatusRequest() => Assert.Equal(new byte[] { 0x1B }.Concat(Key32).ToArray(), PacketBuilder.SendStatusRequest(Key32));
    [Fact] public void GetChannel0() => Assert.Equal(new byte[] { 0x1F, 0x00 }, PacketBuilder.GetChannel(0));

    [Fact]
    public void SetChannelGeneral()
    {
        var secret = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var expected = new byte[] { 0x20, 0x00 }.Concat("General"u8.ToArray().PaddedOrTruncated(32)).Concat(secret).ToArray();
        Assert.Equal(expected, PacketBuilder.SetChannel(0, "General", secret));
    }

    [Fact] public void StatsCore() => Assert.Equal(new byte[] { 0x38, 0x00 }, PacketBuilder.GetStatsCore());
    [Fact] public void StatsRadio() => Assert.Equal(new byte[] { 0x38, 0x01 }, PacketBuilder.GetStatsRadio());
    [Fact] public void StatsPackets() => Assert.Equal(new byte[] { 0x38, 0x02 }, PacketBuilder.GetStatsPackets());
    [Fact] public void SelfTelemetry() => Assert.Equal(new byte[] { 0x27, 0x00, 0x00, 0x00 }, PacketBuilder.GetSelfTelemetry());
    [Fact] public void ExportPrivateKey() => Assert.Equal(new byte[] { 0x17 }, PacketBuilder.ExportPrivateKey());
    [Fact] public void PathDiscovery() => Assert.Equal(new byte[] { 0x34, 0x00 }.Concat(Key32).ToArray(), PacketBuilder.SendPathDiscovery(Key32));
    [Fact] public void SendTrace() => Assert.Equal(new byte[] { 0x24, 0x39, 0x30, 0x00, 0x00, 0x32, 0x09, 0x01, 0x00, 0x00 }, PacketBuilder.SendTrace(12345, 67890, 0));

    [Fact]
    public void UpdateContactIs147Bytes()
    {
        var c = new MeshContact(Key32, ContactType.Chat, 1, ContactFlags.Favorite, 0xFF, [], "Alice", T, 1, 2, T);
        var frame = PacketBuilder.UpdateContact(c);
        Assert.Equal(147, frame.Length);
        Assert.Equal((byte)CommandCode.UpdateContact, frame[0]);
        Assert.Equal(0xFF, frame[35]);
    }

    [Fact]
    public void SetNameTruncatesAtGraphemeBoundary()
    {
        var name = new string('a', 30) + "é😀"; // 30 + 2 + 4 bytes
        var frame = PacketBuilder.SetName(name);
        Assert.True(frame.Length - 1 <= 31);
        Assert.Equal(new string('a', 30), System.Text.Encoding.UTF8.GetString(frame, 1, frame.Length - 1)[..30]);
    }

    [Fact]
    public void DefaultFloodScopeLayout()
    {
        var frame = PacketBuilder.SetDefaultFloodScope("region", new byte[16]);
        Assert.Equal(1 + 31 + 16, frame.Length);
        Assert.Equal(new byte[] { (byte)CommandCode.SetDefaultFloodScope }, PacketBuilder.SetDefaultFloodScope("", new byte[16]));
    }

    [Fact]
    public void AnonRequestReversesPath()
    {
        var frame = PacketBuilder.SendAnonReq(Key32, AnonRequestType.Regions, 2, [0xAA, 0xBB]);
        Assert.Equal(new byte[] { 0xBB, 0xAA }, frame[^2..]);
    }
}
