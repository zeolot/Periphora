using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Periphora.UI
{
    /// <summary>
    /// Start-with-Windows via the per-user Run key.
    ///
    /// HKCU is used rather than a scheduled task or the Startup folder: no admin
    /// rights, no extra files, and the entry can be removed cleanly when the switch
    /// is turned off.
    /// </summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Periphora";

        /// <summary>
        /// True for any real .exe on disk. The old check required the file to be named
        /// exactly Periphora.exe, which disabled the switch in the delivered build
        /// (Periphora-新版界面.exe). What actually matters is that the Run key would point
        /// at a real executable rather than a script host (the in-memory dev build runs
        /// under powershell.exe and would pop a console window at every boot).
        /// </summary>
        public static bool IsSupported()
        {
            try
            {
                string exe = Application.ExecutablePath;
                if (string.IsNullOrEmpty(exe)) return false;
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

                string host = Path.GetFileName(exe);
                string[] scriptHosts = { "powershell.exe", "pwsh.exe", "cmd.exe",
                                         "wscript.exe", "cscript.exe", "dotnet.exe" };
                foreach (string h in scriptHosts)
                    if (string.Equals(host, h, StringComparison.OrdinalIgnoreCase)) return false;

                return File.Exists(exe);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    return key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;

                    if (enabled)
                    {
                        // Quoted: the path may contain spaces.
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
