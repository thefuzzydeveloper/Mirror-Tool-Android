using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WiFiAutoStreamSync;

public static class TrayIconHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateDynamicIcon(bool syncing)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark slate background circle
            using var bgBrush = new SolidBrush(Color.FromArgb(255, 30, 41, 59));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            // Sync arcs
            Color arcColor = syncing ? Color.FromArgb(74, 222, 128) : Color.FromArgb(56, 189, 248);
            using var arcPen = new Pen(arcColor, 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(arcPen, 6, 6, 20, 20, 30, 120);
            g.DrawArc(arcPen, 6, 6, 20, 20, 210, 120);

            // Center status core
            Color centerColor = syncing ? Color.FromArgb(250, 204, 21) : Color.FromArgb(148, 163, 184);
            using var centerBrush = new SolidBrush(centerColor);
            g.FillEllipse(centerBrush, 13, 13, 6, 6);
        }

        IntPtr hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }
}