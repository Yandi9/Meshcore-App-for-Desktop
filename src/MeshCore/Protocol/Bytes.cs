using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MeshCore;

/// <summary>Binary helpers mirroring the Swift Data extensions (little-endian readers, hex, padding).</summary>
public static class Bytes
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ToHex(this ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();
    public static string ToHex(this byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
    public static string ToHexUpper(this byte[] data) => Convert.ToHexString(data);

    public static byte[]? FromHex(string? hex)
    {
        if (hex is null) return null;
        hex = hex.Trim();
        if (hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    public static uint ReadUInt32LE(this byte[] data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : 0;

    public static int ReadInt32LE(this byte[] data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) : 0;

    public static ushort ReadUInt16LE(this byte[] data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : (ushort)0;

    public static short ReadInt16LE(this byte[] data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset)) : (short)0;

    public static byte[] Slice(this byte[] data, int start, int length)
    {
        if (start >= data.Length || length <= 0) return [];
        length = Math.Min(length, data.Length - start);
        return data.AsSpan(start, length).ToArray();
    }

    public static byte[] From(this byte[] data, int start) =>
        start >= data.Length ? [] : data.AsSpan(start).ToArray();

    public static byte[] Prefix(this byte[] data, int count) =>
        count >= data.Length ? (byte[])data.Clone() : data.AsSpan(0, Math.Max(0, count)).ToArray();

    public static bool StartsWith(this byte[] data, byte[] prefix) =>
        prefix.Length <= data.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    public static bool SequenceEquals(this byte[]? a, byte[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.AsSpan().SequenceEqual(b);
    }

    public static byte[] PaddedOrTruncated(this byte[] data, int length)
    {
        if (length <= 0) return [];
        var result = new byte[length];
        Array.Copy(data, result, Math.Min(length, data.Length));
        return result;
    }

    /// <summary>Returns the longest prefix of <paramref name="s"/> whose UTF-8 encoding fits in <paramref name="maxBytes"/>,
    /// never splitting a grapheme cluster.</summary>
    public static string Utf8Prefix(this string s, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        var count = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            var n = Encoding.UTF8.GetByteCount(el);
            if (count + n > maxBytes) break;
            count += n;
            sb.Append(el);
        }
        return sb.ToString();
    }

    public static byte[] Utf8PaddedOrTruncated(this string s, int length) =>
        Encoding.UTF8.GetBytes(s.Utf8Prefix(length)).PaddedOrTruncated(length);

    public static int Utf8Length(this string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Decodes UTF-8, falling back to the longest valid prefix (firmware truncates byte-wise).</summary>
    public static string DecodeLongestValidUtf8Prefix(this byte[] data)
    {
        for (var len = data.Length; len >= 1; len--)
        {
            try { return StrictUtf8.GetString(data, 0, len); }
            catch (DecoderFallbackException) { }
        }
        return "";
    }

    public static string? TryDecodeUtf8(this byte[] data)
    {
        try { return StrictUtf8.GetString(data); }
        catch (DecoderFallbackException) { return null; }
    }

    public static string DecodeUtf8Lossy(this byte[] data) => Encoding.UTF8.GetString(data);

    /// <summary>Trims leading/trailing control characters (Swift's .controlCharacters).</summary>
    public static string TrimControl(this string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && char.IsControl(s[start])) start++;
        while (end > start && char.IsControl(s[end - 1])) end--;
        return s[start..end];
    }

    public static byte[] TrimAtNull(this byte[] data)
    {
        var idx = Array.IndexOf(data, (byte)0);
        return idx < 0 ? data : data.AsSpan(0, idx).ToArray();
    }

    public static double SnrFromByte(byte b) => (sbyte)b / 4.0;

    /// <summary>Clamps a date to firmware epoch seconds (UInt32).</summary>
    public static uint EpochSeconds32(DateTimeOffset date)
    {
        var seconds = date.ToUnixTimeMilliseconds() / 1000.0;
        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        if (seconds >= uint.MaxValue) return uint.MaxValue;
        return (uint)seconds;
    }

    public static DateTimeOffset FromEpoch(uint seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    public static uint NowEpoch() => EpochSeconds32(DateTimeOffset.UtcNow);
}

/// <summary>Growable byte buffer used by the packet builder.</summary>
public sealed class ByteWriter
{
    private readonly List<byte> _bytes = new(64);

    public ByteWriter(params byte[] initial) { _bytes.AddRange(initial); }

    public ByteWriter U8(byte v) { _bytes.Add(v); return this; }
    public ByteWriter I8(sbyte v) { _bytes.Add(unchecked((byte)v)); return this; }
    public ByteWriter U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _bytes.AddRange(b.ToArray()); return this; }
    public ByteWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _bytes.AddRange(b.ToArray()); return this; }
    public ByteWriter I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _bytes.AddRange(b.ToArray()); return this; }
    public ByteWriter Raw(ReadOnlySpan<byte> v) { _bytes.AddRange(v.ToArray()); return this; }
    public ByteWriter Utf8(string s) { _bytes.AddRange(Encoding.UTF8.GetBytes(s)); return this; }
    public int Count => _bytes.Count;
    public byte[] ToArray() => _bytes.ToArray();
}
