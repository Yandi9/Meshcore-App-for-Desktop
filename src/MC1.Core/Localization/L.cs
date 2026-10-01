using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MC1.Core.Localization;

/// <summary>A language the app can be shown in: its code (as in the iOS app), and its name in that language.</summary>
public sealed record AppLanguage(string Code, string NativeName, string EnglishName)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// The app's translations. Strings are written in English in the code (<c>L.T("Settings")</c>, <c>{l:T Settings}</c> in
/// AXAML); the English text is the key into the chosen language's table (Localization/Strings/&lt;code&gt;.json). Anything
/// without a translation shows in English.
/// </summary>
public static class L
{
    /// <summary>The languages the app is translated into (the iOS app's, so far without Russian, Ukrainian, Korean and Chinese).</summary>
    public static IReadOnlyList<AppLanguage> Languages { get; } =
    [
        new("en", "English", "English"),
        new("de", "Deutsch", "German"),
        new("es", "Español", "Spanish"),
        new("fr", "Français", "French"),
        new("it", "Italiano", "Italian"),
        new("nl", "Nederlands", "Dutch"),
        new("pl", "Polski", "Polish"),
        new("pt", "Português", "Portuguese"),
    ];

    /// <summary>Windows' display language when the app started (before the app changed anything).</summary>
    public static CultureInfo SystemUiCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>Windows' regional format when the app started.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentCulture;

    /// <summary>The language in use ("en", "es", "zh-Hans", …).</summary>
    public static string Code { get; private set; } = "en";

    public static AppLanguage Current => Languages.FirstOrDefault(l => l.Code == Code) ?? Languages[0];

    /// <summary>
    /// Culture for month and day names in dates: Windows' regional format when it's in the app's language, otherwise the
    /// app's language (in the same country where .NET knows the combination).
    /// </summary>
    public static CultureInfo DateCulture { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>Raised after the language changed.</summary>
    public static event Action? Changed;

    private static Dictionary<string, Entry> s_table = new(StringComparer.Ordinal);

    private readonly record struct Entry(string? Text, Dictionary<string, string>? Forms);

    /// <summary>The language code Windows' display language maps to (English when the app doesn't have it).</summary>
    public static string SystemLanguageCode => Match(SystemUiCulture) ?? "en";

    /// <summary>The language code for a setting (null or "" = Windows' language).</summary>
    public static string Resolve(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) && Languages.Any(l => l.Code == setting) ? setting! : SystemLanguageCode;

    private static string? Match(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Languages.FirstOrDefault(l => l.Code == c.TwoLetterISOLanguageName) is { } lang) return lang.Code;
        }
        return null;
    }

    /// <summary>Switches to a language (a code from <see cref="Languages"/>; anything else means English).</summary>
    public static void SetLanguage(string code)
    {
        if (Languages.All(l => l.Code != code)) code = "en";
        s_table = code == "en" ? new(StringComparer.Ordinal) : Load(code);
        Code = code;
        var ui = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;
        DateCulture = PickDateCulture(code);
        try { Changed?.Invoke(); } catch { /* listeners must not break the switch */ }
    }

    private static CultureInfo PickDateCulture(string code)
    {
        if (Match(SystemCulture) == code) return SystemCulture;
        var baseName = code == "zh-Hans" ? "zh-CN" : code;
        try
        {
            var region = new RegionInfo(SystemCulture.Name).TwoLetterISORegionName;
            if (code != "zh-Hans" && CultureInfo.GetCultures(CultureTypes.SpecificCultures).Any(c => c.Name == $"{code}-{region}"))
                return CultureInfo.GetCultureInfo($"{code}-{region}");
        }
        catch { /* invariant or unknown region */ }
        return CultureInfo.GetCultureInfo(baseName);
    }

    /// <summary>The loaded translations of a language (for the tests and the harness).</summary>
    public static IReadOnlyCollection<string> KeysOf(string code) => Load(code).Keys;

    private static Dictionary<string, Entry> Load(string code)
    {
        var table = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"MC1.Core.Strings.{code}.json");
            if (stream is null) return table;
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    var s = p.Value.GetString();
                    if (!string.IsNullOrEmpty(s)) table[p.Name] = new(s, null);
                }
                else if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    var forms = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var f in p.Value.EnumerateObject())
                        if (f.Value.GetString() is { Length: > 0 } v) forms[f.Name] = v;
                    if (forms.Count > 0) table[p.Name] = new(forms.GetValueOrDefault("other"), forms);
                }
            }
        }
        catch { /* a broken table means English */ }
        return table;
    }

    /// <summary>The text in the app's language (the English text when there's no translation).</summary>
    public static string T(string english) =>
        s_table.TryGetValue(english, out var e) && e.Text is { } t ? t : english;

    /// <summary>A translated format string ("{0} is on the {1} preset.") filled in.</summary>
    public static string F(string english, params object?[] args)
    {
        var format = T(english);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, english, args); }
    }

    /// <summary>
    /// A count with the right plural ("1 contact" / "5 contacts"): <paramref name="one"/> and <paramref name="other"/> are the
    /// English forms, with {0} for the number; more arguments are {1}, {2}…. Languages with more forms (Polish, Russian,
    /// Ukrainian) have them in their table under the <paramref name="other"/> key.
    /// </summary>
    public static string Plural(long n, string one, string other, params object?[] more)
    {
        string format;
        if (s_table.TryGetValue(other, out var e) && e.Forms is { } forms)
        {
            var cat = Category(Code, n);
            format = forms.GetValueOrDefault(cat) ?? forms.GetValueOrDefault("other") ?? forms.Values.First();
        }
        else if (s_table.TryGetValue(other, out var single) && single.Text is { } t)
            format = n == 1 && s_table.TryGetValue(one, out var o) && o.Text is { } ot ? ot : t;
        else format = n == 1 ? one : other;
        object?[] args = [n, .. more];
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, n == 1 ? one : other, args); }
    }

    /// <summary>CLDR plural category of a whole number: "one", "few", "many" or "other".</summary>
    public static string Category(string code, long n)
    {
        var abs = Math.Abs(n);
        var mod10 = abs % 10;
        var mod100 = abs % 100;
        return code switch
        {
            "ko" or "zh-Hans" => "other",
            "fr" or "pt" => abs <= 1 ? "one" : "other",
            "ru" or "uk" => mod10 == 1 && mod100 != 11 ? "one"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many",
            "pl" => abs == 1 ? "one"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many",
            _ => abs == 1 ? "one" : "other",
        };
    }

    /// <summary>The plural forms a language uses, in order (for the translation tables).</summary>
    public static string[] PluralForms(string code) => code switch
    {
        "ko" or "zh-Hans" => ["other"],
        "ru" or "uk" or "pl" => ["one", "few", "many"],
        _ => ["one", "other"],
    };
}
