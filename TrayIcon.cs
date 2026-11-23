using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MonitorDial;

/// <summary>
/// Ícone da bandeja desenhado em código: um monitor cuja tela se enche conforme o brilho.
/// Preto ou branco conforme o tema da barra de tarefas.
/// </summary>
public static class TrayIcon
{
    private const int Supersample = 4;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static void Destroy(Icon icon)
    {
        DestroyIcon(icon.Handle);
        icon.Dispose();
    }

    public static Icon Draw(int size, int? percent, bool light)
    {
        var color = light ? Color.Black : Color.White;
        int big = size * Supersample;

        using var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            DrawMonitor(g, big / 16f, color, percent);
        }

        using var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, size, size));
        }
        return Icon.FromHandle(result.GetHicon());
    }

    // Desenha numa grade de 16 x 16 unidades.
    private static void DrawMonitor(Graphics g, float u, Color color, int? percent)
    {
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 1.5f * u) { LineJoin = LineJoin.Round };

        var screen = new RectangleF(1.25f * u, 2f * u, 13.5f * u, 9f * u);
        using (var outline = Rounded(screen, 1.4f * u))
            g.DrawPath(pen, outline);

        if (percent is int p)
        {
            var inner = RectangleF.Inflate(screen, -2f * u, -2f * u);
            float width = inner.Width * Math.Clamp(p, 0, 100) / 100f;
            if (width > 0) g.FillRectangle(brush, inner.X, inner.Y, width, inner.Height);
        }

        g.FillRectangle(brush, 7.25f * u, 11f * u, 1.5f * u, 2f * u);           // haste
        using var foot = Rounded(new RectangleF(4.5f * u, 12.75f * u, 7f * u, 1.5f * u), 0.7f * u);
        g.FillPath(brush, foot);                                               // base
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
