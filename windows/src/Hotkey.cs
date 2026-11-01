// Shortcut text ("Ctrl+Alt+Space") <-> RegisterHotKey arguments, and the hotkey window.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace KeyboardSwitch
{
    static class Shortcut
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        static readonly Dictionary<string, Keys> Symbols = new Dictionary<string, Keys>
        {
            { "-", Keys.OemMinus }, { "=", Keys.Oemplus }, { "[", Keys.OemOpenBrackets }, { "]", Keys.OemCloseBrackets },
            { "\\", Keys.OemPipe }, { ";", Keys.OemSemicolon }, { "'", Keys.OemQuotes }, { ",", Keys.Oemcomma },
            { ".", Keys.OemPeriod }, { "/", Keys.OemQuestion }, { "`", Keys.Oemtilde },
        };

        static readonly HashSet<Keys> ModifierKeys = new HashSet<Keys>
        {
            Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey,
            Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin,
        };

        public static bool IsModifier(Keys key)
        {
            return ModifierKeys.Contains(key);
        }

        public static bool IsFunctionKey(Keys key)
        {
            return key >= Keys.F1 && key <= Keys.F24;
        }

        public static string KeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + (int)(key - Keys.NumPad0);
            foreach (var kv in Symbols) if (kv.Value == key) return kv.Key;
            return key.ToString();
        }

        static bool TryParseKey(string name, out Keys key)
        {
            key = Keys.None;
            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                key = Keys.D0 + (name[0] - '0');
                return true;
            }
            if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3]))
            {
                key = Keys.NumPad0 + (name[3] - '0');
                return true;
            }
            if (Symbols.TryGetValue(name, out key)) return true;
            if (name.All(char.IsDigit)) return false;   // Enum.TryParse would accept raw numbers
            return Enum.TryParse(name, true, out key) && !IsModifier(key) && key != Keys.None;
        }

        public static string Format(bool ctrl, bool alt, bool shift, bool win, Keys key)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            // "Ctrl++" style isn't used; "=" is the name of the plus key.
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win": case "super": mods |= MOD_WIN; break;
                    default: return false;
                }
            }
            Keys key;
            if (!TryParseKey(parts[parts.Length - 1].Trim(), out key)) return false;
            vk = (uint)key;
            return mods != 0 || IsFunctionKey(key);
        }
    }

    /// <summary>Hidden window that receives WM_HOTKEY.</summary>
    class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const int Id = 1;
        bool registered;

        public event Action Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        /// <summary>Register `shortcut`; returns an error message, or "" on success.</summary>
        public string Register(string shortcut)
        {
            Unregister();
            if (string.IsNullOrEmpty(shortcut)) return "";
            uint mods, vk;
            if (!Shortcut.TryParse(shortcut, out mods, out vk)) return shortcut + " is not a valid shortcut.";
            if (!Native.RegisterHotKey(Handle, Id, mods | Shortcut.MOD_NOREPEAT, vk))
            {
                return shortcut + " is already used by another program. Pick another combination.";
            }
            registered = true;
            return "";
        }

        public void Unregister()
        {
            if (registered) Native.UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && Pressed != null) Pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
