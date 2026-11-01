// Installed keyboard layouts, the active layout of a window, switching it, and per-layout
// character tables built with ToUnicodeEx.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KeyboardSwitch
{
    class WinLayouts : ISystemLayouts
    {
        const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
        const uint MAPVK_VK_TO_VSC = 0;
        const uint TOUNICODE_NO_STATE_CHANGE = 0x4;
        const uint VK_SPACE = 0x20;
        const string RegLayouts = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

        class RegistryInfo
        {
            public string Text = "";
            public int LayoutId = -1;
        }

        static Dictionary<string, RegistryInfo> registry;
        static readonly byte[][] KeyStates = Enumerable.Range(0, Converter.States.Length).Select(StateBuffer).ToArray();
        static readonly byte[] EmptyState = new byte[256];

        public static string IdOf(IntPtr hkl)
        {
            return hkl.ToInt64().ToString("X16");
        }

        /// <summary>KLID -> registry info (read once).</summary>
        static Dictionary<string, RegistryInfo> Registry()
        {
            if (registry != null) return registry;
            registry = new Dictionary<string, RegistryInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegLayouts))
                {
                    foreach (string klid in root.GetSubKeyNames())
                    {
                        using (RegistryKey key = root.OpenSubKey(klid))
                        {
                            var info = new RegistryInfo();
                            info.Text = (key.GetValue("Layout Text") as string) ?? "";
                            string id = key.GetValue("Layout Id") as string;
                            if (id != null) info.LayoutId = Convert.ToInt32(id, 16);
                            registry[klid.ToUpperInvariant()] = info;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Names fall back to the KLID.
            }
            return registry;
        }

        /// <summary>Keyboard layout id (KLID, e.g. "00000429") of an HKL, without activating it.</summary>
        public static string KlidOf(IntPtr hkl)
        {
            long v = hkl.ToInt64();
            int lang = (int)(v & 0xFFFF);
            int device = (int)((v >> 16) & 0xFFFF);
            if ((device & 0xF000) == 0xF000)
            {
                // Layout variant: the low bits are the registry "Layout Id".
                int id = device & 0x0FFF;
                foreach (var kv in Registry())
                {
                    if (kv.Value.LayoutId == id) return kv.Key;
                }
            }
            if ((device & 0xF000) == 0xE000) return (v & 0xFFFFFFFF).ToString("X8");   // IME
            return (device != 0 ? device : lang).ToString("X8");
        }

        static string LanguageName(int langId)
        {
            try
            {
                return new CultureInfo(langId).EnglishName;
            }
            catch (Exception)
            {
                return langId.ToString("X4");
            }
        }

        public static IntPtr[] ListHkls()
        {
            int n = Native.GetKeyboardLayoutList(0, null);
            var list = new IntPtr[Math.Max(n, 1)];
            int got = Native.GetKeyboardLayoutList(list.Length, list);
            return list.Take(got).ToArray();
        }

        /// <summary>Identity of the set of installed layouts, cheap to poll for changes.</summary>
        public static string Signature()
        {
            return string.Join(",", ListHkls().Select(IdOf));
        }

        static byte[] StateBuffer(int state)
        {
            var ks = new byte[256];
            if ((state & 1) != 0) ks[0x10] = ks[0xA0] = 0x80;                          // Shift, LShift
            if ((state & 2) != 0) ks[0x11] = ks[0xA2] = ks[0x12] = ks[0xA5] = 0x80;     // AltGr = Ctrl + Alt
            return ks;
        }

        static bool IsControl(string s)
        {
            foreach (char c in s) if (c < 0x20 || c == 0x7F) return true;
            return false;
        }

        static bool SkipVk(int vk)
        {
            // Numpad keys type the same digits/operators in every layout.
            return (vk >= 0x60 && vk <= 0x6F) || vk == 0x90;
        }

        public static Layout Load(IntPtr hkl)
        {
            string klid = KlidOf(hkl);
            RegistryInfo info;
            string text = Registry().TryGetValue(klid, out info) && info.Text != "" ? info.Text : klid;
            var layout = new Layout
            {
                Id = IdOf(hkl),
                Hkl = hkl,
                Klid = klid,
                Name = text + " (" + LanguageName((int)(hkl.ToInt64() & 0xFFFF)) + ")",
            };

            var buf = new char[16];
            var flush = new char[16];
            for (int state = 0; state < Converter.States.Length; state++)
            {
                for (int vk = 1; vk < 256; vk++)
                {
                    if (SkipVk(vk)) continue;
                    uint scan = Native.MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_VSC, hkl);
                    int n = Native.ToUnicodeEx((uint)vk, scan, KeyStates[state], buf, buf.Length, TOUNICODE_NO_STATE_CHANGE, hkl);
                    if (n < 0)
                    {
                        // Dead key: the buffer holds its spacing form. Flush the pending dead key state.
                        n = 1;
                        for (int i = 0; i < 4 && Native.ToUnicodeEx(VK_SPACE, 0x39, EmptyState, flush, flush.Length, TOUNICODE_NO_STATE_CHANGE, hkl) < 0; i++) { }
                    }
                    if (n <= 0) continue;
                    string s = new string(buf, 0, n);
                    if (IsControl(s)) continue;
                    // AltGr giving the same as the plain key adds nothing.
                    string plain;
                    if ((state & 2) != 0 && layout.Fwd.TryGetValue(Converter.KeyId(vk, state & 1), out plain) && plain == s) continue;
                    layout.Add(Converter.KeyId(vk, state), s);
                }
            }
            return layout;
        }

        public static List<Layout> LoadAll()
        {
            return ListHkls().Select(Load).ToList();
        }

        /// <summary>Thread that owns the keyboard focus for a top-level window (UWP frames host other threads).</summary>
        static uint FocusThread(IntPtr hwnd)
        {
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(hwnd, out pid);
            var info = new Native.GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (tid != 0 && Native.GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero)
            {
                uint focusTid = Native.GetWindowThreadProcessId(info.hwndFocus, out pid);
                if (focusTid != 0) return focusTid;
            }
            return tid;
        }

        public string ActiveId(IntPtr hwnd)
        {
            return IdOf(Native.GetKeyboardLayout(FocusThread(hwnd)));
        }

        public void SwitchTo(IntPtr hwnd, IntPtr hkl)
        {
            Native.PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        }
    }
}
