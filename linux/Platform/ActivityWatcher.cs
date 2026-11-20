// Watches the receiver's input collection for traffic, so a device that just woke
// up is re-read immediately instead of waiting for the next scheduled poll.
//
// The app deliberately polls slowly while hidden (one query every five minutes):
// a 2.4G receiver stops answering if it is asked too often, which shows up as
// cards flapping between a real percentage and "asleep". Raw Input made this
// cheap on Windows; on Linux the equivalent is a non-blocking poll() on the
// hidraw node that carries the device's own input reports. When the user moves
// the mouse or presses a key, that node becomes readable and we know to refresh.
//
// Two rules keep this from breaking the protocol layer:
//   * only Generic Desktop collections are watched (usage page 0x01). Those carry
//     input reports the kernel also delivers to the input stack, so draining them
//     here steals nothing from anybody.
//   * any node a provider is currently querying is skipped. Logitech HID++ and
//     friends receive their replies as input reports on the vendor collection,
//     and a second reader there would swallow the answer and make a healthy
//     device look asleep.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Periphora.Interop;

namespace Periphora.Linux.Platform
{
    internal sealed class ActivityWatcher : IDisposable
    {
        private const short POLLIN = 0x0001;
        private const short POLLERR = 0x0008;
        private const short POLLHUP = 0x0010;
        private const short POLLNVAL = 0x0020;
        private const int O_RDONLY = 0;
        private const int O_NONBLOCK = 0x800;
        private const ushort UsagePageGenericDesktop = 0x01;
        private const int RebuildIntervalMs = 30000;

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr read(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        private static extern int poll([In, Out] PollFd[] fds, uint count, int timeoutMs);

        private readonly Func<HashSet<string>> _blocked;
        private readonly Action _onActivity;
        private readonly int _cooldownMs;
        private readonly Thread _thread;
        private readonly List<PollFd> _fds = new List<PollFd>();
        private volatile bool _stop;
        private long _lastFireMs;
        private long _lastRebuildMs;

        private ActivityWatcher(Func<HashSet<string>> blocked, Action onActivity, int cooldownMs)
        {
            _blocked = blocked;
            _onActivity = onActivity;
            _cooldownMs = cooldownMs;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "periphora-activity";
        }

        /// <summary>
        /// Never throws: when /dev/hidraw is unavailable or permissions were not
        /// granted, the caller simply keeps its timer-driven behaviour.
        /// </summary>
        internal static ActivityWatcher TryStart(Func<HashSet<string>> blocked, Action onActivity, int cooldownMs)
        {
            try
            {
                ActivityWatcher watcher = new ActivityWatcher(blocked, onActivity, cooldownMs);
                watcher._thread.Start();
                return watcher;
            }
            catch
            {
                return null;
            }
        }

        private void Loop()
        {
            byte[] scratch = new byte[128];
            while (!_stop)
            {
                try
                {
                    long now = Environment.TickCount64;
                    if (_fds.Count == 0 || now - _lastRebuildMs > RebuildIntervalMs)
                    {
                        Rebuild();
                        if (_fds.Count == 0)
                        {
                            Thread.Sleep(2000);
                            continue;
                        }
                    }

                    PollFd[] entries = _fds.ToArray();
                    int ready = poll(entries, (uint)entries.Length, 1000);
                    if (ready <= 0) continue;

                    bool sawActivity = false;
                    bool stale = false;
                    for (int i = 0; i < entries.Length; i++)
                    {
                        short revents = entries[i].revents;
                        if ((revents & POLLIN) != 0)
                        {
                            // Drain so the next poll blocks again instead of spinning.
                            long drained = read(entries[i].fd, scratch, (IntPtr)scratch.Length).ToInt64();
                            while (drained > 0)
                                drained = read(entries[i].fd, scratch, (IntPtr)scratch.Length).ToInt64();
                            sawActivity = true;
                        }
                        if ((revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) stale = true;
                    }

                    if (stale)
                    {
                        // A receiver was unplugged; rebuild the set on the next pass.
                        CloseAll();
                        continue;
                    }

                    if (sawActivity)
                    {
                        long nowMs = Environment.TickCount64;
                        if (nowMs - _lastFireMs >= _cooldownMs)
                        {
                            _lastFireMs = nowMs;
                            try { _onActivity(); } catch { }
                        }
                    }
                }
                catch
                {
                    CloseAll();
                    Thread.Sleep(1000);
                }
            }
        }

        private void Rebuild()
        {
            CloseAll();
            _lastRebuildMs = Environment.TickCount64;

            HashSet<string> blocked = null;
            try { blocked = _blocked == null ? null : _blocked(); } catch { }
            if (blocked == null) blocked = new HashSet<string>();

            List<HidDeviceInfo> catalog;
            try { catalog = HidNative.Enumerate(); }
            catch { return; }

            HashSet<string> opened = new HashSet<string>();
            foreach (HidDeviceInfo info in catalog)
            {
                if (info == null || string.IsNullOrEmpty(info.Path)) continue;
                // Generic Desktop only: these are the nodes that also feed the input
                // stack, so reading them here cannot swallow a protocol reply.
                if (info.UsagePage != UsagePageGenericDesktop) continue;
                if (blocked.Contains(info.Path)) continue;
                if (!opened.Add(info.Path)) continue;

                int fd = open(info.Path, O_RDONLY | O_NONBLOCK);
                if (fd < 0) continue;   // permission missing: stay silent, timer still works
                PollFd entry = new PollFd();
                entry.fd = fd;
                entry.events = POLLIN;
                _fds.Add(entry);
            }
            Program.Log("activity watcher: watching " + _fds.Count + " input node(s)");
        }

        private void CloseAll()
        {
            foreach (PollFd entry in _fds)
            {
                try { close(entry.fd); } catch { }
            }
            _fds.Clear();
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread.Join(1500); } catch { }
            CloseAll();
        }
    }
}
