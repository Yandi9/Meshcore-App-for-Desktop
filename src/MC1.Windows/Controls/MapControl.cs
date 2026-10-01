using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MC1.Windows.Services;

namespace MC1.Windows.Controls;

public enum MapMarkerKind { Chat, Repeater, Room, Sensor, Discovered, Self, Pin, Hop }

public sealed class MapMarker
{
    public required string Id { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required string Label { get; init; }
    public MapMarkerKind Kind { get; init; }
    public string Color { get; init; } = "#2463EB";
    public string? Badge { get; init; }
    /// <summary>Emoji from the node's name, drawn instead of the type icon.</summary>
    public string? Emoji { get; init; }
    public object? Tag { get; init; }
}

public sealed class MapPolyline
{
    public required IReadOnlyList<(double Lat, double Lon)> Points { get; init; }
    public string Color { get; init; } = "#2463EB";
    public double Thickness { get; init; } = 3;
    public bool Dashed { get; init; }
}

/// <summary>A lightweight slippy map: raster tiles, markers, polylines, drag/zoom.</summary>
public sealed class MapControl : Control
{
    public static readonly StyledProperty<double> CenterLatitudeProperty =
        AvaloniaProperty.Register<MapControl, double>(nameof(CenterLatitude), 39.5, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> CenterLongitudeProperty =
        AvaloniaProperty.Register<MapControl, double>(nameof(CenterLongitude), -98.35, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<MapControl, double>(nameof(Zoom), 4, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> LayerIdProperty =
        AvaloniaProperty.Register<MapControl, string?>(nameof(LayerId), "Standard");
    public static readonly StyledProperty<IEnumerable?> MarkersProperty =
        AvaloniaProperty.Register<MapControl, IEnumerable?>(nameof(Markers));
    public static readonly StyledProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.Register<MapControl, IEnumerable?>(nameof(Lines));
    public static readonly StyledProperty<MapMarker?> SelectedMarkerProperty =
        AvaloniaProperty.Register<MapControl, MapMarker?>(nameof(SelectedMarker), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(IsInteractive), true);
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(ShowLabels), true);
    public static readonly StyledProperty<bool> ShowAttributionProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(ShowAttribution), true);
    public static readonly StyledProperty<bool> MetricProperty =
        AvaloniaProperty.Register<MapControl, bool>(nameof(Metric), true);

    public double CenterLatitude { get => GetValue(CenterLatitudeProperty); set => SetValue(CenterLatitudeProperty, value); }
    public double CenterLongitude { get => GetValue(CenterLongitudeProperty); set => SetValue(CenterLongitudeProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public string? LayerId { get => GetValue(LayerIdProperty); set => SetValue(LayerIdProperty, value); }
    public IEnumerable? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public IEnumerable? Lines { get => GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public MapMarker? SelectedMarker { get => GetValue(SelectedMarkerProperty); set => SetValue(SelectedMarkerProperty, value); }
    public bool IsInteractive { get => GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }
    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public bool ShowAttribution { get => GetValue(ShowAttributionProperty); set => SetValue(ShowAttributionProperty, value); }
    public bool Metric { get => GetValue(MetricProperty); set => SetValue(MetricProperty, value); }

    /// <summary>Raised on right-click with the geographic position under the pointer.</summary>
    public event Action<double, double>? MapContextRequested;
    /// <summary>Raised when the pointer moves (lat, lon) for a coordinate readout.</summary>
    public event Action<double, double>? PointerLocationChanged;

    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SmallFace = new(FontFamily.Default);
    private static readonly Typeface EmojiFace = new(new FontFamily("Segoe UI Emoji, Noto Color Emoji, Segoe UI Symbol"));
    private readonly Dictionary<string, Geometry?> _iconCache = new();
    private Point? _dragStart;
    private (double X, double Y) _dragCenterPx;
    private bool _dragged;
    private INotifyCollectionChanged? _markersIncc, _linesIncc;

    static MapControl()
    {
        AffectsRender<MapControl>(CenterLatitudeProperty, CenterLongitudeProperty, ZoomProperty, LayerIdProperty, SelectedMarkerProperty, ShowLabelsProperty, MarkersProperty, LinesProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MapControl>(true);
        FocusableProperty.OverrideDefaultValue<MapControl>(true);
    }

    public MapControl()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TileService.Instance.TileLoaded += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TileService.Instance.TileLoaded -= InvalidateVisual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty) Rewire(ref _markersIncc, change.NewValue);
        else if (change.Property == LinesProperty) Rewire(ref _linesIncc, change.NewValue);
    }

    private void Rewire(ref INotifyCollectionChanged? field, object? value)
    {
        if (field is not null) field.CollectionChanged -= OnCollectionChanged;
        field = value as INotifyCollectionChanged;
        if (field is not null) field.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private MapLayerInfo LayerInfo => TileService.Layer(LayerId);

    private double ClampZoom(double z) => Math.Clamp(z, 1, LayerInfo.MaxZoom);

    private (double X, double Y) CenterPx(double zoom) => MapMath.ToPixel(CenterLatitude, CenterLongitude, zoom);

    public Point ToScreen(double lat, double lon)
    {
        var (cx, cy) = CenterPx(Zoom);
        var (px, py) = MapMath.ToPixel(lat, lon, Zoom);
        return new Point(px - cx + Bounds.Width / 2, py - cy + Bounds.Height / 2);
    }

    public (double Lat, double Lon) FromScreen(Point p)
    {
        var (cx, cy) = CenterPx(Zoom);
        return MapMath.FromPixel(cx + p.X - Bounds.Width / 2, cy + p.Y - Bounds.Height / 2, Zoom);
    }

    /// <summary>The visible geographic bounds (north, south, west, east).</summary>
    public (double North, double South, double West, double East) VisibleBounds()
    {
        var (n, w) = FromScreen(new Point(0, 0));
        var (s, e) = FromScreen(new Point(Bounds.Width, Bounds.Height));
        return (n, s, w, e);
    }

    public void ZoomBy(double delta, Point? around = null)
    {
        var anchor = around ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var (lat, lon) = FromScreen(anchor);
        var newZoom = ClampZoom(Zoom + delta);
        // keep the anchor's geo position under the same screen point
        var (ax, ay) = MapMath.ToPixel(lat, lon, newZoom);
        var cx = ax - (anchor.X - Bounds.Width / 2);
        var cy = ay - (anchor.Y - Bounds.Height / 2);
        var (clat, clon) = MapMath.FromPixel(cx, cy, newZoom);
        Zoom = newZoom;
        CenterLatitude = Math.Clamp(clat, -MapMath.MaxLatitude, MapMath.MaxLatitude);
        CenterLongitude = NormalizeLon(clon);
    }

    private static double NormalizeLon(double lon)
    {
        while (lon > 180) lon -= 360;
        while (lon < -180) lon += 360;
        return lon;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var layer = LayerInfo;
        var background = layer.Background is { } lb ? Color.Parse(lb) : dark ? Color.Parse("#1C1F24") : Color.Parse("#E8EAED");
        ctx.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, w, h));

        var zoom = ClampZoom(Zoom);
        var zi = (int)Math.Floor(zoom);
        var s = Math.Pow(2, zoom - zi);
        var (cx, cy) = CenterPx(zi);
        var left = cx - w / 2 / s;
        var top = cy - h / 2 / s;
        var n = 1 << zi;
        var tx0 = (int)Math.Floor(left / MapMath.TileSize);
        var ty0 = (int)Math.Floor(top / MapMath.TileSize);
        var tx1 = (int)Math.Floor((left + w / s) / MapMath.TileSize);
        var ty1 = (int)Math.Floor((top + h / s) / MapMath.TileSize);
        var tileScreen = MapMath.TileSize * s;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
        {
            for (var ty = Math.Max(0, ty0); ty <= Math.Min(n - 1, ty1); ty++)
            {
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    var wx = ((tx % n) + n) % n;
                    var dest = new Rect((tx * MapMath.TileSize - left) * s, (ty * MapMath.TileSize - top) * s, tileScreen + 0.5, tileScreen + 0.5);
                    var bmp = TileService.Instance.GetTile(layer.Id, zi, wx, ty);
                    if (bmp is not null)
                    {
                        ctx.DrawImage(bmp, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height), dest);
                    }
                    else if (TileService.Instance.GetFallback(layer.Id, zi, wx, ty) is { } fb)
                    {
                        var part = fb.Bitmap.Size.Width / (1 << fb.Dz);
                        ctx.DrawImage(fb.Bitmap, new Rect(fb.Ox * part, fb.Oy * part, part, part), dest);
                    }
                }
            }
        }

        DrawLines(ctx);
        DrawMarkers(ctx, dark);
        if (ShowAttribution) DrawOverlayText(ctx, layer, w, h, dark);
    }

    private void DrawLines(DrawingContext ctx)
    {
        if (Lines is null) return;
        foreach (var obj in Lines)
        {
            if (obj is not MapPolyline line || line.Points.Count < 2) continue;
            var color = Color.Parse(line.Color);
            var pen = new Pen(new SolidColorBrush(color), line.Thickness, line.Dashed ? new DashStyle([2, 2], 0) : null, PenLineCap.Round, PenLineJoin.Round);
            var halo = new Pen(new SolidColorBrush(Colors.White, 0.8), line.Thickness + 3, null, PenLineCap.Round, PenLineJoin.Round);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(ToScreen(line.Points[0].Lat, line.Points[0].Lon), false);
                for (var i = 1; i < line.Points.Count; i++) g.LineTo(ToScreen(line.Points[i].Lat, line.Points[i].Lon));
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, halo, geo);
            ctx.DrawGeometry(null, pen, geo);
        }
    }

    private Geometry? Icon(string key)
    {
        if (_iconCache.TryGetValue(key, out var g)) return g;
        g = Application.Current?.TryGetResource(key, null, out var res) == true ? res as Geometry : null;
        _iconCache[key] = g;
        return g;
    }

    private static string IconFor(MapMarkerKind k) => k switch
    {
        MapMarkerKind.Repeater => "Icon.RadioTower",
        MapMarkerKind.Room => "Icon.ForumOutline",
        MapMarkerKind.Sensor => "Icon.Thermometer",
        MapMarkerKind.Self => "Icon.CrosshairsGps",
        MapMarkerKind.Pin => "Icon.MapMarker",
        MapMarkerKind.Hop => "Icon.RouterWireless",
        _ => "Icon.Account",
    };

    private List<(MapMarker Marker, Point P)> VisibleMarkers()
    {
        var list = new List<(MapMarker, Point)>();
        if (Markers is null) return list;
        foreach (var obj in Markers)
        {
            if (obj is not MapMarker m) continue;
            var p = ToScreen(m.Latitude, m.Longitude);
            if (p.X < -40 || p.Y < -40 || p.X > Bounds.Width + 40 || p.Y > Bounds.Height + 40) continue;
            list.Add((m, p));
        }
        return list;
    }

    private void DrawMarkers(DrawingContext ctx, bool dark)
    {
        var visible = VisibleMarkers();
        var small = !IsInteractive || Bounds.Width < 320;
        var radius = small ? 9.0 : 12.0;
        var labelled = ShowLabels && !small && (visible.Count < 60 || Zoom >= 11);
        var selected = SelectedMarker;
        foreach (var (m, p) in visible.OrderBy(v => v.Marker == selected ? 1 : 0))
        {
            var color = Color.Parse(m.Color);
            var isSel = selected is not null && selected.Id == m.Id;
            var r = isSel ? radius + 3 : radius;
            if (m.Kind == MapMarkerKind.Pin)
            {
                var pin = Icon("Icon.MapMarker");
                if (pin is not null)
                {
                    var size = small ? 28.0 : 36.0;
                    var b = pin.Bounds;
                    var scale = size / Math.Max(b.Width, b.Height);
                    using (ctx.PushTransform(Matrix.CreateTranslation(-b.X - b.Width / 2, -b.Y - b.Height) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(p.X, p.Y)))
                        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#E5484D")), new Pen(Brushes.White, 1.2 / scale), pin);
                }
                if (!small && !string.IsNullOrEmpty(m.Label)) DrawLabel(ctx, m.Label, new Point(p.X, p.Y + 4), dark, true);
                continue;
            }
            var fill = m.Kind == MapMarkerKind.Discovered ? new SolidColorBrush(color, 0.55) : new SolidColorBrush(color);
            ctx.DrawEllipse(new SolidColorBrush(Colors.Black, 0.25), null, new Point(p.X, p.Y + 1.5), r + 2, r + 2);
            if (!string.IsNullOrEmpty(m.Emoji))
            {
                // Emoji avatar: white disc with a ring in the node-type colour.
                var er = r + 2;
                ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(color), isSel ? 3.5 : 2.5, m.Kind == MapMarkerKind.Discovered ? new DashStyle([2, 1.5], 0) : null), p, er, er);
                var et = new FormattedText(m.Emoji, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, EmojiFace, er * 1.1, Brushes.Black);
                ctx.DrawText(et, new Point(p.X - et.Width / 2, p.Y - et.Height / 2));
            }
            else ctx.DrawEllipse(fill, new Pen(Brushes.White, isSel ? 3 : 2, m.Kind == MapMarkerKind.Discovered ? new DashStyle([2, 1.5], 0) : null), p, r, r);
            var icon = string.IsNullOrEmpty(m.Emoji) ? Icon(IconFor(m.Kind)) : null;
            if (icon is not null)
            {
                var b = icon.Bounds;
                var size = r * 1.15;
                var scale = size / Math.Max(b.Width, b.Height);
                using (ctx.PushTransform(Matrix.CreateTranslation(-b.X - b.Width / 2, -b.Y - b.Height / 2) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(p.X, p.Y)))
                    ctx.DrawGeometry(Brushes.White, null, icon);
            }
            if (m.Badge is { Length: > 0 } badge)
            {
                var ft = new FormattedText(badge, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, Brushes.White);
                var bw = Math.Max(16, ft.Width + 8);
                var br = new Rect(p.X + r - 6, p.Y - r - 6, bw, 16);
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#111827")), new Pen(Brushes.White, 1.5), br, 8, 8);
                ctx.DrawText(ft, new Point(br.X + (bw - ft.Width) / 2, br.Y + (16 - ft.Height) / 2));
            }
            if (labelled || isSel) DrawLabel(ctx, m.Label, new Point(p.X, p.Y + r + 3), dark, isSel);
        }
    }

    private static void DrawLabel(DrawingContext ctx, string text, Point topCenter, bool dark, bool emphasize)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, emphasize ? 12.5 : 11.5,
            new SolidColorBrush(dark ? Colors.White : Color.Parse("#111827")));
        ft.MaxTextWidth = 180;
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        var rect = new Rect(topCenter.X - ft.Width / 2 - 5, topCenter.Y, ft.Width + 10, ft.Height + 3);
        ctx.DrawRectangle(new SolidColorBrush(dark ? Color.Parse("#E0202328") : Color.Parse("#E6FFFFFF")), null, rect, 5, 5);
        ctx.DrawText(ft, new Point(rect.X + 5, rect.Y + 1.5));
    }

    private void DrawOverlayText(DrawingContext ctx, MapLayerInfo layer, double w, double h, bool dark)
    {
        var fg = new SolidColorBrush(Color.Parse("#333333"));
        var ft = new FormattedText(layer.Attribution, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, SmallFace, 10, fg);
        var rect = new Rect(w - ft.Width - 10, h - ft.Height - 4, ft.Width + 10, ft.Height + 4);
        ctx.DrawRectangle(new SolidColorBrush(Colors.White, 0.75), null, rect);
        ctx.DrawText(ft, new Point(rect.X + 5, rect.Y + 2));

        if (!IsInteractive) return;
        // scale bar
        var metersPerPixel = 156543.03392 * Math.Cos(CenterLatitude * Math.PI / 180) / Math.Pow(2, Zoom);
        var maxBar = 110.0;
        var maxMeters = metersPerPixel * maxBar;
        double niceUnits;
        string label;
        if (Metric)
        {
            niceUnits = Nice(maxMeters);
            label = niceUnits >= 1000 ? $"{niceUnits / 1000:0.#} km" : $"{niceUnits:0} m";
        }
        else
        {
            var feet = maxMeters * 3.28084;
            if (feet > 5280) { var miles = Nice(feet / 5280); niceUnits = miles * 1609.344; label = $"{miles:0.#} mi"; }
            else { var f = Nice(feet); niceUnits = f / 3.28084; label = $"{f:0} ft"; }
        }
        var len = niceUnits / metersPerPixel;
        var y = h - 26;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#333333")), 2);
        ctx.DrawRectangle(new SolidColorBrush(Colors.White, 0.7), null, new Rect(8, y - 16, len + 8, 24), 3, 3);
        ctx.DrawLine(pen, new Point(12, y), new Point(12 + len, y));
        ctx.DrawLine(pen, new Point(12, y - 5), new Point(12, y + 1));
        ctx.DrawLine(pen, new Point(12 + len, y - 5), new Point(12 + len, y + 1));
        var lt = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, SmallFace, 10, fg);
        ctx.DrawText(lt, new Point(14, y - 15));
    }

    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var d = v / p;
        return (d >= 5 ? 5 : d >= 2 ? 2 : 1) * p;
    }

    // ---- interaction ----

    private MapMarker? HitTestMarker(Point p)
    {
        MapMarker? best = null;
        var bestDist = 18.0;
        foreach (var (m, sp) in VisibleMarkers())
        {
            var d = Math.Sqrt(Math.Pow(sp.X - p.X, 2) + Math.Pow(sp.Y - p.Y, 2));
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsInteractive) return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            var (lat, lon) = FromScreen(point.Position);
            MapContextRequested?.Invoke(lat, lon);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2)
        {
            ZoomBy(1, point.Position);
            e.Handled = true;
            return;
        }
        _dragStart = point.Position;
        _dragCenterPx = CenterPx(Zoom);
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (IsInteractive && PointerLocationChanged is not null)
        {
            var (lat, lon) = FromScreen(pos);
            PointerLocationChanged(lat, lon);
        }
        if (_dragStart is not { } start) return;
        var dx = pos.X - start.X;
        var dy = pos.Y - start.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragged = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        var (lat2, lon2) = MapMath.FromPixel(_dragCenterPx.X - dx, _dragCenterPx.Y - dy, Zoom);
        CenterLatitude = Math.Clamp(lat2, -MapMath.MaxLatitude, MapMath.MaxLatitude);
        CenterLongitude = NormalizeLon(lon2);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;
        var pos = e.GetPosition(this);
        if (!_dragged) SelectedMarker = HitTestMarker(pos);
        _dragStart = null;
        _dragged = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsInteractive) return;
        ZoomBy(e.Delta.Y * 0.5, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsInteractive) return;
        const double step = 80;
        var (cx, cy) = CenterPx(Zoom);
        (double X, double Y)? target = e.Key switch
        {
            Key.Left => (cx - step, cy),
            Key.Right => (cx + step, cy),
            Key.Up => (cx, cy - step),
            Key.Down => (cx, cy + step),
            _ => null,
        };
        if (target is { } t)
        {
            var (lat, lon) = MapMath.FromPixel(t.X, t.Y, Zoom);
            CenterLatitude = Math.Clamp(lat, -MapMath.MaxLatitude, MapMath.MaxLatitude);
            CenterLongitude = NormalizeLon(lon);
            e.Handled = true;
        }
        else if (e.Key is Key.OemPlus or Key.Add) { ZoomBy(1); e.Handled = true; }
        else if (e.Key is Key.OemMinus or Key.Subtract) { ZoomBy(-1); e.Handled = true; }
    }
}
