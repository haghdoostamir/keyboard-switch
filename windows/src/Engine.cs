// The convert-the-selection flow. All system access goes through the interfaces below so it can
// be tested with fakes.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KeyboardSwitch
{
    public class ForegroundInfo
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string ProcessName = "";
        public string ClassName = "";
        public bool? Elevated;   // null: unknown
    }

    public interface IInput
    {
        bool SelfElevated { get; }
        Task<bool> WaitForModifiersReleased();
        ForegroundInfo Foreground();
        Task Copy(bool terminal);
        Task SelectAll();
        Task TypeText(string text);
        Task SelectLeft(int count);
        Task Sleep(int ms);
    }

    public interface IClipboard
    {
        uint Sequence();
        string ReadText();
        bool EmptySelectionCopy();
        object Save();
        void Restore(object saved);
    }

    public interface ISystemLayouts
    {
        string ActiveId(IntPtr hwnd);
        void SwitchTo(IntPtr hwnd, IntPtr hkl);
    }

    public class Settings
    {
        // Win+Space, Alt+Shift and Ctrl+Shift already switch layouts in Windows.
        public const string DefaultShortcut = "Ctrl+Alt+Space";

        public string Shortcut = DefaultShortcut;
        // Layout ids (HKL as 16 hex digits) left out of the cycle; new layouts take part.
        public List<string> DisabledLayouts = new List<string>();
        // With nothing selected, select the whole text field (Ctrl+A) and convert that.
        public bool SelectAll = true;
        // Leave the window on the layout the text was converted to.
        public bool SwitchLayout = true;
        // Select the converted text again, so pressing the shortcut twice restores the original.
        public bool Reselect = true;
    }

    public class Plan
    {
        public int Src, Dst;
        public string Text, Result;
    }

    public class Engine
    {
        const int CopyTimeout = 300;
        const int MaxLength = 5000;

        static readonly HashSet<string> TerminalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe",
            "pwsh.exe", "mintty.exe", "alacritty.exe", "wezterm-gui.exe", "putty.exe",
        };
        static readonly HashSet<string> TerminalClasses = new HashSet<string>
        {
            "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY",
        };

        readonly IInput input;
        readonly IClipboard clipboard;
        readonly ISystemLayouts system;
        public Settings Settings;
        public Action<string> Notify;
        public List<Layout> Layouts = new List<Layout>();
        bool busy;

        public Engine(IInput input, IClipboard clipboard, ISystemLayouts system, Settings settings, Action<string> notify)
        {
            this.input = input;
            this.clipboard = clipboard;
            this.system = system;
            Settings = settings;
            Notify = notify;
        }

        public static bool IsTerminal(ForegroundInfo fg)
        {
            return TerminalProcesses.Contains(fg.ProcessName ?? "") || TerminalClasses.Contains(fg.ClassName ?? "");
        }

        /// <summary>Indices of the layouts that take part in the cycle.</summary>
        public List<int> Enabled()
        {
            var result = new List<int>();
            for (int i = 0; i < Layouts.Count; i++)
            {
                if (!Settings.DisabledLayouts.Contains(Layouts[i].Id)) result.Add(i);
            }
            return result;
        }

        /// <summary>Source/target layout indices and the converted text, given the active layout id.</summary>
        public Plan MakePlan(string text, string activeId)
        {
            List<int> enabled = Enabled();
            var candidates = new List<Layout>();
            int cur = -1;
            for (int k = 0; k < enabled.Count; k++)
            {
                candidates.Add(Layouts[enabled[k]]);
                if (Layouts[enabled[k]].Id == activeId) cur = k;
            }
            int src = enabled[Converter.DetectSource(text, candidates, cur)];
            int dst = Converter.NextLayout(src, enabled);
            return new Plan { Src = src, Dst = dst, Text = text, Result = Converter.Convert(text, Layouts[src], Layouts[dst]) };
        }

        /// <summary>Run one conversion; never throws. Returns the conversion done, or null.</summary>
        public async Task<Plan> Trigger()
        {
            if (busy) return null;
            busy = true;
            try
            {
                return await ConvertSelection();
            }
            catch (Exception e)
            {
                Notify("Conversion failed: " + e.Message);
                return null;
            }
            finally
            {
                busy = false;
            }
        }

        Plan Fail(string message)
        {
            Notify(message);
            return null;
        }

        /// <summary>Copy the selection; null when the clipboard didn't change (nothing selected).</summary>
        async Task<string> CopySelection(bool terminal)
        {
            uint before = clipboard.Sequence();
            await input.Copy(terminal);
            for (int waited = 0; clipboard.Sequence() == before; waited += 10)
            {
                if (waited >= CopyTimeout) return null;
                await input.Sleep(10);
            }
            // The sequence number changes when the app empties the clipboard; the data follows shortly.
            for (int i = 0; i < 10; i++)
            {
                if (clipboard.EmptySelectionCopy()) return null;
                string text = clipboard.ReadText();
                if (!string.IsNullOrEmpty(text)) return text;
                await input.Sleep(10);
            }
            return null;
        }

        class Selection
        {
            public string Text, Error;
        }

        async Task<Selection> ReadSelection(ForegroundInfo fg)
        {
            bool terminal = IsTerminal(fg);
            object saved = clipboard.Save();
            try
            {
                string text = await CopySelection(terminal);
                if (text == null && Settings.SelectAll && !terminal)
                {
                    await input.SelectAll();
                    await input.Sleep(30);
                    text = await CopySelection(false);
                    if (text == null) return new Selection { Error = "Nothing is selected, and select all didn't work here." };
                }
                if (text == null)
                {
                    return new Selection { Error = terminal ? "Select the text with the mouse first (terminal)." : "Select some text first." };
                }
                return new Selection { Text = text };
            }
            finally
            {
                clipboard.Restore(saved);
            }
        }

        async Task<Plan> ConvertSelection()
        {
            if (Enabled().Count < 2) return Fail("Enable at least two keyboard layouts.");
            if (!await input.WaitForModifiersReleased()) return Fail("Release the shortcut keys to convert.");

            ForegroundInfo fg = input.Foreground();
            if (fg.Elevated == true && !input.SelfElevated)
            {
                return Fail((fg.ProcessName != "" ? fg.ProcessName : "This window") + " runs as administrator; Windows " +
                    "doesn't let Keyboard Switch control it. Run Keyboard Switch as administrator too.");
            }

            Selection sel = await ReadSelection(fg);
            if (sel.Error != null) return Fail(sel.Error);
            string text = sel.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Trim().Length == 0) return Fail("Select some text first.");
            if (text.Length > MaxLength) return Fail("The selection is too long (over " + MaxLength + " characters).");

            Plan plan = MakePlan(text, system.ActiveId(fg.Hwnd));
            // Identical text (e.g. two Persian variants): keep the selection, only move on in the cycle.
            bool changed = plan.Result != text;
            if (changed) await input.TypeText(plan.Result);
            if (Settings.SwitchLayout)
            {
                system.SwitchTo(fg.Hwnd, Layouts[plan.Dst].Hkl);
                await input.Sleep(20);
            }
            if (changed && Settings.Reselect)
            {
                await input.Sleep(20);
                await input.SelectLeft(Converter.GraphemeCount(plan.Result));
            }
            return plan;
        }
    }
}
