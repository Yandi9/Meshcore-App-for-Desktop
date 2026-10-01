using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MC1.Core.Utilities;

namespace MC1.Windows.Controls;

/// <summary>Terrain cross-section with line-of-sight ray and first Fresnel zone.</summary>
public sealed class TerrainProfile : Control
{
    public static readonly StyledProperty<IReadOnlyList<ElevationSample>?> ProfileProperty =
        AvaloniaProperty.Register<TerrainProfile, IReadOnlyList<ElevationSample>?>(nameof(Profile));
    public static readonly StyledProperty<double> HeightAProperty = AvaloniaProperty.Register<TerrainProfile, double>(nameof(HeightA), 2);
    public static readonly StyledProperty<double> HeightBProperty = AvaloniaProperty.Register<TerrainProfile, double>(nameof(HeightB), 10);
    public static readonly StyledProperty<double> FrequencyMHzProperty = AvaloniaProperty.Register<TerrainProfile, double>(nameof(FrequencyMHz), 910);
    public static readonly StyledProperty<double> RefractionKProperty = AvaloniaProperty.Register<TerrainProfile, double>(nameof(RefractionK), 1.333);
    public static readonly StyledProperty<bool> MetricProperty = AvaloniaProperty.Register<TerrainProfile, bool>(nameof(Metric), true);
    public static readonly StyledProperty<string> LabelAProperty = AvaloniaProperty.Register<TerrainProfile, string>(nameof(LabelA), "A");
    public static readonly StyledProperty<string> LabelBProperty = AvaloniaProperty.Register<TerrainProfile, string>(nameof(LabelB), "B");

    public IReadOnlyList<ElevationSample>? Profile { get => GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public double HeightA { get => GetValue(HeightAProperty); set => SetValue(HeightAProperty, value); }
    public double HeightB { get => GetValue(HeightBProperty); set => SetValue(HeightBProperty, value); }
    public double FrequencyMHz { get => GetValue(FrequencyMHzProperty); set => SetValue(FrequencyMHzProperty, value); }
    public double RefractionK { get => GetValue(RefractionKProperty); set => SetValue(RefractionKProperty, value); }
    public bool Metric { get => GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public string LabelA { get => GetValue(LabelAProperty); set => SetValue(LabelAProperty, value); }
    public string LabelB { get => GetValue(LabelBProperty); set => SetValue(LabelBProperty, value); }

    private static readonly Typeface Face = new(FontFamily.Default);

    static TerrainProfile()
    {
        AffectsRender<TerrainProfile>(ProfileProperty, HeightAProperty, HeightBProperty, FrequencyMHzProperty, RefractionKProperty, MetricProperty, LabelAProperty, LabelBProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TerrainProfile>(true);
    }

    private IBrush Res(string key, string fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : new SolidColorBrush(Color.Parse(fallback));

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var text = Res("App.TextSecondary", "#6B7280");
        var profile = Profile;
        if (profile is not { Count: >= 2 } || w < 60 || h < 60)
        {
            var ft = new FormattedText(L.T("Pick two points and run the analysis to see the terrain profile."), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, text);
            ft.MaxTextWidth = Math.Max(50, w - 20);
            ctx.DrawText(ft, new Point(Math.Max(10, (w - ft.Width) / 2), (h - ft.Height) / 2));
            return;
        }
        var origin = profile[0].DistanceFromAMeters;
        var length = profile[^1].DistanceFromAMeters - origin;
        if (length <= 0) return;
        var k = RefractionK;
        var f = FrequencyMHz;
        var startH = profile[0].Elevation + HeightA;
        var endH = profile[^1].Elevation + HeightB;

        var terrain = profile.Select(s =>
        {
            var dA = s.DistanceFromAMeters - origin;
            return (D: dA, H: s.Elevation + RfCalculator.EarthBulge(dA, length - dA, k));
        }).ToList();
        double Los(double d) => startH + d / length * (endH - startH);
        double Fr(double d) => RfCalculator.FresnelRadius(f, d, length - d);

        var minH = Math.Min(terrain.Min(t => t.H), Math.Min(startH, endH)) - 10;
        var maxH = Math.Max(terrain.Max(t => t.H), terrain.Max(t => Los(t.D) + Fr(t.D))) + 10;
        maxH = Math.Max(maxH, Math.Max(startH, endH) + 10);

        const double left = 56, right = 14, top = 12, bottom = 28;
        var pw = w - left - right;
        var ph = h - top - bottom;
        double X(double d) => left + d / length * pw;
        double Y(double e) => top + (1 - (e - minH) / (maxH - minH)) * ph;

        var grid = new Pen(Res("App.Border", "#E5E7EB"), 1);
        for (var i = 0; i <= 4; i++)
        {
            var e = minH + (maxH - minH) * i / 4;
            var y = Y(e);
            ctx.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = Metric ? $"{e:0} m" : $"{e * 3.28084:0} ft";
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, text);
            ctx.DrawText(ft, new Point(left - ft.Width - 6, y - ft.Height / 2));
        }
        for (var i = 0; i <= 4; i++)
        {
            var d = length * i / 4;
            var label = Metric ? (length > 2000 ? $"{d / 1000:0.#} km" : $"{d:0} m") : (length > 3000 ? $"{d / 1609.344:0.#} mi" : $"{d * 3.28084:0} ft");
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, text);
            ctx.DrawText(ft, new Point(Math.Clamp(X(d) - ft.Width / 2, left - 10, w - ft.Width - 2), h - bottom + 7));
        }

        // Fresnel zone (upper & lower bound) as a filled band
        var zone = new StreamGeometry();
        using (var g = zone.Open())
        {
            var n = 60;
            g.BeginFigure(new Point(X(0), Y(Los(0))), true);
            for (var i = 1; i <= n; i++) { var d = length * i / n; g.LineTo(new Point(X(d), Y(Los(d) + Fr(d)))); }
            for (var i = n; i >= 0; i--) { var d = length * i / n; g.LineTo(new Point(X(d), Y(Los(d) - Fr(d)))); }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#2463EB"), 0.12), new Pen(new SolidColorBrush(Color.Parse("#2463EB"), 0.45), 1, new DashStyle([4, 3], 0)), zone);

        // terrain
        var ground = new StreamGeometry();
        using (var g = ground.Open())
        {
            g.BeginFigure(new Point(X(0), top + ph), true);
            foreach (var t in terrain) g.LineTo(new Point(X(t.D), Y(t.H)));
            g.LineTo(new Point(X(length), top + ph));
            g.EndFigure(true);
        }
        var groundBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#8FB573"), 0), new GradientStop(Color.Parse("#6B8F55"), 1) },
        };
        ctx.DrawGeometry(groundBrush, new Pen(new SolidColorBrush(Color.Parse("#4E7038")), 1.5), ground);

        // obstructions
        foreach (var t in terrain)
        {
            var fr = Fr(t.D);
            var clearance = Los(t.D) - t.H;
            if (fr <= 0 || t.D <= 1 || length - t.D <= 1) continue;
            var pct = clearance / fr * 100;
            if (pct < RfCalculator.MarginalThreshold)
                ctx.DrawEllipse(new SolidColorBrush(Color.Parse(pct < 0 ? "#E5484D" : "#F59E0B")), null, new Point(X(t.D), Y(t.H)), 3.5, 3.5);
        }

        // LOS ray + antennas
        var losPen = new Pen(new SolidColorBrush(Color.Parse("#E5484D")), 2);
        ctx.DrawLine(losPen, new Point(X(0), Y(startH)), new Point(X(length), Y(endH)));
        var mast = new Pen(Res("App.Text", "#111827"), 2);
        ctx.DrawLine(mast, new Point(X(0), Y(profile[0].Elevation)), new Point(X(0), Y(startH)));
        ctx.DrawLine(mast, new Point(X(length), Y(profile[^1].Elevation)), new Point(X(length), Y(endH)));
        ctx.DrawEllipse(Brushes.White, mast, new Point(X(0), Y(startH)), 4, 4);
        ctx.DrawEllipse(Brushes.White, mast, new Point(X(length), Y(endH)), 4, 4);
        var la = new FormattedText(LabelA, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 11.5, Res("App.Text", "#111827"));
        var lb = new FormattedText(LabelB, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 11.5, Res("App.Text", "#111827"));
        ctx.DrawText(la, new Point(X(0) + 6, Math.Max(top, Y(startH) - la.Height - 4)));
        ctx.DrawText(lb, new Point(X(length) - lb.Width - 6, Math.Max(top, Y(endH) - lb.Height - 4)));
    }
}
