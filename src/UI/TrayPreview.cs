using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Periphora.UI
{
    /// <summary>
    /// Draws a Windows-style horizontal battery icon reflecting a device's level.
    /// Green / amber / red fill, charging bolt overlay. Used as tray icon when
    /// exactly one device is present.
    /// </summary>
    internal static class BatteryIconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        public static Icon Create(int? percent, bool charging, bool online, int size)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    Draw(g, percent, charging, online, size);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        private static void Draw(Graphics g, int? percent, bool charging, bool online, int size)
        {
            float s = size / 32f;
            // horizontal battery body
            float bx = 3f * s, by = 9f * s, bw = 23f * s, bh = 14f * s;
            float r = 3f * s;
            Color outline = Color.FromArgb(235, 235, 240);
            Color fill = (!percent.HasValue || !online) ? Color.FromArgb(150, 150, 158)
                       : percent.Value >= 50 ? Color.FromArgb(46, 200, 106)
                       : percent.Value >= 20 ? Color.FromArgb(232, 172, 40)
                       : Color.FromArgb(232, 78, 78);

            using (GraphicsPath body = RoundRect(bx, by, bw, bh, r))
            using (Pen pen = new Pen(outline, 1.6f * s))
                g.DrawPath(pen, body);

            // terminal nub on the right
            using (SolidBrush sb = new SolidBrush(outline))
                g.FillRectangle(sb, bx + bw + 1f * s, by + 4.5f * s, 2.2f * s, 5f * s);

            // fill
            if (percent.HasValue && online && percent.Value > 0)
            {
                float pad = 2.2f * s;
                float innerW = (bw - pad * 2f) * Math.Min(100f, percent.Value) / 100f;
                float innerH = bh - pad * 2f;
                if (innerW > 0.5f)
                {
                    using (GraphicsPath fp = RoundRect(bx + pad, by + pad, innerW, innerH, r * 0.6f))
                    using (SolidBrush fb = new SolidBrush(fill))
                        g.FillPath(fb, fp);
                }
            }

            // charging bolt
            if (charging)
            {
                using (GraphicsPath bolt = new GraphicsPath())
                {
                    float cx = bx + bw * 0.5f;
                    float cy = by + bh * 0.5f;
                    bolt.AddLine(cx + 1f * s, cy - 6f * s, cx - 3f * s, cy + 0.5f * s);
                    bolt.AddLine(cx - 3f * s, cy + 0.5f * s, cx - 0.3f * s, cy + 0.5f * s);
                    bolt.AddLine(cx - 0.3f * s, cy + 0.5f * s, cx - 1.5f * s, cy + 6f * s);
                    bolt.AddLine(cx - 1.5f * s, cy + 6f * s, cx + 3.2f * s, cy - 0.5f * s);
                    bolt.AddLine(cx + 3.2f * s, cy - 0.5f * s, cx + 0.5f * s, cy - 0.5f * s);
                    bolt.CloseFigure();
                    using (SolidBrush bb = new SolidBrush(Color.White))
                        g.FillPath(bb, bolt);
                }
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2f;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
