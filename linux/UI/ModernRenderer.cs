using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Periphora.Linux.UI
{
    internal enum ModernIcon { Keyboard, Mouse, Headset, Gamepad, Other }

    internal sealed class ModernRow
    {
        internal ModernIcon Icon;
        internal string Name = "";
        internal string Subtitle = "";
        internal int? Percent;
        internal bool Online;
        internal bool Sleeping;
        internal bool Charging;
        internal double Display;
        internal double Opacity = 1;
    }

    internal sealed class PaintState
    {
        internal List<ModernRow> Rows = new List<ModernRow>();
        internal string UpdatedText = "";
        internal string IntervalText = "";
        internal double RefreshSpin;
        internal double RefreshDeny;
        internal bool RefreshHot;
        internal bool SettingsHot;
        internal bool ChevronHot;
        internal string Hint = "";
        internal double HintAlpha;
        internal bool Empty;
    }

    /// <summary>
    /// Linux/Skia port of the light control-centre panel. Geometry, colours,
    /// baselines and gauge measurements intentionally match ModernRenderer.
    /// </summary>
    internal static class ModernRenderer
    {
        internal const double DesignW = 704;
        internal const double HeaderH = 112;
        internal const double RowH = 150;
        internal const double FooterH = 62;
        internal const double PadX = 36;
        internal const double CornerRadius = 26;

        private static readonly Color PanelTop = Color.FromArgb(205, 252, 252, 254);
        private static readonly Color PanelBottom = Color.FromArgb(205, 238, 238, 243);
        private static readonly Color PanelStroke = Color.FromArgb(190, 255, 255, 255);
        private static readonly Color Sep = Color.FromArgb(255, 232, 232, 237);
        private static readonly Color TitleColor = Color.FromArgb(255, 58, 58, 62);
        private static readonly Color NameColor = Color.FromArgb(255, 44, 44, 48);
        private static readonly Color SubColor = Color.FromArgb(255, 142, 142, 148);
        private static readonly Color IconColor = Color.FromArgb(255, 122, 122, 130);
        private static readonly Color BtnBg = Color.FromArgb(255, 224, 224, 231);
        private static readonly Color BtnBgHot = Color.FromArgb(255, 206, 206, 215);
        private static readonly Color BtnIcon = Color.FromArgb(255, 92, 92, 100);
        private static readonly Color TrackColor = Color.FromArgb(255, 228, 228, 233);
        private static readonly Color Green = Color.FromArgb(255, 38, 200, 106);
        private static readonly Color Amber = Color.FromArgb(255, 238, 176, 40);
        private static readonly Color Red = Color.FromArgb(255, 235, 86, 86);
        private static readonly Color Grey = Color.FromArgb(255, 174, 174, 182);
        private static readonly Color HintBg = Color.FromArgb(255, 242, 242, 246);
        private static readonly Color HintText = Color.FromArgb(255, 110, 110, 118);

        private static readonly FontFamily CjkFamily =
            new FontFamily("Noto Sans CJK SC, Source Han Sans SC, WenQuanYi Micro Hei, WenQuanYi Zen Hei, sans-serif");
        private static readonly FontFamily LatinFamily =
            new FontFamily("Montserrat, Noto Sans, DejaVu Sans, sans-serif");

        private static IBrush Brush(Color color) { return new SolidColorBrush(color); }

        private static FormattedText TextShape(string text, bool cjk, double size, bool bold, Color color)
        {
            Typeface face = new Typeface(cjk ? CjkFamily : LatinFamily, FontStyle.Normal,
                bold ? FontWeight.SemiBold : FontWeight.Normal);
            return new FormattedText(text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face, size, Brush(color));
        }

        private static double Measure(string text, bool cjk, double size, bool bold)
        {
            return string.IsNullOrEmpty(text) ? 0 : TextShape(text, cjk, size, bold, NameColor).Width;
        }

        private static void Text(DrawingContext context, string text, bool cjk, double size, bool bold,
            double x, double baseline, Color color, TextAlign align = TextAlign.Left)
        {
            if (string.IsNullOrEmpty(text)) return;
            FormattedText shape = TextShape(text, cjk, size, bold, color);
            double drawX = x;
            if (align == TextAlign.Right) drawX = x - shape.Width;
            else if (align == TextAlign.Center) drawX = x - shape.Width / 2;
            context.DrawText(shape, new Point(drawX, baseline - shape.Baseline));
        }

        private static string Fit(string text, bool cjk, double size, bool bold, double maxWidth)
        {
            if (string.IsNullOrEmpty(text) || Measure(text, cjk, size, bold) <= maxWidth) return text;
            string value = text;
            while (value.Length > 1 && Measure(value + "…", cjk, size, bold) > maxWidth)
                value = value.Substring(0, value.Length - 1);
            return value + "…";
        }

        private static bool HasCjk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text) if (c >= 0x2E80 && c <= 0x9FFF) return true;
            return false;
        }

        private static void RoundRect(DrawingContext context, IBrush fill, IPen pen,
            double x, double y, double w, double h, double r)
        {
            double radius = Math.Min(r, Math.Min(w, h) / 2);
            context.DrawRectangle(fill, pen, new Rect(x, y, w, h), radius, radius);
        }

        private static StreamGeometry Polygon(params Point[] points)
        {
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext c = geometry.Open())
            {
                c.BeginFigure(points[0], true);
                for (int i = 1; i < points.Length; i++) c.LineTo(points[i]);
                c.EndFigure(true);
            }
            return geometry;
        }

        private static StreamGeometry Arc(double cx, double cy, double radius, double startDeg, double sweepDeg)
        {
            double start = startDeg * Math.PI / 180;
            double end = (startDeg + sweepDeg) * Math.PI / 180;
            Point from = new Point(cx + Math.Cos(start) * radius, cy + Math.Sin(start) * radius);
            Point to = new Point(cx + Math.Cos(end) * radius, cy + Math.Sin(end) * radius);
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext c = geometry.Open())
            {
                c.BeginFigure(from, false);
                c.ArcTo(to, new Size(radius, radius), 0, Math.Abs(sweepDeg) > 180,
                    sweepDeg >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            }
            return geometry;
        }

        private static Point Rotate(Point point, Point center, double degrees)
        {
            double angle = degrees * Math.PI / 180;
            double dx = point.X - center.X;
            double dy = point.Y - center.Y;
            return new Point(center.X + dx * Math.Cos(angle) - dy * Math.Sin(angle),
                center.Y + dx * Math.Sin(angle) + dy * Math.Cos(angle));
        }

        internal static double HeightFor(int rows)
        {
            int n = rows < 1 ? 1 : rows;
            return Math.Ceiling(HeaderH + n * RowH + FooterH);
        }

        internal static Rect[] ButtonRects(double width)
        {
            double bw = 64, bh = 52, gap = 12;
            double right = width - PadX;
            double x3 = right - bw;
            double x2 = x3 - gap - bw;
            double x1 = x2 - gap - bw;
            return new[]
            {
                new Rect(x1, 26, bw, bh),
                new Rect(x2, 26, bw, bh),
                new Rect(x3, 26, bw, bh)
            };
        }

        internal static int HitButton(double x, double y, double width)
        {
            Rect[] rects = ButtonRects(width);
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(new Point(x, y))) return i + 1;
            return 0;
        }

        internal static void Paint(DrawingContext context, double w, double h, PaintState state)
        {
            // Panel surface. The translucent top/bottom colours preserve the
            // original frosted look while the transparent window exposes corners.
            LinearGradientBrush background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(PanelTop, 0),
                    new GradientStop(PanelBottom, 1)
                }
            };
            Pen border = new Pen(Brush(PanelStroke), 1);
            RoundRect(context, background, border, 0.5, 0.5, w - 1, h - 1, CornerRadius);

            LinearGradientBrush wash = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.6, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb(16, 236, 150, 170), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                }
            };
            RoundRect(context, wash, null, 0.5, 0.5, w - 1, h - 1, CornerRadius);

            DrawArrow(context, w);

            double px = PadX;
            double right = w - px;
            Text(context, "外设电量", true, 24, true, px, 72, TitleColor);
            DrawButtons(context, w, state);

            context.DrawLine(new Pen(Brush(Sep), 1), new Point(px, HeaderH - 0.5), new Point(right, HeaderH - 0.5));

            int n = state.Rows.Count;
            if (state.Empty || n == 0)
            {
                DrawEmpty(context, w);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    double top = HeaderH + i * RowH;
                    using (context.PushOpacity(Math.Max(0, Math.Min(1, state.Rows[i].Opacity))))
                        DrawRow(context, state.Rows[i], top, w);
                    if (i < n - 1)
                        context.DrawLine(new Pen(Brush(Sep), 1),
                            new Point(px, top + RowH - 0.5), new Point(right, top + RowH - 0.5));
                }
            }

            double fy = HeaderH + Math.Max(1, n) * RowH;
            Text(context, state.UpdatedText, true, 14, false, px, fy + 40, SubColor);
            Text(context, state.IntervalText, true, 14, false, right, fy + 40, SubColor, TextAlign.Right);

            if (state.HintAlpha > 0.01 && !string.IsNullOrEmpty(state.Hint))
                DrawHint(context, w, state);
        }

        private static void DrawArrow(DrawingContext context, double w)
        {
            double cx = w / 2;
            const double half = 13;
            const double height = 11;
            context.DrawGeometry(Brush(PanelTop), new Pen(Brush(PanelStroke), 1),
                Polygon(new Point(cx - half, 0.5), new Point(cx, height), new Point(cx + half, 0.5)));
        }

        private static void DrawButtons(DrawingContext context, double w, PaintState state)
        {
            Rect[] rects = ButtonRects(w);
            bool[] hot = { state.RefreshHot, state.SettingsHot, state.ChevronHot };
            for (int i = 0; i < 3; i++)
                RoundRect(context, Brush(hot[i] ? BtnBgHot : BtnBg), null,
                    rects[i].X, rects[i].Y, rects[i].Width, rects[i].Height, 14);
            DrawRefresh(context, rects[0], state);
            DrawGear(context, rects[1]);
            DrawChevron(context, rects[2]);
        }

        private static void DrawRefresh(DrawingContext context, Rect rect, PaintState state)
        {
            Point center = rect.Center;
            double angle = state.RefreshSpin > 0 ? 360 * EaseOutCubic(state.RefreshSpin) : 0;
            if (state.RefreshDeny > 0)
                angle += Math.Sin(state.RefreshDeny * Math.PI * 4) * 10 * (1 - state.RefreshDeny);

            const double radius = 10;
            Point start = Rotate(new Point(center.X + Math.Cos(40 * Math.PI / 180) * radius,
                center.Y + Math.Sin(40 * Math.PI / 180) * radius), center, angle);
            Point end = Rotate(new Point(center.X + Math.Cos(340 * Math.PI / 180) * radius,
                center.Y + Math.Sin(340 * Math.PI / 180) * radius), center, angle);
            StreamGeometry arc = new StreamGeometry();
            using (StreamGeometryContext c = arc.Open())
            {
                c.BeginFigure(start, false);
                c.ArcTo(end, new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            }
            context.DrawGeometry(null, new Pen(Brush(BtnIcon), 2.4, lineCap: PenLineCap.Round), arc);

            Point[] arrow =
            {
                new Point(start.X - 5, start.Y - 1),
                new Point(start.X + 2, start.Y - 5),
                new Point(start.X + 1, start.Y + 4)
            };
            for (int i = 0; i < arrow.Length; i++) arrow[i] = Rotate(arrow[i], center, angle);
            context.DrawGeometry(Brush(BtnIcon), null, Polygon(arrow));
        }

        private static double EaseOutCubic(double t)
        {
            double inverse = 1 - t;
            return 1 - inverse * inverse * inverse;
        }

        private static void DrawGear(DrawingContext context, Rect rect)
        {
            Point center = rect.Center;
            double inner = 7.5, outer = 11;
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                Point a = new Point(center.X + Math.Cos(angle) * 5, center.Y + Math.Sin(angle) * 5);
                Point b = new Point(center.X + Math.Cos(angle) * outer, center.Y + Math.Sin(angle) * outer);
                context.DrawLine(new Pen(Brush(BtnIcon), 4.8, lineCap: PenLineCap.Flat), a, b);
            }
            context.DrawEllipse(Brush(BtnIcon), null, center, outer - 1, outer - 1);
            context.DrawEllipse(Brush(BtnBg), null, center, inner * 0.62, inner * 0.62);
        }

        private static void DrawChevron(DrawingContext context, Rect rect)
        {
            Point center = new Point(rect.Center.X, rect.Center.Y - 1);
            Pen pen = new Pen(Brush(BtnIcon), 2.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawLine(pen, new Point(center.X - 8, center.Y - 1.5), new Point(center.X, center.Y + 3.5));
            context.DrawLine(pen, new Point(center.X, center.Y + 3.5), new Point(center.X + 8, center.Y - 1.5));
        }

        private static Color LevelColor(int? percent, bool online)
        {
            if (!percent.HasValue || !online) return Grey;
            if (percent.Value >= 50) return Green;
            if (percent.Value >= 20) return Amber;
            return Red;
        }

        private static void DrawRow(DrawingContext context, ModernRow row, double top, double width)
        {
            double cy = top + RowH * 0.5;
            DrawDeviceIcon(context, row.Icon, PadX + 22, cy);

            double textX = PadX + 90;
            double gaugeRight = width - PadX;
            double maxName = (gaugeRight - 190) - textX;

            string name = Fit(row.Name ?? "", HasCjk(row.Name), 21, true, maxName);
            Text(context, name, HasCjk(name), 21, true, textX, top + 64, NameColor);
            string subtitle = Fit(row.Subtitle ?? "", HasCjk(row.Subtitle), 15, false, maxName);
            Text(context, subtitle, HasCjk(subtitle), 15, false, textX, top + 96, SubColor);

            // A sleeping device keeps its last figure in grey with an empty bar;
            // only a device that never answered stays at "--".
            bool hasValue = row.Percent.HasValue;
            bool live = hasValue && row.Online;
            string percentText = hasValue ? (row.Percent.Value >= 100 ? "100%" : row.Percent.Value + "%") : "--";
            Color color = live ? LevelColor(row.Percent, row.Online) : Grey;
            Text(context, percentText, false, 34, true, gaugeRight, top + 68, color, TextAlign.Right);

            double barW = 170, barH = 9;
            double barX = gaugeRight - barW;
            double barY = top + 88;
            RoundRect(context, Brush(TrackColor), null, barX, barY, barW, barH, barH / 2);
            if (live && row.Display > 0.5)
            {
                double fill = Math.Max(barH, barW * (Math.Min(100, row.Display) / 100));
                RoundRect(context, Brush(color), null, barX, barY, fill, barH, barH / 2);
            }
        }

        private static void DrawDeviceIcon(DrawingContext context, ModernIcon kind, double cx, double cy)
        {
            Pen pen = new Pen(Brush(IconColor), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            switch (kind)
            {
                case ModernIcon.Mouse:
                    RoundRect(context, null, pen, cx - 14, cy - 20, 28, 40, 14);
                    context.DrawLine(pen, new Point(cx, cy - 20), new Point(cx, cy - 4));
                    context.DrawLine(pen, new Point(cx - 14, cy - 4), new Point(cx + 14, cy - 4));
                    RoundRect(context, null, pen, cx - 2.5, cy - 15, 5, 9, 2.5);
                    break;
                case ModernIcon.Keyboard:
                    RoundRect(context, null, pen, cx - 21, cy - 13, 42, 26, 4);
                    for (int row = 0; row < 2; row++)
                        for (int column = 0; column < 4; column++)
                            context.DrawRectangle(null, pen,
                                new Rect(cx - 15 + column * 9, cy - 7 + row * 8, 5, 4));
                    context.DrawLine(pen, new Point(cx - 10, cy + 10), new Point(cx + 10, cy + 10));
                    break;
                case ModernIcon.Headset:
                    context.DrawGeometry(null, pen, Arc(cx, cy - 2, 16, 180, 180));
                    context.DrawRectangle(Brush(IconColor), null, new Rect(cx - 18, cy - 3, 7, 14), 2, 2);
                    context.DrawRectangle(Brush(IconColor), null, new Rect(cx + 11, cy - 3, 7, 14), 2, 2);
                    break;
                default:
                    RoundRect(context, null, pen, cx - 12, cy - 12, 24, 24, 6);
                    break;
            }
        }

        private static void DrawEmpty(DrawingContext context, double width)
        {
            double cy = HeaderH + RowH * 0.5;
            Text(context, "没有检测到受支持的设备", true, 16, false, width / 2, cy, SubColor, TextAlign.Center);
            Text(context, "请确认接收器已插入，设备已开机", true, 13, false,
                width / 2, cy + 30, Grey, TextAlign.Center);
        }

        private static void DrawHint(DrawingContext context, double width, PaintState state)
        {
            double textWidth = Measure(state.Hint, true, 14, false) + 36;
            double height = 40;
            double x = (width - textWidth) / 2;
            double y = HeaderH - 4;
            byte alpha = (byte)(Math.Max(0, Math.Min(1, state.HintAlpha)) * 255);
            RoundRect(context,
                Brush(Color.FromArgb((byte)(alpha * HintBg.A / 255), HintBg.R, HintBg.G, HintBg.B)),
                new Pen(Brush(Color.FromArgb((byte)(alpha * PanelStroke.A / 255), 255, 255, 255)), 1),
                x, y, textWidth, height, 12);
            Text(context, state.Hint, true, 14, false, x + 18, y + 26,
                Color.FromArgb((byte)(alpha * HintText.A / 255), HintText.R, HintText.G, HintText.B));
        }

        internal enum TextAlign { Left, Center, Right }
    }
}
