using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MC1.Core.Services;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Services;

/// <summary>Brushes and image resolved from a <see cref="ChatTheme"/>, ready for binding.</summary>
public sealed class ChatAppearance
{
    public IBrush? Background { get; init; }
    public Bitmap? Image { get; init; }
    public double ImageDim { get; init; }
    public bool HasImage => Image is not null;
    public IBrush? IncomingBubble { get; init; }
    public IBrush? IncomingText { get; init; }
    public IBrush? OutgoingBubble { get; init; }
    public IBrush? OutgoingText { get; init; }
    /// <summary>Colour the top of the background is closest to (for colour and gradient backgrounds).</summary>
    public Color? Tone { get; init; }
    /// <summary>Average colour of the picture's top part (for picture backgrounds).</summary>
    public Color? ImageAverage { get; init; }

    /// <summary>Approximate colour behind the chat header, given the app's own background colour.</summary>
    public Color ToneOver(Color appBackground)
    {
        if (HasImage)
        {
            var img = ImageAverage ?? Tone ?? appBackground;
            return Mix(img, appBackground, ImageDim);
        }
        return Tone ?? appBackground;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Relative luminance (0 = black, 1 = white).</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>Average colour of the top third of a picture (what shows behind the name bar).</summary>
    private static Color? AverageTop(Bitmap bmp)
    {
        try
        {
            const int w = 12, h = 12;
            using var small = bmp.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.MediumQuality);
            var buf = new byte[w * h * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { small.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, w * 4); }
            finally { handle.Free(); }
            var rgba = small.Format is { } f && f == Avalonia.Platform.PixelFormats.Rgba8888;
            double r = 0, g = 0, b = 0, n = 0;
            for (var y = 0; y < h / 3 + 1; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    var a = buf[i + 3] / 255.0;
                    if (a <= 0) continue;
                    var c0 = buf[i] / a; var c2 = buf[i + 2] / a; var c1 = buf[i + 1] / a;
                    r += rgba ? c0 : c2; g += c1; b += rgba ? c2 : c0; n++;
                }
            if (n == 0) return null;
            return Color.FromRgb((byte)Math.Clamp(r / n, 0, 255), (byte)Math.Clamp(g / n, 0, 255), (byte)Math.Clamp(b / n, 0, 255));
        }
        catch { return null; }
    }

    /// <summary>Preview-only fallbacks so a swatch never shows an empty bubble.</summary>
    public IBrush PreviewIncomingBubble => IncomingBubble ?? new SolidColorBrush(Color.Parse("#E9ECF1"));
    public IBrush PreviewIncomingText => IncomingText ?? new SolidColorBrush(Color.Parse("#1F2937"));
    public IBrush PreviewOutgoingBubble => OutgoingBubble ?? new SolidColorBrush(Color.Parse("#2463EB"));
    public IBrush PreviewOutgoingText => OutgoingText ?? Brushes.White;

    public static ChatAppearance? From(ChatTheme? t)
    {
        if (t is null || t.IsEmpty) return null;
        IBrush? bg = null;
        Bitmap? image = null;
        Color? tone = null;
        switch (t.BackgroundKind)
        {
            case "Color" when Parse(t.BackgroundColor) is { } c:
                bg = new SolidColorBrush(c);
                tone = c;
                break;
            case "Gradient" when Parse(t.BackgroundColor) is { } c1:
                var c2 = Parse(t.BackgroundColor2) ?? c1;
                tone = t.GradientDirection == "Horizontal" ? Mix(c1, c2, 0.5) : c1;
                var (start, end) = t.GradientDirection switch
                {
                    "Horizontal" => (new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative)),
                    "Diagonal" => (new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative)),
                    _ => (new RelativePoint(0.5, 0, RelativeUnit.Relative), new RelativePoint(0.5, 1, RelativeUnit.Relative)),
                };
                bg = new LinearGradientBrush { StartPoint = start, EndPoint = end, GradientStops = { new GradientStop(c1, 0), new GradientStop(c2, 1) } };
                break;
            case "Image":
                image = ChatThemes.LoadImage(t.ImageFile);
                if (Parse(t.BackgroundColor) is { } under) { bg = new SolidColorBrush(under); tone = under; }
                break;
        }
        return new ChatAppearance
        {
            Background = bg,
            Image = image,
            Tone = tone,
            ImageAverage = image is null ? null : AverageTop(image),
            ImageDim = Math.Clamp(t.ImageDim, 0, 0.9),
            IncomingBubble = Brush(t.IncomingBubble),
            IncomingText = Brush(t.IncomingText),
            OutgoingBubble = Brush(t.OutgoingBubble),
            OutgoingText = Brush(t.OutgoingText),
        };
    }

    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        return Color.TryParse(hex, out var c) ? c : null;
    }

    private static IBrush? Brush(string? hex) => Parse(hex) is { } c ? new SolidColorBrush(c) : null;
}

/// <summary>A built-in look. <see cref="Name"/> is the English name stored in settings; <see cref="DisplayName"/> is shown.</summary>
public sealed record ChatThemePreset(string Name, ChatTheme Theme)
{
    public ChatAppearance? Appearance { get; } = ChatAppearance.From(Theme);

    /// <summary>The preset's name in the app's language.</summary>
    public string DisplayName => ChatThemes.PresetDisplayName(Name) ?? Name;
}

/// <summary>Stores per-conversation looks in settings.json (keyed per radio) plus an all-chats default.</summary>
public static class ChatThemes
{
    public const string DefaultKey = "*";
    public static event Action? Changed;

    private static AppSettings S => AppHost.Core.Settings.Current;
    private static string StorageKey(string conversationKey) => $"{AppHost.Core.RadioId ?? "offline"}|{conversationKey}";

    public static bool Enabled
    {
        get => S.ChatThemesEnabled;
        set
        {
            AppHost.Core.Settings.Update(s => s.ChatThemesEnabled = value);
            Changed?.Invoke();
        }
    }

    public static ChatTheme? GetOwn(string conversationKey) => S.ChatThemes.GetValueOrDefault(StorageKey(conversationKey));
    public static ChatTheme? GetDefault() => S.ChatThemes.GetValueOrDefault(DefaultKey);

    /// <summary>What a chat actually shows: its own look, else the all-chats default (nothing when disabled).</summary>
    public static ChatTheme? Effective(string conversationKey) => !Enabled ? null : GetOwn(conversationKey) ?? GetDefault();

    public static void Set(string conversationKey, ChatTheme? theme) => Store(StorageKey(conversationKey), theme);
    public static void SetDefault(ChatTheme? theme) => Store(DefaultKey, theme);

    private static void Store(string key, ChatTheme? theme)
    {
        AppHost.Core.Settings.Update(s =>
        {
            if (theme is null || theme.IsEmpty) s.ChatThemes.Remove(key);
            else s.ChatThemes[key] = theme.Clone();
        });
        CleanupUnusedImages();
        Changed?.Invoke();
    }

    /// <summary>Conversation keys (for the connected/offline radio) that have their own look.</summary>
    public static IReadOnlyList<(string ConversationKey, ChatTheme Theme)> Customized()
    {
        var prefix = (AppHost.Core.RadioId ?? "offline") + "|";
        return S.ChatThemes.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (kv.Key[prefix.Length..], kv.Value)).ToList();
    }

    public static void ResetAll()
    {
        AppHost.Core.Settings.Update(s => s.ChatThemes.Clear());
        CleanupUnusedImages();
        Changed?.Invoke();
    }

    /// <summary>Copies a chosen picture into the app's backgrounds folder and returns its stored file name.</summary>
    public static async Task<string> ImportImageAsync(string sourcePath)
    {
        Directory.CreateDirectory(AppPaths.Backgrounds);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) ext = ".png";
        var name = Guid.NewGuid().ToString("N") + ext;
        await using (var src = File.OpenRead(sourcePath))
        await using (var dst = File.Create(Path.Combine(AppPaths.Backgrounds, name)))
            await src.CopyToAsync(dst);
        // Validate that it decodes.
        if (LoadImage(name) is null)
        {
            File.Delete(Path.Combine(AppPaths.Backgrounds, name));
            throw new InvalidDataException(L.T("That file isn't an image this app can read (use PNG, JPG, BMP, GIF or WebP)."));
        }
        return name;
    }

    private static readonly Dictionary<string, Bitmap?> ImageCache = new();

    public static Bitmap? LoadImage(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        lock (ImageCache)
        {
            if (ImageCache.TryGetValue(file, out var cached)) return cached;
            Bitmap? bmp = null;
            try
            {
                var path = Path.Combine(AppPaths.Backgrounds, Path.GetFileName(file));
                if (File.Exists(path))
                {
                    using var fs = File.OpenRead(path);
                    bmp = Bitmap.DecodeToWidth(fs, 1920, BitmapInterpolationMode.MediumQuality);
                }
            }
            catch { bmp = null; }
            ImageCache[file] = bmp;
            return bmp;
        }
    }

    /// <summary>Deletes background pictures no chat uses any more.</summary>
    public static void CleanupUnusedImages()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Backgrounds)) return;
            var used = S.ChatThemes.Values.Select(t => t.ImageFile).Where(f => f is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(AppPaths.Backgrounds))
            {
                var name = Path.GetFileName(f);
                if (used.Contains(name) || PendingImages.Contains(name)) continue;
                try { File.Delete(f); } catch { /* in use */ }
                lock (ImageCache) ImageCache.Remove(name);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Images picked in an open editor but not saved yet (kept until the editor closes).</summary>
    public static readonly HashSet<string> PendingImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A built-in look's name (as stored in <see cref="ChatTheme.PresetName"/>, always English) in the app's language;
    /// other names are returned unchanged.
    /// </summary>
    public static string? PresetDisplayName(string? presetName) => presetName switch
    {
        "Midnight" => L.T("Midnight"),
        "Ocean" => L.T("Ocean"),
        "Forest" => L.T("Forest"),
        "Sunset" => L.T("Sunset"),
        "Lavender" => L.T("Lavender"),
        "Paper" => L.T("Paper"),
        "Terminal" => L.T("Terminal"),
        "High contrast" => L.T("High contrast"),
        _ => presetName,
    };

    /// <summary>The built-in looks. Names are English identifiers (stored in settings); show <see cref="ChatThemePreset.DisplayName"/>.</summary>
    public static readonly IReadOnlyList<ChatThemePreset> Presets =
    [
        new("Midnight", new ChatTheme { BackgroundKind = "Gradient", BackgroundColor = "#FF0F172A", BackgroundColor2 = "#FF1E293B", IncomingBubble = "#FF273449", IncomingText = "#FFE2E8F0", OutgoingBubble = "#FF6366F1", OutgoingText = "#FFFFFFFF", PresetName = "Midnight" }),
        new("Ocean", new ChatTheme { BackgroundKind = "Gradient", GradientDirection = "Diagonal", BackgroundColor = "#FF38BDF8", BackgroundColor2 = "#FF1E3A8A", IncomingBubble = "#FFFFFFFF", IncomingText = "#FF0F172A", OutgoingBubble = "#FF0C4A6E", OutgoingText = "#FFFFFFFF", PresetName = "Ocean" }),
        new("Forest", new ChatTheme { BackgroundKind = "Gradient", BackgroundColor = "#FF14532D", BackgroundColor2 = "#FF166534", IncomingBubble = "#FFF0FDF4", IncomingText = "#FF14532D", OutgoingBubble = "#FF4ADE80", OutgoingText = "#FF052E16", PresetName = "Forest" }),
        new("Sunset", new ChatTheme { BackgroundKind = "Gradient", GradientDirection = "Diagonal", BackgroundColor = "#FFF97316", BackgroundColor2 = "#FFDB2777", IncomingBubble = "#FFFFF7ED", IncomingText = "#FF7C2D12", OutgoingBubble = "#FF831843", OutgoingText = "#FFFFFFFF", PresetName = "Sunset" }),
        new("Lavender", new ChatTheme { BackgroundKind = "Gradient", BackgroundColor = "#FFEDE9FE", BackgroundColor2 = "#FFFCE7F3", IncomingBubble = "#FFFFFFFF", IncomingText = "#FF4C1D95", OutgoingBubble = "#FF7C3AED", OutgoingText = "#FFFFFFFF", PresetName = "Lavender" }),
        new("Paper", new ChatTheme { BackgroundKind = "Color", BackgroundColor = "#FFF5F0E6", IncomingBubble = "#FFFFFFFF", IncomingText = "#FF3F3A33", OutgoingBubble = "#FF8B5E34", OutgoingText = "#FFFFFFFF", PresetName = "Paper" }),
        new("Terminal", new ChatTheme { BackgroundKind = "Color", BackgroundColor = "#FF050805", IncomingBubble = "#FF0F1F0F", IncomingText = "#FF39FF14", OutgoingBubble = "#FF1B5E20", OutgoingText = "#FFCCFFCC", PresetName = "Terminal" }),
        new("High contrast", new ChatTheme { BackgroundKind = "Color", BackgroundColor = "#FF000000", IncomingBubble = "#FFFFFFFF", IncomingText = "#FF000000", OutgoingBubble = "#FFFFD600", OutgoingText = "#FF000000", PresetName = "High contrast" }),
    ];
}
