using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Periphora.UI
{
    /// <summary>
    /// macOS Control-Centre styled panel renderer.
    /// Pure GDI+, no external resources. All geometry is expressed in design units
    /// and multiplied by scale so the panel measures the same on any DPI.
    /// </summary>
    public static class ModernRenderer
    {
        // ---- design geometry (logical pixels @100%) ----
        public const float DesignW = 704f;
        public const float HeaderH = 112f;
        public const float RowH = 150f;
        public const float FooterH = 62f;
        public const float PadX = 36f;
        // Corner radius shared by the painted panel and the window region. Both
        // layers must use the identical shape or the acrylic leaks out as square
        // patches at the corners.
        public const float CornerRadius = 26f;

        // ---- palette ----
        // Background board is translucent so the DWM acrylic blur behind the window
        // shows through — this is what gives the panel its frosted-glass look.
        private static readonly Color PanelTop = Color.FromArgb(205, 252, 252, 254);
        private static readonly Color PanelBottom = Color.FromArgb(205, 238, 238, 243);
        private static readonly Color PanelStroke = Color.FromArgb(190, 255, 255, 255);
        private static readonly Color Sep = Color.FromArgb(232, 232, 237);
        private static readonly Color TitleColor = Color.FromArgb(58, 58, 62);
        private static readonly Color NameColor = Color.FromArgb(44, 44, 48);
        private static readonly Color SubColor = Color.FromArgb(142, 142, 148);
        private static readonly Color IconColor = Color.FromArgb(122, 122, 130);
        private static readonly Color BtnBg = Color.FromArgb(224, 224, 231);
        private static readonly Color BtnBgHot = Color.FromArgb(206, 206, 215);
        private static readonly Color BtnIcon = Color.FromArgb(92, 92, 100);
        private static readonly Color TrackColor = Color.FromArgb(228, 228, 233);
        private static readonly Color Green = Color.FromArgb(38, 200, 106);
        private static readonly Color Amber = Color.FromArgb(238, 176, 40);
        private static readonly Color Red = Color.FromArgb(235, 86, 86);
        private static readonly Color Grey = Color.FromArgb(174, 174, 182);
        private static readonly Color HintBg = Color.FromArgb(242, 242, 246);
        private static readonly Color HintText = Color.FromArgb(110, 110, 118);

        public enum ModernIcon { Keyboard, Mouse, Headset, Gamepad, Other }

        public class ModernRow
        {
            public ModernIcon Icon;
            public string Name;
            public string Subtitle;
            public int? Percent;
            public bool Online;
            public bool Sleeping;
            public bool Charging;
            public float Display;    // animated 0..100
        }

        public class PaintState
        {
            public List<ModernRow> Rows = new List<ModernRow>();
            public string UpdatedText = "";
            public string IntervalText = "";
            public float RefreshSpin;     // 0..1, one full turn
            public float RefreshDeny;     // 0..1 shake
            public bool RefreshHot;
            public bool SettingsHot;
            public bool ChevronHot;
            public string Hint = "";
            public float HintAlpha;
            public bool Empty;
        }

        // ---- cached fonts ----
        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        private static readonly Dictionary<string, Bitmap> _bgCache = new Dictionary<string, Bitmap>();

        private static Font Face(bool cjk, float size, bool bold)
        {
            string name = cjk ? "Microsoft YaHei UI" : "Segoe UI";
            string key = name + "|" + size.ToString("F1") + "|" + bold;
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            try { f = new Font(name, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { f = new Font(FontFamily.GenericSansSerif, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
            _fonts[key] = f;
            return f;
        }

        private static float Ascent(Font f)
        {
            float em = f.FontFamily.GetEmHeight(f.Style);
            if (em <= 0f) return f.Size * 0.8f;
            return f.FontFamily.GetCellAscent(f.Style) * f.Size / em;
        }

        /// <summary>Draws text by baseline, which is how the layout numbers are defined.
        /// For Far/Center alignment, x is the right edge / centre; a wide virtual
        /// rectangle keeps the glyphs inside the window instead of clipping them.</summary>
        private static void Text(Graphics g, string s, bool cjk, float size, bool bold,
                                 float x, float baseline, Color color, StringAlignment align)
        {
            if (string.IsNullOrEmpty(s)) return;
            Font f = Face(cjk, size, bold);

            // Resolve alignment into an explicit left edge. GDI+'s Far/Center
            // alignment inside a very wide rectangle is unreliable in GDI+;
            // measuring and drawing left-aligned always lands where intended.
            float drawX = x;
            if (align != StringAlignment.Near)
            {
                float tw;
                using (StringFormat mf = new StringFormat(StringFormat.GenericTypographic))
                    tw = g.MeasureString(s, f, 10000, mf).Width;
                if (align == StringAlignment.Far) drawX = x - tw;
                else drawX = x - tw / 2f;
            }

            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Near;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(s, f, b, new RectangleF(drawX, baseline - Ascent(f),
                    g.MeasureString(s, f, 10000, sf).Width + 8f,
                    f.GetHeight(g) + 6f), sf);
            }
        }

        /// <summary>Measures a single-line string (generic typographic kerning).</summary>
        private static float Measure(Graphics g, string s, bool cjk, float size, bool bold)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                return g.MeasureString(s, Face(cjk, size, bold), 10000, sf).Width;
            }
        }

        /// <summary>Shrinks a string with ellipsis so a long model never reaches the gauge.</summary>
        private static string Fit(Graphics g, string s, bool cjk, float size, bool bold, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (Measure(g, s, cjk, size, bold) <= maxW) return s;
            string t = s;
            while (t.Length > 1 && Measure(g, t + "…", cjk, size, bold) > maxW)
                t = t.Substring(0, t.Length - 1);
            return t + "…";
        }

        private static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2f;
            if (d > w) d = w;
            if (d > h) d = h;
            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            p.AddArc(x, y + h - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static int HeightFor(int rows, float scale)
        {
            int n = rows < 1 ? 1 : rows;
            return (int)Math.Ceiling((HeaderH + n * RowH + FooterH) * scale);
        }

        private static Bitmap Background(int w, int h, float s)
        {
            string key = w + "x" + h + "@" + s.ToString("F2");
            Bitmap bmp;
            if (_bgCache.TryGetValue(key, out bmp)) return bmp;
            if (_bgCache.Count > 4)
            {
                foreach (Bitmap b in _bgCache.Values) b.Dispose();
                _bgCache.Clear();
            }

            bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float radius = CornerRadius * s;
                using (GraphicsPath panel = Round(0.5f, 0.5f, w - 1f, h - 1f, radius))
                {
                    using (LinearGradientBrush grad = new LinearGradientBrush(
                        new PointF(0f, 0f), new PointF(0f, h), PanelTop, PanelBottom))
                    {
                        g.FillPath(grad, panel);
                    }
                    using (Pen pen = new Pen(PanelStroke, Math.Max(1f, s)))
                    {
                        g.DrawPath(pen, panel);
                    }

                    // very soft colour wash near the top, to hint at frosted glass
                    g.SetClip(panel);
                    using (LinearGradientBrush wash = new LinearGradientBrush(
                        new PointF(0f, 0f), new PointF(w, h * 0.6f),
                        Color.FromArgb(16, 236, 150, 170), Color.FromArgb(0, 255, 255, 255)))
                    {
                        g.FillRectangle(wash, 0f, 0f, w, h * 0.7f);
                    }
                    g.ResetClip();
                }
            }
            _bgCache[key] = bmp;
            return bmp;
        }

        public static void Paint(Graphics g, int w, int h, float s, PaintState st)
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // panel surface
            g.DrawImage(Background(w, h, s), 0, 0);

            // popover arrow at top centre
            DrawArrow(g, w, s);

            float px = PadX * s;
            float right = w - px;

            // ---- header ----
            Text(g, "外设电量", true, 24f * s, true, px, 72f * s, TitleColor, StringAlignment.Near);
            DrawButtons(g, w, s, st);

            // header separator
            using (Pen p = new Pen(Sep, Math.Max(1f, s)))
                g.DrawLine(p, px, HeaderH * s - 0.5f, right, HeaderH * s - 0.5f);

            // ---- rows ----
            int n = st.Rows.Count;
            if (st.Empty || n == 0)
            {
                DrawEmpty(g, w, h, s);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float top = HeaderH * s + i * RowH * s;
                    DrawRow(g, st.Rows[i], top, w, s);
                    if (i < n - 1)
                        using (Pen p = new Pen(Sep, Math.Max(1f, s)))
                            g.DrawLine(p, px, top + RowH * s - 0.5f, right, top + RowH * s - 0.5f);
                }
            }

            // ---- footer ----
            float fy = (HeaderH + Math.Max(1, n) * RowH) * s;
            Text(g, st.UpdatedText, true, 14f * s, false, px, fy + 40f * s, SubColor, StringAlignment.Near);
            Text(g, st.IntervalText, true, 14f * s, false, right, fy + 40f * s, SubColor, StringAlignment.Far);

            // ---- hint toast ----
            if (st.HintAlpha > 0.01f && !string.IsNullOrEmpty(st.Hint))
                DrawHint(g, w, s, st);
        }

        private static void DrawArrow(Graphics g, int w, float s)
        {
            float cx = w * 0.5f;
            float half = 13f * s;
            float hgt = 11f * s;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddLine(cx - half, 0.5f, cx, hgt);
                p.AddLine(cx, hgt, cx + half, 0.5f);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(PanelTop))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(PanelStroke, Math.Max(1f, s)))
                {
                    g.DrawLine(pen, cx - half, 0.5f, cx, hgt);
                    g.DrawLine(pen, cx, hgt, cx + half, 0.5f);
                }
            }
        }

        // button hit rects, in client pixels
        public static RectangleF[] ButtonRects(int w, float s)
        {
            float bw = 64f * s, bh = 52f * s, gap = 12f * s;
            float pad = PadX * s;
            float right = w - pad;
            float x3 = right - bw;
            float x2 = x3 - gap - bw;
            float x1 = x2 - gap - bw;
            float y = 26f * s;
            return new RectangleF[]
            {
                new RectangleF(x1, y, bw, bh),
                new RectangleF(x2, y, bw, bh),
                new RectangleF(x3, y, bw, bh)
            };
        }

        public static int HitButton(int x, int y, int w, float s)
        {
            RectangleF[] r = ButtonRects(w, s);
            for (int i = 0; i < r.Length; i++)
                if (r[i].Contains(x, y)) return i + 1;
            return 0;
        }

        private static void DrawButtons(Graphics g, int w, float s, PaintState st)
        {
            RectangleF[] r = ButtonRects(w, s);
            bool[] hot = { st.RefreshHot, st.SettingsHot, st.ChevronHot };
            for (int i = 0; i < 3; i++)
            {
                using (GraphicsPath p = Round(r[i].X, r[i].Y, r[i].Width, r[i].Height, 14f * s))
                {
                    using (SolidBrush b = new SolidBrush(hot[i] ? BtnBgHot : BtnBg))
                        g.FillPath(b, p);
                }
            }
            DrawRefreshIcon(g, r[0], s, st);
            DrawGear(g, r[1], s);
            DrawChevron(g, r[2], s);
        }

        private static void DrawRefreshIcon(Graphics g, RectangleF r, float s, PaintState st)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            GraphicsState gs = g.Save();
            g.TranslateTransform(cx, cy);
            if (st.RefreshSpin > 0f)
            {
                float spin = 360f * st.RefreshSpin;
                float e = 1f;
                float t = st.RefreshSpin;
                float inv = 1f - t; e = 1f - inv * inv * inv;
                g.RotateTransform(spin * e);
            }
            if (st.RefreshDeny > 0f)
            {
                float shake = (float)Math.Sin(st.RefreshDeny * Math.PI * 4f) * 10f * (1f - st.RefreshDeny);
                g.RotateTransform(shake);
            }
            g.TranslateTransform(-cx, -cy);

            float rad = 10f * s;
            using (Pen p = new Pen(BtnIcon, 2.4f * s))
            {
                g.DrawArc(p, cx - rad, cy - rad, rad * 2f, rad * 2f, 40f, 300f);
            }
            // arrow head at the arc start
            float ang = 40f * (float)Math.PI / 180f;
            float ax = cx + (float)Math.Cos(ang) * rad;
            float ay = cy + (float)Math.Sin(ang) * rad;
            using (SolidBrush b = new SolidBrush(BtnIcon))
            {
                GraphicsPath tri = new GraphicsPath();
                tri.AddLine(ax - 5f * s, ay - 1f * s, ax + 2f * s, ay - 5f * s);
                tri.AddLine(ax + 2f * s, ay - 5f * s, ax + 1f * s, ay + 4f * s);
                tri.CloseFigure();
                g.FillPath(b, tri);
            }
            g.Restore(gs);
        }

        private static void DrawGear(Graphics g, RectangleF r, float s)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float ro = 11f * s, ri = 7.5f * s;
            using (SolidBrush b = new SolidBrush(BtnIcon))
            {
                for (int i = 0; i < 8; i++)
                {
                    GraphicsState gs = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(i * 45f);
                    g.FillRectangle(b, -2.4f * s, -ro, 4.8f * s, 4.6f * s);
                    g.Restore(gs);
                }
                g.FillEllipse(b, cx - ro + 1f * s, cy - ro + 1f * s, (ro - 1f * s) * 2f, (ro - 1f * s) * 2f);
            }
            using (SolidBrush hole = new SolidBrush(BtnBg))
            {
                g.FillEllipse(hole, cx - ri * 0.62f, cy - ri * 0.62f, ri * 1.24f, ri * 1.24f);
            }
        }

        private static void DrawChevron(Graphics g, RectangleF r, float s)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f - 1f * s;
            float w = 8f * s, h = 5f * s;
            using (Pen p = new Pen(BtnIcon, 2.8f * s))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLine(p, cx - w, cy - h * 0.3f, cx, cy + h * 0.7f);
                g.DrawLine(p, cx, cy + h * 0.7f, cx + w, cy - h * 0.3f);
            }
        }

        private static Color LevelColor(int? pct, bool online, bool sleeping)
        {
            if (!pct.HasValue || !online) return Grey;
            int p = pct.Value;
            if (p >= 50) return Green;
            if (p >= 20) return Amber;
            return Red;
        }

        private static void DrawRow(Graphics g, ModernRow row, float top, int w, float s)
        {
            float cy = top + RowH * s * 0.5f;

            // icon
            DrawDeviceIcon(g, row.Icon, PadX * s + 22f * s, cy, s);

            float textX = PadX * s + 90f * s;
            float gaugeRight = w - PadX * s;

            // name + subtitle
            string name = row.Name ?? "";
            bool cjk = HasCjk(name);
            float nameSize = 21f * s;
            float maxName = (gaugeRight - 190f * s) - textX;
            name = Fit(g, name, cjk, nameSize, true, maxName);
            Text(g, name, cjk, nameSize, true, textX, top + 64f * s, NameColor, StringAlignment.Near);

            string sub = row.Subtitle ?? "";
            if (!string.IsNullOrEmpty(sub))
            {
                bool scjk = HasCjk(sub);
                sub = Fit(g, sub, scjk, 15f * s, false, maxName);
                Text(g, sub, scjk, 15f * s, false, textX, top + 96f * s, SubColor, StringAlignment.Near);
            }

            // gauge: big number on top, thin progress bar underneath
            float disp = row.Display;
            bool hasLevel = row.Percent.HasValue && row.Online;
            string pctText = hasLevel
                ? ((row.Percent.Value >= 100) ? "100%" : row.Percent.Value + "%")
                : "--";
            Color col = LevelColor(row.Percent, row.Online, row.Sleeping);
            if (!hasLevel) col = Grey;
            Text(g, pctText, false, 34f * s, true, gaugeRight, top + 68f * s, col, StringAlignment.Far);

            float barW = 170f * s;
            float barH = 9f * s;
            float barX = gaugeRight - barW;
            float barY = top + 88f * s;
            using (GraphicsPath slot = Round(barX, barY, barW, barH, barH / 2f))
            using (SolidBrush sb = new SolidBrush(TrackColor))
                g.FillPath(sb, slot);
            if (hasLevel && disp > 0.5f)
            {
                float fill = Math.Max(barH, barW * (Math.Min(100f, disp) / 100f));
                using (GraphicsPath fp = Round(barX, barY, fill, barH, barH / 2f))
                using (SolidBrush fb = new SolidBrush(col))
                    g.FillPath(fb, fp);
            }
        }

        private static void DrawDeviceIcon(Graphics g, ModernIcon kind, float cx, float cy, float s)
        {
            float bx = cx - 22f * s, by = cy - 22f * s, bw = 44f * s, bh = 44f * s;
            using (Pen p = new Pen(IconColor, 2.2f * s))
            {
                p.LineJoin = LineJoin.Round;
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                switch (kind)
                {
                    case ModernIcon.Mouse:
                    {
                        using (GraphicsPath body = Round(cx - 14f * s, cy - 20f * s, 28f * s, 40f * s, 14f * s))
                            g.DrawPath(p, body);
                        g.DrawLine(p, cx, cy - 20f * s, cx, cy - 4f * s);
                        g.DrawLine(p, cx - 14f * s, cy - 4f * s, cx + 14f * s, cy - 4f * s);
                        using (GraphicsPath wheel = Round(cx - 2.5f * s, cy - 15f * s, 5f * s, 9f * s, 2.5f * s))
                            g.DrawPath(p, wheel);
                        break;
                    }
                    case ModernIcon.Keyboard:
                    {
                        using (GraphicsPath body = Round(bx + 1f * s, cy - 13f * s, 42f * s, 26f * s, 4f * s))
                            g.DrawPath(p, body);
                        // key rows
                        for (int row = 0; row < 2; row++)
                        {
                            float ky = cy - 7f * s + row * 8f * s;
                            for (int c = 0; c < 4; c++)
                            {
                                float kx = cx - 15f * s + c * 9f * s;
                                g.DrawRectangle(p, kx, ky, 5f * s, 4f * s);
                            }
                        }
                        g.DrawLine(p, cx - 10f * s, cy + 10f * s, cx + 10f * s, cy + 10f * s);
                        break;
                    }
                    case ModernIcon.Headset:
                    {
                        using (GraphicsPath arc = new GraphicsPath())
                        {
                            arc.AddArc(cx - 16f * s, cy - 18f * s, 32f * s, 30f * s, 180f, 180f);
                            g.DrawPath(p, arc);
                        }
                        g.FillRectangle(new SolidBrush(IconColor), cx - 18f * s, cy - 3f * s, 7f * s, 14f * s);
                        g.FillRectangle(new SolidBrush(IconColor), cx + 11f * s, cy - 3f * s, 7f * s, 14f * s);
                        break;
                    }
                    default:
                    {
                        using (GraphicsPath dot = Round(cx - 12f * s, cy - 12f * s, 24f * s, 24f * s, 6f * s))
                            g.DrawPath(p, dot);
                        break;
                    }
                }
            }
        }

        private static void DrawEmpty(Graphics g, int w, int h, float s)
        {
            float cy = HeaderH * s + RowH * s * 0.5f;
            Text(g, "没有检测到受支持的设备", true, 16f * s, false,
                w / 2f, cy, SubColor, StringAlignment.Center);
            Text(g, "请确认接收器已插入，设备已开机", true, 13f * s, false,
                w / 2f, cy + 30f * s, Grey, StringAlignment.Center);
        }

        private static void DrawHint(Graphics g, int w, float s, PaintState st)
        {
            float tw = Measure(g, st.Hint, true, 14f * s, false) + 36f * s;
            float th = 40f * s;
            float x = (w - tw) / 2f;
            float y = (HeaderH - 4f) * s;
            int a = (int)(Math.Max(0f, Math.Min(1f, st.HintAlpha)) * 255f);
            using (GraphicsPath p = Round(x, y, tw, th, 12f * s))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a), HintBg)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(Math.Min(255, a), PanelStroke), Math.Max(1f, s)))
                    g.DrawPath(pen, p);
            }
            Text(g, st.Hint, true, 14f * s, false, w / 2f - tw / 2f + 18f * s,
                y + 26f * s, Color.FromArgb(Math.Min(255, a), HintText), StringAlignment.Near);
        }

        private static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= 0x2E80 && s[i] <= 0x9FFF) return true;
            return false;
        }
    }
}
