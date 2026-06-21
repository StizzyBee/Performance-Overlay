using System.Drawing;
using System.Drawing.Drawing2D;

// NOTE: the project pins Color/Brushes to WPF (System.Windows.Media) via global usings,
// so GDI+ colors are written fully-qualified as System.Drawing.Color below.
using D = System.Drawing.Color;

namespace PerformanceOverlay;

/// <summary>
/// Draws the speedometer app icon at runtime for the system-tray NotifyIcon.
/// (Window/taskbar icons use the embedded app.ico; the tray needs a GDI+ icon, and
/// drawing it here sidesteps PNG-frame .ico decoding limitations in System.Drawing.)
/// </summary>
internal static class IconFactory
{
    public static Icon CreateTrayIcon(int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(D.Transparent);

            int r = (int)Math.Round(0.20 * size);
            int d = r * 2, max = size - 1;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(max - d, 0, d, d, 270, 90);
            path.AddArc(max - d, max - d, d, d, 0, 90);
            path.AddArc(0, max - d, d, d, 90, 90);
            path.CloseFigure();
            using (var bg = new SolidBrush(D.FromArgb(255, 22, 25, 33)))
                g.FillPath(bg, path);

            float cx = size / 2f, cy = size * 0.56f, rad = size * 0.30f;
            int gx = (int)(cx - rad), gy = (int)(cy - rad), gw = (int)(rad * 2);
            float thick = Math.Max(2f, size * 0.075f);

            using (var pt = new Pen(D.FromArgb(255, 42, 49, 64), thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pt, gx, gy, gw, gw, 135, 270);
            using (var pv = new Pen(D.FromArgb(255, 91, 140, 255), thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pv, gx, gy, gw, gw, 135, 200);

            double ang = (135 + 205) * Math.PI / 180.0;
            float nx = (float)(cx + Math.Cos(ang) * rad * 0.92);
            float ny = (float)(cy + Math.Sin(ang) * rad * 0.92);
            using (var pn = new Pen(D.FromArgb(255, 61, 214, 140), Math.Max(1.5f, size * 0.045f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pn, cx, cy, nx, ny);

            float hub = size * 0.05f;
            g.FillEllipse(System.Drawing.Brushes.White, cx - hub, cy - hub, hub * 2, hub * 2);
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
