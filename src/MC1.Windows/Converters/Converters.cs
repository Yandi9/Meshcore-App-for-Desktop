using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MC1.Windows.Converters;

/// <summary>Resolves an icon resource key ("Icon.Send") to its geometry.</summary>
public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();
    private static readonly Dictionary<string, Geometry?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string ?? parameter as string;
        if (string.IsNullOrEmpty(key)) return null;
        if (Cache.TryGetValue(key, out var g)) return g;
        g = Application.Current?.TryGetResource(key, null, out var r) == true ? r as Geometry : null;
        Cache[key] = g;
        return g;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → brush.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();
    private static readonly Dictionary<string, IBrush> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0) return null;
        var alpha = parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 1.0;
        var key = s + "|" + alpha;
        if (Cache.TryGetValue(key, out var b)) return b;
        try { b = new SolidColorBrush(Color.Parse(s), alpha).ToImmutable(); }
        catch { return null; }
        Cache[key] = b;
        return b;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum value → spaced words in the app's language ("MentionsOnly" → "Mentions only").</summary>
public sealed partial class EnumDisplayConverter : IValueConverter
{
    public static readonly EnumDisplayConverter Instance = new();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex Split();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "";
        var words = Split().Split(value.ToString()!);
        return Translate(string.Join(" ", words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant())));
    }

    /// <summary>
    /// The English text of an enum value (as made above) in the app's language. The values shown through this converter
    /// are written out so each is a key in the translation tables.
    /// </summary>
    private static string Translate(string english) => english switch
    {
        // ContactTypeFilter (Contacts filter chips)
        "All" => L.T("All"),
        "Companions" => L.T("Companions"),
        "Repeaters" => L.T("Repeaters"),
        "Rooms" => L.T("Rooms"),
        "Sensors" => L.T("Sensors"),
        "Favorites" => L.T("Favorites"),
        "Blocked" => L.T("Blocked"),
        // ChatFilter (Chats filter chips): All, Unread, Direct, Channels, Rooms
        "Unread" => L.T("Unread"),
        "Direct" => L.T("Direct"),
        "Channels" => L.T("Channels"),
        // RxFilter (RX log): All, Messages, Channel, Direct, Adverts, Other
        "Messages" => L.T("Messages"),
        "Channel" => L.T("Channel"),
        "Adverts" => L.T("Adverts"),
        "Other" => L.T("Other"),
        // NotificationLevel (chat and contact notification pickers): All, Mentions only, Muted
        "Mentions only" => L.T("Mentions only"),
        "Muted" => L.T("Muted"),
        _ => L.T(english),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter (ToString comparison) — for radio-button style selection.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 → percentage width helper (multiplies by the parameter).</summary>
public sealed class ScaleConverter : IValueConverter
{
    public static readonly ScaleConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture);
        var f = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1;
        return Math.Max(0, v * f);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Count → visible when > 0.</summary>
public sealed class PositiveConverter : IValueConverter
{
    public static readonly PositiveConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i > 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>IsOutgoing → right alignment.</summary>
public sealed class AlignConverter : IValueConverter
{
    public static readonly AlignConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>ContactType → "Companion", "Repeater"…</summary>
public sealed class ContactTypeNameConverter : IValueConverter
{
    public static readonly ContactTypeNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MeshCore.ContactType t ? MC1.Windows.ViewModels.Formatters.TypeName(t) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
