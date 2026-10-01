using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeshCore;

namespace MC1.Core.Utilities;

public sealed record ParsedReaction(string Emoji, string TargetSender, string MessageHash);
public sealed record ParsedDmReaction(string Emoji, string MessageHash);

public static class EmojiDetector
{
    private static readonly (int Lo, int Hi)[] HighRanges =
    [
        (0x2390, 0x23FF), (0x24C2, 0x24C2), (0x25AA, 0x25FE), (0x2600, 0x27BF), (0x2934, 0x2935),
        (0x2B05, 0x2B55), (0x3030, 0x3030), (0x303D, 0x303D), (0x3297, 0x3297), (0x3299, 0x3299),
        (0x1F000, 0x1FAFF),
    ];

    private static readonly (int Lo, int Hi)[] LowEmoji =
    [
        (0x23, 0x23), (0x2A, 0x2A), (0x30, 0x39), (0xA9, 0xA9), (0xAE, 0xAE), (0x203C, 0x203C), (0x2049, 0x2049),
        (0x2122, 0x2122), (0x2139, 0x2139), (0x2194, 0x2199), (0x21A9, 0x21AA), (0x231A, 0x231B), (0x2328, 0x2328),
    ];

    /// <summary>Mirrors the Swift Character.isEmoji extension used for reaction validation.</summary>
    public static bool IsEmojiGrapheme(string grapheme)
    {
        if (string.IsNullOrEmpty(grapheme)) return false;
        var runes = grapheme.EnumerateRunes().ToList();
        var first = runes[0].Value;
        if (HighRanges.Any(r => first >= r.Lo && first <= r.Hi)) return true;
        return runes.Count > 1 && LowEmoji.Any(r => first >= r.Lo && first <= r.Hi);
    }

    /// <summary>True when <paramref name="value"/> is exactly one emoji grapheme cluster.</summary>
    public static bool IsSingleEmoji(string value)
    {
        var info = new StringInfo(value);
        return info.LengthInTextElements == 1 && IsEmojiGrapheme(value);
    }
}

/// <summary>Reaction wire formats (see docs/Reactions.md in the MeshCore One repo).</summary>
public static class ReactionParser
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private static readonly sbyte[] DecodeTable = BuildTable();

    private static sbyte[] BuildTable()
    {
        var t = Enumerable.Repeat((sbyte)-1, 128).ToArray();
        for (var i = 0; i < Alphabet.Length; i++)
        {
            t[Alphabet[i]] = (sbyte)i;
            t[char.ToUpperInvariant(Alphabet[i])] = (sbyte)i;
        }
        t['O'] = 0; t['o'] = 0; t['I'] = 1; t['i'] = 1; t['L'] = 1; t['l'] = 1;
        return t;
    }

    private static string EncodeCrockford(ReadOnlySpan<byte> five)
    {
        ulong bits = 0;
        foreach (var b in five) bits = (bits << 8) | b;
        var sb = new StringBuilder(8);
        for (var shift = 35; shift >= 0; shift -= 5) sb.Append(Alphabet[(int)((bits >> shift) & 0x1F)]);
        return sb.ToString();
    }

    private static bool IsValidCrockford(string s) => s.All(c => c < 128 && DecodeTable[c] >= 0);

    private static string NormalizeCrockford(string s) => new(s.Where(c => c < 128 && DecodeTable[c] >= 0).Select(c => Alphabet[DecodeTable[c]]).ToArray());

    /// <summary>SHA-256(UTF-8 text ‖ LE u32 sender timestamp)[0..5] as 8 Crockford base32 chars.</summary>
    public static string GenerateMessageHash(string text, uint timestamp)
    {
        var w = new ByteWriter().Utf8(text).U32(timestamp);
        return EncodeCrockford(SHA256.HashData(w.ToArray()).AsSpan(0, 5));
    }

    public static string BuildChannelReaction(string emoji, string targetSender, string targetText, uint targetTimestamp) =>
        $"@[{targetSender}]{emoji}\n{GenerateMessageHash(targetText, targetTimestamp)}";

    public static string BuildDmReaction(string emoji, string targetText, uint targetTimestamp) =>
        $"{emoji}\n{GenerateMessageHash(targetText, targetTimestamp)}";

    public static bool IsReactionText(string text, bool isDm) =>
        MeshCoreOpenReactionParser.Parse(text) is not null || MeshCoreOpenReactionParser.ParseV1(text) is not null ||
        (isDm ? ParseDm(text) is not null : Parse(text) is not null);

    public static ParsedReaction? Parse(string text)
    {
        var nl = text.LastIndexOf('\n');
        if (nl < 0) return null;
        var rawHash = text[(nl + 1)..];
        if (rawHash.Length != 8 || !IsValidCrockford(rawHash)) return null;
        var hash = NormalizeCrockford(rawHash);
        var body = text[..nl];
        var at = body.IndexOf("@[", StringComparison.Ordinal);
        if (at < 0) return null;
        var before = body[..at];
        var after = body[(at + 2)..];
        string emoji, sender;
        if (before.Length == 0)
        {
            var close = after.IndexOf(']');
            if (close < 0) return null;
            sender = after[..close];
            emoji = after[(close + 1)..];
        }
        else
        {
            emoji = before;
            if (!after.EndsWith(']')) return null;
            sender = after[..^1];
        }
        if (!EmojiDetector.IsSingleEmoji(emoji) || sender.Length == 0) return null;
        return new ParsedReaction(emoji, sender, hash);
    }

    public static ParsedDmReaction? ParseDm(string text)
    {
        if (text.Contains("@[", StringComparison.Ordinal)) return null;
        var nl = text.LastIndexOf('\n');
        if (nl < 0) return null;
        var rawHash = text[(nl + 1)..];
        if (rawHash.Length != 8 || !IsValidCrockford(rawHash)) return null;
        var emoji = text[..nl];
        return EmojiDetector.IsSingleEmoji(emoji) ? new ParsedDmReaction(emoji, NormalizeCrockford(rawHash)) : null;
    }

    public static string BuildSummary(IEnumerable<(string Emoji, long ReceivedAt)> reactions) =>
        string.Join(",", reactions.GroupBy(r => r.Emoji)
            .Select(g => (Emoji: g.Key, Count: g.Count(), Earliest: g.Min(x => x.ReceivedAt)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Earliest)
            .Select(x => $"{x.Emoji}:{x.Count}"));

    public static IReadOnlyList<(string Emoji, int Count)> ParseSummary(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return [];
        var list = new List<(string, int)>();
        foreach (var part in summary.Split(','))
        {
            var idx = part.LastIndexOf(':');
            if (idx <= 0) continue;
            if (int.TryParse(part[(idx + 1)..], out var n)) list.Add((part[..idx], n));
        }
        return list;
    }
}

/// <summary>Interop with the MeshCore Open (Flutter) client's reaction formats.</summary>
public static class MeshCoreOpenReactionParser
{
    public sealed record Mco(string Emoji, string DartHash);
    public sealed record McoV1(string Emoji, uint TimestampSeconds, uint SenderNameHash, uint TextHash);

    public static Mco? Parse(string text)
    {
        if (text.Length != 9 || !text.StartsWith("r:") || text[6] != ':') return null;
        var hash = text[2..6];
        var idx = text[7..];
        if (!IsLowerHex(hash) || !IsLowerHex(idx)) return null;
        var i = Convert.ToInt32(idx, 16);
        return i < EmojiTable.Length ? new Mco(EmojiTable[i], hash) : null;
    }

    public static McoV1? ParseV1(string text)
    {
        if (!text.StartsWith("r:")) return null;
        var last = text.LastIndexOf(':');
        if (last <= 2) return null;
        var id = text[2..last];
        var emoji = text[(last + 1)..];
        if (emoji.Length == 0) return null;
        var parts = id.Split('_');
        if (parts.Length != 3) return null;
        if (!ulong.TryParse(parts[0], out var ms) || !uint.TryParse(parts[1], out var sh) || !uint.TryParse(parts[2], out var th)) return null;
        var secs = ms / 1000;
        return secs > uint.MaxValue ? null : new McoV1(emoji, (uint)secs, sh, th);
    }

    public static uint DartStringHash(IEnumerable<char> codeUnits)
    {
        uint hash = 0;
        foreach (var u in codeUnits)
        {
            unchecked
            {
                hash += u;
                hash += hash << 10;
                hash ^= hash >> 6;
            }
        }
        unchecked
        {
            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
        }
        hash &= (1u << 30) - 1;
        return hash == 0 ? 1 : hash;
    }

    public static string ComputeReactionHash(uint timestamp, string? senderName, string text)
    {
        var units = new List<char>();
        units.AddRange(timestamp.ToString(CultureInfo.InvariantCulture));
        if (senderName is not null) units.AddRange(senderName);
        units.AddRange(text.Take(5));
        return (DartStringHash(units) & 0xFFFF).ToString("x4");
    }

    private static bool IsLowerHex(string s) => s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static readonly string[] EmojiTable =
    [
        "👍", "❤️", "😂", "🎉", "👏", "🔥",
        "😀", "😃", "😄", "😁", "😅", "😂", "🤣", "😊",
        "😇", "🙂", "🙃", "😉", "😌", "😍", "🥰", "😘",
        "😗", "😙", "😚", "😋", "😛", "😝", "😜", "🤪",
        "🤨", "🧐", "🤓", "😎", "🥸", "🤩", "🥳", "😏",
        "😒", "😞", "😔", "😟", "😕", "🙁", "😣", "😖",
        "😫", "😩", "🥺", "😢", "😭", "😤", "😠", "😡",
        "🤬", "🤯", "😳", "🥵", "🥶", "😱", "😨", "😰",
        "😥", "😓", "🤗", "🤔", "🤭", "🤫", "🤥", "😶",
        "👍", "👎", "👊", "✊", "🤛", "🤜", "🤞", "✌️",
        "🤟", "🤘", "👌", "🤌", "🤏", "👈", "👉", "👆",
        "👇", "☝️", "👋", "🤚", "🖐️", "✋", "🖖", "👏",
        "🙌", "👐", "🤲", "🤝", "🙏", "✍️", "💅", "🤳",
        "💪",
        "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍",
        "🤎", "💔", "❤️‍🔥", "❤️‍🩹", "💕", "💞", "💓", "💗",
        "💖", "💘", "💝", "💟", "💌", "💢", "💥", "💫",
        "💦", "💨", "🕳️", "💬", "👁️‍🗨️", "🗨️", "🗯️", "💭",
        "🎉", "🎊", "🎈", "🎁", "🎀", "🪅", "🪆", "🏆",
        "🥇", "🥈", "🥉", "⚽", "⚾", "🥎", "🏀", "🏐",
        "🏈", "🏉", "🎾", "🥏", "🎳", "🏏", "🏑", "🏒",
        "🥍", "🏓", "🏸", "🥊", "🥋", "🥅", "⛳", "🔥",
        "⭐", "🌟", "✨", "⚡", "💡", "🔦", "🏮", "🪔",
        "📱", "💻", "⌚", "📷", "📺", "📻", "🎵", "🎶",
        "🚀",
    ];
}
