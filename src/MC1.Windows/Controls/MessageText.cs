using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using MC1.Core.Utilities;

namespace MC1.Windows.Controls;

/// <summary>Selectable message text with clickable links, mentions, hashtags and coordinates.</summary>
public sealed class MessageText : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<LinkDetector.Token>?> TokensProperty =
        AvaloniaProperty.Register<MessageText, IReadOnlyList<LinkDetector.Token>?>(nameof(Tokens));
    public static readonly StyledProperty<IBrush?> LinkBrushProperty =
        AvaloniaProperty.Register<MessageText, IBrush?>(nameof(LinkBrush));
    public static readonly StyledProperty<ICommand?> LinkCommandProperty =
        AvaloniaProperty.Register<MessageText, ICommand?>(nameof(LinkCommand));

    public IReadOnlyList<LinkDetector.Token>? Tokens { get => GetValue(TokensProperty); set => SetValue(TokensProperty, value); }
    public IBrush? LinkBrush { get => GetValue(LinkBrushProperty); set => SetValue(LinkBrushProperty, value); }
    public ICommand? LinkCommand { get => GetValue(LinkCommandProperty); set => SetValue(LinkCommandProperty, value); }

    private readonly List<(int Start, int Length, LinkDetector.Token Token)> _links = new();
    private Point? _pressAt;

    public MessageText()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TokensProperty || change.Property == LinkBrushProperty) Rebuild();
    }

    private void Rebuild()
    {
        _links.Clear();
        var inlines = new InlineCollection();
        var pos = 0;
        foreach (var t in Tokens ?? [])
        {
            var display = t.Kind == LinkDetector.TokenKind.Mention ? "@" + t.Target : t.Text;
            var run = new Run(display);
            switch (t.Kind)
            {
                case LinkDetector.TokenKind.Text:
                    break;
                case LinkDetector.TokenKind.Contact:
                    run.FontWeight = FontWeight.SemiBold;
                    if (LinkBrush is not null) run.Foreground = LinkBrush;
                    run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                    _links.Add((pos, display.Length, t));
                    break;
                case LinkDetector.TokenKind.Mention:
                    run.FontWeight = FontWeight.SemiBold;
                    if (LinkBrush is not null) run.Foreground = LinkBrush;
                    _links.Add((pos, display.Length, t));
                    break;
                default:
                    if (LinkBrush is not null) run.Foreground = LinkBrush;
                    run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                    _links.Add((pos, display.Length, t));
                    break;
            }
            inlines.Add(run);
            pos += display.Length;
        }
        Inlines = inlines;
    }

    private LinkDetector.Token? LinkAt(Point p)
    {
        if (_links.Count == 0) return null;
        var layout = TextLayout;
        var hit = layout.HitTestPoint(new Point(p.X - Padding.Left, p.Y - Padding.Top));
        if (!hit.IsInside) return null;
        var index = hit.TextPosition;
        foreach (var l in _links)
            if (index >= l.Start && index < l.Start + l.Length) return l.Token;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) is not null ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressAt = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressAt is not { } start) return;
        _pressAt = null;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - start.X) > 3 || Math.Abs(pos.Y - start.Y) > 3) return;
        if (SelectionEnd != SelectionStart && Math.Abs(SelectionEnd - SelectionStart) > 0 && LinkAt(pos) is null) return;
        if (LinkAt(pos) is { } token && LinkCommand?.CanExecute(token) == true)
        {
            ClearSelection();
            LinkCommand.Execute(token);
            e.Handled = true;
        }
    }
}
