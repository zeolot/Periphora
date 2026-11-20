// Standalone tool: render a clean line-art battery+bolt icon into a multi-size ICO.
// Style: transparent background, dark ink stroke with a white halo so the icon stays
// legible on both light and dark taskbars. No vendor branding, no color gradient.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public class MakeIcon
{
    // All geometry in a 24x24 design box.
    static GraphicsPath Body()
    {
        // rounded rectangle shell: x=2..20, y=6..18, corner radius 2.4
        float x = 2f, y = 6f, w = 18f, h = 12f, r = 2.4f, d = r * 2f;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Bolt(Graphics g, Pen pen)
    {
        // lighting bolt drawn inside the shell, centred with even padding
        PointF[] pts = {
            new PointF(12.4f, 8.9f),
            new PointF(9.6f, 12.7f),
            new PointF(11.7f, 12.7f),
            new PointF(10.6f, 16.4f),
            new PointF(14.2f, 11.9f),
            new PointF(12.3f, 11.9f)
        };
        g.DrawLines(pen, pts);
        g.DrawLine(pen, pts[pts.Length - 1], pts[0]);
    }

    static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float pad = size * 0.09f;
            float box = size - pad * 2f;
            float k = box / 24f;
            g.TranslateTransform(pad, pad);
            g.ScaleTransform(k, k);

            // ink width in design units; slightly thicker on tiny sizes for clarity
            float ink = size <= 24 ? 2.1f : 1.9f;
            float halo = ink + 2.2f;

            using (Pen white = new Pen(Color.FromArgb(240, 255, 255, 255), halo))
            using (Pen dark = new Pen(Color.FromArgb(32, 38, 48), ink))
            {
                white.LineJoin = LineJoin.Round; white.StartCap = LineCap.Round; white.EndCap = LineCap.Round;
                dark.LineJoin = LineJoin.Round; dark.StartCap = LineCap.Round; dark.EndCap = LineCap.Round;

                using (GraphicsPath shell = Body())
                {
                    // white halo pass first, then dark ink on top
                    g.DrawLine(white, 20.6f, 10f, 20.6f, 14f);
                    g.DrawPath(white, shell);
                    Bolt(g, white);

                    g.DrawLine(dark, 20.6f, 10f, 20.6f, 14f);
                    g.DrawPath(dark, shell);
                    Bolt(g, dark);
                }
            }
        }
        return bmp;
    }

    public static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();

        foreach (int s in sizes)
        {
            using (Bitmap b = Render(s))
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }
        }

        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);
            long offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim);
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)images[i].Length);
                w.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
        Console.WriteLine("wrote " + outPath);
    }
}
