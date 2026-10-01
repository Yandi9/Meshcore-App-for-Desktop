namespace MeshCore;

public readonly record struct PathLenDecoded(int HashSize, int HopCount, int ByteLength);

/// <summary>Path length byte encoding: upper 2 bits = hash mode (0..2 → 1..3 bytes per hop), lower 6 bits = hop count.</summary>
public static class PathEncoding
{
    public const int MaxPathHashMode = 2;
    public const int MaxHopCount = 63;
    public const int MaxPathBytes = 64;
    public const byte FloodSentinel = 0xFF;

    public static PathLenDecoded? Decode(byte encoded)
    {
        var mode = encoded >> 6;
        if (mode >= 3) return null;
        var hashSize = mode + 1;
        var hops = encoded & 63;
        return new PathLenDecoded(hashSize, hops, hashSize * hops);
    }

    public static byte Encode(int hashSize, int hopCount)
    {
        if (hashSize is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(hashSize));
        var mode = (byte)(hashSize - 1);
        var hops = (byte)Math.Min(hopCount, MaxHopCount);
        return (byte)((mode << 6) | hops);
    }

    /// <summary>Splits raw path bytes into per-hop hex strings (e.g. "A1", "B2C3").</summary>
    public static IReadOnlyList<string> HopHexes(byte[] path, int hashSize)
    {
        if (hashSize < 1) hashSize = 1;
        var list = new List<string>();
        for (var i = 0; i < path.Length; i += hashSize)
            list.Add(Convert.ToHexString(path, i, Math.Min(hashSize, path.Length - i)));
        return list;
    }
}
