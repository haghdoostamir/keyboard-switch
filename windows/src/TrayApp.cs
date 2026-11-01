// Tray icon, global hotkey, layout polling and the glue between the engine and the window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeyboardSwitch
{
    static class AppIcon
    {
        public static Icon Get(Size size)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeyboardSwitch.icon.ico"))
            {
                return new Icon(stream, size);
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        public const string ShowEventName = @"Local\KeyboardSwitch.Show";
        public const string ConvertEventName = @"Local\KeyboardSwitch.Convert";

        // Taskbar / tray windows: the focus goes there while the tray menu is open.
        static readonly HashSet<string> ShellClasses = new HashSet<string>
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
        };

        public readonly Engine Engine;
        public readonly Settings Settings;
        public readonly WinLayouts SystemLayouts = new WinLayouts();
        readonly WinInput input = new WinInput();
        public string ShortcutError = "";

        readonly NotifyIcon tray;
        readonly MainForm form;
        HelpForm help;
        readonly HotkeyWindow hotkey = new HotkeyWindow();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
        readonly List<RegisteredWaitHandle> waits = new List<RegisteredWaitHandle>();
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        string signature = "";
        IntPtr lastExternalWindow;

        public TrayApp(bool hidden, bool convert)
        {
            Settings = SettingsStore.Load();
            Engine = new Engine(input, new WinClipboard(), SystemLayouts, Settings, Notify);
            ReloadLayouts(true);

            form = new MainForm(this);
            IntPtr unused = form.Handle;   // create it now: BeginInvoke target for other threads

            var menu = new ContextMenuStrip();
            menu.Items.Add("Settings", null, delegate { ShowSettings(); });
            menu.Items.Add("Convert selection", null, delegate { ConvertFromTray(); });
            menu.Items.Add("Help", null, delegate { ShowHelp(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Quit(); });
            tray = new NotifyIcon
            {
                Icon = AppIcon.Get(SystemInformation.SmallIconSize),
                Text = "Keyboard Switch",
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

            hotkey.Pressed += Convert;
            ShortcutError = hotkey.Register(Settings.Shortcut);
            // The window shows it in its status line; only the balloon here.
            if (ShortcutError != "") tray.ShowBalloonTip(4000, "Keyboard Switch", ShortcutError, ToolTipIcon.Warning);

            Listen(ShowEventName, ShowSettings);
            Listen(ConvertEventName, Convert);

            timer.Tick += delegate { Poll(); };
            timer.Start();

            if (convert) Convert();
            if (!hidden) ShowSettings();
        }

        /// <summary>Run `action` on the UI thread whenever another instance signals the named event.</summary>
        void Listen(string name, Action action)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            waits.Add(ThreadPool.RegisterWaitForSingleObject(ev, delegate { form.BeginInvoke(action); }, null, -1, false));
        }

        public void Notify(string message)
        {
            tray.ShowBalloonTip(4000, "Keyboard Switch", message, ToolTipIcon.Warning);
            form.ShowMessage(message);
        }

        public async void Convert()
        {
            await Engine.Trigger();
        }

        async void ConvertFromTray()
        {
            if (lastExternalWindow == IntPtr.Zero || !WinInput.Activate(lastExternalWindow))
            {
                Notify("Click into the text you want to convert first, then use the shortcut.");
                return;
            }
            await Task.Delay(150);
            await Engine.Trigger();
        }

        public void ShowSettings()
        {
            form.ShowWindow();
        }

        public void ShowHelp()
        {
            if (help == null || help.IsDisposed) help = new HelpForm(DiagnosticInfo);
            help.Show();
            if (help.WindowState == FormWindowState.Minimized) help.WindowState = FormWindowState.Normal;
            help.Activate();
        }

        /// <summary>State of the running app for the diagnostic report.</summary>
        string DiagnosticInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Shortcut: " + Settings.Shortcut + (ShortcutError == "" ? " (registered)" : " - " + ShortcutError));
            sb.AppendLine("Options: selectAll=" + Settings.SelectAll + ", switchLayout=" + Settings.SwitchLayout
                + ", reselect=" + Settings.Reselect + ", autostart=" + SettingsStore.Autostart);
            sb.AppendLine("Disabled layouts: " + (Settings.DisabledLayouts.Count == 0 ? "none" : string.Join(", ", Settings.DisabledLayouts)));
            sb.AppendLine("Running as administrator: " + input.SelfElevated);
            return sb.ToString();
        }

        public void Save()
        {
            try
            {
                SettingsStore.Save(Settings);
            }
            catch (Exception e)
            {
                Notify("Could not save the settings: " + e.Message);
            }
        }

        public bool ReloadLayouts(bool force)
        {
            string sig = WinLayouts.Signature();
            if (!force && sig == signature) return false;
            signature = sig;
            Engine.Layouts = WinLayouts.LoadAll();
            return true;
        }

        /// <summary>Change the shortcut; returns an error message, or "" on success.</summary>
        public string SetShortcut(string shortcut)
        {
            string error = hotkey.Register(shortcut);
            if (error != "")
            {
                hotkey.Register(Settings.Shortcut);   // keep the old one working
                return error;
            }
            Settings.Shortcut = shortcut;
            ShortcutError = "";
            Save();
            return "";
        }

        /// <summary>While the shortcut field has focus, release the hotkey so it can be typed there.</summary>
        public void CaptureShortcut(bool capturing)
        {
            if (capturing) hotkey.Unregister();
            else ShortcutError = hotkey.Register(Settings.Shortcut);
        }

        void Poll()
        {
            // Remember the last window outside this app, for "Convert selection" in the tray menu.
            IntPtr hwnd = Native.GetForegroundWindow();
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (hwnd != IntPtr.Zero && pid != ownPid)
            {
                var cls = new StringBuilder(256);
                Native.GetClassName(hwnd, cls, cls.Capacity);
                if (!ShellClasses.Contains(cls.ToString())) lastExternalWindow = hwnd;
            }
            bool changed = false;
            try
            {
                changed = ReloadLayouts(false);
            }
            catch (Exception e)
            {
                Debug.WriteLine("reading layouts: " + e);
            }
            form.Poll(changed);
        }

        void Quit()
        {
            timer.Stop();
            foreach (RegisteredWaitHandle w in waits) w.Unregister(null);
            hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            form.Quit();
            ExitThread();
        }
    }
}
