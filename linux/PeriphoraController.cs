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
        // Waking a sleeping device should show up at once rather than on the next
        // five-minute tick. The watcher fires on any input from the receiver, and
        // this cooldown keeps a moving mouse from turning into a poll storm - the
        // receivers stop answering when they are queried too often.
        private const int WakeRefreshCooldownMs = 8000;
        private const int IdleResetMs = 4000;

        private readonly DeviceManager _manager = new DeviceManager();
        private readonly DispatcherTimer _timer;
        private readonly SettingsData _settings;
        private readonly ActivityWatcher _watcher;
        private bool _refreshing;
        private bool _queued;
        private volatile bool _anySleeping;
        private List<DeviceSnapshot> _lastSnapshot;
        private long _lastWakeMs = -WakeRefreshCooldownMs;
        private long _lastInputMs = -IdleResetMs;

        internal event EventHandler<List<DeviceSnapshot>> SnapshotReady;

        internal PeriphoraController()
        {
            _settings = LinuxSettings.Load();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds) };
            _timer.Tick += delegate { RefreshNow(); };
            _timer.Start();

            _watcher = ActivityWatcher.TryStart(ClaimedPaths, OnDeviceActivity, WakeRefreshCooldownMs);
        }

        /// <summary>
        /// Nodes a provider is already talking to must stay untouched: their replies
        /// arrive as input reports, and a second reader would eat them.
        /// </summary>
        private HashSet<string> ClaimedPaths()
        {
            HashSet<string> paths = new HashSet<string>();
            List<DeviceSnapshot> snapshot = _lastSnapshot;
            if (snapshot == null) return paths;
            foreach (DeviceSnapshot item in snapshot)
            {
                if (item == null || item.Info == null) continue;
                if (!string.IsNullOrEmpty(item.Info.HidPath)) paths.Add(item.Info.HidPath);
            }
            return paths;
        }

        private void OnDeviceActivity()
        {
            long now = Environment.TickCount64;
            long gap = now - _lastInputMs;
            _lastInputMs = now;

            // A mouse being dragged is one continuous burst; only the first report
            // after a quiet spell means the device woke up. Refreshing on every
            // packet would be the poll storm that receivers refuse to answer.
            if (gap < IdleResetMs) return;
            if (!_anySleeping) return;   // nothing stale, the timer is enough
            if (now - _lastWakeMs < WakeRefreshCooldownMs) return;
            _lastWakeMs = now;
            Dispatcher.UIThread.Post(RefreshNow);
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
                    _lastSnapshot = snapshot;
                    bool sleeping = false;
                    foreach (DeviceSnapshot item in snapshot)
                        if (item != null && item.Reading != null && item.Reading.Sleeping) { sleeping = true; break; }
                    _anySleeping = sleeping;

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
