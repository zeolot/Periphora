using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Periphora.UI
{
    internal enum MenuRowKind
    {
        Level,      // name + bar + percentage
        Asleep,     // name + grey bar + 休眠中
        Autostart,  // 开机启动 with a toggle
        Quit        // 退出应用
    }

    internal class MenuRow
    {
        public string Name;
        public string Value;
        public int? Percent;
        public bool GreyValue;   // sleeping row: last level shown dimmed
        public MenuRowKind Kind;
    }

    /// <summary>
    /// Tray popup, redrawn in the same light "control centre" language as the
    /// main panel: translucent frosted surface, soft rounded corners, no purple
    /// header and no grid. Rows are contiguous so the whole card reads as one
    /// glass object rather than a stack of dark strips.
    /// </summary>
    internal class MenuPanel : Control
    {
        // ---- geometry, design units @100% ----
        private const float W = 300f;
        private const float HeaderH = 50f;
        private const float RowH = 45f;
        private const float FooterPad = 8f;
        private const float Corner = 14f;
        private const float PadX = 18f;

        private const float BarX = 148f;
        private const float BarW = 88f;
        private const float BarH = 6f;
        private const float TextBaseline = 29f;   // relative to each row top

        // autostart pill
        private const float SwW = 44f;
        private const float SwH = 24f;
        private const float SwKnobR = 10f;

        // ---- palette, kept in step with ModernRenderer ----
        private static readonly Color PanelTop = Color.FromArgb(215, 252, 252, 254);
        private static readonly Color PanelBottom = Color.FromArgb(215, 238, 238, 243);
        private static readonly Color PanelStroke = Color.FromArgb(190, 255, 255, 255);
        private static readonly Color Sep = Color.FromArgb(230, 230, 235);
        private static readonly Color TitleColor = Color.FromArgb(58, 58, 62);
        private static readonly Color NameColor = Color.FromArgb(44, 44, 48);
        private static readonly Color Muted = Color.FromArgb(150, 150, 156);
        private static readonly Color Green = Color.FromArgb(38, 200, 106);
        private static readonly Color Amber = Color.FromArgb(238, 176, 40);
        private static readonly Color Red = Color.FromArgb(235, 86, 86);
        private static readonly Color Track = Color.FromArgb(226, 226, 232);
        private static readonly Color SwitchOff = Color.FromArgb(198, 198, 206);

        private readonly List<MenuRow> _rows;
        private readonly float _scale;

        // Cached faces, same pairing as the main panel (Segoe / YaHei UI).
        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        private readonly float[] _hover;
        private System.Windows.Forms.Timer _anim;
        private int _hot = -1;

        public event EventHandler QuitRequested;
        public event EventHandler AutostartToggled;

        private bool _autostartOn;
        private float _switchT;                                  // 0 = off, 1 = on (animated)
        private System.Windows.Forms.Timer _switchAnim;
        private bool _autostartEnabled = true;

        /// <summary>Current switch position, read back by the host when it saves.</summary>
        public bool AutostartOn
        {
            get { return _autostartOn; }
            set { _autostartOn = value; _switchT = value ? 1f : 0f; }
        }

        /// <summary>False when the process is not a real Periphora.exe (in-memory dev build),
        /// where writing a Run entry would point at powershell.exe instead.</summary>
        public bool AutostartEnabled { get { return _autostartEnabled; } set { _autostartEnabled = value; } }

        public int LineCount { get { return _rows.Count + 2; } }   // + autostart + quit

        public MenuPanel(List<MenuRow> rows, float scale,
                         Font nameFont, Font valueFont, Font cnFont, Font markFont)
        {
            _rows = rows;
            _scale = scale;
            _hover = new float[rows.Count + 2];

            SetStyle(ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);

            Margin = Padding.Empty;
            Size = new Size((int)Math.Round(W * scale), (int)Math.Round(TotalHeight() * scale));
            // The host window carries the acrylic blur; the surface we paint is
            // translucent so that blur is visible through it.
            BackColor = Color.Transparent;
        }

        /// <summary>50 + n*45 + 8.</summary>
        public float TotalHeight()
        {
            return HeaderH + LineCount * RowH + FooterPad;
        }

        public int ScaledCorner { get { return (int)Math.Round(Corner * _scale); } }

        private float RowTop(int index)
        {
            return HeaderH + index * RowH;
        }

        // --------------------------------------------------------------- input

        private int RowAt(int y)
        {
            for (int i = 0; i < LineCount; i++)
            {
                float top = RowTop(i) * _scale;
                float bottom = top + RowH * _scale;
                if (y >= top && y < bottom) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = RowAt(e.Y);
            if (hit != _hot)
            {
                _hot = hit;
                Cursor = (hit >= _rows.Count && hit <= _rows.Count + 1) ? Cursors.Hand : Cursors.Default;
                StartAnim();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = -1;
            Cursor = Cursors.Default;
            StartAnim();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseClick(e); return; }

            int hit = RowAt(e.Y);

            if (hit == _rows.Count && _autostartEnabled)
            {
                // Autostart row: flip the switch and let the host write the Run entry.
                _autostartOn = !_autostartOn;
                StartSwitchAnim();
                if (AutostartToggled != null) AutostartToggled(this, EventArgs.Empty);
            }
            else if (hit == _rows.Count + 1)
            {
                if (QuitRequested != null) QuitRequested(this, EventArgs.Empty);
            }

            base.OnMouseClick(e);
        }

        private void StartAnim()
        {
            if (_anim == null)
            {
                _anim = new System.Windows.Forms.Timer();
                _anim.Interval = 16;
                _anim.Tick += delegate
                {
                    bool moving = false;

                    for (int i = 0; i < _hover.Length; i++)
                    {
                        float target = (i == _hot) ? 1f : 0f;
                        float delta = target - _hover[i];

                        if (Math.Abs(delta) < 0.01f)
                        {
                            _hover[i] = target;
                            continue;
                        }

                        _hover[i] += delta * 0.35f;
                        moving = true;
                    }

                    Invalidate();
                    if (!moving) _anim.Stop();
                };
            }

            if (!_anim.Enabled) _anim.Start();
        }

        // -------------------------------------------------------------- paint

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float s = _scale;

            // One continuous glass surface, rounded on all four corners.
            using (GraphicsPath panel = Round(0.5f, 0.5f, Width - 1f, Height - 1f, Corner * s))
            {
                using (LinearGradientBrush grad = new LinearGradientBrush(
                    new PointF(0f, 0f), new PointF(0f, Height), PanelTop, PanelBottom))
                {
                    g.FillPath(grad, panel);
                }
                using (Pen pen = new Pen(PanelStroke, Math.Max(1f, s)))
                {
                    g.DrawPath(pen, panel);
                }
            }

            // Header: a quiet title, matching the main panel's 「外设电量」.
            DrawText(g, "外设电量", true, 16f * s, true,
                 PadX * s, 33f * s, TitleColor, StringAlignment.Near);

            using (Pen p = new Pen(Sep, Math.Max(1f, s)))
            {
                g.DrawLine(p, PadX * s, HeaderH * s - 0.5f,
                           Width - PadX * s, HeaderH * s - 0.5f);
            }

            for (int i = 0; i < LineCount; i++)
            {
                DrawRow(g, s, i);
            }
        }

        private void DrawRow(Graphics g, float s, int index)
        {
            float top = RowTop(index) * s;
            float h = RowH * s;

            // Row order: devices..., autostart, quit.
            bool isAutostart = index == _rows.Count;
            bool isQuit = index == _rows.Count + 1;
            MenuRow row = (isQuit || isAutostart) ? null : _rows[index];

            // Hover: a soft inset wash rather than a hard stripe.
            float hover = _hover[index];
            if (hover > 0.01f)
            {
                using (GraphicsPath hp = Round(6f * s, top + 2f * s,
                                               Width - 12f * s, h - 4f * s, 10f * s))
                using (SolidBrush hb = new SolidBrush(
                           Color.FromArgb((int)(22f * hover), 90, 90, 100)))
                {
                    g.FillPath(hb, hp);
                }
            }

            // Hairline between rows, inset like the main panel's separators.
            if (index < LineCount - 1)
            {
                using (Pen p = new Pen(Sep, Math.Max(1f, s)))
                {
                    g.DrawLine(p, PadX * s, top + h - 0.5f,
                               Width - PadX * s, top + h - 0.5f);
                }
            }

            float baseline = top + TextBaseline * s;

            if (isQuit)
            {
                DrawText(g, "退出应用", true, 14f * s, false,
                     PadX * s, baseline, NameColor, StringAlignment.Near);
                return;
            }

            if (isAutostart)
            {
                // Label dims slightly when the host cannot write a Run entry
                // (dev build running under powershell.exe).
                Color label = _autostartEnabled ? NameColor : Muted;
                DrawText(g, "开机启动", true, 14f * s, false,
                     PadX * s, baseline, label, StringAlignment.Near);
                DrawSwitch(g, s, top);
                return;
            }

            // ---- device row ----
            bool cjk = CardRenderer.HasCjk(row.Name);
            float nameSize = 17f * s;
            string name = Fit(g, row.Name, cjk, nameSize, (BarX - PadX - 8f) * s);
            Color nameColor = row.Kind == MenuRowKind.Asleep ? Muted : NameColor;
            DrawText(g, name, cjk, nameSize, false,
                 PadX * s, baseline, nameColor, StringAlignment.Near);

            // Gauge: light track + tinted fill.
            float barX = BarX * s;
            float barY = top + ((RowH - BarH) / 2f) * s;
            float barW = BarW * s;
            float barH = BarH * s;
            float radius = barH / 2f;

            using (GraphicsPath back = Round(barX, barY, barW, barH, radius))
            using (SolidBrush backBrush = new SolidBrush(Track))
            {
                g.FillPath(backBrush, back);
            }

            if (row.Kind == MenuRowKind.Level && row.Percent.HasValue)
            {
                float fill = Math.Max(barH, barW * (row.Percent.Value / 100f));
                using (GraphicsPath front = Round(barX, barY, fill, barH, radius))
                using (SolidBrush fillBrush = new SolidBrush(Tone(row)))
                {
                    g.FillPath(fillBrush, front);
                }
            }

            // Numeric values use the Latin face including a remembered level on a
            // sleeping row; only the Chinese "休眠中" belongs to the CJK face.
            bool numeric = row.Kind == MenuRowKind.Level
                           || (row.GreyValue && row.Percent.HasValue);
            DrawText(g, row.Value, !numeric, 17f * s, false,
                 (W - PadX) * s, baseline, numeric ? Tone(row) : Muted,
                 StringAlignment.Far);
        }

        /// <summary>
        /// Drives the pill: knob slides and the track cross-fades grey -> green over
        /// ~150 ms. Eased, not linear, so the knob settles instead of stopping dead.
        /// </summary>
        private void StartSwitchAnim()
        {
            if (_switchAnim == null)
            {
                _switchAnim = new System.Windows.Forms.Timer();
                _switchAnim.Interval = 16;
                _switchAnim.Tick += delegate
                {
                    float target = _autostartOn ? 1f : 0f;
                    float delta = target - _switchT;

                    if (Math.Abs(delta) < 0.02f)
                    {
                        _switchT = target;
                        _switchAnim.Stop();
                    }
                    else
                    {
                        _switchT += delta * 0.32f;
                    }

                    Invalidate();
                };
            }

            if (!_switchAnim.Enabled) _switchAnim.Start();
        }

        /// <summary>Modern pill: 44 x 24 with a 20 px knob that eases into place.</summary>
        private void DrawSwitch(Graphics g, float s, float rowTop)
        {
            float w = SwW * s;
            float h = SwH * s;
            float x = (W - PadX) * s - w;
            float y = rowTop + ((RowH - SwH) / 2f) * s;
            float r = h / 2f;

            float t = _switchT;
            float eased = 1f - (1f - t) * (1f - t);

            Color track = _autostartEnabled ? Mix(SwitchOff, Green, eased) : SwitchOff;
            using (GraphicsPath path = Round(x, y, w, h, r))
            using (SolidBrush brush = new SolidBrush(track))
            {
                g.FillPath(brush, path);
            }

            float knobR = SwKnobR * s;
            float cx = x + r + (w - 2f * r) * eased;
            float cy = y + h / 2f;
            Color knobColor = _autostartEnabled ? Color.White : Color.FromArgb(150, 150, 158);

            using (SolidBrush knob = new SolidBrush(knobColor))
            {
                g.FillEllipse(knob, cx - knobR, cy - knobR, knobR * 2f, knobR * 2f);
            }
        }

        // ------------------------------------------------------------ helpers

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

        /// <summary>
        /// Draws text by baseline. Far/Center alignment is resolved with an explicit
        /// measurement because GDI+ is unreliable with wide or negative rectangles.
        /// </summary>
        private static void DrawText(Graphics g, string s, bool cjk, float size, bool bold,
                                 float x, float baseline, Color color, StringAlignment align)
        {
            if (string.IsNullOrEmpty(s)) return;
            Font f = Face(cjk, size, bold);

            float tw;
            using (StringFormat mf = new StringFormat(StringFormat.GenericTypographic))
                tw = g.MeasureString(s, f, 10000, mf).Width;

            float drawX = x;
            if (align == StringAlignment.Far) drawX = x - tw;
            else if (align == StringAlignment.Center) drawX = x - tw / 2f;

            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(s, f, b, new RectangleF(drawX, baseline - Ascent(f),
                             tw + 8f, f.GetHeight(g) + 6f), sf);
            }
        }

        /// <summary>Shrinks a long device name with an ellipsis before the gauge.</summary>
        private static string Fit(Graphics g, string s, bool cjk, float size, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            Font f = Face(cjk, size, false);
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                if (g.MeasureString(s, f, 10000, sf).Width <= maxW) return s;
                string t = s;
                while (t.Length > 1 && g.MeasureString(t + "…", f, 10000, sf).Width > maxW)
                    t = t.Substring(0, t.Length - 1);
                return t + "…";
            }
        }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            if (d <= 0f)
            {
                p.AddRectangle(r);
                return p;
            }

            p.AddArc(r.Left, r.Top, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Top, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();

            return p;
        }

        private static GraphicsPath Round(float x, float y, float w, float h, float radius)
        {
            return Round(new RectangleF(x, y, w, h), radius);
        }

        /// <summary>Menu percentages follow the same green/amber/red tiers as the cards.</summary>
        private static Color Tone(MenuRow row)
        {
            if (row == null || row.GreyValue) return Muted;
            if (!row.Percent.HasValue) return Green;
            int p = row.Percent.Value;
            if (p >= 60) return Green;
            if (p >= 25) return Amber;
            return Red;
        }

        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_anim != null) { _anim.Stop(); _anim.Dispose(); _anim = null; }
                if (_switchAnim != null) { _switchAnim.Stop(); _switchAnim.Dispose(); _switchAnim = null; }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Borderless popup that hosts the panel.
    ///
    /// A ContextMenuStrip was the obvious host but it lays its items out with its own
    /// padding (left the dark strip down two edges) and, when shown manually, did not
    /// always close on an outside click. Owning the window fixes both: exact client
    /// size, no system chrome, and Deactivate does the dismissing.
    /// </summary>
    internal class TrayMenuForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinCompAttr
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }
        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        public MenuPanel Panel { get; set; }

        private bool _armed;
        private System.Windows.Forms.Timer _armTimer;

        public TrayMenuForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            // Acrylic is enabled in OnShown; a transparent BackColor lets the blur
            // behind the window show through the panel's translucent surface.
            BackColor = Color.Transparent;
            Padding = Padding.Empty;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Corners come from DWM, not SetWindowRgn. A region is a 1-bit mask, so the
            // arc is stair-stepped at every DPI; DWM rounds during composition and the
            // edge is antialiased.
            try
            {
                int preference = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */,
                                      ref preference, sizeof(int));
            }
            catch
            {
            }

            EnableAcrylic();

            // Ignore the Deactivate that fires from the click that opened us.
            // Reused rather than recreated per show: a popup dismissed inside 150 ms
            // used to leave its timer running until the first tick.
            _armed = false;

            if (_armTimer == null)
            {
                _armTimer = new System.Windows.Forms.Timer();
                _armTimer.Interval = 150;
                _armTimer.Tick += delegate
                {
                    _armTimer.Stop();
                    _armed = true;
                };
            }

            _armTimer.Stop();
            _armTimer.Start();
        }

        /// <summary>GradientColor is ABGR: low alpha + near-white keeps the glass light.</summary>
        private void EnableAcrylic()
        {
            try
            {
                AccentPolicy accent = new AccentPolicy();
                accent.AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND;
                accent.AccentFlags = 0x02;         // draw rounded rectangle
                accent.GradientColor = 0x18FFFFFF; // ABGR: alpha 24, white
                accent.AnimationId = 0;

                int len = Marshal.SizeOf(typeof(AccentPolicy));
                IntPtr ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, true);
                    WinCompAttr data = new WinCompAttr();
                    data.Attribute = WCA_ACCENT_POLICY;
                    data.Data = ptr;
                    data.SizeOfData = len;
                    SetWindowCompositionAttribute(Handle, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch
            {
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_armed) Close();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _armTimer != null)
            {
                _armTimer.Stop();
                _armTimer.Dispose();
                _armTimer = null;
            }
            base.Dispose(disposing);
        }
    }
}
