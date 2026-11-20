using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Periphora.Core;
using Periphora.Linux.UI;

namespace Periphora.Linux
{
    internal sealed class MainWindow : Window
    {
        private sealed class Row
        {
            internal string Id;
            internal ModernRow View = new ModernRow();
            internal double Opacity = 1;
            internal bool Leaving;
        }

        private readonly PeriphoraController _controller;
        private readonly List<Row> _rows = new List<Row>();
        private readonly Dictionary<string, double> _lastPercent = new Dictionary<string, double>();
        private readonly DispatcherTimer _frameTimer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _lastFrameMs;
        private double _spin = 1;
        private double _deny = 1;
        private double _hintT = 1;
        private string _hint = "";
        private long _lastRefreshMs = -999999;
        private bool _hotRefresh, _hotSettings, _hotChevron;
        private bool _pressRefresh, _pressSettings, _pressChevron;
        private bool _allowClose;
        private bool _demo;

        internal MainWindow(PeriphoraController controller, bool demo)
        {
            _controller = controller;
            _demo = demo;
            Width = ModernRenderer.DesignW;
            Height = ModernRenderer.HeightFor(1);
            MinWidth = ModernRenderer.DesignW;
            MaxWidth = ModernRenderer.DesignW;
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Title = "Periphora";
            RefreshSeconds = controller != null ? controller.RefreshSeconds : 30;

            _frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _frameTimer.Tick += delegate { OnFrame(); };

            Opened += delegate
            {
                if (_controller != null) _controller.ApplyInterval(true);
                if (_demo) ShowDemo();
                else if (_controller != null) _controller.RefreshNow();
            };
            Closing += delegate(object sender, WindowClosingEventArgs e)
            {
                if (_allowClose || !TrayAvailable) return;
                e.Cancel = true;
                Hide();
            };
            PropertyChanged += delegate(object sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == IsVisibleProperty)
                {
                    if (IsVisible) _frameTimer.Start();
                    else _frameTimer.Stop();
                }
            };
        }

        internal int RefreshSeconds { get; set; }
        internal bool TrayAvailable { get; set; }

        internal void AllowClose() { _allowClose = true; }

        internal void ShowPanel()
        {
            if (!IsVisible) Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_controller != null)
            {
                _controller.ApplyInterval(true);
                _controller.RefreshNow();
            }
        }

        internal void SetSnapshot(List<DeviceSnapshot> snapshot)
        {
            if (_demo) return;
            List<DeviceSnapshot> visible = new List<DeviceSnapshot>();
            if (snapshot != null)
            {
                foreach (DeviceSnapshot item in snapshot)
                {
                    if (item == null || item.Info == null || item.Reading == null) continue;
                    if (!item.Reading.Online && !item.Reading.Sleeping) continue;
                    visible.Add(item);
                }
            }
            visible = CollapseTransports(visible);

            HashSet<string> seen = new HashSet<string>();
            foreach (DeviceSnapshot item in visible)
            {
                string id = item.Info.Id;
                seen.Add(id);
                Row row = null;
                foreach (Row candidate in _rows) if (candidate.Id == id) { row = candidate; break; }
                if (row == null)
                {
                    row = new Row { Id = id, Opacity = 0 };
                    _rows.Add(row);
                }
                row.Leaving = false;

                bool hasLevel = item.Reading.Online && item.Reading.Percent.HasValue;
                int percent = 0;
                if (hasLevel) percent = item.Reading.Percent.Value;
                else
                {
                    double last;
                    if (_lastPercent.TryGetValue(id, out last)) percent = (int)Math.Round(last);
                }
                if (hasLevel) _lastPercent[id] = percent;

                ModernRow view = row.View;
                view.Icon = ToIcon(item.Info.Kind);
                view.Name = string.IsNullOrEmpty(item.Info.Name)
                    ? item.Info.Brand + " " + item.Info.Model : item.Info.Name;
                view.Online = item.Reading.Online;
                view.Sleeping = item.Reading.Sleeping;
                view.Charging = item.Reading.Charging;
                view.Percent = hasLevel ? (int?)percent : null;

                if (item.Reading.Charging) view.Subtitle = "充电中";
                else if (!item.Reading.Online || item.Reading.Sleeping) view.Subtitle = "设备休眠中";
                else if (!string.IsNullOrEmpty(item.Reading.Detail)) view.Subtitle = item.Reading.Detail;
                else view.Subtitle = hasLevel ? "电池放电中" : "等待读取";
                if (hasLevel && view.Display <= 0.5) view.Display = 0;
            }

            foreach (Row row in _rows)
                if (!seen.Contains(row.Id)) row.Leaving = true;

            _rows.Sort(delegate(Row a, Row b)
            {
                bool aa = a.View.Online && a.View.Percent.HasValue;
                bool bb = b.View.Online && b.View.Percent.HasValue;
                if (aa == bb) return 0;
                return aa ? -1 : 1;
            });

            int count = 0;
            foreach (Row row in _rows) if (!row.Leaving) count++;
            Height = ModernRenderer.HeightFor(Math.Max(1, count));
            _frameTimer.Start();
            InvalidateVisual();
        }

        private static ModernIcon ToIcon(DeviceKind kind)
        {
            if (kind == DeviceKind.Mouse) return ModernIcon.Mouse;
            if (kind == DeviceKind.Keyboard) return ModernIcon.Keyboard;
            if (kind == DeviceKind.Headset) return ModernIcon.Headset;
            return ModernIcon.Other;
        }

        private static List<DeviceSnapshot> CollapseTransports(List<DeviceSnapshot> input)
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            Dictionary<string, int> slots = new Dictionary<string, int>();
            foreach (DeviceSnapshot item in input)
            {
                if (item.Info.ReceiverId != null) { result.Add(item); continue; }
                string key = item.Info.Brand + "|" + item.Info.Model;
                int index;
                if (!slots.TryGetValue(key, out index))
                {
                    slots[key] = result.Count;
                    result.Add(item);
                    continue;
                }
                DeviceSnapshot kept = result[index];
                if (item.Reading.Online && !kept.Reading.Online) result[index] = item;
                else if (!item.Reading.Online && kept.Reading.Online) { }
                else result.Add(item);
            }
            return result;
        }

        private void OnFrame()
        {
            if (!IsVisible) return;
            long now = _clock.ElapsedMilliseconds;
            double elapsed = (now - _lastFrameMs) / 1000.0;
            _lastFrameMs = now;
            if (elapsed <= 0) elapsed = 0.016;
            if (elapsed > 0.12) elapsed = 0.12;

            bool dirty = false;
            if (_spin < 1) { _spin = Math.Min(1, _spin + elapsed / 0.62); dirty = true; }
            if (_deny < 1) { _deny = Math.Min(1, _deny + elapsed / 0.42); dirty = true; }
            if (_hintT < 1) { _hintT = Math.Min(1, _hintT + elapsed / 1.9); dirty = true; }

            List<Row> dead = null;
            foreach (Row row in _rows)
            {
                double targetOpacity = row.Leaving ? 0 : 1;
                if (Math.Abs(row.Opacity - targetOpacity) > 0.01)
                {
                    row.Opacity += (targetOpacity - row.Opacity) * (1 - Math.Exp(-16 * elapsed));
                    if (Math.Abs(row.Opacity - targetOpacity) < 0.01) row.Opacity = targetOpacity;
                    dirty = true;
                }
                if (row.Leaving && row.Opacity <= 0.02)
                {
                    if (dead == null) dead = new List<Row>();
                    dead.Add(row);
                    continue;
                }
                double target = row.View.Percent.HasValue && row.View.Online ? row.View.Percent.Value : 0;
                if (Math.Abs(row.View.Display - target) > 0.3)
                {
                    row.View.Display += (target - row.View.Display) * (1 - Math.Exp(-11 * elapsed));
                    if (Math.Abs(row.View.Display - target) < 0.3) row.View.Display = target;
                    dirty = true;
                }
            }
            if (dead != null) foreach (Row row in dead) _rows.Remove(row);
            if (dirty) InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            PaintState state = new PaintState();
            state.Rows = new List<ModernRow>();
            foreach (Row row in _rows)
            {
                row.View.Opacity = row.Opacity;
                if (!row.Leaving || row.Opacity > 0.02) state.Rows.Add(row.View);
            }
            state.UpdatedText = "更新于 " + DateTime.Now.ToString("HH:mm");
            state.IntervalText = "每 " + RefreshSeconds + " 秒";
            state.RefreshSpin = _spin < 1 ? _spin : 0;
            state.RefreshDeny = _deny < 1 ? _deny : 0;
            state.RefreshHot = _hotRefresh || _pressRefresh;
            state.SettingsHot = _hotSettings || _pressSettings;
            state.ChevronHot = _hotChevron || _pressChevron;
            state.Empty = _rows.Count == 0;
            if (_hintT < 1)
            {
                state.Hint = _hint;
                if (_hintT < 0.12) state.HintAlpha = _hintT / 0.12;
                else if (_hintT > 1.55) state.HintAlpha = (1.9 - _hintT) / 0.35;
                else state.HintAlpha = 1;
            }
            ModernRenderer.Paint(context, Bounds.Width, Bounds.Height, state);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point point = e.GetPosition(this);
            int over = ModernRenderer.HitButton(point.X, point.Y, Bounds.Width);
            bool refresh = over == 1, settings = over == 2, chevron = over == 3;
            if (refresh != _hotRefresh || settings != _hotSettings || chevron != _hotChevron)
            {
                _hotRefresh = refresh;
                _hotSettings = settings;
                _hotChevron = chevron;
                Cursor = over == 0 ? null : new Cursor(StandardCursorType.Hand);
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hotRefresh = _hotSettings = _hotChevron = false;
            _pressRefresh = _pressSettings = _pressChevron = false;
            Cursor = null;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            PointerPoint point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            int over = ModernRenderer.HitButton(point.Position.X, point.Position.Y, Bounds.Width);
            if (over == 0)
            {
                try { BeginMoveDrag(e); } catch { }
                return;
            }
            _pressRefresh = over == 1;
            _pressSettings = over == 2;
            _pressChevron = over == 3;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            Point point = e.GetPosition(this);
            int over = ModernRenderer.HitButton(point.X, point.Y, Bounds.Width);
            _pressRefresh = _pressSettings = _pressChevron = false;

            if (over == 1) TryRefresh();
            else if (over == 2)
            {
                int[] choices = { 15, 30, 90 };
                int next = choices[0];
                for (int i = 0; i < choices.Length; i++)
                    if (choices[i] == RefreshSeconds) next = choices[(i + 1) % choices.Length];
                RefreshSeconds = next;
                if (_controller != null) _controller.SetRefreshSeconds(next);
                ShowHint("刷新间隔已设为 " + next + " 秒");
            }
            else if (over == 3)
            {
                if (TrayAvailable) Hide();
                else Close();
            }
            InvalidateVisual();
        }

        private void TryRefresh()
        {
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastRefreshMs < 5000)
            {
                _deny = 0;
                ShowHint("刷新过于频繁，请稍候");
                return;
            }
            _lastRefreshMs = now;
            _spin = 0;
            if (_controller != null) _controller.RefreshNow();
            InvalidateVisual();
        }

        private void ShowHint(string text)
        {
            _hint = text;
            _hintT = 0;
            InvalidateVisual();
        }

        private void ShowDemo()
        {
            List<DeviceSnapshot> data = new List<DeviceSnapshot>();
            data.Add(Demo("demo-kb", "MCHOSE", "K99 v2", "MCHOSE 2.4G 键盘",
                DeviceKind.Keyboard, 95, false, "2.4G"));
            data.Add(Demo("demo-ms", "VXE", "R1", "VXE R1 星闪版",
                DeviceKind.Mouse, 60, true, "2.4G"));
            Height = ModernRenderer.HeightFor(2);
            bool demoFlag = _demo;
            _demo = false;
            SetSnapshot(data);
            _demo = demoFlag;
        }

        private static DeviceSnapshot Demo(string id, string brand, string model, string name,
            DeviceKind kind, int percent, bool charging, string detail)
        {
            DeviceSnapshot item = new DeviceSnapshot();
            item.Info = new DeviceInfo
            {
                Id = id,
                Brand = brand,
                Model = model,
                Name = name,
                Kind = kind
            };
            item.Reading = new DeviceReading
            {
                Online = true,
                Percent = percent,
                Charging = charging,
                Detail = detail,
                UpdatedAt = DateTime.Now
            };
            return item;
        }
    }
}
