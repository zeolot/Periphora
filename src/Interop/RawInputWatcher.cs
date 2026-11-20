// Global mouse/keyboard activity via Raw Input.
//
// Why not just poll faster: a wireless receiver stops answering if it is queried
// too often, which shows up as cards flapping between a real percentage and
// "asleep". Polling every 15s while hidden made that worse, so the app idles at
// one query every five minutes instead. The catch is that a mouse which just
// woke up keeps showing its stale figure for up to five minutes.
//
// Raw Input solves that without touching the HID layer: the OS delivers every
// input packet to a RIDEV_INPUTSINK window even when the app is in the
// background, so we learn "the user just moved the mouse" for free. The handler
// only raises an event; the caller decides whether a refresh is worth it.
//
// Registering for raw input does not open the device, so it cannot steal reports
// from the vendor software (G HUB et al.) the way a second HID read would.
using System;
using System.Runtime.InteropServices;

namespace Periphora.Interop
{
    internal sealed class RawInputWatcher : IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const int WM_DESTROY = 0x0002;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_REMOVE = 0x00000001;
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
        private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        private readonly Action _onActivity;
        private readonly WndProcDelegate _proc;   // keep alive: the OS holds a raw pointer
        private IntPtr _hwnd = IntPtr.Zero;
        private string _className;
        private bool _registered;

        private RawInputWatcher(Action onActivity)
        {
            _onActivity = onActivity;
            _proc = WindowProc;
        }

        /// <summary>
        /// Returns null when the platform refuses to register (for example a
        /// session without a desktop). Callers fall back to timer polling.
        /// </summary>
        internal static RawInputWatcher TryStart(Action onActivity)
        {
            RawInputWatcher watcher = new RawInputWatcher(onActivity);
            return watcher.Start() ? watcher : null;
        }

        private bool Start()
        {
            try
            {
                _className = "PeriphoraRawInput_" + Guid.NewGuid().ToString("N");
                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = _proc;
                wc.hInstance = GetModuleHandle(null);
                wc.lpszClassName = _className;
                if (RegisterClassEx(ref wc) == 0) return false;

                // Message-only window: never visible, never in the taskbar, but it
                // still receives WM_INPUT because the thread pumps its queue.
                _hwnd = CreateWindowEx(0, _className, "Periphora", 0, 0, 0, 0, 0,
                    HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) return false;

                RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[2];
                devices[0].UsagePage = HID_USAGE_PAGE_GENERIC;
                devices[0].Usage = HID_USAGE_GENERIC_MOUSE;
                devices[0].Flags = RIDEV_INPUTSINK;
                devices[0].Target = _hwnd;
                devices[1].UsagePage = HID_USAGE_PAGE_GENERIC;
                devices[1].Usage = HID_USAGE_GENERIC_KEYBOARD;
                devices[1].Flags = RIDEV_INPUTSINK;
                devices[1].Target = _hwnd;

                uint size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE));
                if (!RegisterRawInputDevices(devices, (uint)devices.Length, size))
                {
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                    return false;
                }
                _registered = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                try { _onActivity(); } catch { }
            }
            else if (msg == WM_DESTROY)
            {
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_registered)
            {
                // Drop the sink registration first, otherwise the system keeps
                // posting to a window that is about to disappear.
                RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[2];
                devices[0].UsagePage = HID_USAGE_PAGE_GENERIC;
                devices[0].Usage = HID_USAGE_GENERIC_MOUSE;
                devices[1].UsagePage = HID_USAGE_PAGE_GENERIC;
                devices[1].Usage = HID_USAGE_GENERIC_KEYBOARD;
                devices[0].Flags = devices[1].Flags = RIDEV_REMOVE;
                devices[0].Target = devices[1].Target = IntPtr.Zero;
                try
                {
                    RegisterRawInputDevices(devices, (uint)devices.Length,
                        (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                }
                catch { }
                _registered = false;
            }
            if (_hwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_hwnd); } catch { }
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
