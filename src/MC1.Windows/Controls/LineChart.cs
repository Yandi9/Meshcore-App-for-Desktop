using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MC1.Windows.Controls;

public readonly record struct ChartPoint(double X, double Y);

public sealed class ChartSeries
{
    public required string Name { get; init; }
    public string Color { get; init; } = "#2463EB";
    public required IReadOnlyList<ChartPoint> Points { get; init; }
    public bool Fill { get; init; }
    public bool ShowDots { get; init; }
}

/// <summary>Minimal line chart with auto-scaled axes and hover readout.</summary>
public sealed class LineChart : Control
{
    public static readonly StyledProperty<IEnumerable?> SeriesProperty = AvaloniaProperty.Register<LineChart, IEnumerable?>(nameof(Series));
    public static readonly StyledProperty<bool> XIsTimeProperty = AvaloniaProperty.Register<LineChart, bool>(nameof(XIsTime), true);
    public static readonly StyledProperty<string> YFormatProperty = AvaloniaProperty.Register<LineChart, string>(nameof(YFormat), "0.#");
    public static readonly StyledProperty<string> YUnitProperty = AvaloniaProperty.Register<LineChart, string>(nameof(YUnit), "");
    /// <summary>Text shown when there's nothing to plot (null = "No data yet" in the app's language, looked up when drawn).</summary>
    public static readonly StyledProperty<string?> EmptyTextProperty = AvaloniaProperty.Register<LineChart, string?>(nameof(EmptyText));
    public static readonly StyledProperty<double> YMinProperty = AvaloniaProperty.Register<LineChart, double>(nameof(YMin), double.NaN);
    public static readonly StyledProperty<double> YMaxProperty = AvaloniaProperty.Register<LineChart, double>(nameof(YMax), double.NaN);
    public static readonly StyledProperty<int> RevisionProperty = AvaloniaProperty.Register<LineChart, int>(nameof(Revision));

    public IEnumerable? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public bool XIsTime { get => GetValue(XIsTimeProperty); set => SetValue(XIsTimeProperty, value); }
    public string YFormat { get => GetValue(YFormatProperty); set => SetValue(YFormatProperty, value); }
    public string YUnit { get => GetValue(YUnitProperty); set => SetValue(YUnitProperty, value); }
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public double YMin { get => GetValue(YMinProperty); set => SetValue(YMinProperty, value); }
    public double YMax { get => GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }
    /// <summary>Bump to force a redraw when points are mutated in place.</summary>
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    private static readonly Typeface Face = new(FontFamily.Default);
    private Point? _hover;
    private INotifyCollectionChanged? _incc;

    static LineChart()
    {
        AffectsRender<LineChart>(SeriesProperty, XIsTimeProperty, YFormatProperty, YUnitProperty, YMinProperty, YMaxProperty, RevisionProperty);
        ClipToBoundsProperty.OverrideDefaultValue<LineChart>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeriesProperty)
        {
            if (_incc is not null) _incc.CollectionChanged -= Changed;
            _incc = change.NewValue as INotifyCollectionChanged;
            if (_incc is not null) _incc.CollectionChanged += Changed;
        }
    }

    private void Changed(object? s, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); _hover = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = null; InvalidateVisual(); }

    private IBrush Res(string key, string fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : new SolidColorBrush(Color.Parse(fallback));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40) return;
        var text = Res("App.TextSecondary", "#6B7280");
        var grid = new Pen(Res("App.Border", "#E5E7EB"), 1);
        var series = Series?.OfType<ChartSeries>().Where(s => s.Points.Count > 0).ToList() ?? [];
        if (series.Count == 0)
        {
            var ft = new FormattedText(EmptyText ?? L.T("No data yet"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, text);
            ctx.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }
        var all = series.SelectMany(s => s.Points).ToList();
        var xMin = all.Min(p => p.X);
        var xMax = all.Max(p => p.X);
        if (xMax - xMin < 1e-9) { xMin -= 1; xMax += 1; }
        var yMin = double.IsNaN(YMin) ? all.Min(p => p.Y) : YMin;
        var yMax = double.IsNaN(YMax) ? all.Max(p => p.Y) : YMax;
        if (yMax - yMin < 1e-9) { yMin -= 1; yMax += 1; }
        var pad = (yMax - yMin) * 0.08;
        if (double.IsNaN(YMin)) yMin -= pad;
        if (double.IsNaN(YMax)) yMax += pad;

        const double left = 52, right = 12, top = 10, bottom = 26;
        var pw = w - left - right;
        var ph = h - top - bottom;
        double X(double x) => left + (x - xMin) / (xMax - xMin) * pw;
        double Y(double y) => top + (1 - (y - yMin) / (yMax - yMin)) * ph;

        // y grid
        for (var i = 0; i <= 4; i++)
        {
            var v = yMin + (yMax - yMin) * i / 4;
            var y = Y(v);
            ctx.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = v.ToString(YFormat, CultureInfo.CurrentCulture) + (i == 4 && YUnit.Length > 0 ? " " + YUnit : "");
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, text);
            ctx.DrawText(ft, new Point(left - ft.Width - 6, y - ft.Height / 2));
        }
        // x labels
        for (var i = 0; i <= 4; i++)
        {
            var v = xMin + (xMax - xMin) * i / 4;
            string label;
            if (XIsTime)
            {
                var d = DateTimeOffset.FromUnixTimeMilliseconds((long)v).LocalDateTime;
                label = (xMax - xMin) > 36 * 3600_000 ? d.ToString("MMM d HH:mm", L.DateCulture) : (xMax - xMin) > 600_000 ? d.ToString("HH:mm") : d.ToString("HH:mm:ss");
            }
            else label = v.ToString("0.#", CultureInfo.CurrentCulture);
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, text);
            var x = Math.Clamp(X(v) - ft.Width / 2, left - 10, w - ft.Width - 2);
            ctx.DrawText(ft, new Point(x, h - bottom + 6));
        }

        foreach (var s in series)
        {
            var color = Color.Parse(s.Color);
            var pts = s.Points.OrderBy(p => p.X).Select(p => new Point(X(p.X), Y(p.Y))).ToList();
            if (s.Fill && pts.Count > 1)
            {
                var fill = new StreamGeometry();
                using (var g = fill.Open())
                {
                    g.BeginFigure(new Point(pts[0].X, top + ph), true);
                    foreach (var p in pts) g.LineTo(p);
                    g.LineTo(new Point(pts[^1].X, top + ph));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(color, 0.15), null, fill);
            }
            if (pts.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(pts[0], false);
                    for (var i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2, lineJoin: PenLineJoin.Round), geo);
            }
            if (s.ShowDots || pts.Count == 1)
                foreach (var p in pts) ctx.DrawEllipse(new SolidColorBrush(color), null, p, 3, 3);
        }

        if (_hover is { } hp && hp.X >= left && hp.X <= w - right)
        {
            var xv = xMin + (hp.X - left) / pw * (xMax - xMin);
            var lines = new List<(string Text, Color Color)>();
            double? snapX = null;
            foreach (var s in series)
            {
                var nearest = s.Points.MinBy(p => Math.Abs(p.X - xv));
                snapX ??= nearest.X;
                lines.Add(($"{s.Name}: {nearest.Y.ToString(YFormat, CultureInfo.CurrentCulture)} {YUnit}".TrimEnd(), Color.Parse(s.Color)));
                ctx.DrawEllipse(new SolidColorBrush(Color.Parse(s.Color)), new Pen(Brushes.White, 1.5), new Point(X(nearest.X), Y(nearest.Y)), 4, 4);
            }
            if (snapX is { } sx)
            {
                ctx.DrawLine(new Pen(text, 1, new DashStyle([3, 3], 0)), new Point(X(sx), top), new Point(X(sx), top + ph));
                var header = XIsTime ? DateTimeOffset.FromUnixTimeMilliseconds((long)sx).LocalDateTime.ToString("g") : sx.ToString("0.##");
                var fts = new List<FormattedText> { new(header, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, text) };
                fts.AddRange(lines.Select(l => new FormattedText(l.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 11.5, new SolidColorBrush(l.Color))));
                var bw = fts.Max(f => f.Width) + 16;
                var bh = fts.Sum(f => f.Height) + 10;
                var bx = X(sx) + 10 + bw > w ? X(sx) - bw - 10 : X(sx) + 10;
                var rect = new Rect(bx, top + 4, bw, bh);
                ctx.DrawRectangle(Res("App.Surface", "#FFFFFF"), new Pen(Res("App.Border", "#E5E7EB"), 1), rect, 6, 6);
                var y = rect.Y + 5;
                foreach (var f in fts) { ctx.DrawText(f, new Point(rect.X + 8, y)); y += f.Height; }
            }
        }
    }
}
