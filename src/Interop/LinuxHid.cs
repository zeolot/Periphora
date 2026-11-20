#if LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Periphora.Interop
{
    /// <summary>Linux hidraw enumeration and report-descriptor metadata.</summary>
    internal static class LinuxHid
    {
        private const int OpenReadOnly = 0;
        private const int OpenReadWrite = 2;
        private const int OpenWriteOnly = 1;
        private const int OpenNonBlocking = 0x800;
        private const short PollIn = 0x001;
        private const short PollOut = 0x004;
        private const short PollError = 0x008 | 0x010 | 0x020;
        private const int Again = 11;
        private const int NotSupported = 25;

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Result;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr read(int fd, byte[] buffer, UIntPtr count);
        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr write(int fd, byte[] buffer, UIntPtr count);
        [DllImport("libc", SetLastError = true)]
        private static extern int poll([In, Out] PollFd[] fds, UIntPtr count, int timeoutMs);
        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(int fd, UIntPtr request, byte[] value);
        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(int fd, UIntPtr request, ref int value);

        private const uint IocWrite = 1;
        private const uint IocRead = 2;

        private static UIntPtr Request(uint direction, byte number, int length)
        {
            uint value = (direction << 30) | ((uint)length << 16) | ((uint)'H' << 8) | number;
            return new UIntPtr(value);
        }

        private static UIntPtr GetDescriptorSizeRequest()
        {
            return Request(IocRead, 0x01, 4);
        }

        private static UIntPtr GetDescriptorRequest()
        {
            return Request(IocRead, 0x02, 4100);
        }

        internal static List<HidDeviceInfo> Enumerate()
        {
            List<HidDeviceInfo> result = new List<HidDeviceInfo>();
            const string root = "/sys/class/hidraw";
            if (!Directory.Exists(root)) return result;

            string[] nodes;
            try { nodes = Directory.GetDirectories(root, "hidraw*"); }
            catch { return result; }
            Array.Sort(nodes, StringComparer.Ordinal);

            foreach (string sys in nodes)
            {
                try
                {
                    string name = Path.GetFileName(sys);
                    string deviceNode = "/dev/" + name;
                    Dictionary<string, string> uevent = ReadKeyValues(Path.Combine(sys, "device", "uevent"));
                    ushort vid, pid;
                    ParseHidId(uevent, out vid, out pid);
                    if (vid == 0 && pid == 0) continue;

                    string devicePath = RealPath(Path.Combine(sys, "device"));
                    DirectoryInfo usbParent = FindUsbParent(devicePath);
                    string product = Value(uevent, "HID_NAME") ?? ReadText(usbParent, "product");
                    string manufacturer = ReadText(usbParent, "manufacturer");
                    string physicalId = usbParent != null ? usbParent.FullName : devicePath;
                    int interfaceNumber = InterfaceOf(devicePath);
                    byte[] descriptor = ReadBytes(Path.Combine(sys, "device", "report_descriptor"));
                    if (descriptor == null || descriptor.Length == 0)
                        descriptor = ReadDescriptorViaDevice(deviceNode);
                    List<ReportCollection> collections = ReportDescriptorParser.Parse(descriptor);

                    if (collections.Count == 0)
                    {
                        HidDeviceInfo item = BaseInfo(deviceNode, vid, pid, product, manufacturer,
                            physicalId, interfaceNumber);
                        result.Add(item);
                        continue;
                    }

                    for (int i = 0; i < collections.Count; i++)
                    {
                        ReportCollection c = collections[i];
                        HidDeviceInfo item = BaseInfo(deviceNode, vid, pid, product, manufacturer,
                            physicalId, interfaceNumber);
                        item.UsagePage = c.UsagePage;
                        item.Usage = c.Usage;
                        item.InputReportLength = c.InputLength;
                        item.OutputReportLength = c.OutputLength;
                        item.FeatureReportLength = c.FeatureLength;
                        item.CollectionNumber = i + 1;
                        result.Add(item);
                    }
                }
                catch
                {
                    // A single inaccessible or disappearing receiver must not hide
                    // the other devices from the shared catalog.
                }
            }
            return result;
        }

        private static HidDeviceInfo BaseInfo(string path, ushort vid, ushort pid,
            string product, string manufacturer, string physicalId, int interfaceNumber)
        {
            return new HidDeviceInfo
            {
                Path = path,
                VendorId = vid,
                ProductId = pid,
                ProductName = product,
                Manufacturer = manufacturer,
                PhysicalId = physicalId,
                InterfaceNumber = interfaceNumber
            };
        }

        private static Dictionary<string, string> ReadKeyValues(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int split = line.IndexOf('=');
                    if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1);
                }
            }
            catch { }
            return values;
        }

        private static string Value(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }

        private static void ParseHidId(Dictionary<string, string> values, out ushort vid, out ushort pid)
        {
            vid = 0;
            pid = 0;
            string id = Value(values, "HID_ID");
            if (id == null) return;
            string[] parts = id.Split(':');
            if (parts.Length != 3) return;
            uint parsedVid, parsedPid;
            if (uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out parsedVid))
                vid = (ushort)parsedVid;
            if (uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out parsedPid))
                pid = (ushort)parsedPid;
        }

        private static string RealPath(string path)
        {
            try
            {
                DirectoryInfo info = new DirectoryInfo(path);
                FileSystemInfo target = info.ResolveLinkTarget(true);
                return target != null ? target.FullName : info.FullName;
            }
            catch { return path; }
        }

        private static DirectoryInfo FindUsbParent(string path)
        {
            try
            {
                DirectoryInfo current = new DirectoryInfo(path);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "idVendor")) &&
                        File.Exists(Path.Combine(current.FullName, "idProduct"))) return current;
                    current = current.Parent;
                }
            }
            catch { }
            return null;
        }

        private static string ReadText(DirectoryInfo directory, string name)
        {
            if (directory == null) return null;
            try { return File.ReadAllText(Path.Combine(directory.FullName, name)).Trim(); }
            catch { return null; }
        }

        private static byte[] ReadBytes(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch { return null; }
        }

        private static int InterfaceOf(string path)
        {
            if (path == null) return -1;
            Match match = Regex.Match(path, @":\d+\.(\d+)(?:/|$)");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value) ? value : -1;
        }

        private static byte[] ReadDescriptorViaDevice(string path)
        {
            int fd = open(path, OpenReadOnly | OpenNonBlocking);
            if (fd < 0) return null;
            try
            {
                int size = 0;
                if (ioctl(fd, GetDescriptorSizeRequest(), ref size) < 0 || size <= 0 || size > 4096)
                    return null;
                byte[] buffer = new byte[4100];
                if (ioctl(fd, GetDescriptorRequest(), buffer) < 0) return null;
                int actual = BitConverter.ToInt32(buffer, 0);
                if (actual <= 0 || actual > 4096) return null;
                byte[] descriptor = new byte[actual];
                Array.Copy(buffer, 4, descriptor, 0, actual);
                return descriptor;
            }
            catch { return null; }
            finally { close(fd); }
        }

        internal sealed class Session : IDisposable
        {
            private int _fd;
            private readonly int _inputLength;

            internal Session(string path, int inputLength, bool readOnly, bool writeOnly)
            {
                _inputLength = Math.Max(1, inputLength);
                int access = writeOnly ? OpenWriteOnly : readOnly ? OpenReadOnly : OpenReadWrite;
                _fd = open(path, access | OpenNonBlocking);
                if (_fd < 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    string hint = error == 13 ? "（权限不足，请安装随包提供的 udev 规则）" : "";
                    throw new InvalidOperationException("无法打开 HID 设备 " + path + "，错误 " + error + hint);
                }
            }

            internal bool Write(byte[] report, int timeoutMs)
            {
                if (_fd < 0 || report == null || report.Length == 0) return false;
                Stopwatch clock = Stopwatch.StartNew();
                while (true)
                {
                    IntPtr written = write(_fd, report, new UIntPtr((uint)report.Length));
                    if (written.ToInt64() == report.Length) return true;
                    if (written.ToInt64() >= 0) return false;
                    int error = Marshal.GetLastWin32Error();
                    if (error != Again || clock.ElapsedMilliseconds >= timeoutMs) return false;
                    int left = Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds);
                    if (!Wait(PollOut, left)) return false;
                }
            }

            internal byte[] Read(int timeoutMs)
            {
                if (_fd < 0) return null;
                if (!Wait(PollIn, Math.Max(0, timeoutMs))) return null;
                byte[] buffer = new byte[_inputLength];
                IntPtr count = read(_fd, buffer, new UIntPtr((uint)buffer.Length));
                int readCount = (int)count.ToInt64();
                if (readCount <= 0) return null;

                // hidraw omits the synthetic zero report id for unnumbered
                // devices. Windows HIDP_CAPS includes it, so restore that byte
                // when the difference is exactly one to preserve provider offsets.
                if (_inputLength > 1 && readCount == _inputLength - 1)
                {
                    byte[] normalized = new byte[_inputLength];
                    Array.Copy(buffer, 0, normalized, 1, readCount);
                    return normalized;
                }
                byte[] result = new byte[readCount];
                Array.Copy(buffer, result, readCount);
                return result;
            }

            internal bool SetFeature(byte[] report)
            {
                return FeatureIoctl(0x06, report);
            }

            internal bool GetFeature(byte[] report)
            {
                return FeatureIoctl(0x07, report);
            }

            internal bool SetOutputReport(byte[] report, int timeoutMs)
            {
                if (FeatureIoctl(0x0B, report)) return true;
                // HIDIOCSOUTPUT was added after the original hidraw API. Only
                // fall back when the running kernel does not implement it.
                return Marshal.GetLastWin32Error() == NotSupported && Write(report, timeoutMs);
            }

            private bool FeatureIoctl(byte number, byte[] report)
            {
                if (_fd < 0 || report == null || report.Length == 0) return false;
                return ioctl(_fd, Request(IocRead | IocWrite, number, report.Length), report) >= 0;
            }

            private bool Wait(short events, int timeoutMs)
            {
                PollFd[] fds = { new PollFd { Fd = _fd, Events = events } };
                int status;
                do { status = poll(fds, new UIntPtr(1), timeoutMs); }
                while (status < 0 && Marshal.GetLastWin32Error() == 4);
                if (status <= 0) return false;
                return (fds[0].Result & (events | PollError)) != 0 && (fds[0].Result & PollError) == 0;
            }

            public void Dispose()
            {
                if (_fd < 0) return;
                close(_fd);
                _fd = -1;
            }
        }

        internal sealed class ReportCollection
        {
            internal ushort UsagePage;
            internal ushort Usage;
            internal int InputLength;
            internal int OutputLength;
            internal int FeatureLength;
            internal readonly Dictionary<int, int> InputBits = new Dictionary<int, int>();
            internal readonly Dictionary<int, int> OutputBits = new Dictionary<int, int>();
            internal readonly Dictionary<int, int> FeatureBits = new Dictionary<int, int>();

            internal void Finish()
            {
                InputLength = Length(InputBits);
                OutputLength = Length(OutputBits);
                FeatureLength = Length(FeatureBits);
            }

            private static int Length(Dictionary<int, int> reports)
            {
                int length = 0;
                foreach (KeyValuePair<int, int> report in reports)
                {
                    int bytes = (report.Value + 7) / 8 + (report.Key != 0 ? 1 : 0);
                    if (bytes > length) length = bytes;
                }
                return length;
            }
        }

        private static class ReportDescriptorParser
        {
            private struct GlobalState
            {
                internal ushort UsagePage;
                internal int ReportSize;
                internal int ReportCount;
                internal int ReportId;
            }

            internal static List<ReportCollection> Parse(byte[] data)
            {
                List<ReportCollection> result = new List<ReportCollection>();
                if (data == null || data.Length == 0) return result;
                GlobalState global = new GlobalState();
                Stack<GlobalState> pushed = new Stack<GlobalState>();
                List<uint> usages = new List<uint>();
                ReportCollection current = null;
                int offset = 0;
                while (offset < data.Length)
                {
                    byte prefix = data[offset++];
                    if (prefix == 0xFE)
                    {
                        if (offset >= data.Length) break;
                        int longLength = data[offset++];
                        offset++; // long item tag
                        offset += longLength;
                        continue;
                    }
                    int sizeCode = prefix & 3;
                    int size = sizeCode == 3 ? 4 : sizeCode;
                    if (offset + size > data.Length) break;
                    uint value = 0;
                    for (int i = 0; i < size; i++) value |= (uint)data[offset++] << (8 * i);
                    int type = (prefix >> 2) & 3;
                    int tag = (prefix >> 4) & 15;

                    if (type == 1)
                    {
                        switch (tag)
                        {
                            case 0: global.UsagePage = (ushort)value; break;
                            case 7: global.ReportSize = (int)value; break;
                            case 8: global.ReportId = (int)value; break;
                            case 9: global.ReportCount = (int)value; break;
                            case 10: pushed.Push(global); break;
                            case 11: if (pushed.Count > 0) global = pushed.Pop(); break;
                        }
                    }
                    else if (type == 2)
                    {
                        if (tag == 0) usages.Add(value);
                    }
                    else if (type == 0)
                    {
                        if (tag == 10)
                        {
                            if ((value & 0xFF) == 1)
                            {
                                current = new ReportCollection { UsagePage = global.UsagePage };
                                if (usages.Count > 0)
                                {
                                    uint usage = usages[0];
                                    current.Usage = (ushort)(usage & 0xFFFF);
                                    if (usage > 0xFFFF) current.UsagePage = (ushort)(usage >> 16);
                                }
                                result.Add(current);
                            }
                        }
                        else if (tag == 12)
                        {
                            if (current != null) current.Finish();
                            current = null;
                        }
                        else if ((tag == 8 || tag == 9 || tag == 11) && current != null)
                        {
                            int bits = global.ReportSize * global.ReportCount;
                            Dictionary<int, int> reports = tag == 8 ? current.InputBits :
                                tag == 9 ? current.OutputBits : current.FeatureBits;
                            int previous;
                            reports.TryGetValue(global.ReportId, out previous);
                            reports[global.ReportId] = Math.Max(previous, bits);
                        }
                        usages.Clear();
                    }
                }
                foreach (ReportCollection collection in result) collection.Finish();
                return result;
            }
        }
    }

    /// <summary>Cross-platform session facade; protocol providers stay unchanged.</summary>
    public sealed class HidSession : IHidReportSession
    {
        private readonly LinuxHid.Session _session;
        private bool _closed;

        public HidSession(string path, int inputLen, int outputLen)
            : this(path, inputLen, outputLen, false) { }

        public HidSession(string path, int inputLen, int outputLen, bool readOnly)
            : this(path, inputLen, outputLen, readOnly, false) { }

        public HidSession(string path, int inputLen, int outputLen, bool readOnly, bool writeOnly)
        {
            _session = new LinuxHid.Session(path, inputLen, readOnly, writeOnly);
        }

        public bool Write(byte[] report, int timeoutMs)
        {
            return !_closed && _session.Write(report, timeoutMs);
        }

        public byte[] Read(int timeoutMs)
        {
            return _closed ? null : _session.Read(timeoutMs);
        }

        public void Flush(int idleMs)
        {
            for (int i = 0; i < 32; i++) if (Read(idleMs) == null) break;
        }

        public bool SetOutputReport(byte[] report)
        {
            return !_closed && _session.SetOutputReport(report, 500);
        }

        public bool SetFeature(byte[] report)
        {
            return !_closed && _session.SetFeature(report);
        }

        public bool GetFeature(byte[] report)
        {
            return !_closed && _session.GetFeature(report);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _session.Dispose();
        }
    }
}
#endif
