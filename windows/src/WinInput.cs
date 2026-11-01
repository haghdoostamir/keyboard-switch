// Synthetic keyboard input, modifier state and foreground window info.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace KeyboardSwitch
{
    class WinInput : IInput
    {
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_EXTENDEDKEY = 0x1;
        const uint KEYEVENTF_KEYUP = 0x2;
        const uint KEYEVENTF_UNICODE = 0x4;
        const int Chunk = 64;   // events per SendInput call

        const ushort VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        const ushort VK_LEFT = 0x25, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_A = 0x41, VK_C = 0x43;
        static readonly int[] Modifiers = { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN };
        static readonly HashSet<int> Extended = new HashSet<int> { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, VK_LWIN, VK_RWIN };
        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        readonly bool selfElevated = ProcessElevated(Native.GetCurrentProcess()) == true;

        public bool SelfElevated { get { return selfElevated; } }

        static Native.INPUT KeyEvent(ushort vk, bool up)
        {
            uint flags = up ? KEYEVENTF_KEYUP : 0;
            if (Extended.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            var input = new Native.INPUT { type = INPUT_KEYBOARD };
            input.u.ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = flags };
            return input;
        }

        static Native.INPUT UnicodeEvent(char unit, bool up)
        {
            var input = new Native.INPUT { type = INPUT_KEYBOARD };
            input.u.ki = new Native.KEYBDINPUT { wScan = unit, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) };
            return input;
        }

        /// <summary>Send events in small batches with a short pause, so slow apps keep up.</summary>
        static async Task Send(List<Native.INPUT> events)
        {
            for (int i = 0; i < events.Count; i += Chunk)
            {
                Native.INPUT[] part = events.GetRange(i, Math.Min(Chunk, events.Count - i)).ToArray();
                if (Native.SendInput((uint)part.Length, part, InputSize) != part.Length)
                {
                    throw new InvalidOperationException("Windows rejected the synthetic key events");
                }
                if (i + Chunk < events.Count) await Task.Delay(4);
            }
        }

        static void Tap(List<Native.INPUT> ev, ushort vk)
        {
            ev.Add(KeyEvent(vk, false));
            ev.Add(KeyEvent(vk, true));
        }

        /// <summary>Press `vk` `times` times while holding `mods`.</summary>
        public static Task Combo(ushort[] mods, ushort vk, int times)
        {
            var ev = new List<Native.INPUT>();
            foreach (ushort m in mods) ev.Add(KeyEvent(m, false));
            for (int i = 0; i < times; i++) Tap(ev, vk);
            for (int i = mods.Length - 1; i >= 0; i--) ev.Add(KeyEvent(mods[i], true));
            return Send(ev);
        }

        static bool ModifiersDown()
        {
            foreach (int vk in Modifiers) if (Native.GetAsyncKeyState(vk) < 0) return true;
            return false;
        }

        public async Task<bool> WaitForModifiersReleased()
        {
            int end = Environment.TickCount + 2000;
            while (ModifiersDown())
            {
                if (Environment.TickCount > end) return false;
                await Task.Delay(10);
            }
            return true;
        }

        /// <summary>Type `text` as Unicode key events, independent of the active layout.</summary>
        public Task TypeText(string text)
        {
            var ev = new List<Native.INPUT>();
            foreach (char c in text.Replace("\r\n", "\n"))
            {
                if (c == '\n' || c == '\r')
                {
                    // Shift+Enter: a new line everywhere, while plain Enter sends the message in chat apps.
                    ev.Add(KeyEvent(VK_SHIFT, false));
                    Tap(ev, VK_RETURN);
                    ev.Add(KeyEvent(VK_SHIFT, true));
                }
                else if (c == '\t') Tap(ev, VK_TAB);
                else
                {
                    ev.Add(UnicodeEvent(c, false));
                    ev.Add(UnicodeEvent(c, true));
                }
            }
            return Send(ev);
        }

        public Task Copy(bool terminal)
        {
            return Combo(terminal ? new[] { VK_CONTROL, VK_SHIFT } : new[] { VK_CONTROL }, VK_C, 1);
        }

        public Task SelectAll()
        {
            return Combo(new[] { VK_CONTROL }, VK_A, 1);
        }

        public Task SelectLeft(int count)
        {
            return count > 0 ? Combo(new[] { VK_SHIFT }, VK_LEFT, count) : Task.FromResult(0);
        }

        public Task Sleep(int ms)
        {
            return Task.Delay(ms);
        }

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x8;
        const int TokenElevation = 20;
        const int ERROR_ACCESS_DENIED = 5;

        /// <summary>true / false, or null when it can't be determined.</summary>
        static bool? ProcessElevated(IntPtr process)
        {
            IntPtr token;
            if (!Native.OpenProcessToken(process, TOKEN_QUERY, out token))
            {
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? (bool?)null : false;
            }
            try
            {
                int elevated, len;
                if (Native.GetTokenInformation(token, TokenElevation, out elevated, 4, out len)) return elevated != 0;
                return null;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }

        public ForegroundInfo Foreground()
        {
            var fg = new ForegroundInfo { Hwnd = Native.GetForegroundWindow() };
            Native.GetWindowThreadProcessId(fg.Hwnd, out fg.Pid);
            var cls = new StringBuilder(256);
            Native.GetClassName(fg.Hwnd, cls, cls.Capacity);
            fg.ClassName = cls.ToString();
            IntPtr process = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, fg.Pid);
            if (process != IntPtr.Zero)
            {
                try
                {
                    var buf = new StringBuilder(1024);
                    uint size = (uint)buf.Capacity;
                    if (Native.QueryFullProcessImageName(process, 0, buf, ref size)) fg.ProcessName = Path.GetFileName(buf.ToString());
                    fg.Elevated = ProcessElevated(process);
                }
                finally
                {
                    Native.CloseHandle(process);
                }
            }
            return fg;
        }

        /// <summary>Give the focus back to `hwnd` (e.g. after the tray menu took it); false if it's gone.</summary>
        public static bool Activate(IntPtr hwnd)
        {
            return Native.IsWindow(hwnd) && Native.SetForegroundWindow(hwnd);
        }
    }
}
