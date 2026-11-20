using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Periphora.Core;
using Periphora.Linux.Platform;

namespace Periphora.Linux
{
    internal sealed class PeriphoraController
    {
        private readonly DeviceManager _manager = new DeviceManager();
        private readonly DispatcherTimer _timer;
        private readonly SettingsData _settings;
        private bool _refreshing;
        private bool _queued;

        internal event EventHandler<List<DeviceSnapshot>> SnapshotReady;

        internal PeriphoraController()
        {
            _settings = LinuxSettings.Load();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds) };
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();
        }

        internal int RefreshSeconds { get { return _settings.RefreshSeconds; } }

        internal void SetRefreshSeconds(int seconds)
        {
            if (seconds != 15 && seconds != 30 && seconds != 90) return;
            _settings.RefreshSeconds = seconds;
            LinuxSettings.Save(_settings);
            ApplyInterval(false);
        }

        internal void ApplyInterval(bool panelVisible)
        {
            _timer.Interval = TimeSpan.FromSeconds(panelVisible ? _settings.RefreshSeconds : 300);
        }

        internal void RefreshNow()
        {
            if (_refreshing)
            {
                _queued = true;
                return;
            }
            _refreshing = true;
            Task.Run(delegate
            {
                try { _manager.Refresh(); }
                catch { }
                finally { _refreshing = false; }

                List<DeviceSnapshot> snapshot = _manager.Snapshot();
                Dispatcher.UIThread.Post(delegate
                {
                    EventHandler<List<DeviceSnapshot>> handler = SnapshotReady;
                    if (handler != null) handler(this, snapshot);
                });

                if (_queued)
                {
                    _queued = false;
                    Dispatcher.UIThread.Post(RefreshNow);
                }
            });
        }
    }
}
