using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Periphora.Core;

namespace Periphora.UI
{
    /// <summary>
    /// Control-Centre style host window. Device discovery/battery logic lives entirely
    /// in DeviceManager; this class only lays out the panel, animates transitions and
    /// forwards the three header buttons.
    ///
    /// Window shape note: the frosted panel is drawn by ModernRenderer into a normal
    /// (non-layered) window, and the exact rounded silhouette is cut with SetWindowRgn.
    /// There is deliberately NO DWM acrylic backdrop: acrylic is painted as a rectangle
    /// by DWM and ignores the window region, which is what produced the light square
    /// patches at the four corners. With the region alone, everything outside the radius
    /// is truly cut away and the desktop shows through.
    /// </summary>
    public class MainForm : Form
    {
        private float _scale = 1f;
        private int PanelW;
        private readonly DeviceManager _manager;

        // one visual row per device, plus the id it came from (for LastPercent)
        private class Row
        {
            public string Id;
            public ModernRenderer.ModernRow View = new ModernRenderer.ModernRow();
            public float Opacity;
            public bool Leaving;
        }
        private readonly List<Row> _rows = new List<Row>();
        private readonly Dictionary<string, float> _lastPct = new Dictionary<string, float>();

        // ---- animation clock ----
        private Thread _frameThread;
        private readonly ManualResetEvent _stopFrame = new ManualResetEvent(false);
        private int _framePending;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastTickMs;
        private const int FastInterval = 12;
        private const int IdleInterval = 120;

        private float _spinT = 1f;          // >=1 means idle
        private const float SpinSeconds = 0.62f;
        private float _denyT = 1f;
        private const float DenySeconds = 0.42f;
        private float _hintT = 1f;
        private const float HintSeconds = 1.9f;
        private string _hint = "";

        private bool _hotRefresh, _hotSettings, _hotChevron;
        private bool _pressRefresh, _pressSettings, _pressChevron;
        private bool _dragging;
        private Point _dragStart;
        private long _lastRefreshAt = -999999;
        private const long RefreshCooldownMs = 5000;

        public event EventHandler RefreshRequested;
        public event EventHandler RefreshRateChanged;

        // Rounded window silhouette. SetWindowRgn is the single authority for shape,
        // using the exact same radius ModernRenderer uses to paint the panel.
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WmFrame = 0x800A;
        private bool _timerResRaised;

        public MainForm(DeviceManager manager)
        {
            _manager = manager;
            using (Graphics probe = Graphics.FromHwnd(IntPtr.Zero))
                _scale = Math.Max(1f, probe.DpiX / 96f);

            PanelW = (int)Math.Round(ModernRenderer.DesignW * _scale);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(PanelW, ModernRenderer.HeightFor(1, _scale));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // Opaque light background: ModernRenderer paints a translucent frosted
            // gradient over it, while SetWindowRgn clips the corners away entirely.
            BackColor = Color.FromArgb(248, 248, 250);
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Text = "Periphora";
        }

        // ---------------------------------------------------------------- shape
        /// <summary>
        /// Cut the window to the same rounded radius the renderer paints. The radius in
        /// the renderer is in design units scaled to device pixels; GDI regions use
        /// integer device pixels. SetWindowRgn takes ownership of the region handle.
        /// </summary>
        private void ApplyRegion()
        {
            if (!IsHandleCreated) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            int diameter = (int)Math.Round(ModernRenderer.CornerRadius * 2f * _scale);
            IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
            if (rgn != IntPtr.Zero) SetWindowRgn(Handle, rgn, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Disable Windows 11's own window rounding so it does not stack a second,
            // differently-sized radius on top of ours (which shows as a seam).
            int donotRound = 1; // DWMWCP_DONOTROUND
            try { DwmSetWindowAttribute(Handle, 33, ref donotRound, sizeof(int)); }
            catch { }
            ApplyRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) ApplyRegion();
        }

        // ---------------------------------------------------------------- api
        public void ShowPanel()
        {
            Show();
            Activate();
            BringToFront();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            RequestRefresh();
        }

        public int? LastPercent(string id)
        {
            float v;
            if (_lastPct.TryGetValue(id, out v) && v > 0.5f) return (int)Math.Round(v);
            return null;
        }

        public int RefreshSeconds { get; set; }

        public void RequestRefresh()
        {
            _lastRefreshAt = _clock.ElapsedMilliseconds;
            SpinRefresh();
            if (RefreshRequested != null) RefreshRequested(this, EventArgs.Empty);
        }

        public void SpinRefresh()
        {
            _spinT = 0f;
            StartAnimating();
        }

        public void Wake() { }

        private void TryRefresh()
        {
            long since = _clock.ElapsedMilliseconds - _lastRefreshAt;
            if (since < RefreshCooldownMs)
            {
                _denyT = 0f;
                ShowHint("刷新过于频繁，请稍候");
                return;
            }
            RequestRefresh();
        }

        private void ShowHint(string text)
        {
            _hint = text;
            _hintT = 0f;
            StartAnimating();
        }

        // ------------------------------------------------------------ snapshot
        public void UpdateSnapshot(List<DeviceSnapshot> snaps)
        {
            List<DeviceSnapshot> visible = new List<DeviceSnapshot>();
            if (snaps != null)
            {
                foreach (DeviceSnapshot s in snaps)
                {
                    if (s == null || s.Info == null || s.Reading == null) continue;
                    if (!s.Reading.Online && !s.Reading.Sleeping) continue;
                    visible.Add(s);
                }
            }
            visible = CollapseTransports(visible);

            HashSet<string> seen = new HashSet<string>();
            foreach (DeviceSnapshot s in visible)
            {
                string id = s.Info.Id;
                seen.Add(id);
                Row row = null;
                foreach (Row r in _rows) if (r.Id == id) { row = r; break; }

                int pct;
                bool hasLevel = s.Reading.Online && s.Reading.Percent.HasValue;
                bool remembered = false;
                if (hasLevel) pct = s.Reading.Percent.Value;
                else
                {
                    pct = 0;
                    float lv;
                    if (_lastPct.TryGetValue(id, out lv)) { pct = (int)Math.Round(lv); remembered = true; }
                }

                if (hasLevel) _lastPct[id] = pct;
                if (row == null)
                {
                    row = new Row();
                    row.Id = id;
                    row.Opacity = 0f;
                    _rows.Add(row);
                }
                if (row.Leaving) { row.Leaving = false; row.Opacity = Math.Max(row.Opacity, 0.1f); }

                ModernRenderer.ModernRow v = row.View;
                v.Icon = ToIcon(s.Info.Kind);
                v.Name = string.IsNullOrEmpty(s.Info.Name) ? (s.Info.Brand + " " + s.Info.Model) : s.Info.Name;
                v.Online = s.Reading.Online;
                v.Sleeping = s.Reading.Sleeping;
                v.Charging = s.Reading.Charging;
                v.Percent = hasLevel || remembered ? (int?)pct : null;
                if (s.Reading.Charging) v.Subtitle = "充电中";
                else if (!s.Reading.Online || s.Reading.Sleeping) v.Subtitle = "设备休眠中";
                else if (!string.IsNullOrEmpty(s.Reading.Detail)) v.Subtitle = s.Reading.Detail;
                else v.Subtitle = hasLevel ? "电池放电中" : "等待读取";
                if (hasLevel && row.View.Display <= 0.5f) row.View.Display = 0f; // replay fill
            }

            // devices that vanished fade out
            foreach (Row r in _rows)
                if (!seen.Contains(r.Id)) r.Leaving = true;

            SortRows();
            SizePanel();
            StartAnimating();
            Invalidate();
        }

        private static ModernRenderer.ModernIcon ToIcon(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Mouse: return ModernRenderer.ModernIcon.Mouse;
                case DeviceKind.Keyboard: return ModernRenderer.ModernIcon.Keyboard;
                case DeviceKind.Headset: return ModernRenderer.ModernIcon.Headset;
                case DeviceKind.Other: return ModernRenderer.ModernIcon.Other;
                default: return ModernRenderer.ModernIcon.Other;
            }
        }

        private static List<DeviceSnapshot> CollapseTransports(List<DeviceSnapshot> input)
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            Dictionary<string, int> slot = new Dictionary<string, int>();
            foreach (DeviceSnapshot s in input)
            {
                if (s.Info.ReceiverId != null) { result.Add(s); continue; }
                string key = s.Info.Brand + "|" + s.Info.Model;
                int idx;
                if (!slot.TryGetValue(key, out idx))
                {
                    slot[key] = result.Count;
                    result.Add(s);
                    continue;
                }
                DeviceSnapshot kept = result[idx];
                if (s.Reading.Online && !kept.Reading.Online) result[idx] = s;
                else if (!s.Reading.Online && kept.Reading.Online) { }
                else result.Add(s);
            }
            return result;
        }

        private void SortRows()
        {
            _rows.Sort(delegate(Row a, Row b)
            {
                bool la = a.View.Online && a.View.Percent.HasValue;
                bool lb = b.View.Online && b.View.Percent.HasValue;
                if (la == lb) return 0;
                return la ? -1 : 1;
            });
        }

        private void SizePanel()
        {
            int count = 0;
            foreach (Row r in _rows) if (!r.Leaving) count++;
            if (count < 1) count = 1;
            int h = ModernRenderer.HeightFor(count, _scale);
            if (ClientSize.Height != h) ClientSize = new Size(PanelW, h);
        }

        // ----------------------------------------------------------- animation
        private void StartAnimating()
        {
            if (!Visible) return;
            _lastTickMs = _clock.ElapsedMilliseconds;
            if (!_timerResRaised) { timeBeginPeriod(1); _timerResRaised = true; }
            if (_frameThread == null || !_frameThread.IsAlive)
            {
                _stopFrame.Reset();
                _frameThread = new Thread(FrameLoop);
                _frameThread.IsBackground = true;
                _frameThread.Name = "periphora-frame";
                _frameThread.Start();
            }
        }

        private void FrameLoop()
        {
            while (true)
            {
                if (_stopFrame.WaitOne(FastInterval)) return;
                if (Interlocked.Exchange(ref _framePending, 1) == 1) continue;
                try
                {
                    if (IsHandleCreated) PostMessage(Handle, WmFrame, IntPtr.Zero, IntPtr.Zero);
                    else Interlocked.Exchange(ref _framePending, 0);
                }
                catch { Interlocked.Exchange(ref _framePending, 0); }
            }
        }

        private bool Advance(float dt, out int rate)
        {
            bool fast = false;
            List<Row> dead = null;

            if (_spinT < 1f)
            {
                _spinT += dt / SpinSeconds;
                if (_spinT > 1f) _spinT = 1f;
                fast = true;
            }
            if (_denyT < 1f)
            {
                _denyT += dt / DenySeconds;
                if (_denyT > 1f) _denyT = 1f;
                fast = true;
            }
            if (_hintT < 1f)
            {
                _hintT += dt / HintSeconds;
                if (_hintT > 1f) _hintT = 1f;
                fast = true;
            }

            foreach (Row r in _rows)
            {
                float targetOpacity = r.Leaving ? 0f : 1f;
                if (Math.Abs(r.Opacity - targetOpacity) > 0.01f)
                {
                    r.Opacity += (targetOpacity - r.Opacity) * (1f - (float)Math.Exp(-16f * dt));
                    if (Math.Abs(r.Opacity - targetOpacity) < 0.01f) r.Opacity = targetOpacity;
                    fast = true;
                }
                if (r.Leaving && r.Opacity <= 0.02f)
                {
                    if (dead == null) dead = new List<Row>();
                    dead.Add(r);
                    continue;
                }
                float target = r.View.Percent.HasValue && r.View.Online ? r.View.Percent.Value : 0f;
                if (Math.Abs(r.View.Display - target) > 0.3f)
                {
                    r.View.Display += (target - r.View.Display) * (1f - (float)Math.Exp(-11f * dt));
                    if (Math.Abs(r.View.Display - target) < 0.3f) r.View.Display = target;
                    fast = true;
                }
            }
            if (dead != null) foreach (Row r in dead) _rows.Remove(r);

            rate = fast ? FastInterval : IdleInterval;
            return fast;
        }

        private void OnTick()
        {
            Interlocked.Exchange(ref _framePending, 0);
            long now = _clock.ElapsedMilliseconds;
            float dt = (now - _lastTickMs) / 1000f;
            _lastTickMs = now;
            if (dt <= 0f) dt = 0.015f;
            if (dt > 0.12f) dt = 0.12f;

            int rate;
            bool fast = Advance(dt, out rate);
            if (fast) Invalidate();
            if (!fast && rate == IdleInterval && _timerResRaised)
            {
                timeEndPeriod(1);
                _timerResRaised = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmFrame) { OnTick(); return; }
            base.WndProc(ref m);
        }

        // -------------------------------------------------------------- paint
        protected override void OnPaint(PaintEventArgs e)
        {
            ModernRenderer.PaintState st = new ModernRenderer.PaintState();
            foreach (Row r in _rows)
                if (!r.Leaving || r.Opacity > 0.02f)
                    st.Rows.Add(r.View);

            st.UpdatedText = "更新于 " + DateTime.Now.ToString("HH:mm");
            st.IntervalText = "每 " + RefreshSeconds + " 秒";
            st.RefreshSpin = _spinT < 1f ? _spinT : 0f;
            st.RefreshDeny = _denyT < 1f ? _denyT : 0f;
            st.RefreshHot = _hotRefresh || _pressRefresh;
            st.SettingsHot = _hotSettings || _pressSettings;
            st.ChevronHot = _hotChevron || _pressChevron;
            st.Empty = _rows.Count == 0;
            if (_hintT < 1f)
            {
                st.Hint = _hint;
                if (_hintT < 0.12f) st.HintAlpha = _hintT / 0.12f;
                else if (_hintT > HintSeconds - 0.35f) st.HintAlpha = (HintSeconds - _hintT) / 0.35f;
                else st.HintAlpha = 1f;
            }

            ModernRenderer.Paint(e.Graphics, Width, Height, _scale, st);
            base.OnPaint(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { ApplyRegion(); StartAnimating(); Invalidate(); }
            else
            {
                if (_stopFrame != null) _stopFrame.Set();
                if (_timerResRaised) { timeEndPeriod(1); _timerResRaised = false; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _stopFrame.Set(); } catch { }
            }
            if (_timerResRaised) { try { timeEndPeriod(1); } catch { } _timerResRaised = false; }
            base.Dispose(disposing);
        }

        // -------------------------------------------------------------- input
        private int HitIcon(int x, int y)
        {
            return ModernRenderer.HitButton(x, y, Width, _scale);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int over = HitIcon(e.X, e.Y);
            bool nr = over == 1, ns = over == 2, nc = over == 3;
            if (nr != _hotRefresh || ns != _hotSettings || nc != _hotChevron)
            {
                _hotRefresh = nr; _hotSettings = ns; _hotChevron = nc;
                Cursor = over == 0 ? Cursors.Default : Cursors.Hand;
                StartAnimating();
                Invalidate();
            }
            if (!_dragging) return;
            if (Math.Abs(e.X - _dragStart.X) > 3 || Math.Abs(e.Y - _dragStart.Y) > 3)
            {
                Point screen = PointToScreen(new Point(e.X, e.Y));
                Location = new Point(screen.X - _dragStart.X, screen.Y - _dragStart.Y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int over = HitIcon(e.X, e.Y);
            if (over == 0)
            {
                _dragging = true;
                _dragStart = new Point(e.X, e.Y);
                return;
            }
            _pressRefresh = over == 1;
            _pressSettings = over == 2;
            _pressChevron = over == 3;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDragging = _dragging;
            _dragging = false;
            int over = HitIcon(e.X, e.Y);
            _pressRefresh = _pressSettings = _pressChevron = false;

            if (wasDragging) { Invalidate(); return; }
            if (over == 1) TryRefresh();
            else if (over == 2)
            {
                // settings button cycles the refresh interval
                int[] opts = { 15, 30, 90 };
                int cur = RefreshSeconds;
                int next = opts[0];
                for (int i = 0; i < opts.Length; i++)
                    if (opts[i] == cur) next = opts[(i + 1) % opts.Length];
                RefreshSeconds = next;
                ShowHint("刷新间隔已设为 " + next + " 秒");
                if (RefreshRateChanged != null) RefreshRateChanged(this, EventArgs.Empty);
            }
            else if (over == 3) Hide();
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hotRefresh = _hotSettings = _hotChevron = false;
            _pressRefresh = _pressSettings = _pressChevron = false;
            _dragging = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }
}
