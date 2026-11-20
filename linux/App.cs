using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Periphora.Linux.Platform;

namespace Periphora.Linux
{
    internal sealed class App : Application
    {
        private TrayIcon _tray;
        private MainWindow _window;
        private PeriphoraController _controller;

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            Program.Log("framework init, lifetime=" + (ApplicationLifetime == null ? "null" : ApplicationLifetime.GetType().Name));
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // --demo renders the panel from synthetic readings. An optional
                // suffix picks the scenario: --demo=2 / --demo=3 / --demo=4 /
                // --demo=empty. Used to capture the screenshots in docs/.
                string demo = null;
                foreach (string argument in Environment.GetCommandLineArgs())
                {
                    if (!argument.StartsWith("--demo", StringComparison.OrdinalIgnoreCase)) continue;
                    int equals = argument.IndexOf('=');
                    demo = equals < 0 ? string.Empty : argument.Substring(equals + 1);
                }
                bool interactive = demo == null;

                _controller = interactive ? new PeriphoraController() : null;
                _window = new MainWindow(_controller, demo);
                try
                {
                    _window.Icon = new WindowIcon(
                        Avalonia.Platform.AssetLoader.Open(new Uri("avares://Periphora/Assets/periphora.png")));
                }
                catch { }

                desktop.MainWindow = _window;
                _window.Show();
                Program.Log("window shown, visible=" + _window.IsVisible);

                string shot = Environment.GetEnvironmentVariable("PERIPHORA_SHOT");
                if (!string.IsNullOrEmpty(shot))
                {
                    Avalonia.Threading.DispatcherTimer.RunOnce(delegate
                    {
                        try
                        {
                            // PERIPHORA_SHOT_SCALE=2 renders at 2x so the images in
                            // docs/ stay crisp on high-DPI displays.
                            double scale = 1;
                            string scaleText = Environment.GetEnvironmentVariable("PERIPHORA_SHOT_SCALE");
                            if (!string.IsNullOrEmpty(scaleText))
                            {
                                double parsed;
                                if (double.TryParse(scaleText, out parsed) && parsed > 0) scale = parsed;
                            }
                            PixelSize size = new PixelSize(
                                Math.Max(1, (int)Math.Ceiling(_window.Bounds.Width * scale)),
                                Math.Max(1, (int)Math.Ceiling(_window.Bounds.Height * scale)));
                            using (Avalonia.Media.Imaging.RenderTargetBitmap bitmap =
                                new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale)))
                            {
                                bitmap.Render(_window);
                                bitmap.Save(shot);
                            }
                            Program.Log("saved shot " + shot);
                        }
                        catch (Exception ex) { Program.Log("shot failed: " + ex); }
                        _window.AllowClose();
                        desktop.Shutdown();
                    }, TimeSpan.FromMilliseconds(1800));
                }

                if (interactive)
                {
                    _controller.SnapshotReady += delegate(object sender, System.Collections.Generic.List<Periphora.Core.DeviceSnapshot> snapshot)
                    {
                        _window.SetSnapshot(snapshot);
                        UpdateTray(snapshot);
                    };
                    SetupTray(desktop);
                    _window.Closed += delegate
                    {
                        if (!_window.TrayAvailable) desktop.Shutdown();
                    };
                }
            }
            base.OnFrameworkInitializationCompleted();
        }

        private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _tray = new TrayIcon
                {
                    Icon = _window.Icon,
                    ToolTipText = "Periphora - 外设电量",
                    IsVisible = true
                };
                _tray.Clicked += delegate { TogglePanel(); };
                UpdateTray(null);
                TrayIcon.SetIcons(this, new TrayIcons { _tray });
                _window.TrayAvailable = true;
            }
            catch
            {
                // Some minimal desktops do not expose StatusNotifierItem. The
                // panel remains fully usable even without a tray icon.
            }
        }

        private void TogglePanel()
        {
            if (_window.IsVisible) _window.Hide();
            else _window.ShowPanel();
        }

        private void UpdateTray(System.Collections.Generic.List<Periphora.Core.DeviceSnapshot> snapshot)
        {
            if (snapshot != null) _lastSnapshot = snapshot;
            if (_tray == null) return;
            NativeMenu menu = new NativeMenu();
            NativeMenuItem header = new NativeMenuItem("外设电量") { IsEnabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new NativeMenuItemSeparator());

            int count = 0;
            if (snapshot != null)
            {
                foreach (Periphora.Core.DeviceSnapshot item in snapshot)
                {
                    if (item == null || item.Info == null || item.Reading == null) continue;
                    count++;
                    string label = Label(item);
                    NativeMenuItem device = new NativeMenuItem(label);
                    device.Click += delegate { _window.ShowPanel(); };
                    menu.Items.Add(device);
                }
            }
            if (count == 0)
                menu.Items.Add(new NativeMenuItem("未检测到设备") { IsEnabled = false });

            menu.Items.Add(new NativeMenuItemSeparator());
            NativeMenuItem interval = new NativeMenuItem("刷新间隔");
            NativeMenu intervalMenu = new NativeMenu();
            int[] choices = { 15, 30, 90 };
            foreach (int seconds in choices)
            {
                string prefix = seconds == _controller.RefreshSeconds ? "✓ " : "   ";
                NativeMenuItem choice = new NativeMenuItem(prefix + seconds + " 秒");
                int selected = seconds;
                choice.Click += delegate
                {
                    _controller.SetRefreshSeconds(selected);
                    _window.RefreshSeconds = selected;
                    _window.InvalidateVisual();
                    UpdateTray(_lastSnapshot);
                };
                intervalMenu.Items.Add(choice);
            }
            interval.Menu = intervalMenu;
            menu.Items.Add(interval);

            bool autostart = LinuxAutostart.IsEnabled();
            NativeMenuItem autostartItem = new NativeMenuItem(
                (autostart ? "✓ " : "   ") + "开机启动");
            autostartItem.Click += delegate
            {
                LinuxAutostart.Set(!LinuxAutostart.IsEnabled());
                UpdateTray(_lastSnapshot);
            };
            menu.Items.Add(autostartItem);

            NativeMenuItem show = new NativeMenuItem("打开面板");
            show.Click += delegate { _window.ShowPanel(); };
            menu.Items.Add(show);
            NativeMenuItem quit = new NativeMenuItem("退出应用");
            quit.Click += delegate
            {
                _window.AllowClose();
                desktopShutdown();
            };
            menu.Items.Add(quit);
            _tray.Menu = menu;

            if (count == 1)
            {
                foreach (Periphora.Core.DeviceSnapshot item in snapshot)
                {
                    if (item == null || item.Info == null || item.Reading == null) continue;
                    _tray.ToolTipText = Label(item);
                    break;
                }
            }
            else if (count == 0) _tray.ToolTipText = "Periphora - 未检测到设备";
            else _tray.ToolTipText = "Periphora - " + count + " 台设备";
        }

        private static string Label(Periphora.Core.DeviceSnapshot item)
        {
            string name = item.Info.ShortModel;
            if (string.IsNullOrEmpty(name)) name = item.Info.Model;
            if (string.IsNullOrEmpty(name)) name = item.Info.Name;
            if (string.IsNullOrEmpty(name)) name = item.Info.Brand;
            if (string.IsNullOrEmpty(name)) name = "设备";
            if (item.Reading.Online && item.Reading.Percent.HasValue)
                return name + " " + item.Reading.Percent.Value + "%" + (item.Reading.Charging ? " (充电中)" : "");
            return name + " 休眠中";
        }

        private System.Collections.Generic.List<Periphora.Core.DeviceSnapshot> _lastSnapshot;

        private void desktopShutdown()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }
    }
}
