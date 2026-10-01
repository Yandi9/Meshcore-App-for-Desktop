namespace MC1.Windows.Services;

public sealed record EmojiGroup(string Name, IReadOnlyList<string> Emoji);

/// <summary>A compact emoji set for reactions and the composer picker.</summary>
public static class EmojiCatalog
{
    /// <summary>The groups with their names in the app's language (built on each read, so a language change shows).</summary>
    public static IReadOnlyList<EmojiGroup> Groups =>
    [
        new(L.T("Smileys"), Smileys),
        new(L.T("Gestures"), Gestures),
        new(L.T("Hearts & symbols"), HeartsAndSymbols),
        new(L.T("Radio & outdoors"), RadioAndOutdoors),
    ];

    private static readonly IReadOnlyList<string> Smileys = ["😀", "😃", "😄", "😁", "😆", "😅", "🤣", "😂", "🙂", "😉", "😊", "😇", "🥰", "😍", "🤩", "😘", "😋", "😛", "😜", "🤪", "🤔", "🤨", "😐", "😑", "😶", "🙄", "😏", "😬", "😌", "😴", "🤯", "🤠", "🥳", "😎", "🤓", "😕", "😟", "😮", "😲", "😳", "🥺", "😢", "😭", "😱", "😤", "😡", "🤬", "💀", "🤡", "👻"];

    private static readonly IReadOnlyList<string> Gestures = ["👍", "👎", "👌", "✌️", "🤞", "🤟", "🤘", "🤙", "👈", "👉", "👆", "👇", "☝️", "✋", "👋", "👏", "🙌", "🙏", "🤝", "💪", "🫡", "🫶", "👀", "🧠"];

    private static readonly IReadOnlyList<string> HeartsAndSymbols = ["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💔", "💯", "✅", "❌", "⚠️", "❓", "❗", "💤", "🔥", "✨", "⭐", "🌟", "⚡", "💥", "🎉", "🎊"];

    private static readonly IReadOnlyList<string> RadioAndOutdoors = ["📡", "📻", "🔋", "🪫", "🔌", "📶", "🛰️", "🗺️", "📍", "🧭", "⛰️", "🏔️", "🌲", "🏕️", "🚗", "🚲", "🥾", "☀️", "🌧️", "❄️", "🌙", "🌈", "☕", "🍺", "🍕"];

    public static IReadOnlyList<string> All { get; } = [.. Smileys, .. Gestures, .. HeartsAndSymbols, .. RadioAndOutdoors];
}
