using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace MC1.Windows.Controls;

/// <summary>
/// One setting: a title (and optional hint) with its control. The control sits on the right when there's room and
/// moves under the text when the window gets narrow. Rows after the first draw a thin divider above themselves.
/// </summary>
public sealed class SettingRow : Control
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));
    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Hint));
    public static readonly StyledProperty<Control?> ContentProperty = AvaloniaProperty.Register<SettingRow, Control?>(nameof(Content));
    /// <summary>Below this width the control goes under the text.</summary>
    public static readonly StyledProperty<double> WrapWidthProperty = AvaloniaProperty.Register<SettingRow, double>(nameof(WrapWidth), 480);
    public static readonly StyledProperty<bool> ShowDividerProperty = AvaloniaProperty.Register<SettingRow, bool>(nameof(ShowDivider), true);

    private const double Gap = 16, VerticalPadding = 10, StackSpacing = 8;
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { IsVisible = false };
    private readonly StackPanel _text;
    private bool _stacked;

    static SettingRow()
    {
        AffectsMeasure<SettingRow>(WrapWidthProperty, ContentProperty);
        AffectsRender<SettingRow>(ShowDividerProperty);
    }

    public SettingRow()
    {
        _hint.Classes.Add("hint");
        _text = new StackPanel { Spacing = 2, Children = { _title, _hint } };
        LogicalChildren.Add(_text);
        VisualChildren.Add(_text);
        MinHeight = 50;
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }
    [Content]
    public Control? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    public double WrapWidth { get => GetValue(WrapWidthProperty); set => SetValue(WrapWidthProperty, value); }
    public bool ShowDivider { get => GetValue(ShowDividerProperty); set => SetValue(ShowDividerProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) _title.Text = Title;
        else if (change.Property == HintProperty)
        {
            _hint.Text = Hint;
            _hint.IsVisible = !string.IsNullOrWhiteSpace(Hint);
        }
        else if (change.Property == ContentProperty)
        {
            if (change.OldValue is Control old)
            {
                LogicalChildren.Remove(old);
                VisualChildren.Remove(old);
            }
            if (change.NewValue is Control c)
            {
                LogicalChildren.Add(c);
                VisualChildren.Add(c);
            }
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 10000 : available.Width;
        var content = Content;
        if (content is null)
        {
            _text.Measure(new Size(width, double.PositiveInfinity));
            return new Size(Math.Min(width, _text.DesiredSize.Width), _text.DesiredSize.Height + 2 * VerticalPadding);
        }
        content.Measure(new Size(width, double.PositiveInfinity));
        var cw = content.DesiredSize.Width;
        _stacked = width < WrapWidth || cw > width * 0.6;
        if (!_stacked)
        {
            _text.Measure(new Size(Math.Max(0, width - cw - Gap), double.PositiveInfinity));
            var h = Math.Max(_text.DesiredSize.Height, content.DesiredSize.Height);
            return new Size(double.IsInfinity(available.Width) ? _text.DesiredSize.Width + Gap + cw : width, h + 2 * VerticalPadding);
        }
        _text.Measure(new Size(width, double.PositiveInfinity));
        return new Size(width, _text.DesiredSize.Height + StackSpacing + content.DesiredSize.Height + 2 * VerticalPadding);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var content = Content;
        if (content is null)
        {
            _text.Arrange(new Rect(0, (final.Height - _text.DesiredSize.Height) / 2, final.Width, _text.DesiredSize.Height));
            return final;
        }
        var cs = content.DesiredSize;
        if (!_stacked)
        {
            var textWidth = Math.Max(0, final.Width - cs.Width - Gap);
            _text.Arrange(new Rect(0, (final.Height - _text.DesiredSize.Height) / 2, textWidth, _text.DesiredSize.Height));
            content.Arrange(new Rect(final.Width - cs.Width, (final.Height - cs.Height) / 2, cs.Width, cs.Height));
        }
        else
        {
            _text.Arrange(new Rect(0, VerticalPadding, final.Width, _text.DesiredSize.Height));
            var cwidth = content.HorizontalAlignment == HorizontalAlignment.Stretch ? final.Width : Math.Min(cs.Width, final.Width);
            content.Arrange(new Rect(0, VerticalPadding + _text.DesiredSize.Height + StackSpacing, cwidth, cs.Height));
        }
        return final;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!ShowDivider || Parent is not Panel panel || panel.Children.IndexOf(this) <= 0) return;
        // Only draw between visible rows.
        var index = panel.Children.IndexOf(this);
        if (!panel.Children.Take(index).Any(c => c.IsVisible)) return;
        var brush = this.FindResource("App.Border") as IBrush ?? Brushes.LightGray;
        context.DrawLine(new Pen(brush, 1), new Point(0, 0.5), new Point(Bounds.Width, 0.5));
    }
}
