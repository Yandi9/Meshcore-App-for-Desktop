using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MC1.Core.Services;
using MC1.Windows.Services;

namespace MC1.Windows.ViewModels;

/// <summary>Editor for one chat's background and colours (or the all-chats default).</summary>
public sealed partial class ChatAppearanceViewModel : ViewModelBase
{
    // Stored values (English identifiers in settings); BackgroundKinds / GradientDirections show them, in the same order.
    private static readonly string[] Kinds = ["None", "Color", "Gradient", "Image"];
    private static readonly string[] Directions = ["Vertical", "Horizontal", "Diagonal"];
    private readonly string? _conversationKey;
    private bool _loading;

    /// <param name="conversationKey">The chat to edit, or null to edit the default used by all chats.</param>
    public ChatAppearanceViewModel(string? conversationKey, string title)
    {
        _conversationKey = conversationKey;
        Title = title;
        IsDefault = conversationKey is null;
        var own = conversationKey is null ? ChatThemes.GetDefault() : ChatThemes.GetOwn(conversationKey);
        FollowsDefault = !IsDefault && own is null && ChatThemes.GetDefault() is not null;
        Load(own ?? (IsDefault ? null : ChatThemes.GetDefault()) ?? new ChatTheme());
        PropertyChanged += OnAnyPropertyChanged;
    }

    public string Title { get; }
    public bool IsDefault { get; }
    public bool IsPerChat => !IsDefault;
    public bool FollowsDefault { get; }
    public bool ThemesDisabled => !ChatThemes.Enabled;
    public IReadOnlyList<ChatThemePreset> Presets => ChatThemes.Presets;
    public IReadOnlyList<string> BackgroundKinds { get; } = [L.T("Theme default"), L.T("Solid colour"), L.T("Gradient"), L.T("Picture")];
    public IReadOnlyList<string> GradientDirections { get; } = [L.T("Top to bottom"), L.T("Left to right"), L.T("Diagonal")];
    public Action? Close { get; set; }

    [ObservableProperty] private int _backgroundKindIndex;
    [ObservableProperty] private Color _color1 = Color.Parse("#FF1E293B");
    [ObservableProperty] private Color _color2 = Color.Parse("#FF0F172A");
    [ObservableProperty] private int _gradientDirectionIndex;
    [ObservableProperty] private string? _imageFile;
    [ObservableProperty] private Bitmap? _imagePreview;
    [ObservableProperty] private double _imageDim = 0.35;
    [ObservableProperty] private bool _customIncomingBubble;
    [ObservableProperty] private Color _incomingBubbleColor = Color.Parse("#FFFFFFFF");
    [ObservableProperty] private bool _customIncomingText;
    [ObservableProperty] private Color _incomingTextColor = Color.Parse("#FF111827");
    [ObservableProperty] private bool _customOutgoingBubble;
    [ObservableProperty] private Color _outgoingBubbleColor = Color.Parse("#FF2463EB");
    [ObservableProperty] private bool _customOutgoingText;
    [ObservableProperty] private Color _outgoingTextColor = Color.Parse("#FFFFFFFF");
    /// <summary>The built-in look this is based on: its English name as stored (shown via <see cref="ChatThemes.PresetDisplayName"/>).</summary>
    [ObservableProperty] private string? _presetName;

    // Preview
    [ObservableProperty] private IBrush? _previewBackground;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private double _previewDim;
    [ObservableProperty] private IBrush? _previewIncomingBubble;
    [ObservableProperty] private IBrush? _previewIncomingText;
    [ObservableProperty] private IBrush? _previewOutgoingBubble;
    [ObservableProperty] private IBrush? _previewOutgoingText;
    [ObservableProperty] private string? _contrastWarning;
    [ObservableProperty] private string? _error;

    public bool IsColor => BackgroundKindIndex == 1;
    public bool IsGradient => BackgroundKindIndex == 2;
    public bool IsImage => BackgroundKindIndex == 3;
    public bool UsesColor1 => BackgroundKindIndex is 1 or 2;

    private void Load(ChatTheme t)
    {
        _loading = true;
        BackgroundKindIndex = Math.Max(0, Array.IndexOf(Kinds, t.BackgroundKind));
        if (ChatAppearance.Parse(t.BackgroundColor) is { } c1) Color1 = c1;
        if (ChatAppearance.Parse(t.BackgroundColor2) is { } c2) Color2 = c2;
        GradientDirectionIndex = Math.Max(0, Array.IndexOf(Directions, t.GradientDirection));
        ImageFile = t.ImageFile;
        ImagePreview = ChatThemes.LoadImage(t.ImageFile);
        ImageDim = t.ImageDim;
        CustomIncomingBubble = SetIf(t.IncomingBubble, c => IncomingBubbleColor = c);
        CustomIncomingText = SetIf(t.IncomingText, c => IncomingTextColor = c);
        CustomOutgoingBubble = SetIf(t.OutgoingBubble, c => OutgoingBubbleColor = c);
        CustomOutgoingText = SetIf(t.OutgoingText, c => OutgoingTextColor = c);
        PresetName = t.PresetName;
        _loading = false;
        UpdatePreview();
    }

    private static bool SetIf(string? hex, Action<Color> set)
    {
        if (ChatAppearance.Parse(hex) is not { } c) return false;
        set(c);
        return true;
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private ChatTheme Build() => new()
    {
        BackgroundKind = Kinds[Math.Clamp(BackgroundKindIndex, 0, 3)],
        BackgroundColor = BackgroundKindIndex is 1 or 2 ? Hex(Color1) : null,
        BackgroundColor2 = BackgroundKindIndex == 2 ? Hex(Color2) : null,
        GradientDirection = Directions[Math.Clamp(GradientDirectionIndex, 0, 2)],
        ImageFile = BackgroundKindIndex == 3 ? ImageFile : null,
        ImageDim = Math.Round(ImageDim, 2),
        IncomingBubble = CustomIncomingBubble ? Hex(IncomingBubbleColor) : null,
        IncomingText = CustomIncomingText ? Hex(IncomingTextColor) : null,
        OutgoingBubble = CustomOutgoingBubble ? Hex(OutgoingBubbleColor) : null,
        OutgoingText = CustomOutgoingText ? Hex(OutgoingTextColor) : null,
        PresetName = PresetName,
    };

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName is null || e.PropertyName.StartsWith("Preview") || e.PropertyName is nameof(ContrastWarning) or nameof(Error)) return;
        if (e.PropertyName == nameof(BackgroundKindIndex))
        {
            OnPropertyChanged(nameof(IsColor));
            OnPropertyChanged(nameof(IsGradient));
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(UsesColor1));
        }
        if (e.PropertyName != nameof(PresetName)) PresetName = null;
        UpdatePreview();
    }

    private static IBrush ThemeBrush(string key, string fallback) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b ? b : new SolidColorBrush(Color.Parse(fallback));

    private void UpdatePreview()
    {
        var a = ChatAppearance.From(Build());
        PreviewBackground = a?.Background ?? ThemeBrush("App.Background", "#F3F5F9");
        PreviewImage = a?.Image;
        PreviewDim = a?.ImageDim ?? 0;
        PreviewIncomingBubble = a?.IncomingBubble ?? ThemeBrush("App.BubbleIn", "#FFFFFF");
        PreviewIncomingText = a?.IncomingText ?? ThemeBrush("App.Text", "#141A24");
        PreviewOutgoingBubble = a?.OutgoingBubble ?? ThemeBrush("App.BubbleOut", "#2463EB");
        PreviewOutgoingText = a?.OutgoingText ?? ThemeBrush("App.BubbleOutText", "#FFFFFF");
        var incoming = Contrast(PreviewIncomingText, PreviewIncomingBubble) is < 3;
        var outgoing = Contrast(PreviewOutgoingText, PreviewOutgoingBubble) is < 3;
        ContrastWarning = (incoming, outgoing) switch
        {
            (true, true) => L.T("The text may be hard to read in received messages and your messages — pick more contrasting colours."),
            (true, false) => L.T("The text may be hard to read in received messages — pick more contrasting colours."),
            (false, true) => L.T("The text may be hard to read in your messages — pick more contrasting colours."),
            _ => null,
        };
    }

    private static double? Contrast(IBrush? fg, IBrush? bg)
    {
        if (fg is not ISolidColorBrush f || bg is not ISolidColorBrush b) return null;
        static double L(Color c)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var l1 = L(f.Color);
        var l2 = L(b.Color);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [RelayCommand]
    private void ApplyPreset(ChatThemePreset preset)
    {
        Load(preset.Theme);
        PresetName = preset.Name; // the English name is what's stored
    }

    [RelayCommand]
    private async Task ChooseImage()
    {
        var path = await AppHost.Dialogs.PickOpenFile(L.T("Choose a background picture"), ["png", "jpg", "jpeg", "bmp", "gif", "webp"], L.T("Pictures"));
        if (path is null) return;
        try
        {
            var name = await ChatThemes.ImportImageAsync(path);
            ChatThemes.PendingImages.Add(name);
            ImageFile = name;
            ImagePreview = ChatThemes.LoadImage(name);
            BackgroundKindIndex = 3;
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void RemoveImage()
    {
        ImageFile = null;
        ImagePreview = null;
        if (BackgroundKindIndex == 3) BackgroundKindIndex = 0;
    }

    [RelayCommand]
    private void SwapIncoming() => (IncomingBubbleColor, IncomingTextColor) = (IncomingTextColor, IncomingBubbleColor);

    [RelayCommand]
    private void SwapOutgoing() => (OutgoingBubbleColor, OutgoingTextColor) = (OutgoingTextColor, OutgoingBubbleColor);

    [RelayCommand]
    private void Save()
    {
        var theme = Build();
        if (IsDefault) ChatThemes.SetDefault(theme);
        else ChatThemes.Set(_conversationKey!, theme);
        Finish();
    }

    [RelayCommand]
    private void UseForAllChats()
    {
        ChatThemes.SetDefault(Build());
        if (!IsDefault) ChatThemes.Set(_conversationKey!, null);
        AppHost.Main?.ShowToast(L.T("Chat appearance"), L.T("This look is now the default for every chat without its own."));
        Finish();
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        if (IsDefault) ChatThemes.SetDefault(null);
        else ChatThemes.Set(_conversationKey!, null);
        Finish();
    }

    [RelayCommand]
    private void Cancel() => Finish();

    private void Finish()
    {
        ChatThemes.PendingImages.Clear();
        ChatThemes.CleanupUnusedImages();
        Close?.Invoke();
    }
}
