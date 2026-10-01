using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Services;

/// <summary>Shows the total unread count in the window title, on the tray icon and (on Windows) on the taskbar button.</summary>
public static class UnreadBadge
{
    private static Bitmap? _baseIcon;
    private static WindowIcon? _plainTrayIcon;
    private static int _last = -1;

    /// <summary>Set by the Windows platform layer: draws/clears the taskbar overlay for a window.</summary>
    public static Action<Window, int>? TaskbarOverlay { get; set; }

    public static void Update(int unread)
    {
        var app = Application.Current;
        if (app is null) return;
        var enabled = AppHost.Core?.Settings.Current.ShowUnreadOnTaskbar ?? true;
        var shown = enabled ? unread : 0;
        var window = (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is not null) window.Title = unread > 0 ? $"MeshCore ({unread})" : "MeshCore";
        if (TrayIcon.GetIcons(app) is { Count: > 0 } icons)
        {
            icons[0].ToolTipText = unread > 0 ? L.Plural(unread, "MeshCore — {0} unread message", "MeshCore — {0} unread messages") : "MeshCore";
            if (shown != _last)
            {
                try
                {
                    _plainTrayIcon ??= icons[0].Icon;
                    icons[0].Icon = shown > 0 ? RenderTrayIcon(shown) : _plainTrayIcon;
                }
                catch { /* keep the plain icon */ }
            }
        }
        if (window is not null && shown != _last)
        {
            try { TaskbarOverlay?.Invoke(window, shown); } catch { /* optional */ }
        }
        _last = shown;
    }

    private static string Label(int n) => n > 99 ? "99+" : n.ToString();

    /// <summary>App icon with a red count bubble in the top-right corner.</summary>
    private static WindowIcon RenderTrayIcon(int count)
    {
        _baseIcon ??= new Bitmap(AssetLoader.Open(new Uri("avares://MeshCoreOne/Assets/AppIcon256.png")));
        const int size = 64;
        using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(_baseIcon, new Rect(0, 0, _baseIcon.Size.Width, _baseIcon.Size.Height), new Rect(0, 0, size, size));
            DrawBubble(ctx, count, new Rect(size * 0.40, 0, size * 0.60, size * 0.60));
        }
        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    /// <summary>A red disc with the count, filling <paramref name="area"/>.</summary>
    public static void DrawBubble(DrawingContext ctx, int count, Rect area)
    {
        var d = Math.Min(area.Width, area.Height);
        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#E5484D")), new Pen(Brushes.White, Math.Max(1, d * 0.07)), center, d / 2 - 0.5, d / 2 - 0.5);
        var text = Label(count);
        var fontSize = d * (text.Length switch { 1 => 0.62, 2 => 0.5, _ => 0.38 });
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), fontSize, Brushes.White);
        ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    /// <summary>Just the bubble, as 32-bit BGRA pixels (used for the Windows taskbar overlay).</summary>
    public static byte[] RenderBubblePixels(int count, int size)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext()) DrawBubble(ctx, count, new Rect(0, 0, size, size));
        var pixels = new byte[size * size * 4];
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, size, size), buffer, pixels.Length, size * 4);
            System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        return pixels;
    }
}
