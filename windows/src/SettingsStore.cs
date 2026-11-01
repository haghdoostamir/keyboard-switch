// Settings persisted as JSON in %APPDATA%\Keyboard Switch, and the autostart entry.
using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardSwitch
{
    static class SettingsStore
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "Keyboard Switch";

        static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyboard Switch"); }
        }

        public static Settings Load()
        {
            try
            {
                Settings s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Path.Combine(Dir, "settings.json")));
                if (s != null) return s;
            }
            catch (Exception)
            {
                // Missing or broken file: defaults.
            }
            return new Settings();
        }

        public static void Save(Settings settings)
        {
            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, "settings.json");
            File.WriteAllText(file + ".tmp", new JavaScriptSerializer().Serialize(settings));
            if (File.Exists(file)) File.Replace(file + ".tmp", file, null);
            else File.Move(file + ".tmp", file);
        }

        public static bool Autostart
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(RunValue) != null;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --hidden");
                    else key.DeleteValue(RunValue, false);
                }
            }
        }
    }
}
