using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeshCore;

namespace MC1.Core.Utilities;

/// <summary>"@[Name]" mentions (MeshCore One wire format).</summary>
public static partial class MentionUtilities
{
    [GeneratedRegex(@"@\[([^\]]+)\]")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"^@\[[^\]]+\]\s*")]
    private static partial Regex LeadingMentionRegex();

    public static string CreateMention(string name) => $"@[{name}]";

    public static string AppendMention(string name, string draft)
    {
        var mention = CreateMention(name);
        if (string.IsNullOrEmpty(draft)) return mention + " ";
        var sep = char.IsWhiteSpace(draft[^1]) ? "" : " ";
        return draft + sep + mention + " ";
    }

    public static IReadOnlyList<string> ExtractMentions(string text) =>
        MentionRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

    public static bool ContainsSelfMention(string text, string selfName) =>
        !string.IsNullOrEmpty(selfName) && ExtractMentions(text).Any(m => string.Equals(m, selfName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the partial "@query" being typed at the end of <paramref name="text"/>, or null.</summary>
    public static string? DetectActiveMention(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var searchEnd = text.Length;
        while (searchEnd > 0)
        {
            var at = text.LastIndexOf('@', searchEnd - 1);
            if (at < 0) return null;
            var atStart = at == 0;
            var afterWs = !atStart && char.IsWhiteSpace(text[at - 1]);
            if (!atStart && !afterWs) { searchEnd = at; continue; }
            var after = text[(at + 1)..];
            if (after.Length == 0) return "";
            if (char.IsWhiteSpace(after[0]) || after[0] == '@') return null;
            if (after[0] == '[')
            {
                var close = after.IndexOf(']');
                if (close < 0) return null;
                if (close == after.Length - 1) return null;
                searchEnd = at;
                continue;
            }
            var end = 0;
            while (end < after.Length && !char.IsWhiteSpace(after[end])) end++;
            return after[..end];
        }
        return null;
    }

    public static string BuildReplyText(string mentionName, string messageText)
    {
        var m = LeadingMentionRegex().Match(messageText);
        var source = m.Success ? messageText[m.Length..] : messageText;
        var info = new StringInfo(source);
        var preview = info.LengthInTextElements > 10 ? info.SubstringByTextElements(0, 10) : source;
        var suffix = info.LengthInTextElements > 10 ? ".." : "";
        return $"{CreateMention(mentionName)}\n>{preview}{suffix}\n";
    }
}

public static partial class HashtagUtilities
{
    [GeneratedRegex("#[A-Za-z0-9][A-Za-z0-9-]*")]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static IReadOnlyList<(string Name, int Index, int Length)> ExtractHashtags(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var urls = UrlRegex().Matches(text).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var list = new List<(string, int, int)>();
        foreach (Match m in HashtagRegex().Matches(text))
        {
            if (urls.Any(u => m.Index >= u.Index && m.Index + m.Length <= u.End)) continue;
            list.Add((m.Value, m.Index, m.Length));
        }
        return list;
    }

    private static bool Allowed(char c, bool allowHyphen) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' || (allowHyphen && c == '-');

    public static bool IsValidHashtagName(string name) =>
        name.Length > 0 && Allowed(name[0], false) && name.All(c => Allowed(c, true));

    public static string SanitizeHashtagNameInput(string input)
    {
        var sb = new StringBuilder();
        foreach (var c in input.ToLowerInvariant()) if (Allowed(c, true)) sb.Append(c);
        return sb.ToString().TrimStart('-');
    }

    public static string NormalizeHashtagName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith('#') ? n[1..] : n;
    }

    /// <summary>Channel name and secret for a public hashtag channel.</summary>
    public static (string Name, byte[] Secret) HashtagChannel(string tag)
    {
        var name = "#" + NormalizeHashtagName(tag);
        return (name, ChannelSecrets.HashSecret(name));
    }
}

public static class ChannelMessageFormat
{
    /// <summary>Splits "Sender: text" as sent on group channels.</summary>
    public static (string? Sender, string Text) Parse(string text)
    {
        var idx = text.IndexOf(':');
        if (idx <= 0) return (null, text);
        return (text[..idx].Trim(), text[(idx + 1)..].Trim());
    }
}

public static class DeduplicationKey
{
    public static string ContentBased(string? contactId, int? channelIndex, string? senderName, long timestamp, string content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)), 0, 4);
        if (channelIndex is { } idx) return $"ch-{idx}-{timestamp}-{senderName ?? ""}-{hash}";
        return $"dm-{contactId ?? "unknown"}-{timestamp}-{hash}";
    }
}

public static class AckCodeBuilder
{
    /// <summary>Predicts the firmware's expected-ACK code: SHA-256(ts LE ‖ attempt&amp;3 ‖ text ‖ senderPubKey)[0..4].</summary>
    public static byte[] ExpectedAck(uint timestamp, byte attempt, string text, byte[] senderPublicKey)
    {
        var w = new ByteWriter().U32(timestamp).U8((byte)(attempt & 0x03)).Utf8(text).Raw(senderPublicKey);
        return SHA256.HashData(w.ToArray()).Prefix(4);
    }
}

/// <summary>Contact share tokens embedded in chat: &lt;PUBKEYHEX:type:name&gt;.</summary>
public static partial class ContactShareUtilities
{
    [GeneratedRegex(@"<[0-9a-fA-F]{64}:\d+:[^>]+>")]
    private static partial Regex TokenRegex();

    public static string FormatShare(byte[] publicKey, ContactType type, string name) =>
        $"<{Convert.ToHexString(publicKey)}:{(byte)type}:{name.Replace(">", "")}>";

    public sealed record SharedContact(string Name, byte[] PublicKey, ContactType Type);

    /// <summary>Where the share tokens are in a text.</summary>
    public static IEnumerable<(int Index, int Length, string Token)> FindTokens(string text)
    {
        foreach (Match m in TokenRegex().Matches(text)) yield return (m.Index, m.Length, m.Value);
    }

    public static IReadOnlyList<SharedContact> ExtractShares(string text) =>
        TokenRegex().Matches(text).Select(m => Parse(m.Value)).OfType<SharedContact>().ToList();

    public static SharedContact? Parse(string token)
    {
        var m = TokenRegex().Match(token);
        if (!m.Success) return null;
        var inner = m.Value[1..^1];
        var parts = inner.Split(':', 3);
        if (parts.Length != 3) return null;
        var key = Bytes.FromHex(parts[0]);
        if (key is not { Length: 32 }) return null;
        if (!byte.TryParse(parts[1], out var t) || !Enum.IsDefined(typeof(ContactType), t)) return null;
        if (parts[2].Length == 0) return null;
        return new SharedContact(parts[2], key, (ContactType)t);
    }
}

/// <summary>meshcore:// deep links (channel/add, contact/add, map).</summary>
public static class MeshCoreUrl
{
    public sealed record ChannelLink(string Name, byte[] Secret, string? RegionScope)
    {
        public bool HasHashtagSecretMismatch
        {
            get
            {
                if (!Name.StartsWith('#')) return false;
                var body = Name[1..];
                return HashtagUtilities.IsValidHashtagName(body) && !Secret.AsSpan().SequenceEqual(ChannelSecrets.HashSecret("#" + body.ToLowerInvariant()));
            }
        }
    }

    public sealed record ContactLink(string Name, byte[] PublicKey, ContactType Type);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var k = idx < 0 ? part : part[..idx];
            var v = idx < 0 ? "" : part[(idx + 1)..];
            d.TryAdd(Uri.UnescapeDataString(k), FormDecode(v));
        }
        return d;
    }

    /// <summary>Raw '+' is a space; %2B is a plus.</summary>
    private static string FormDecode(string v) => Uri.UnescapeDataString(v.Replace("+", " "));

    private static string FormEncode(string v) => Uri.EscapeDataString(v);

    private static bool TrySplit(string url, string host, string path, out Dictionary<string, string> query)
    {
        query = [];
        if (!url.StartsWith("meshcore://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = url["meshcore://".Length..];
        var q = rest.IndexOf('?');
        var hp = q < 0 ? rest : rest[..q];
        if (!string.Equals(hp.TrimEnd('/'), host + path, StringComparison.OrdinalIgnoreCase)) return false;
        query = ParseQuery(q < 0 ? "" : rest[(q + 1)..]);
        return true;
    }

    public static ChannelLink? ParseChannel(string url)
    {
        if (!TrySplit(url, "channel", "/add", out var q)) return null;
        var name = q.GetValueOrDefault("name") ?? "";
        if (name.Length == 0) return null;
        var region = NormalizeRegion(q.GetValueOrDefault("region_scope"));
        if (q.TryGetValue("secret", out var secretHex) && secretHex.Length > 0)
        {
            var secret = Bytes.FromHex(secretHex);
            return secret is { Length: 16 } ? new ChannelLink(name, secret, region) : null;
        }
        if (!name.StartsWith('#')) return null;
        var body = name[1..];
        if (!HashtagUtilities.IsValidHashtagName(body)) return null;
        var normalized = "#" + body.ToLowerInvariant();
        return new ChannelLink(normalized, ChannelSecrets.HashSecret(normalized), region);
    }

    public static ContactLink? ParseContact(string url)
    {
        if (!TrySplit(url, "contact", "/add", out var q)) return null;
        var name = q.GetValueOrDefault("name") ?? "";
        var key = Bytes.FromHex(q.GetValueOrDefault("public_key"));
        if (name.Length == 0 || key is not { Length: 32 }) return null;
        var type = int.TryParse(q.GetValueOrDefault("type"), out var t) && Enum.IsDefined(typeof(ContactType), (byte)t) ? (ContactType)(byte)t : ContactType.Chat;
        return new ContactLink(name, key, type);
    }

    public static (double Lat, double Lon)? ParseMap(string url)
    {
        if (!TrySplit(url, "map", "", out var q)) return null;
        if (!double.TryParse(q.GetValueOrDefault("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
        if (!double.TryParse(q.GetValueOrDefault("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;
        if (lat is < -90 or > 90 || lon is < -180 or > 180) return null;
        return (lat, lon);
    }

    public static string ChannelUri(string name, byte[] secret, string? regionScope = null)
    {
        var s = $"meshcore://channel/add?name={FormEncode(name)}&secret={Convert.ToHexString(secret)}";
        if (!string.IsNullOrEmpty(regionScope)) s += "&region_scope=" + FormEncode(regionScope);
        return s;
    }

    public static string ContactUri(string name, byte[] publicKey, ContactType type) =>
        $"meshcore://contact/add?name={FormEncode(name)}&public_key={Convert.ToHexString(publicKey)}&type={(byte)type}";

    public static string MapUri(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"meshcore://map?lat={lat:0.000000}&lon={lon:0.000000}");

    public static string? NormalizeRegion(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        if (t.Length == 0) return null;
        var capped = t.Utf8Prefix(30);
        return capped.Length == 0 ? null : capped;
    }
}

public static partial class LinkDetector
{
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"meshcore://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex MeshCoreRegex();

    [GeneratedRegex(@"(?<![\d.])(-?\d{1,2}\.\d{3,}),\s*(-?\d{1,3}\.\d{3,})(?![\d.])")]
    private static partial Regex CoordinateRegex();

    public sealed record Token(string Text, TokenKind Kind, string? Target = null);
    public enum TokenKind { Text, Url, MeshCoreLink, Mention, Hashtag, Coordinate, Contact }

    /// <summary>Splits message text into styled runs (links, mentions, hashtags, coordinates).</summary>
    public static IReadOnlyList<Token> Tokenize(string text)
    {
        var spans = new List<(int Start, int Length, TokenKind Kind, string? Target)>();
        void Add(int s, int l, TokenKind k, string? t)
        {
            if (spans.Any(x => s < x.Start + x.Length && x.Start < s + l)) return;
            spans.Add((s, l, k, t));
        }
        // Shared contacts (<PUBKEY:type:name>, from "Share contact" / "Share my info"): shown as the contact's name.
        foreach (var (index, length, raw) in ContactShareUtilities.FindTokens(text)) Add(index, length, TokenKind.Contact, raw);
        foreach (Match m in MeshCoreRegex().Matches(text)) Add(m.Index, m.Length, TokenKind.MeshCoreLink, m.Value);
        foreach (Match m in UrlRegex().Matches(text))
        {
            var v = m.Value.TrimEnd('.', ',', ')', '!', '?', ';', ':');
            Add(m.Index, v.Length, TokenKind.Url, v.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + v : v);
        }
        foreach (Match m in Regex.Matches(text, @"@\[([^\]]+)\]")) Add(m.Index, m.Length, TokenKind.Mention, m.Groups[1].Value);
        foreach (var h in HashtagUtilities.ExtractHashtags(text)) Add(h.Index, h.Length, TokenKind.Hashtag, h.Name);
        foreach (Match m in CoordinateRegex().Matches(text))
        {
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
                Add(m.Index, m.Length, TokenKind.Coordinate, MeshCoreUrl.MapUri(lat, lon));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new List<Token>();
        var pos = 0;
        foreach (var s in spans)
        {
            if (s.Start > pos) result.Add(new Token(text[pos..s.Start], TokenKind.Text));
            var shown = text.Substring(s.Start, s.Length);
            if (s.Kind == TokenKind.Contact && ContactShareUtilities.Parse(shown) is { } shared) shown = L.F("Contact: {0}", shared.Name);
            result.Add(new Token(shown, s.Kind, s.Target));
            pos = s.Start + s.Length;
        }
        if (pos < text.Length) result.Add(new Token(text[pos..], TokenKind.Text));
        return result;
    }

    public static IReadOnlyList<string> Urls(string text) =>
        Tokenize(text).Where(t => t.Kind == TokenKind.Url).Select(t => t.Target!).ToList();

    private static readonly HashSet<string> ImageExtensions = ["jpg", "jpeg", "png", "gif", "webp", "bmp"];

    public static bool IsImageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var ext = System.IO.Path.GetExtension(u.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return ImageExtensions.Contains(ext) || ResolveGiphy(u) is not null;
    }

    public static string DirectImageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && ResolveGiphy(u) is { } g ? g : url;

    private static string? ResolveGiphy(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        if (host is not ("giphy.com" or "www.giphy.com")) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2) return null;
        var section = parts[0].ToLowerInvariant();
        if (section is not ("gifs" or "embed")) return null;
        var id = section == "gifs" ? parts[1].Split('-').Last() : parts[1];
        return id.Length == 0 ? null : $"https://i.giphy.com/media/{id}/giphy.gif";
    }
}
