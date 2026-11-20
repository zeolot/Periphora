// Standalone tool: render the Periphora mark into an ICO or a PNG.
//
// The mark is a green rounded square holding a battery gauge: a translucent
// track with a solid fill at ~64%, plus the cell's terminal nub. That is the
// same visual language as the panel itself (a rounded progress bar under the
// percentage), so the icon and the product no longer disagree.
//
// Everything is drawn from one set of coordinates in a 24x24 design box and
// scaled with GDI+, so the 16px tray frame and the 512px README image come from
// the same geometry.
//
// Usage:
//   MakeIcon.exe out.ico             multi-size ICO (16..256)
//   MakeIcon.exe out.png 512         single PNG at the given pixel size
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public class MakeIcon
{
    // Design box is 24x24. Palette: a green that survives both light and dark
    // desktop backgrounds, and pure white for the gauge.
    static readonly Color BackTop = Color.FromArgb(255, 99, 220, 136);
    static readonly Color BackBottom = Color.FromArgb(255, 40, 172, 96);
    static readonly Color Ink = Color.FromArgb(255, 255, 255, 255);

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        float d = r * 2f;
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Paint(Graphics g, float size)
    {
        // The rounded square bleeds to the edges of the canvas, like an app icon.
        float radius = size * 0.224f;
        using (GraphicsPath plate = Rounded(0, 0, size, size, radius))
        using (LinearGradientBrush bg = new LinearGradientBrush(
                   new RectangleF(0, 0, size, size), BackTop, BackBottom, 90f))
        {
            g.FillPath(bg, plate);
        }

        // Gauge geometry, in design units of the 24 box: a battery shell with the
        // charge drawn inside it, plus the terminal nub. An outline plus a short
        // filled block stays readable at 16px, where a translucent track would
        // turn to mush.
        float k = size / 24f;

        // Below 24px there is not enough room for a 1.3px outline plus a block:
        // the two blur into one grey lump. At that size the shape is reduced to a
        // bold shell and its terminal, drawn slightly larger, which still reads
        // as a battery in a taskbar.
        bool tiny = size <= 24;
        float stroke = (tiny ? 2.5f : 1.9f) * k;
        float sx = (tiny ? 4.2f : 4.0f) * k;
        float sw = (tiny ? 12.6f : 13.4f) * k;
        float sh = (tiny ? 8.6f : 8.8f) * k;
        float sr = (tiny ? 2.6f : 2.5f) * k;
        float sy = 12f * k - sh / 2f;   // vertically centred on the 12 line

        using (GraphicsPath shell = Rounded(sx, sy, sw, sh, sr))
        using (Pen p = new Pen(Ink, stroke))
        {
            p.LineJoin = LineJoin.Round;
            g.DrawPath(p, shell);
        }

        if (!tiny)
        {
            // charge inside the shell, hugging the left terminal so it reads as a
            // level rather than a block floating in the middle
            using (GraphicsPath level = Rounded(5.7f * k, 9.7f * k, 5.5f * k, 4.6f * k, 1.1f * k))
            using (SolidBrush b = new SolidBrush(Ink))
                g.FillPath(b, level);
        }

        // terminal nub on the right edge
        float nubW = (tiny ? 2.0f : 1.9f) * k;
        float nubH = (tiny ? 3.6f : 3.2f) * k;
        float nubX = sx + sw + 0.5f * k;
        using (GraphicsPath nub = Rounded(nubX, 12f * k - nubH / 2f, nubW, nubH, nubW / 2f))
        using (SolidBrush b = new SolidBrush(Ink))
            g.FillPath(b, nub);
    }

    static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            Paint(g, size);
        }
        return bmp;
    }

    static void WriteIco(string outPath)
    {
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
    }

    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: MakeIcon <out.ico> | MakeIcon <out.png> <size>");
            Environment.ExitCode = 1;
            return;
        }

        string outPath = args[0];
        if (outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            int size = args.Length > 1 ? int.Parse(args[1]) : 512;
            using (Bitmap b = Render(size)) b.Save(outPath, ImageFormat.Png);
        }
        else
        {
            WriteIco(outPath);
        }
        Console.WriteLine("wrote " + outPath);
    }
}
