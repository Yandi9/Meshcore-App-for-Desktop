using Avalonia;
using Avalonia.Media;
using QRCoder;

namespace MC1.Windows.Services;

public static class QrService
{
    /// <summary>Vector QR code (dark modules only) — stays crisp at any size. Includes the quiet zone in its bounds.</summary>
    public static Geometry? Render(string text)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var matrix = data.ModuleMatrix;
            var n = matrix.Count;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.SetFillRule(FillRule.NonZero);
                // Invisible corner points keep the quiet zone inside the geometry bounds.
                ctx.BeginFigure(new Point(0, 0), false);
                ctx.LineTo(new Point(0, 0));
                ctx.EndFigure(false);
                ctx.BeginFigure(new Point(n, n), false);
                ctx.LineTo(new Point(n, n));
                ctx.EndFigure(false);
                for (var y = 0; y < n; y++)
                {
                    var row = matrix[y];
                    var x = 0;
                    while (x < n)
                    {
                        if (!row[x]) { x++; continue; }
                        var start = x;
                        while (x < n && row[x]) x++;
                        ctx.BeginFigure(new Point(start, y), true);
                        ctx.LineTo(new Point(x, y));
                        ctx.LineTo(new Point(x, y + 1));
                        ctx.LineTo(new Point(start, y + 1));
                        ctx.EndFigure(true);
                    }
                }
            }
            return geo;
        }
        catch { return null; }
    }

    public static byte[] RenderPng(string text, int pixelsPerModule = 10)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
