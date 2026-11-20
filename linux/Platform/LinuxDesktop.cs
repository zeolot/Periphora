using System;
using System.IO;
using System.Text.Json;

namespace Periphora.Linux.Platform
{
    internal sealed class SettingsData
    {
        public int RefreshSeconds { get; set; } = 90;
    }

    internal static class LinuxSettings
    {
        private static string DirectoryPath
        {
            get
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrWhiteSpace(xdg))
                    xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(xdg, "Periphora");
            }
        }

        private static string FilePath { get { return Path.Combine(DirectoryPath, "settings.json"); } }

        internal static SettingsData Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new SettingsData();
                SettingsData value = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
                if (value == null) return new SettingsData();
                if (value.RefreshSeconds != 15 && value.RefreshSeconds != 30 && value.RefreshSeconds != 90)
                    value.RefreshSeconds = 90;
                return value;
            }
            catch { return new SettingsData(); }
        }

        internal static void Save(SettingsData value)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(value,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    internal static class LinuxAutostart
    {
        private static string DirectoryPath
        {
            get
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrWhiteSpace(xdg))
                    xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(xdg, "autostart");
            }
        }

        private static string FilePath { get { return Path.Combine(DirectoryPath, "periphora.desktop"); } }

        internal static bool IsEnabled()
        {
            try { return File.Exists(FilePath); }
            catch { return false; }
        }

        internal static bool Set(bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return true;
                }

                string executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) return false;
                Directory.CreateDirectory(DirectoryPath);
                string escaped = executable.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string desktop =
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=Periphora\n" +
                    "Name[zh_CN]=外设电量\n" +
                    "Comment=Wireless peripheral battery monitor\n" +
                    "Exec=\"" + escaped + "\"\n" +
                    "Terminal=false\n" +
                    "X-GNOME-Autostart-enabled=true\n";
                File.WriteAllText(FilePath, desktop);
                return true;
            }
            catch { return false; }
        }
    }
}
