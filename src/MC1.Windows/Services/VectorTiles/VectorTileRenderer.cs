using System.Text.Json.Nodes;
using SkiaSharp;

namespace MC1.Windows.Services.VectorTiles;

/// <summary>
/// Draws one 256-px map tile (as a hi-res bitmap) from a vector tile and a MapLibre style.
/// MapLibre zoom levels are one lower than raster ones at the same scale (512-px vs 256-px tiles), so a raster tile
/// at zoom Z is drawn with the style evaluated at Z-1 from the vector tile at zoom Z-1 (or its zoom-14 ancestor).
/// </summary>
public static class VectorTileRenderer
{
    private const double LogicalTileSize = 256;
    private static SKTypeface? _typeface;

    /// <param name="styleZoom">MapLibre zoom (raster zoom − 1).</param>
    /// <param name="k">How many zoom levels the displayed tile is below the vector tile (it shows a 1/2^k slice).</param>
    /// <param name="ox">Column of the slice inside the vector tile (0 … 2^k−1).</param>
    /// <param name="oy">Row of the slice inside the vector tile.</param>
    public static byte[] RenderPng(MapStyle style, VectorTile tile, double styleZoom, int k, int ox, int oy, int outputSize = 512)
    {
        var info = new SKImageInfo(outputSize, outputSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var pxScale = outputSize / LogicalTileSize; // style px → output px
        var ctx0 = new StyleEval.Ctx(styleZoom, null);
        var labels = new List<LabelCandidate>();

        foreach (var layer in style.Layers)
        {
            if (!layer.ShowsAt(styleZoom)) continue;
            if (layer.Type == "background")
            {
                var bg = StyleEval.Color(layer.Paint, "background-color", ctx0) ?? SKColors.Black;
                var op = StyleEval.Number(layer.Paint, "background-opacity", ctx0, 1);
                using var p = new SKPaint { Color = WithOpacity(bg, op) };
                canvas.DrawRect(0, 0, outputSize, outputSize, p);
                continue;
            }
            if (layer.SourceLayer is null || !tile.Layers.TryGetValue(layer.SourceLayer, out var data)) continue;
            var unit = (float)(LogicalTileSize * Math.Pow(2, k) / data.Extent * pxScale);
            var offX = (float)(ox * outputSize);
            var offY = (float)(oy * outputSize);
            var margin = 64f * (float)pxScale;
            var bounds = new SKRect(-margin, -margin, outputSize + margin, outputSize + margin);

            foreach (var feature in data.Features)
            {
                var ctx = new StyleEval.Ctx(styleZoom, feature);
                if (!StyleEval.Filter(layer.Filter, ctx)) continue;
                switch (layer.Type)
                {
                    case "fill" when feature.Kind == GeometryKind.Polygon:
                        DrawFill(canvas, layer, feature, ctx, unit, offX, offY, bounds, pxScale);
                        break;
                    case "line" when feature.Kind is GeometryKind.LineString or GeometryKind.Polygon:
                        DrawLine(canvas, layer, feature, ctx, unit, offX, offY, bounds, pxScale);
                        break;
                    case "symbol":
                        CollectLabel(labels, layer, feature, ctx, unit, offX, offY, outputSize, pxScale);
                        break;
                }
            }
        }

        // Labels that would run over the edge of the vector tile are left out: the neighbouring tile can't draw
        // the rest of them, so they would appear cut in half.
        var dataRect = new SKRect(-ox * outputSize, -oy * outputSize, (float)((-ox + Math.Pow(2, k)) * outputSize), (float)((-oy + Math.Pow(2, k)) * outputSize));
        DrawLabels(canvas, labels, dataRect, new SKRect(0, 0, outputSize, outputSize));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    private static SKColor WithOpacity(SKColor c, double opacity) =>
        c.WithAlpha((byte)Math.Clamp(Math.Round(c.Alpha * Math.Clamp(opacity, 0, 1)), 0, 255));

    private static SKPath? BuildPath(VectorFeature f, float unit, float offX, float offY, SKRect bounds, bool close)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var part in f.Parts)
        {
            if (part.Count < 2) continue;
            for (var i = 0; i < part.Count; i++)
            {
                var x = part[i].X * unit - offX;
                var y = part[i].Y * unit - offY;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (i == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            if (close) path.Close();
        }
        if (path.IsEmpty || maxX < bounds.Left || minX > bounds.Right || maxY < bounds.Top || minY > bounds.Bottom)
        {
            path.Dispose();
            return null;
        }
        return path;
    }

    private static void DrawFill(SKCanvas canvas, StyleLayer layer, VectorFeature f, in StyleEval.Ctx ctx, float unit, float offX, float offY, SKRect bounds, double pxScale)
    {
        using var path = BuildPath(f, unit, offX, offY, bounds, true);
        if (path is null) return;
        var color = StyleEval.Color(layer.Paint, "fill-color", ctx) ?? SKColors.Black;
        var opacity = StyleEval.Number(layer.Paint, "fill-opacity", ctx, 1);
        var antialias = StyleEval.Truthy(StyleEval.Prop(layer.Paint, "fill-antialias", ctx, true));
        using var paint = new SKPaint { Color = WithOpacity(color, opacity), IsAntialias = antialias, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, paint);
        if (antialias && StyleEval.Color(layer.Paint, "fill-outline-color", ctx) is { } outline)
        {
            using var stroke = new SKPaint { Color = WithOpacity(outline, opacity), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(1, pxScale * 0.75) };
            canvas.DrawPath(path, stroke);
        }
    }

    private static void DrawLine(SKCanvas canvas, StyleLayer layer, VectorFeature f, in StyleEval.Ctx ctx, float unit, float offX, float offY, SKRect bounds, double pxScale)
    {
        var width = StyleEval.Number(layer.Paint, "line-width", ctx, 1);
        var opacity = StyleEval.Number(layer.Paint, "line-opacity", ctx, 1);
        if (width <= 0 || opacity <= 0) return;
        using var path = BuildPath(f, unit, offX, offY, bounds, f.Kind == GeometryKind.Polygon);
        if (path is null) return;
        var color = StyleEval.Color(layer.Paint, "line-color", ctx) ?? SKColors.Black;
        var strokeWidth = (float)(width * pxScale);
        using var paint = new SKPaint
        {
            Color = WithOpacity(color, opacity),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = StyleEval.String(layer.Layout, "line-cap", ctx) switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
            StrokeJoin = StyleEval.String(layer.Layout, "line-join", ctx) switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
        };
        if (StyleEval.Numbers(layer.Paint, "line-dasharray", ctx) is { Length: >= 2 } dash && dash.Any(d => d > 0) && !(dash.Length == 2 && dash[1] <= 0))
        {
            // Dash lengths are in line widths.
            var w = Math.Max(strokeWidth, (float)pxScale);
            var intervals = dash.Length % 2 == 0 ? dash : dash.Concat(dash).ToArray();
            paint.PathEffect = SKPathEffect.CreateDash(intervals.Select(d => (float)Math.Max(0.01, d * w)).ToArray(), 0);
        }
        var blur = StyleEval.Number(layer.Paint, "line-blur", ctx, 0);
        if (blur > 0.5) paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(blur * pxScale / 2));
        canvas.DrawPath(path, paint);
    }

    // MARK: Labels

    private sealed record LabelCandidate(
        string Text, SKPoint? Point, SKPath? LinePath, float Size, SKColor Color, SKColor HaloColor, float HaloWidth,
        string Anchor, SKPoint Offset, double Rank, int Order, bool Dot);

    private static void CollectLabel(List<LabelCandidate> labels, StyleLayer layer, VectorFeature f, in StyleEval.Ctx ctx, float unit, float offX, float offY, int size, double pxScale)
    {
        var text = StyleEval.String(layer.Layout, "text-field", ctx);
        var icon = StyleEval.String(layer.Layout, "icon-image", ctx);
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (StyleEval.String(layer.Layout, "text-transform", ctx) == "uppercase") text = text.ToUpperInvariant();
        else if (StyleEval.String(layer.Layout, "text-transform", ctx) == "lowercase") text = text.ToLowerInvariant();
        var textSize = (float)(StyleEval.Number(layer.Layout, "text-size", ctx, 16) * pxScale);
        var color = StyleEval.Color(layer.Paint, "text-color", ctx) ?? SKColors.White;
        color = WithOpacity(color, StyleEval.Number(layer.Paint, "text-opacity", ctx, 1));
        var halo = StyleEval.Color(layer.Paint, "text-halo-color", ctx) ?? SKColors.Transparent;
        var haloWidth = (float)(StyleEval.Number(layer.Paint, "text-halo-width", ctx, 0) * pxScale);
        var anchor = StyleEval.String(layer.Layout, "text-anchor", ctx) ?? "center";
        var offset = StyleEval.Numbers(layer.Layout, "text-offset", ctx) is { Length: >= 2 } o ? new SKPoint((float)o[0] * textSize, (float)o[1] * textSize) : SKPoint.Empty;
        var rank = f.Properties.TryGetValue("rank", out var r) && r is double rd ? rd : 1000;
        var placement = StyleEval.String(layer.Layout, "symbol-placement", ctx) ?? "point";
        var order = labels.Count;

        if (placement == "point" || f.Kind == GeometryKind.Point)
        {
            if (f.Parts.Count == 0 || f.Parts[0].Count == 0) return;
            var p = f.Parts[0][0];
            var pt = new SKPoint(p.X * unit - offX, p.Y * unit - offY);
            var dot = !string.IsNullOrEmpty(icon) && icon.Contains("circle", StringComparison.OrdinalIgnoreCase);
            labels.Add(new LabelCandidate(text, pt, null, textSize, color, halo, haloWidth, anchor, offset, rank, order, dot));
            return;
        }
        // Along a line: the longest part, drawn once around its middle.
        List<(float X, float Y)>? best = null;
        double bestLen = 0;
        foreach (var part in f.Parts)
        {
            double len = 0;
            for (var i = 1; i < part.Count; i++) len += Math.Sqrt(Math.Pow((part[i].X - part[i - 1].X) * unit, 2) + Math.Pow((part[i].Y - part[i - 1].Y) * unit, 2));
            if (len > bestLen) { bestLen = len; best = part; }
        }
        if (best is null || bestLen < textSize * 3) return;
        var path = new SKPath();
        for (var i = 0; i < best.Count; i++)
        {
            var x = best[i].X * unit - offX;
            var y = best[i].Y * unit - offY;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        var viewportAligned = StyleEval.String(layer.Layout, "text-rotation-alignment", ctx) == "viewport";
        if (viewportAligned)
        {
            // e.g. motorway numbers: horizontal text at the middle of the road.
            using var measure = new SKPathMeasure(path);
            if (!measure.GetPosition(measure.Length / 2, out var mid)) { path.Dispose(); return; }
            path.Dispose();
            labels.Add(new LabelCandidate(text, mid, null, textSize, color, halo, Math.Max(haloWidth, 2 * (float)pxScale), "center", SKPoint.Empty, rank, order, false));
            return;
        }
        labels.Add(new LabelCandidate(text, null, path, textSize, color, halo, haloWidth, anchor, offset, rank, order, false));
    }

    private static SKTypeface Typeface => _typeface ??= new[] { "Segoe UI", "Noto Sans", "Inter", "DejaVu Sans", "Arial" }
        .Select(n => (Name: n, Face: SKTypeface.FromFamilyName(n)))
        .FirstOrDefault(t => t.Face is not null && string.Equals(t.Face.FamilyName, t.Name, StringComparison.OrdinalIgnoreCase)).Face ?? SKTypeface.Default;

    private static SKTypeface TypefaceFor(string text)
    {
        var tf = Typeface;
        if (tf.ContainsGlyphs(text)) return tf;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x80) continue;
            var s = rune.ToString();
            if (tf.ContainsGlyphs(s)) continue;
            return SKFontManager.Default.MatchCharacter(rune.Value) ?? tf;
        }
        return tf;
    }

    /// <summary>
    /// Places every label of the vector tile (so the neighbouring map tiles cut from the same vector tile make the
    /// same choices) and draws the ones that touch this tile.
    /// </summary>
    private static void DrawLabels(SKCanvas canvas, List<LabelCandidate> labels, SKRect dataRect, SKRect visible)
    {
        bool Inside(SKRect r) => r.Left >= dataRect.Left - 1 && r.Top >= dataRect.Top - 1 && r.Right <= dataRect.Right + 1 && r.Bottom <= dataRect.Bottom + 1;
        var placed = new List<SKRect>();
        // More important places first (lower rank), keeping the style's layer order otherwise.
        foreach (var l in labels.OrderBy(l => l.Rank).ThenBy(l => l.Order))
        {
            using var paint = new SKPaint { IsAntialias = true, Color = l.Color, TextSize = l.Size, Typeface = TypefaceFor(l.Text), SubpixelText = true };
            using var halo = new SKPaint
            {
                IsAntialias = true, Color = l.HaloColor, TextSize = l.Size, Typeface = paint.Typeface, Style = SKPaintStyle.Stroke,
                StrokeWidth = l.HaloWidth * 2, StrokeJoin = SKStrokeJoin.Round,
            };
            var drawHalo = l.HaloWidth > 0 && l.HaloColor.Alpha > 0;
            if (l.LinePath is { } path)
            {
                using (path)
                {
                    var width = paint.MeasureText(l.Text);
                    using var measure = new SKPathMeasure(path);
                    var len = measure.Length;
                    if (width + l.Size > len) continue;
                    var start = (len - width) / 2;
                    using var segment = new SKPath();
                    if (!measure.GetSegment(start, start + width, segment, true)) continue;
                    // Keep the text upright: run it left to right.
                    measure.GetPosition(start, out var a);
                    measure.GetPosition(start + width, out var b);
                    SKPath drawPath = segment;
                    SKPath? reversed = null;
                    if (b.X < a.X)
                    {
                        reversed = Reverse(segment);
                        drawPath = reversed;
                    }
                    var box = drawPath.Bounds;
                    box.Inflate(l.Size * 0.6f, l.Size * 0.6f);
                    if (!Inside(box) || placed.Any(p => p.IntersectsWith(box))) { reversed?.Dispose(); continue; }
                    placed.Add(box);
                    if (!box.IntersectsWith(visible)) { reversed?.Dispose(); continue; }
                    var v = l.Size * 0.35f;
                    if (drawHalo) canvas.DrawTextOnPath(l.Text, drawPath, 0, v, halo);
                    canvas.DrawTextOnPath(l.Text, drawPath, 0, v, paint);
                    reversed?.Dispose();
                }
                continue;
            }
            if (l.Point is not { } pt) continue;
            var lines = l.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;
            var lineHeight = l.Size * 1.2f;
            var widths = lines.Select(t => paint.MeasureText(t)).ToArray();
            var w = widths.Max();
            var h = lineHeight * lines.Length;
            var anchor = new SKPoint(pt.X + l.Offset.X, pt.Y + l.Offset.Y);
            float left = l.Anchor switch
            {
                "left" or "top-left" or "bottom-left" => anchor.X,
                "right" or "top-right" or "bottom-right" => anchor.X - w,
                _ => anchor.X - w / 2,
            };
            float top = l.Anchor switch
            {
                "top" or "top-left" or "top-right" => anchor.Y,
                "bottom" or "bottom-left" or "bottom-right" => anchor.Y - h,
                _ => anchor.Y - h / 2,
            };
            var rect = new SKRect(left - 2, top - 1, left + w + 2, top + h + 1);
            if (l.Dot) rect.Union(new SKRect(pt.X - 4, pt.Y - 4, pt.X + 4, pt.Y + 4));
            if (!Inside(rect) || placed.Any(p => p.IntersectsWith(rect))) continue;
            placed.Add(rect);
            if (!rect.IntersectsWith(visible)) continue;
            if (l.Dot)
            {
                using var dot = new SKPaint { IsAntialias = true, Color = l.Color };
                using var ring = new SKPaint { IsAntialias = true, Color = l.HaloColor.Alpha > 0 ? l.HaloColor : SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                var radius = l.Size * 0.22f;
                canvas.DrawCircle(pt, radius, dot);
                canvas.DrawCircle(pt, radius, ring);
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var lx = l.Anchor.Contains("left") ? left : l.Anchor.Contains("right") ? left + w - widths[i] : left + (w - widths[i]) / 2;
                var baseline = top + lineHeight * i + l.Size * 0.95f;
                if (drawHalo) canvas.DrawText(lines[i], lx, baseline, halo);
                canvas.DrawText(lines[i], lx, baseline, paint);
            }
        }
    }

    private static SKPath Reverse(SKPath path)
    {
        var points = path.Points;
        var r = new SKPath();
        for (var i = points.Length - 1; i >= 0; i--)
        {
            if (i == points.Length - 1) r.MoveTo(points[i]);
            else r.LineTo(points[i]);
        }
        return r;
    }
}
