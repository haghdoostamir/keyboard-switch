// Diagnostic report: system info, detected layouts and the English/Persian test vectors.
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardSwitch
{
    static class Diagnostics
    {
        static string WindowsVersion()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    // Windows 11 still calls itself "Windows 10" in ProductName.
                    string name = Convert.ToString(key.GetValue("ProductName"));
                    int build;
                    if (int.TryParse(Convert.ToString(key.GetValue("CurrentBuild")), out build) && build >= 22000)
                    {
                        name = name.Replace("Windows 10", "Windows 11");
                    }
                    return name + " " + key.GetValue("DisplayVersion") + " (build " + build + "." + key.GetValue("UBR") + ")";
                }
            }
            catch (Exception)
            {
                return Environment.OSVersion.VersionString;
            }
        }

        static string FrameworkRelease()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                return key == null ? "?" : Convert.ToString(key.GetValue("Release"));
            }
        }

        /// <summary>
        /// System info, detected layouts and the English/Persian test vectors, as text for a bug
        /// report. `extra` adds lines from the running app (settings, shortcut state).
        /// </summary>
        public static string Report(bool standard, string extra)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Keyboard Switch " + Assembly.GetExecutingAssembly().GetName().Version);
            sb.AppendLine("Windows: " + WindowsVersion() + (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit"));
            sb.AppendLine(".NET Framework release: " + FrameworkRelease() + ", CLR " + Environment.Version);
            sb.AppendLine("Executable: " + Application.ExecutablePath);
            if (extra != null) sb.Append(extra);

            IntPtr added = IntPtr.Zero;
            if (standard)
            {
                string before = WinLayouts.Signature();
                added = Native.LoadKeyboardLayout("00050429", 0x80);   // KLF_NOTELLSHELL
                if (before.Contains(WinLayouts.IdOf(added))) added = IntPtr.Zero;   // was installed
            }
            try
            {
                var layouts = WinLayouts.LoadAll();
                sb.AppendLine("Active layout: " + new WinLayouts().ActiveId(Native.GetForegroundWindow()));
                foreach (Layout l in layouts)
                {
                    sb.AppendLine(l.Id + "  KLID " + l.Klid + "  " + l.Name + "  (" + l.Fwd.Count + " keys, " + l.Rev.Count + " chars)");
                }
                Layout en = layouts.FirstOrDefault(l => l.Klid == "00000409");
                foreach (string klid in new[] { "00000429", "00050429" })
                {
                    Layout fa = layouts.FirstOrDefault(l => l.Klid == klid);
                    if (en == null || fa == null) continue;
                    sb.AppendLine("Test vectors: US <-> " + fa.Name + " " + klid);
                    string[][] vectors = { new[] { "sghl o,fd?", "سلام خوبی؟" }, new[] { "hdk d; jsj hsj.", "این یک تست است." } };
                    foreach (string[] v in vectors)
                    {
                        string got = Converter.Convert(v[0], en, fa);
                        string back = Converter.Convert(got, fa, en);
                        int src = Converter.DetectSource(got, new[] { en, fa }, 0);
                        sb.AppendLine("  " + v[0] + " -> " + got + (got == v[1] ? " OK" : " FAIL (want " + v[1] + ")")
                            + "; back " + (back == v[0] ? "OK" : "FAIL (" + back + ")") + "; detected source " + (src == 1 ? "OK" : "FAIL"));
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("Error reading layouts: " + e);
            }
            finally
            {
                if (added != IntPtr.Zero) Native.UnloadKeyboardLayout(added);
            }
            return sb.ToString();
        }
    }
}
