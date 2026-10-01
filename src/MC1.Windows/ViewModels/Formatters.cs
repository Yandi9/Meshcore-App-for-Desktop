using System.Globalization;
using MeshCore;

namespace MC1.Windows.ViewModels;

public static class Formatters
{
    private static readonly string[] Palette =
        ["#2463EB", "#8B5CF6", "#EC4899", "#F59E0B", "#10B981", "#06B6D4", "#EF4444", "#6366F1", "#84CC16", "#F97316", "#14B8A6", "#A855F7"];

    public static string ColorFor(string seed)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in seed) h = h * 31 + ch;
            return Palette[(h & 0x7FFFFFFF) % Palette.Length];
        }
    }

    public static string Initials(string name)
    {
        var parts = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = new StringInfo(parts[0]).LengthInTextElements > 0 ? new StringInfo(parts[0]).SubstringByTextElements(0, 1) : "?";
        if (parts.Length == 1) return first.ToUpperInvariant();
        var second = new StringInfo(parts[1]).SubstringByTextElements(0, 1);
        return (first + second).ToUpperInvariant();
    }

    /// <summary>The first emoji in a node's name (e.g. "💧Gutter 1W" → "💧"), used as its avatar; null when there is none.</summary>
    public static string? AvatarEmoji(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var e = StringInfo.GetTextElementEnumerator(name);
        while (e.MoveNext())
        {
            var g = e.GetTextElement();
            if (!MC1.Core.Utilities.EmojiDetector.IsEmojiGrapheme(g)) continue;
            // Ask for the colour (emoji) presentation of symbols such as ☀ or ✳ that default to text style.
            var runes = g.EnumerateRunes().ToList();
            return runes.Count == 1 ? g + "\uFE0F" : g;
        }
        return null;
    }

    public static string ContactIcon(ContactType t) => t switch
    {
        ContactType.Repeater => "Icon.RouterWireless",
        ContactType.Room => "Icon.ForumOutline",
        ContactType.Sensor => "Icon.Thermometer",
        _ => "Icon.Account",
    };

    public static string ContactColor(ContactType t) => t switch
    {
        ContactType.Repeater => "#8B5CF6",
        ContactType.Room => "#F59E0B",
        ContactType.Sensor => "#14B8A6",
        _ => "#2463EB",
    };

    public static string TypeName(ContactType t) => MC1.Core.Services.ContactService.TypeName(t);

    /// <summary>"Add this repeater to your radio's contacts?" (one sentence per kind of node) with the start of its key.</summary>
    public static string AddContactPrompt(ContactType t, byte[] publicKey)
    {
        var key = Convert.ToHexString(publicKey)[..16];
        return t switch
        {
            ContactType.Repeater => L.F("Add this repeater to your radio's contacts?\n{0}…", key),
            ContactType.Room => L.F("Add this room server to your radio's contacts?\n{0}…", key),
            ContactType.Sensor => L.F("Add this sensor to your radio's contacts?\n{0}…", key),
            _ => L.F("Add this companion to your radio's contacts?\n{0}…", key),
        };
    }

    public static bool Use24h => AppHost.Core?.Settings.Current.UseTwentyFourHourTime ?? false;

    public static string Clock(DateTimeOffset d) => d.LocalDateTime.ToString(Use24h ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>"Sep 30" / "Sep 30, 2025" in the app's language (names from <see cref="L.DateCulture"/>).</summary>
    private static string MonthDayPattern(bool withYear) => L.Code switch
    {
        "en" => withYear ? "MMM d, yyyy" : "MMM d",
        "de" => withYear ? "d. MMM yyyy" : "d. MMM",
        "zh-Hans" => withYear ? "yyyy年M月d日" : "M月d日",
        "ko" => withYear ? "yyyy년 M월 d일" : "M월 d일",
        _ => withYear ? "d MMM yyyy" : "d MMM",
    };

    /// <summary>"Tuesday, September 30" (optionally with the year) in the app's language.</summary>
    private static string DayHeaderPattern(bool withYear) => L.Code switch
    {
        "en" => withYear ? "dddd, MMMM d, yyyy" : "dddd, MMMM d",
        "de" => withYear ? "dddd, d. MMMM yyyy" : "dddd, d. MMMM",
        "zh-Hans" => withYear ? "yyyy年M月d日 dddd" : "M月d日 dddd",
        "ko" => withYear ? "yyyy년 M월 d일 dddd" : "M월 d일 dddd",
        "es" or "pt" => withYear ? "dddd, d 'de' MMMM 'de' yyyy" : "dddd, d 'de' MMMM",
        "fr" or "it" or "nl" => withYear ? "dddd d MMMM yyyy" : "dddd d MMMM",
        _ => withYear ? "dddd, d MMMM yyyy" : "dddd, d MMMM",
    };

    /// <summary>A date as "Sep 30, 2025" (in the app's language).</summary>
    public static string FullDate(DateTime local) => local.ToString(MonthDayPattern(true), L.DateCulture);

    public static string RelativeTime(DateTimeOffset d)
    {
        var local = d.LocalDateTime;
        var now = DateTime.Now;
        if (local.Date == now.Date) return Clock(d);
        if (local.Date == now.Date.AddDays(-1)) return L.T("Yesterday");
        if ((now - local).TotalDays < 7) return local.ToString("ddd", L.DateCulture);
        return local.ToString(MonthDayPattern(local.Year != now.Year), L.DateCulture);
    }

    public static string DayHeader(DateTimeOffset d)
    {
        var local = d.LocalDateTime.Date;
        var today = DateTime.Now.Date;
        if (local == today) return L.T("Today");
        if (local == today.AddDays(-1)) return L.T("Yesterday");
        return local.ToString(DayHeaderPattern(local.Year != today.Year), L.DateCulture);
    }

    public static string Ago(DateTimeOffset d)
    {
        var s = DateTimeOffset.UtcNow - d;
        if (s.TotalSeconds < 0) return L.T("just now");
        if (s.TotalSeconds < 60) return L.T("just now");
        if (s.TotalMinutes < 60) return L.F("{0} min ago", (int)s.TotalMinutes);
        if (s.TotalHours < 24) return L.F("{0} h ago", (int)s.TotalHours);
        if (s.TotalDays < 30) return L.F("{0} d ago", (int)s.TotalDays);
        return FullDate(d.LocalDateTime);
    }

    public static string AgoMs(long ms) => ms <= 0 ? L.T("never") : Ago(DateTimeOffset.FromUnixTimeMilliseconds(ms));
    public static string AgoSeconds(long s) => s <= 0 ? L.T("never") : Ago(DateTimeOffset.FromUnixTimeSeconds(s));

    public static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return L.F("{0}d {1}h {2}m", (int)t.TotalDays, t.Hours, t.Minutes);
        if (t.TotalHours >= 1) return L.F("{0}h {1}m", t.Hours, t.Minutes);
        return L.F("{0}m {1}s", t.Minutes, t.Seconds);
    }

    public static string Distance(double meters)
    {
        var metric = AppHost.Core?.Settings.Current.UseMetricUnits ?? true;
        if (metric) return meters >= 1000 ? $"{meters / 1000:0.0} km" : $"{meters:0} m";
        var miles = meters / 1609.344;
        return miles >= 0.2 ? $"{miles:0.0} mi" : $"{meters * 3.28084:0} ft";
    }

    public static string Snr(double? snr) => snr is { } s ? $"{s:0.#} dB" : "—";

    public static string Coordinates(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"{lat:0.00000}, {lon:0.00000}");

    public static string Hex(byte[] b, int max = 0) => max > 0 && b.Length > max ? Convert.ToHexString(b, 0, max) : Convert.ToHexString(b);

    public static string PathText(byte[] path, int hashSize)
    {
        if (path.Length == 0) return L.T("Direct");
        return string.Join(" → ", PathEncoding.HopHexes(path, hashSize));
    }

    public static string Bytes(long n) => n switch
    {
        >= 1024 * 1024 => $"{n / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{n / 1024.0:0.0} KB",
        _ => $"{n} B",
    };
}
