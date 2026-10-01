using System.Text.RegularExpressions;

namespace MC1.Windows.Services;

/// <summary>Recognises the share link meshpic.org generates for an uploaded picture.</summary>
public static partial class MeshPicLinks
{
    public const string SiteUrl = "https://meshpic.org/";

    [GeneratedRegex(@"(?<![\w.])(?:https?://)?(?:www\.)?meshpic\.org/[^\s""'<>()\[\]{}\u2026]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    // Site pages that aren't a picture: language versions, legal pages, assets, API endpoints.
    [GeneratedRegex(@"^/(?:[a-z]{2}(?:[-_][a-zA-Z]{2,4})?/?|(?:static|assets|api|css|js|fonts|img|images|icons|_next|cdn-cgi|lang|locale|privacy|terms|tos|about|faq|help|contact|legal|cookies?|upload|login|logout|settings)(?:/.*)?|favicon[^/]*|robots\.txt|sitemap[^/]*|manifest[^/]*|sw\.js)$", RegexOptions.IgnoreCase)]
    private static partial Regex SitePageRegex();

    private static readonly string[] NotShareable = ["delete", "remove", "manage", "admin", "token=", "secret", "key=", "csrf", "lang="];

    /// <summary>
    /// All meshpic.org links in a piece of text, complete with https:// (trailing punctuation removed). Links shown
    /// shortened ("meshpic.org/ab…") are skipped, since they aren't the whole link.
    /// </summary>
    public static IEnumerable<string> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in LinkRegex().Matches(text))
        {
            var end = m.Index + m.Length;
            if (end < text.Length && text[end] == '\u2026') continue;
            var value = m.Value;
            if (value.EndsWith("...", StringComparison.Ordinal)) continue;
            yield return Normalize(value.TrimEnd('.', ',', ';', ':', '!', '?'));
        }
    }

    /// <summary>The full link: always starting with https://.</summary>
    public static string Normalize(string link)
    {
        link = link.Trim();
        if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "https://" + link[8..];
        if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + link[7..];
        if (link.StartsWith("//", StringComparison.Ordinal)) return "https:" + link;
        return "https://" + link;
    }

    /// <summary>True for a link that points at an uploaded picture (not the home page or another page of the site).</summary>
    public static bool IsPictureLink(string url)
    {
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("meshpic.org", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.meshpic.org", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.AbsolutePath;
        if (path.Length <= 1 || SitePageRegex().IsMatch(path)) return false;
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = url.ToLowerInvariant();
        if (NotShareable.Any(lower.Contains)) return false;
        // A picture id has at least a few characters.
        return path.Trim('/').Replace("/", "").Length >= 3;
    }

    /// <summary>Text to insert into a message at the caret: the link with a space on either side where needed.</summary>
    public static (string Text, int Caret) InsertAt(string current, int selectionStart, int selectionEnd, string insert, bool? spaced = null)
    {
        current ??= "";
        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, current.Length);
        var before = current[..start];
        var after = current[end..];
        var isLink = spaced ?? insert.Contains("://");
        var ins = insert;
        if (isLink && before.Length > 0 && !char.IsWhiteSpace(before[^1])) ins = " " + ins;
        if (isLink && (after.Length == 0 || !char.IsWhiteSpace(after[0]))) ins += " ";
        return (before + ins + after, start + ins.Length);
    }
}
