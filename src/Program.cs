using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Periphora.Core;
using Periphora.UI;

namespace Periphora
{
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// Entry point usable from an in-memory compiled assembly
        /// (Add-Type ... then [Periphora.Program]::Run(@('--show'))).
        /// </summary>
        public static void Run(string[] args)
        {
            Main(args != null ? args : new string[0]);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args != null && args.Length > 0 && args[0] == "--test")
                {
                    RunTest();
                    return;
                }
                if (args != null && args.Length > 0 && args[0] == "--rawtest")
                {
                    RunRawInputTest();
                    return;
                }

                bool autoShow = false;
                bool demo = false;
                bool demo1 = false;
                bool menu = false;
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == "--show") autoShow = true;
                        if (args[i] == "--demo") demo = true;
                        if (args[i] == "--demo1") demo1 = true;
                        if (args[i] == "--menu") menu = true;
                    }
                }

                bool createdNew;
                using (Mutex mutex = new Mutex(true, "Periphora-SingleInstance", out createdNew))
                {
                    if (!createdNew)
                    {
                        return;
                    }

                    SetProcessDPIAware();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                    {
                        Log("UI thread: " + e.Exception);
                    };

                    AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                    {
                        Log("Domain: " + e.ExceptionObject);
                    };

                    using (TrayApp app = new TrayApp())
                    {
                        if (menu) app.ShowMenuDemo();
                        else if (demo) app.ShowDemo();
                        else if (demo1) app.ShowDemoSingle();
                        else if (autoShow) app.ShowPanel();
                        Application.Run();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Main: " + ex);
                throw;
            }
        }

        private static void Log(string msg)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "Periphora-error.txt");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Checks that the raw-input sink the tray app relies on can be installed,
        /// and counts how many packets arrive while the user touches the mouse or
        /// keyboard. Run it when "a woken device updates only after a click" needs
        /// to be narrowed down: registration failing means the refresh falls back to
        /// the timer alone.
        /// </summary>
        private static void RunRawInputTest()
        {
            int count = 0;
            Periphora.Interop.RawInputWatcher watcher =
                Periphora.Interop.RawInputWatcher.TryStart(delegate { count++; });

            Console.WriteLine("raw input sink : " + (watcher == null ? "FAILED (timer fallback)" : "installed"));
            if (watcher == null) return;

            Console.WriteLine("move the mouse or press a key for the next 6 seconds...");
            DateTime until = DateTime.Now.AddSeconds(6);
            while (DateTime.Now < until)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(20);
            }
            watcher.Dispose();
            Console.WriteLine("packets received: " + count);
        }

        private static void RunTest()
        {            DeviceManager m = new DeviceManager();
            m.Refresh();

            List<DeviceSnapshot> snaps = m.Snapshot();
            Console.WriteLine("devices: " + snaps.Count);

            foreach (DeviceSnapshot s in snaps)
            {
                Console.WriteLine("----------------------------------------");
                Console.WriteLine("name    : " + s.Info.Name);
                Console.WriteLine("brand   : " + s.Info.Brand);
                Console.WriteLine("model   : " + s.Info.Model);
                Console.WriteLine("vid/pid : " + s.Info.VendorId.ToString("X4") + ":" + s.Info.ProductId.ToString("X4"));
                Console.WriteLine("path    : " + s.Info.HidPath);
                Console.WriteLine("online  : " + s.Reading.Online);
                Console.WriteLine("percent : " + (s.Reading.Percent.HasValue ? s.Reading.Percent.Value.ToString() + "%" : "null"));
                Console.WriteLine("charging: " + s.Reading.Charging);
                Console.WriteLine("detail  : " + s.Reading.Detail);
                Console.WriteLine("raw     : " + s.Reading.Raw);
                Console.WriteLine("error   : " + s.Reading.Error);
            }
        }
    }
}
