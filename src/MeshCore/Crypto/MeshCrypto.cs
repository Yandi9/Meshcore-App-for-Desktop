using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace MeshCore;

internal static class AesEcb
{
    public static byte[]? Decrypt(byte[] ciphertext, byte[] key)
    {
        if (key.Length < 16 || ciphertext.Length % 16 != 0) return null;
        using var aes = Aes.Create();
        aes.Key = key.Prefix(16);
        return aes.DecryptEcb(ciphertext, PaddingMode.None);
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var padded = new byte[(plaintext.Length + 15) / 16 * 16];
        Array.Copy(plaintext, padded, plaintext.Length);
        using var aes = Aes.Create();
        aes.Key = key.Prefix(16);
        return aes.EncryptEcb(padded, PaddingMode.None);
    }

    public static byte[] Mac(byte[] data, byte[] key, int size) => HMACSHA256.HashData(key, data).Prefix(size);
}

/// <summary>Group (channel) message decryption: [MAC(2)][AES-128-ECB ciphertext].</summary>
public static class ChannelCrypto
{
    public const int MacSize = 2;
    public const int KeySize = 16;

    public abstract record DecryptResult
    {
        public sealed record Success(uint Timestamp, byte TxtType, string Text) : DecryptResult;
        public sealed record HmacFailed : DecryptResult;
        public sealed record DecryptFailed : DecryptResult;
        public sealed record PayloadTooShort : DecryptResult;
    }

    public static DecryptResult Decrypt(byte[] payload, byte[] secret)
    {
        if (payload.Length < MacSize + 16) return new DecryptResult.PayloadTooShort();
        var mac = payload.Prefix(MacSize);
        var ct = payload.From(MacSize);
        if (!AesEcb.Mac(ct, secret, MacSize).AsSpan().SequenceEqual(mac)) return new DecryptResult.HmacFailed();
        var pt = AesEcb.Decrypt(ct, secret);
        if (pt is null || pt.Length < 5) return new DecryptResult.DecryptFailed();
        var ts = pt.ReadUInt32LE(0);
        var type = pt[4];
        var msg = pt.From(5).TrimAtNull();
        var text = msg.TryDecodeUtf8();
        return text is null ? new DecryptResult.DecryptFailed() : new DecryptResult.Success(ts, type, text);
    }

    /// <summary>Encrypts a group text body (used by tests and the radio simulator).</summary>
    public static byte[] Encrypt(uint timestamp, byte txtType, string text, byte[] secret)
    {
        var w = new ByteWriter().U32(timestamp).U8(txtType).Utf8(text);
        var ct = AesEcb.Encrypt(w.ToArray(), secret);
        return [.. AesEcb.Mac(ct, secret, MacSize), .. ct];
    }

    /// <summary>First byte of SHA-256(secret) identifies a channel on air.</summary>
    public static byte ChannelHash(byte[] secret) => SHA256.HashData(secret.Prefix(16))[0];
}

/// <summary>Direct message decryption: [destHash][srcHash][MAC(2)][ciphertext] with an X25519 shared secret.</summary>
public static class DirectMessageCrypto
{
    public const int MacSize = 2;
    public const int HeaderSize = 2;
    public const int MinPacketSize = HeaderSize + MacSize + 16;

    public abstract record DecryptResult
    {
        public sealed record Success(uint Timestamp, byte TypeAttempt, string? Text) : DecryptResult;
        public sealed record MacMismatch : DecryptResult;
        public sealed record DecryptionFailed : DecryptResult;
        public sealed record InvalidPayload : DecryptResult;
        public sealed record KeyError : DecryptResult;
    }

    public static byte[]? ComputeSharedSecret(byte[] myPrivateKey, byte[] theirX25519PublicKey)
    {
        if (myPrivateKey.Length < 32 || theirX25519PublicKey.Length != 32) return null;
        var secret = new byte[32];
        try
        {
            X25519.CalculateAgreement(myPrivateKey.Prefix(32), 0, theirX25519PublicKey, 0, secret, 0);
            return secret;
        }
        catch (Exception) { return null; }
    }

    public static DecryptResult Decrypt(byte[] payload, byte[] myPrivateKey, byte[] senderX25519PublicKey)
    {
        if (payload.Length < MinPacketSize) return new DecryptResult.InvalidPayload();
        var shared = ComputeSharedSecret(myPrivateKey, senderX25519PublicKey);
        if (shared is null) return new DecryptResult.KeyError();
        var mac = payload.Slice(HeaderSize, MacSize);
        var ct = payload.From(HeaderSize + MacSize);
        if (!AesEcb.Mac(ct, shared, MacSize).AsSpan().SequenceEqual(mac)) return new DecryptResult.MacMismatch();
        var pt = AesEcb.Decrypt(ct, shared);
        if (pt is null || pt.Length < 5) return new DecryptResult.DecryptionFailed();
        var ts = pt.ReadUInt32LE(0);
        var ta = pt[4];
        var body = pt.From(5).TrimAtNull();
        return new DecryptResult.Success(ts, ta, body.Length == 0 ? "" : body.TryDecodeUtf8());
    }

    public static uint? ExtractTimestamp(byte[] payload, byte[] myPrivateKey, byte[] senderX25519PublicKey) =>
        Decrypt(payload, myPrivateKey, senderX25519PublicKey) is DecryptResult.Success s ? s.Timestamp : null;

    public static byte[] Encrypt(byte destHash, byte srcHash, uint timestamp, byte typeAttempt, string text, byte[] sharedSecret)
    {
        var w = new ByteWriter().U32(timestamp).U8(typeAttempt).Utf8(text);
        var ct = AesEcb.Encrypt(w.ToArray(), sharedSecret);
        return [destHash, srcHash, .. AesEcb.Mac(ct, sharedSecret, MacSize), .. ct];
    }
}

/// <summary>Converts Ed25519 public keys to X25519 (Montgomery u = (1+y)/(1-y) mod p).</summary>
public static class Ed25519ToX25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;

    public static byte[]? ConvertPublicKey(byte[] ed25519PublicKey)
    {
        if (ed25519PublicKey.Length != 32) return null;
        var y = (byte[])ed25519PublicKey.Clone();
        y[31] &= 0x7F;
        var yi = new BigInteger(y, isUnsigned: true, isBigEndian: false) % P;
        var num = (1 + yi) % P;
        var den = ((1 - yi) % P + P) % P;
        if (den.IsZero) return new byte[32];
        var inv = BigInteger.ModPow(den, P - 2, P);
        var u = num * inv % P;
        var bytes = u.ToByteArray(isUnsigned: true, isBigEndian: false);
        return bytes.PaddedOrTruncated(32);
    }
}

public abstract record RegionMatchResult
{
    public sealed record None : RegionMatchResult;
    public sealed record Unique(string Name) : RegionMatchResult;
    public sealed record Ambiguous(IReadOnlyList<string> Names) : RegionMatchResult;
}

/// <summary>Resolves the region scope of transport-coded packets.</summary>
public static class TransportCodeRegionResolver
{
    public static byte[]? DeriveScopeKey(string regionName)
    {
        var t = regionName.Trim();
        if (t.Length == 0 || t[0] == '$') return null;
        var normalized = t[0] == '#' ? t : "#" + t;
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).Prefix(16);
    }

    public static ushort CalcTransportCode(byte[] scopeKey, byte payloadTypeBits, byte[] payload)
    {
        var combined = new byte[1 + payload.Length];
        combined[0] = (byte)(payloadTypeBits & 0x0F);
        Array.Copy(payload, 0, combined, 1, payload.Length);
        var mac = HMACSHA256.HashData(scopeKey, combined);
        return RewriteReserved((ushort)(mac[0] | (mac[1] << 8)));
    }

    private static ushort RewriteReserved(ushort raw) => raw == 0 ? (ushort)1 : raw == 0xFFFF ? (ushort)0xFFFE : raw;

    internal static ushort CalcTransportCodeRewriteForTest(ushort raw) => RewriteReserved(raw);

    public static RegionMatchResult MatchRegions(IEnumerable<(string Name, byte[] Key)> scopeKeys, ushort expectedCode0, byte payloadTypeBits, byte[] payload)
    {
        var matched = new HashSet<string>();
        foreach (var (name, key) in scopeKeys)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            if (CalcTransportCode(key, payloadTypeBits, payload) == expectedCode0) matched.Add(trimmed);
        }
        var sorted = matched.OrderBy(n => n, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase)).ToList();
        return sorted.Count switch
        {
            0 => new RegionMatchResult.None(),
            1 => new RegionMatchResult.Unique(sorted[0]),
            _ => new RegionMatchResult.Ambiguous(sorted),
        };
    }
}
