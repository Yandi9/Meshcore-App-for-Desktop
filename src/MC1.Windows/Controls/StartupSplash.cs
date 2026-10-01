using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace MC1.Windows.Controls;

/// <summary>
/// The start-up animation, drawn over the main window when MeshCore starts:
/// 1. the MESHCORE wordmark builds up letter by letter out of blocks (blocky, matrix-style), each letter then
///    snapping to its real shape;
/// 2. the outline ("frame") of the logo appears, radio-wave rays fly in from all sides and hit it, and every hit
///    charges it up — the logo fills in from the bottom like a battery;
/// 3. fully charged, it flashes and sends a ripple out.
/// About 2.8 seconds; a click or any key skips it.
/// </summary>
public sealed class StartupSplash : Control, ICustomHitTest
{
    // Timeline (milliseconds)
    internal const double LetterStart = 80, LetterStagger = 75;
    internal const double CellRain = 150, CellJitter = 90, CellDrop = 90; // a letter's blocks land within ~330 ms
    internal const double LetterResolve = CellRain + CellJitter + CellDrop + 20;
    internal const double TextMoveStart = 760, TextMoveDuration = 420;
    internal const double FrameStart = 820, FrameDuration = 300;
    internal const int RayCount = 8, FrontsPerRay = 3;
    internal const double FirstHit = 1180, HitStagger = 75, FrontSpacing = 45, RayTravel = 380, ChargeEase = 120;
    internal static readonly double FullyCharged = FirstHit + (RayCount - 1) * HitStagger + (FrontsPerRay - 1) * FrontSpacing + ChargeEase;
    internal static readonly double RippleStart = FullyCharged + 20;
    internal const double RippleDuration = 820, RippleStagger = 150;
    internal const int RippleCount = 3;
    internal static readonly double FadeStart = RippleStart + 600;
    internal const double FadeDuration = 360;
    public static readonly double TotalDuration = FadeStart + FadeDuration;

    // Where the rays come from (degrees, 0 = right, 270 = up): the sides and above, alternating, so none crosses the word.
    internal static readonly double[] RayAngles = [205, 335, 252, 290, 180, 0, 228, 312];

    private static readonly Color Deep = Color.Parse("#0A1120");
    private static readonly Color Navy = Color.Parse("#141C30");
    private static readonly Color Wave = Color.Parse("#6FA2FF");
    private static readonly Color Spark = Color.Parse("#B9D3FF");
    private static readonly Color Accent = Color.Parse("#2463EB");
    private static readonly Color Block = Color.Parse("#EEF3FF");

    private static Bitmap? _tile, _glyph;
    private static Bitmap[]? _letters;
    private static WordLayout? _word;

    private readonly Stopwatch _clock = new();
    private double _skipOffset;
    private bool _running, _finished;
    private DispatcherTimer? _fallback;

    /// <summary>Raised once the animation has faded out (or was skipped).</summary>
    public event Action? Finished;

    /// <summary>Draws this moment (ms) instead of running the clock — for previews and screenshots.</summary>
    public double? FrozenTime { get; set; }

    /// <summary>Plays the sound effects with it (<see cref="StartupSound"/>), when the platform can play sound.</summary>
    public bool Sound { get; set; }

    /// <summary>Plays a WAV once, asynchronously (set by the platform layer; null where there's no sound).</summary>
    public static Action<byte[]>? PlayWav { get; set; }

    /// <summary>Stops what <see cref="PlayWav"/> started.</summary>
    public static Action? StopWav { get; set; }

    private bool _soundPlaying;

    public StartupSplash()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private double Now => FrozenTime ?? _clock.Elapsed.TotalMilliseconds + _skipOffset;

    public void Start()
    {
        _clock.Restart();
        _running = true;
        if (Sound && FrozenTime is null && PlayWav is { } play)
        {
            try { play(StartupSound.Wav); _soundPlaying = true; }
            catch { /* no audio device: the animation carries on silently */ }
        }
        Focus();
        ScheduleFrame();
    }

    /// <summary>Jumps to the fade-out.</summary>
    public void Skip()
    {
        if (!_running || _finished) return;
        var now = Now;
        if (now < FadeStart) _skipOffset += FadeStart - now;
        StopSound();
    }

    private void StopSound()
    {
        if (!_soundPlaying) return;
        _soundPlaying = false;
        try { StopWav?.Invoke(); } catch { /* ignore */ }
    }

    private void ScheduleFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.RequestAnimationFrame(_ => OnFrame());
            return;
        }
        _fallback ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => OnFrame());
        _fallback.Start();
    }

    private void OnFrame()
    {
        _fallback?.Stop();
        if (!_running || _finished) return;
        InvalidateVisual();
        if (Now >= TotalDuration)
        {
            _finished = true;
            _running = false;
            Finished?.Invoke();
            return;
        }
        ScheduleFrame();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Skip();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Skip();
        e.Handled = true;
    }

    public bool HitTest(Point point) => !_finished && new Rect(Bounds.Size).Contains(point);

    // MARK: Assets

    private static Stream Open(string name) => AssetLoader.Open(new Uri("avares://MeshCoreOne/Assets/" + name));

    /// <summary>The MESHCORE wordmark: where each letter sits, and the blocks it's built from.</summary>
    private sealed record WordLayout(double Width, double Height, int Rows, int Cols, (int Col, int Row, int Letter)[] Cells, Rect[] LetterRects)
    {
        public double Cell => Height / Rows;

        public static WordLayout Load()
        {
            using var reader = new StreamReader(Open("SplashWordBlocks.txt"));
            var head = reader.ReadLine()!.Split(' ');
            double w = double.Parse(head[0], System.Globalization.CultureInfo.InvariantCulture);
            double h = double.Parse(head[1], System.Globalization.CultureInfo.InvariantCulture);
            int rows = int.Parse(head[2]), cols = int.Parse(head[3]);
            var cells = new List<(int, int, int)>();
            for (var r = 0; r < rows; r++)
            {
                var line = reader.ReadLine() ?? "";
                for (var c = 0; c < line.Length; c++)
                    if (char.IsDigit(line[c])) cells.Add((c, r, line[c] - '0'));
            }
            return new WordLayout(w, h, rows, cols, cells.ToArray(), LetterBoxes);
        }
    }

    /// <summary>Each letter image's position in the wordmark (from the MeshCore logo artwork).</summary>
    private static readonly Rect[] LetterBoxes =
    [
        new(-3, -2, 265, 186), new(255, -2, 205, 186), new(449, -2, 209, 186), new(654, -2, 215, 186),
        new(913, -2, 188, 186), new(1092, -2, 224, 186), new(1313, -2, 217, 186), new(1527, -2, 206, 186),
    ];

    private static void EnsureAssets()
    {
        _tile ??= new Bitmap(Open("SplashTile.png"));
        _glyph ??= new Bitmap(Open("SplashGlyph.png"));
        _letters ??= Enumerable.Range(0, 8).Select(i => new Bitmap(Open($"SplashL{i}.png"))).ToArray();
        _word ??= WordLayout.Load();
    }

    // MARK: Helpers

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static double Progress(double t, double start, double duration) => Clamp01((t - start) / duration);
    private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);
    private static double EaseInOutCubic(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    private static double EaseInQuad(double x) => x * x;
    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Clamp01(a) * 255), c.R, c.G, c.B);
    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    private static double Noise(int a, int b) => Math.Abs(Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453) % 1;
    private static IBrush Solid(Color c, double alpha) => new SolidColorBrush(WithAlpha(c, alpha));
    private static Matrix Around(Point c, double scale) =>
        Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(c.X, c.Y);

    private static double HitTime(int ray, int front) => FirstHit + ray * HitStagger + front * FrontSpacing;

    /// <summary>Distance from the logo's centre to its (rounded) edge in a direction.</summary>
    private static double EdgeDistance(double angle, double half)
    {
        var square = half / Math.Max(Math.Abs(Math.Cos(angle)), Math.Abs(Math.Sin(angle)));
        return Math.Min(square, half * 1.18);
    }

    private static void DrawArc(DrawingContext context, Pen pen, Point center, double radius, double midAngle, double halfSweep)
    {
        var a0 = midAngle - halfSweep;
        var a1 = midAngle + halfSweep;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + radius * Math.Cos(a0), center.Y + radius * Math.Sin(a0)), false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1)), new Size(radius, radius), 0,
                halfSweep * 2 > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    // MARK: Drawing

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        EnsureAssets();
        var word = _word!;
        var t = Now;

        var fade = EaseInOutCubic(Progress(t, FadeStart, FadeDuration));
        using var faded = context.PushOpacity(1 - fade);

        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Deep, 0), new GradientStop(Navy, 1) },
        }, new Rect(0, 0, w, h));

        // Layout: logo above the wordmark, the pair centred.
        var logo = Math.Clamp(Math.Min(w, h) * 0.21, 96, 168);
        var textHeight = logo * 0.40;
        var scale = textHeight / word.Height;
        var textWidth = word.Width * scale;
        var gap = logo * 0.30;
        var groupTop = (h - (logo + gap + textHeight)) / 2;
        var center = new Point(w / 2, groupTop + logo / 2);
        var half = logo / 2;
        var logoRect = new Rect(center.X - half, center.Y - half, logo, logo);
        var corner = logo * 0.22;
        var zoom = 1 + fade * 0.06;
        using var zoomed = context.PushTransform(Around(new Point(w / 2, h / 2), zoom));

        var charge = 0.0;
        for (var r = 0; r < RayCount; r++)
            for (var f = 0; f < FrontsPerRay; f++)
                charge += EaseOutCubic(Progress(t, HitTime(r, f), ChargeEase));
        charge /= RayCount * FrontsPerRay;

        // Soft blue glow behind the logo, growing with the charge.
        var glow = Progress(t, FrameStart, FrameDuration) * 0.35 + charge * 0.65;
        if (glow > 0)
        {
            var glowRadius = logo * 2.3;
            context.DrawEllipse(new RadialGradientBrush
            {
                GradientStops = { new GradientStop(WithAlpha(Accent, 0.34 * glow), 0), new GradientStop(WithAlpha(Accent, 0), 1) },
            }, null, center, glowRadius, glowRadius);
        }

        DrawWord(context, word, t, scale, textWidth, textHeight, groupTop + logo + gap, w, h);
        if (t < FrameStart) return;

        var pop = Progress(t, FullyCharged, 260);
        var popScale = 1 + Math.Sin(Math.PI * pop) * 0.06;
        using (context.PushTransform(Around(center, popScale)))
        {
            // The frame: the logo's outline drawn around, with a ghost of the mark inside.
            var frameIn = EaseOutCubic(Progress(t, FrameStart, FrameDuration));
            var framePath = new RectangleGeometry(logoRect, corner, corner);
            var perimeter = 4 * (logo - 2 * corner) + 2 * Math.PI * corner;
            const double frameThickness = 2;
            var frameColor = Mix(Wave, Colors.White, charge * 0.6);
            var dash = new DashStyle([Math.Max(0.01, perimeter * frameIn / frameThickness), perimeter / frameThickness], 0);
            using (context.PushOpacity(0.35 + 0.65 * Math.Max(charge, frameIn * 0.6)))
                context.DrawGeometry(null, new Pen(new SolidColorBrush(frameColor), frameThickness, dash, PenLineCap.Round), framePath);
            using (context.PushOpacity(0.12 * frameIn * (1 - charge)))
                context.DrawImage(_glyph!, new Rect(_glyph!.Size), logoRect);

            // Charging: the logo fills in from the bottom, with a bright, rippling level line.
            if (charge > 0)
            {
                var level = logoRect.Bottom - charge * logo;
                using (context.PushGeometryClip(framePath))
                {
                    using (context.PushClip(new Rect(logoRect.X, level, logo, logoRect.Bottom - level)))
                    {
                        context.DrawImage(_tile!, new Rect(_tile!.Size), logoRect);
                        context.DrawImage(_glyph!, new Rect(_glyph!.Size), logoRect);
                    }
                    if (charge < 0.999)
                    {
                        var surface = new StreamGeometry();
                        using (var g = surface.Open())
                        {
                            for (var i = 0; i <= 24; i++)
                            {
                                var x = logoRect.X + logo * i / 24.0;
                                var y = level + Math.Sin(i * 0.9 + t / 60) * logo * 0.018;
                                if (i == 0) g.BeginFigure(new Point(x, y), false); else g.LineTo(new Point(x, y));
                            }
                            g.EndFigure(false);
                        }
                        context.DrawGeometry(null, new Pen(Solid(Spark, 0.35), 6), surface);
                        context.DrawGeometry(null, new Pen(Solid(Spark, 0.95), 1.8), surface);
                    }
                }
            }

            // Hits: a spark where each wave front meets the frame.
            for (var r = 0; r < RayCount; r++)
            {
                var angle = RayAngles[r] * Math.PI / 180;
                var edge = EdgeDistance(angle, half);
                var at = new Point(center.X + Math.Cos(angle) * edge, center.Y + Math.Sin(angle) * edge);
                for (var f = 0; f < FrontsPerRay; f++)
                {
                    var spark = Progress(t, HitTime(r, f), 220);
                    if (spark is <= 0 or >= 1) continue;
                    var size = logo * (0.05 + 0.1 * spark);
                    context.DrawEllipse(new RadialGradientBrush
                    {
                        GradientStops = { new GradientStop(WithAlpha(Colors.White, (1 - spark) * 0.95), 0), new GradientStop(WithAlpha(Wave, 0), 1) },
                    }, null, at, size, size);
                }
            }

            // Fully charged: a white flash.
            var flash = Progress(t, FullyCharged, 260);
            if (flash is > 0 and < 1)
                using (context.PushGeometryClip(framePath))
                    context.FillRectangle(Solid(Colors.White, 0.45 * (1 - flash)), logoRect);
        }

        // The rays: radio wave fronts flying in from all sides towards the frame.
        for (var r = 0; r < RayCount; r++)
        {
            var angle = RayAngles[r] * Math.PI / 180;
            var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
            var edge = EdgeDistance(angle, half);
            var start = Math.Max(logo * 2.4, Math.Min(w, h) * 0.46);
            var source = center + dir * (start + logo * 0.6);
            for (var f = 0; f < FrontsPerRay; f++)
            {
                var hit = HitTime(r, f);
                var u = Progress(t, hit - RayTravel, RayTravel);
                if (u is <= 0 or >= 1) continue;
                var distance = start + (edge - start) * EaseInQuad(u);   // accelerates into the frame
                var radius = start + logo * 0.6 - distance;
                var halfSweep = Math.Min(1.0, logo * 0.2 / radius);
                var alpha = Math.Min(1, u * 3) * (0.55 + 0.45 * u);
                DrawArc(context, new Pen(Solid(Wave, alpha * 0.25), 8, lineCap: PenLineCap.Round), source, radius, angle + Math.PI, halfSweep);
                DrawArc(context, new Pen(Solid(Spark, alpha), 2.2, lineCap: PenLineCap.Round), source, radius, angle + Math.PI, halfSweep);
            }
        }

        // The ripple: the frame's shape spreading out once it's charged.
        for (var k = 0; k < RippleCount; k++)
        {
            var u = Progress(t, RippleStart + k * RippleStagger, RippleDuration);
            if (u is <= 0 or >= 1) continue;
            var s = 1 + EaseOutCubic(u) * 1.25;
            var rect = new Rect(center.X - half * s, center.Y - half * s, logo * s, logo * s);
            var alpha = Math.Pow(1 - u, 1.5);
            context.DrawRectangle(null, new Pen(Solid(Wave, alpha * 0.2), 9), rect, corner * s, corner * s);
            context.DrawRectangle(null, new Pen(Solid(Spark, alpha * 0.9), 2 * (1 - u) + 1), rect, corner * s, corner * s);
        }
    }

    /// <summary>The wordmark building up out of blocks, letter by letter; each letter then snaps to its real shape.</summary>
    private static void DrawWord(DrawingContext context, WordLayout word, double t, double scale, double textWidth, double textHeight,
        double finalTop, double w, double h)
    {
        var move = EaseInOutCubic(Progress(t, TextMoveStart, TextMoveDuration));
        var top = (h - textHeight) / 2 + (finalTop - (h - textHeight) / 2) * move;
        var left = (w - textWidth) / 2;
        var cell = word.Cell * scale;
        var blockSize = Math.Max(1, cell - Math.Max(1, cell * 0.1));

        // Blocks (only for letters that haven't resolved yet).
        foreach (var (col, row, letter) in word.Cells)
        {
            var letterStart = LetterStart + letter * LetterStagger;
            if (t >= letterStart + LetterResolve) continue;
            var appear = letterStart + row / (double)word.Rows * CellRain + Noise(col, row) * CellJitter;
            var p = Progress(t, appear, CellDrop);
            if (p <= 0) continue;
            var drop = (1 - EaseOutCubic(p)) * cell * 3;          // falls into place
            var color = Mix(Wave, Block, EaseOutCubic(Progress(t, appear + CellDrop * 0.5, 120)));
            context.FillRectangle(new SolidColorBrush(color), new Rect(left + col * cell, top + row * cell - drop, blockSize, blockSize));
        }

        // Letters that have finished building: the real shapes.
        for (var i = 0; i < _letters!.Length; i++)
        {
            if (t < LetterStart + i * LetterStagger + LetterResolve) continue;
            var box = word.LetterRects[i];
            context.DrawImage(_letters[i], new Rect(_letters[i].Size),
                new Rect(left + box.X * scale, top + box.Y * scale, box.Width * scale, box.Height * scale));
        }
    }
}
