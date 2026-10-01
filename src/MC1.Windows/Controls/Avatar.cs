using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MC1.Windows.Converters;

namespace MC1.Windows.Controls;

/// <summary>
/// Round avatar. Shows the emoji from the node's name when it has one (like the iOS app),
/// otherwise the node-type icon, otherwise initials.
/// </summary>
public sealed class Avatar : Border
{
    public static readonly StyledProperty<string?> EmojiProperty = AvaloniaProperty.Register<Avatar, string?>(nameof(Emoji));
    public static readonly StyledProperty<string?> IconKeyProperty = AvaloniaProperty.Register<Avatar, string?>(nameof(IconKey));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Avatar, string?>(nameof(Text));
    public static readonly StyledProperty<string?> ColorProperty = AvaloniaProperty.Register<Avatar, string?>(nameof(Color));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Avatar, double>(nameof(Size), 40);

    public string? Emoji { get => GetValue(EmojiProperty); set => SetValue(EmojiProperty, value); }
    public string? IconKey { get => GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Color-emoji fonts first so symbols like ☀ render as emoji rather than monochrome text.</summary>
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji, Noto Color Emoji, Segoe UI Symbol");

    private IDisposable? _chipBinding;

    public Avatar()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EmojiProperty || change.Property == IconKeyProperty || change.Property == TextProperty ||
            change.Property == ColorProperty || change.Property == SizeProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var size = Size;
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        var accent = Color is { Length: > 0 } c ? HexBrushConverter.Instance.Convert(c, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture) as IBrush : null;
        accent ??= Brushes.SlateGray;
        _chipBinding?.Dispose();
        _chipBinding = null;

        if (!string.IsNullOrEmpty(Emoji))
        {
            // Soft background with a ring in the node-type color keeps the emoji legible and the type recognisable.
            _chipBinding = Bind(BackgroundProperty, this.GetResourceObservable("App.Chip"));
            BorderBrush = accent;
            BorderThickness = new Thickness(Math.Max(1.5, size / 22));
            Child = new TextBlock
            {
                Text = Emoji,
                FontFamily = EmojiFont,
                FontSize = size * 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            return;
        }

        Background = accent;
        BorderThickness = default;
        if (!string.IsNullOrEmpty(IconKey) && IconConverter.Instance.Convert(IconKey, typeof(Geometry), null, System.Globalization.CultureInfo.InvariantCulture) is Geometry icon)
        {
            Child = new PathIcon { Data = icon, Width = size * 0.48, Height = size * 0.48, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return;
        }
        Child = new TextBlock
        {
            Text = Text ?? "",
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            FontSize = size * 0.38,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
