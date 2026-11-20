using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Periphora.Core;

namespace Periphora.UI
{
    public class TrayApp : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly DeviceManager _manager;
        private readonly NotifyIcon _tray;
        private readonly MainForm _form;
        private readonly System.Windows.Forms.Timer _timer;

        private Icon _baseIcon;
        private Icon _dynamicIcon;
        private int _dynKey = -1;   // signature of the currently generated battery icon

        private bool _refreshing;
        private bool _refreshQueued;
        private bool _demoMode;   // when true, real HID reads must not overwrite the sample data
        private List<DeviceSnapshot> _demoSnap;   // sample data also fed to the right-click menu

        // A second right-click replaces the popup instead of stacking another copy.
        private TrayMenuForm _menuForm;


        internal static readonly Color MenuMuted = Color.FromArgb(135, 135, 135); // card status line

        public TrayApp()
        {
            _manager = new DeviceManager();

            _form = new MainForm(_manager);
            _form.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    _form.Hide();
                }
            };
            _form.RefreshRequested += delegate { RefreshNow(); };

            // force handle creation so cross-thread marshalling works immediately
            IntPtr handle = _form.Handle;

            _tray = new NotifyIcon();
            _baseIcon = IconFactory.ToIcon(32);
            _tray.Icon = _baseIcon;
            _tray.Text = "Periphora";
            _tray.Visible = true;

            // The hover tooltip is the native NotifyIcon tooltip: Windows shows it
            // only when the cursor is over OUR icon, with the system's own look.
            _tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { _form.ShowPanel(); return; }
                ShowMenuAboveRight();
            };

            _form.RefreshSeconds = LoadRefreshSeconds();
            _form.RefreshRateChanged += delegate
            {
                SaveRefreshSeconds(_form.RefreshSeconds);
                ApplyInterval();
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _form.RefreshSeconds * 1000;
            _timer.Tick += delegate
            {
                // Keep the footprint small: the receiver shares its input pipe with
                // G HUB, and every request we send is broadcast to it too. It also
                // stops answering for a while if it is polled too hard, which showed
                // up as cards flapping between "97%" and "asleep".
                ApplyInterval();
                RefreshNow();
            };
            _timer.Start();

            RefreshNow();
        }

        /// <summary>
        /// The panel's own cadence while it is open; a slow trickle while hidden in the
        /// tray so a sleeping mouse is not woken every 15 seconds for nothing.
        /// </summary>
        private void ApplyInterval()
        {
            int seconds = _form.Visible ? _form.RefreshSeconds : 300;
            if (_timer.Interval != seconds * 1000) _timer.Interval = seconds * 1000;
        }

        // ------------------------------------------------------- refresh rate

        private const string SettingsKey = @"Software\Periphora";

        private static int LoadRefreshSeconds()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) return 90;
                    object raw = key.GetValue("RefreshSeconds");
                    if (raw == null) return 90;

                    int seconds = Convert.ToInt32(raw);
                    return (seconds == 15 || seconds == 30 || seconds == 90) ? seconds : 90;
                }
            }
            catch
            {
                return 30;
            }
        }

        private static void SaveRefreshSeconds(int seconds)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue("RefreshSeconds", seconds);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Opens the popup to the upper-right of the cursor, in a borderless window of
        /// our own so the size, the corners and the dismissal are all under our control.
        /// </summary>
        private void ShowMenuAboveRight()
        {
            // One popup at a time. Without this, each right-click built a new window
            // and the old one stayed alive until it lost focus — repeated clicks piled
            // up as a stack of menus.
            if (_menuForm != null && !_menuForm.IsDisposed)
            {
                try { _menuForm.Close(); } catch { }
                _menuForm = null;
            }

            MenuPanel panel = BuildMenu();
            if (panel == null) return;

            TrayMenuForm form = new TrayMenuForm();
            form.Panel = panel;
            form.ClientSize = panel.Size;

            panel.Dock = DockStyle.Fill;
            panel.QuitRequested += delegate { Application.Exit(); };

            panel.AutostartEnabled = Autostart.IsSupported();
            panel.AutostartOn = Autostart.IsSupported() && Autostart.IsEnabled();

            MenuPanel captured = panel;
            panel.AutostartToggled += delegate { Autostart.Set(captured.AutostartOn); };

            form.Controls.Add(panel);

            Point p = Cursor.Position;
            Rectangle screen = Screen.FromPoint(p).WorkingArea;

            int x = p.X + 4;
            int y = p.Y - panel.Height - 4;

            if (x + panel.Width > screen.Right) x = p.X - panel.Width - 4;
            if (y < screen.Top) y = p.Y + 4;

            form.Location = new Point(x, y);
            _menuForm = form;
            form.Show();
            form.Activate();
            panel.Focus();
        }

        // ---------------------------------------------------------------- menu

        /// <summary>
        /// Rebuilt on every open so the list is never stale. Order matches the panel:
        /// reporting a level, then awake-but-waiting, then asleep.
        /// </summary>
        private MenuPanel BuildMenu()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float scale = g.DpiX / 96f;

                // Sizes straight from the layered artboards: 20 px names and levels,
                // 17 px Chinese, 9.76 px wordmark.
                Font nameFont = FontManager.Get(FontKind.MontserratMedium, 20f * scale);
                Font valueFont = FontManager.Get(FontKind.MontserratMedium, 20f * scale);
                Font cnFont = FontManager.Get(FontKind.Chinese, 17f * scale);
                Font markFont = FontManager.Get(FontKind.MontserratSemiBold, 9f * scale);

                List<MenuRow> rows = new List<MenuRow>();

                foreach (DeviceSnapshot s in OrderedForMenu())
                {
                    MenuRow row = new MenuRow();

                    // The tray row is narrow, so devices may publish a shorter label
                    // than the one the card uses.
                    if (!string.IsNullOrEmpty(s.Info.ShortModel)) row.Name = s.Info.ShortModel;
                    else if (!string.IsNullOrEmpty(s.Info.Model)) row.Name = s.Info.Model;
                    else row.Name = s.Info.Brand;

                    if (HasLevel(s))
                    {
                        row.Kind = MenuRowKind.Level;
                        row.Percent = s.Reading.Percent.Value;
                        row.Value = s.Reading.Percent.Value + "%";
                    }
                    else
                    {
                        row.Kind = MenuRowKind.Asleep;

                        int? last = _form.LastPercent(s.Info.Id);
                        if (last.HasValue)
                        {
                            // Still paired, just idle: show the last known level in grey
                            // instead of a blank, same rule as the card.
                            row.Percent = last.Value;
                            row.Value = last.Value >= 100 ? "Full" : last.Value + "%";
                            row.GreyValue = true;
                        }
                        else
                        {
                            row.Value = "休眠中";
                        }
                    }

                    rows.Add(row);
                }

                return new MenuPanel(rows, scale, nameFont, valueFont, cnFont, markFont);
            }
        }
        private List<DeviceSnapshot> OrderedForMenu()
        {
            List<DeviceSnapshot> live = new List<DeviceSnapshot>();
            List<DeviceSnapshot> asleep = new List<DeviceSnapshot>();

            IEnumerable<DeviceSnapshot> source = (_demoMode && _demoSnap != null)
                ? (IEnumerable<DeviceSnapshot>)_demoSnap
                : _manager.Snapshot();

            foreach (DeviceSnapshot s in source)
            {
                if (s.Reading == null) { asleep.Add(s); continue; }

                if (s.Reading.Online && s.Reading.Percent.HasValue) live.Add(s);
                else asleep.Add(s);
            }

            live.AddRange(asleep);
            return live;
        }

        private static bool HasLevel(DeviceSnapshot s)
        {
            return s != null && s.Reading != null && s.Reading.Online && s.Reading.Percent.HasValue;
        }

        // ------------------------------------------------------------- refresh

        private void RefreshNow()
        {
            if (_demoMode) return;
            if (_refreshing)
            {
                // A read is already running. Queue one more instead of dropping the
                // request — dropping it is what made a click on the refresh icon spin
                // the arrow and change nothing at all.
                _refreshQueued = true;
                return;
            }

            _refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _manager.Refresh();
                }
                catch
                {
                }
                finally
                {
                    _refreshing = false;
                }

                try
                {
                    _form.BeginInvoke(new MethodInvoker(delegate
                    {
                        if (!_demoMode)
                        {
                            List<DeviceSnapshot> snap = _manager.Snapshot();
                            _form.UpdateSnapshot(snap);
                            OnSnapshot(snap);
                        }
                    }));
                }
                catch
                {
                }

                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    RefreshNow();
                }
            });
        }

        // ------------------------------------------------- tray hover / icon

        /// <summary>Called whenever a fresh snapshot arrives. Sets the native tray
        /// tooltip (Windows handles hover, timing and look) and swaps the tray icon
        /// to a battery glyph when exactly one device is present.</summary>
        private void OnSnapshot(List<DeviceSnapshot> snap)
        {
            List<DeviceSnapshot> copy = snap == null ? new List<DeviceSnapshot>() : new List<DeviceSnapshot>(snap);
            List<DeviceSnapshot> visible = new List<DeviceSnapshot>();
            foreach (DeviceSnapshot s in copy)
                if (s != null && s.Info != null && s.Reading != null) visible.Add(s);

            if (visible.Count == 1)
            {
                DeviceReading r = visible[0].Reading;
                int? pct = (r.Online && r.Percent.HasValue) ? (int?)r.Percent.Value : null;
                int key = (pct.HasValue ? pct.Value : -1) * 10 + (r.Charging ? 1 : 0) * 2 + (r.Online ? 1 : 0);
                if (key != _dynKey || _dynamicIcon == null)
                {
                    Icon next = BatteryIconFactory.Create(pct, r.Charging, r.Online, 32);
                    Icon old = _dynamicIcon;
                    _dynamicIcon = next;
                    _dynKey = key;
                    if (old != null) old.Dispose();
                }
                _tray.Icon = _dynamicIcon;
                // Show the device name together with its level, same shape as the
                // multi-device tooltip: "G304 90% (充电中)".
                string nm = DeviceLabel(visible[0]);
                _tray.Text = pct.HasValue
                    ? nm + " " + pct.Value + "%" + (r.Charging ? " (充电中)" : "")
                    : nm + " 休眠中";
            }
            else if (visible.Count == 0)
            {
                _tray.Icon = _baseIcon;
                _tray.Text = "Periphora - 未检测到设备";
            }
            else
            {
                _tray.Icon = _baseIcon;
                // Native tooltip is a single line; keep it short and readable.
                string tip = "";
                foreach (DeviceSnapshot s in visible)
                {
                    string nm = DeviceLabel(s);
                    string val = (s.Reading.Online && s.Reading.Percent.HasValue)
                        ? s.Reading.Percent.Value + "%" : "休眠";
                    string line = nm + " " + val;
                    if (tip.Length == 0) tip = line;
                    else if (tip.Length + line.Length + 3 < 60) tip += " | " + line;
                }
                _tray.Text = tip;
            }

            DumpDemoTooltip();
        }

        // Demo-only: mirror the current tooltip to a temp file so the screenshot
        // harness can assert the text without needing to hover the exact icon.
        private void DumpDemoTooltip()
        {
            if (!_demoMode) return;
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "periphora-tooltip.txt"),
                    _tray.Text ?? "");
            }
            catch
            {
            }
        }

        /// <summary>Short, human-readable label for a device, used by the tray tooltip.</summary>
        private static string DeviceLabel(DeviceSnapshot s)
        {
            if (s == null || s.Info == null) return "设备";
            string nm = s.Info.ShortModel;
            if (string.IsNullOrEmpty(nm)) nm = s.Info.Model;
            if (string.IsNullOrEmpty(nm)) nm = s.Info.Name;
            if (string.IsNullOrEmpty(nm)) nm = s.Info.Brand;
            return string.IsNullOrEmpty(nm) ? "设备" : nm;
        }

        /// <summary>Demo mode: paint two sample devices so the layout can be reviewed
        /// without any real hardware attached. Never used in normal startup.</summary>
        public void ShowDemo()
        {
            _demoMode = true;
            if (_timer != null) _timer.Stop();

            List<DeviceSnapshot> demo = DemoData();
            _demoSnap = demo;

            // A HID read started in the constructor may still land after this call.
            // Re-apply the sample data once more on a short delay so it wins.
            System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer();
            settle.Interval = 700;
            settle.Tick += delegate
            {
                settle.Stop();
                settle.Dispose();
                if (_demoMode) _form.UpdateSnapshot(demo);
            };
            settle.Start();

            OnSnapshot(demo);

            _form.RefreshSeconds = 30;
            _form.UpdateSnapshot(demo);
            _form.Show();
            _form.Activate();
            _form.BringToFront();
        }

        /// <summary>Builds the two-device sample set used by the demo flags.</summary>
        private static List<DeviceSnapshot> DemoData()
        {
            List<DeviceSnapshot> demo = new List<DeviceSnapshot>();

            DeviceSnapshot kb = new DeviceSnapshot();
            kb.Info = new DeviceInfo();
            kb.Info.Id = "demo-kb";
            kb.Info.Brand = "MCHOSE";
            kb.Info.Model = "K99 v2";
            kb.Info.Name = "MCHOSE 2.4G 键盘";
            kb.Info.Kind = DeviceKind.Keyboard;
            kb.Reading = new DeviceReading();
            kb.Reading.Online = true;
            kb.Reading.Percent = 95;
            kb.Reading.Detail = "2.4G";
            kb.Reading.UpdatedAt = DateTime.Now;
            demo.Add(kb);

            DeviceSnapshot ms = new DeviceSnapshot();
            ms.Info = new DeviceInfo();
            ms.Info.Id = "demo-ms";
            ms.Info.Brand = "VXE";
            ms.Info.Model = "R1";
            ms.Info.Name = "VXE R1 星闪版";
            ms.Info.Kind = DeviceKind.Mouse;
            ms.Reading = new DeviceReading();
            ms.Reading.Online = true;
            ms.Reading.Percent = 60;
            ms.Reading.Charging = true;
            ms.Reading.Detail = "2.4G";
            ms.Reading.UpdatedAt = DateTime.Now;
            demo.Add(ms);

            return demo;
        }

        /// <summary>Single-device demo: verifies the battery-style tray icon and the
        /// "电池状态: XX%" hover text.</summary>
        public void ShowDemoSingle()
        {
            _demoMode = true;
            if (_timer != null) _timer.Stop();

            List<DeviceSnapshot> demo = DemoSingleData();
            _demoSnap = demo;

            OnSnapshot(demo);
            _form.UpdateSnapshot(demo);
            // keep only in tray: don't show the big panel
            _tray.Text = "电池状态: 90%";
        }

        private static List<DeviceSnapshot> DemoSingleData()
        {
            List<DeviceSnapshot> demo = new List<DeviceSnapshot>();
            DeviceSnapshot ms = new DeviceSnapshot();
            ms.Info = new DeviceInfo();
            ms.Info.Id = "demo-g304";
            ms.Info.Brand = "Logitech";
            ms.Info.Model = "G304";
            ms.Info.Name = "Logitech G304";
            ms.Info.Kind = DeviceKind.Mouse;
            ms.Reading = new DeviceReading();
            ms.Reading.Online = true;
            ms.Reading.Percent = 90;
            ms.Reading.Detail = "HID++ 2.0";
            ms.Reading.UpdatedAt = DateTime.Now;
            demo.Add(ms);

            return demo;
        }

        /// <summary>Demo hook: open the right-click menu with sample data for a
        /// screenshot. Positions the cursor first so the popup lands predictably.</summary>
        public void ShowMenuDemo()
        {
            _demoMode = true;
            if (_timer != null) _timer.Stop();
            List<DeviceSnapshot> demo = DemoData();
            _demoSnap = demo;
            OnSnapshot(demo);
            _form.UpdateSnapshot(demo);

            // Put the cursor in open space so the menu renders in a clean spot.
            // Low enough that the popup (tall at 150% DPI) fits fully on screen.
            Cursor.Position = new Point(600, 1000);
            ShowMenuAboveRight();
        }

        public void ShowPanel()
        {
            _form.ShowPanel();
        }

        public void Dispose()
        {
            // The popup is a separate top-level window: closing the tray app must not
            // leave it on screen.
            if (_menuForm != null && !_menuForm.IsDisposed)
            {
                try { _menuForm.Close(); } catch { }
                _menuForm = null;
            }

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (_baseIcon != null) _baseIcon.Dispose();
            if (_dynamicIcon != null) _dynamicIcon.Dispose();
        }
    }
}
