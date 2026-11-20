using System;
using System.IO;
using System.Threading;
using Avalonia;

namespace Periphora.Linux
{
    internal static class Program
    {
        private static Mutex _singleInstance;

        internal static void Log(string message)
        {
            try
            {
                Periphora.Linux.Platform.LinuxSettings.EnsureDirectory();
                File.AppendAllText(Periphora.Linux.Platform.LinuxSettings.LogPath,
                    DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool created;
            _singleInstance = new Mutex(true, "Periphora-Linux-SingleInstance", out created);
            if (!created) return;
            try
            {
                Log("starting " + string.Join(" ", args));
                foreach (string argument in args)
                {
                    if (string.Equals(argument, "--test", StringComparison.OrdinalIgnoreCase))
                    {
                        RunHardwareTest();
                        return;
                    }
                }
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                Log("lifetime exited");
            }
            catch (Exception ex)
            {
                Log("fatal: " + ex);
                throw;
            }
            finally
            {
                if (_singleInstance != null) _singleInstance.Dispose();
            }
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
        }

        private static void RunHardwareTest()
        {
            Console.WriteLine("Periphora Linux HID diagnostic");
            Console.WriteLine("architecture: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            try
            {
                string[] nodes = Directory.GetFiles("/dev", "hidraw*");
                Console.WriteLine("hidraw nodes: " + nodes.Length);
                foreach (string node in nodes) Console.WriteLine("  " + node);
            }
            catch (Exception ex) { Console.WriteLine("hidraw scan: " + ex.Message); }

            Periphora.Core.DeviceManager manager = new Periphora.Core.DeviceManager();
            manager.Refresh();
            System.Collections.Generic.List<Periphora.Core.DeviceSnapshot> snapshot = manager.Snapshot();
            Console.WriteLine("detected devices: " + snapshot.Count);
            foreach (Periphora.Core.DeviceSnapshot item in snapshot)
            {
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("name    : " + item.Info.Name);
                Console.WriteLine("brand   : " + item.Info.Brand);
                Console.WriteLine("model   : " + item.Info.Model);
                Console.WriteLine("vid/pid : " + item.Info.VendorId.ToString("X4") + ":" + item.Info.ProductId.ToString("X4"));
                Console.WriteLine("path    : " + item.Info.HidPath);
                Console.WriteLine("online  : " + item.Reading.Online);
                Console.WriteLine("percent : " + (item.Reading.Percent.HasValue ? item.Reading.Percent.Value + "%" : "null"));
                Console.WriteLine("charging: " + item.Reading.Charging);
                Console.WriteLine("detail  : " + item.Reading.Detail);
                Console.WriteLine("raw     : " + item.Reading.Raw);
                Console.WriteLine("error   : " + item.Reading.Error);
            }
        }
    }
}
