using System.Security.Cryptography;
using MeshCore;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Xunit;

namespace MeshCore.Tests;

public class CryptoTests
{
    /// <summary>Simulates MeshCore firmware identity: Ed25519 seed → (expanded 64-byte private key, public key).</summary>
    internal static (byte[] Private64, byte[] Public) NewIdentity()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var pub = new byte[32];
        Ed25519.GeneratePublicKey(seed, 0, pub, 0);
        var h = SHA512.HashData(seed);
        h[0] &= 248; h[31] &= 63; h[31] |= 64;
        return (h, pub);
    }

    [Fact]
    public void ChannelRoundTrip()
    {
        var secret = ChannelSecrets.HashSecret("#test");
        var ct = ChannelCrypto.Encrypt(1_700_000_000, 0, "Alice: hello mesh", secret);
        var r = Assert.IsType<ChannelCrypto.DecryptResult.Success>(ChannelCrypto.Decrypt(ct, secret));
        Assert.Equal(1_700_000_000u, r.Timestamp);
        Assert.Equal("Alice: hello mesh", r.Text);
        Assert.IsType<ChannelCrypto.DecryptResult.HmacFailed>(ChannelCrypto.Decrypt(ct, ChannelSecrets.HashSecret("#other")));
    }

    [Fact]
    public void PublicChannelSecretMatchesKnownValue() =>
        Assert.Equal("8b3387e9c5cdea6ac9e5edbaa115cd72", ChannelSecrets.PublicChannelSecret.ToHex());

    [Fact]
    public void Ed25519ToX25519AgreementIsSymmetric()
    {
        var a = NewIdentity();
        var b = NewIdentity();
        var ax = Ed25519ToX25519.ConvertPublicKey(a.Public)!;
        var bx = Ed25519ToX25519.ConvertPublicKey(b.Public)!;
        var s1 = DirectMessageCrypto.ComputeSharedSecret(a.Private64, bx)!;
        var s2 = DirectMessageCrypto.ComputeSharedSecret(b.Private64, ax)!;
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void DirectMessageDecrypts()
    {
        var sender = NewIdentity();
        var me = NewIdentity();
        var shared = DirectMessageCrypto.ComputeSharedSecret(sender.Private64, Ed25519ToX25519.ConvertPublicKey(me.Public)!)!;
        var packet = DirectMessageCrypto.Encrypt(me.Public[0], sender.Public[0], 1_700_000_123, 0, "secret hi", shared);
        var r = Assert.IsType<DirectMessageCrypto.DecryptResult.Success>(
            DirectMessageCrypto.Decrypt(packet, me.Private64, Ed25519ToX25519.ConvertPublicKey(sender.Public)!));
        Assert.Equal("secret hi", r.Text);
        Assert.Equal(1_700_000_123u, r.Timestamp);
    }

    [Fact]
    public void TransportCodeNeverReserved()
    {
        Assert.Equal((ushort)1, TransportCodeRegionResolver.CalcTransportCodeRewriteForTest(0));
        Assert.Equal((ushort)0xFFFE, TransportCodeRegionResolver.CalcTransportCodeRewriteForTest(0xFFFF));
    }

    [Fact]
    public void RegionMatchUnique()
    {
        var key = TransportCodeRegionResolver.DeriveScopeKey("usa")!;
        var payload = new byte[] { 1, 2, 3 };
        var code = TransportCodeRegionResolver.CalcTransportCode(key, 5, payload);
        var r = TransportCodeRegionResolver.MatchRegions([("usa", key), ("eu", TransportCodeRegionResolver.DeriveScopeKey("eu")!)], code, 5, payload);
        Assert.Equal("usa", Assert.IsType<RegionMatchResult.Unique>(r).Name);
        Assert.Null(TransportCodeRegionResolver.DeriveScopeKey("$private"));
        Assert.Equal(TransportCodeRegionResolver.DeriveScopeKey("#usa"), key);
    }
}
