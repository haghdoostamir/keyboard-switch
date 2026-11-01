// Settings window: layouts, shortcut, behaviour options and the "Try it" box.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KeyboardSwitch
{
    /// <summary>Text box that receives every key, for capturing a shortcut.</summary>
    class ShortcutBox : TextBox
    {
        protected override bool IsInputKey(Keys keyData)
        {
            return true;
        }
    }

    class MainForm : Form
    {
        static readonly Color Ok = Color.FromArgb(21, 128, 61);
        static readonly Color Bad = Color.FromArgb(185, 28, 28);

        class LayoutItem
        {
            public Layout Layout;
            public bool Active;

            public override string ToString()
            {
                return Layout.Name + "   [" + Layout.Klid + "]" + (Active ? "   ● active" : "");
            }
        }

        readonly TrayApp app;
        readonly TableLayoutPanel root;
        readonly List<Label> wrapping = new List<Label>();
        readonly Label status, message, shortcutInfo, tryRoute, tryOut;
        readonly CheckedListBox list;
        readonly ShortcutBox shortcutBox;
        readonly CheckBox selectAll, switchLayout, reselect, autostart;
        readonly TextBox tryIn;
        readonly Timer messageTimer = new Timer { Interval = 8000 };
        bool updating, quitting;
        string pending;
        string shownActive = "";

        public MainForm(TrayApp app)
        {
            this.app = app;
            Text = "Keyboard Switch";
            Icon = AppIcon.Get(new Size(32, 32));
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(540, 760);
            MinimumSize = new Size(440, 480);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 8, 12, 12), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var title = new Label { Text = "Keyboard Switch", AutoSize = true, Font = new Font("Segoe UI Semibold", 14F) };
            status = WrapLabel();
            status.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            message = WrapLabel();
            message.ForeColor = Bad;
            var helpLink = new LinkLabel { Text = "Help", AutoSize = true, Font = new Font("Segoe UI", 10F), Margin = new Padding(14, 9, 3, 0) };
            helpLink.LinkClicked += delegate { app.ShowHelp(); };
            AddRow(Row(title, helpLink));
            AddRow(status);
            AddRow(message);

            // Layouts
            list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 90 };
            list.ItemCheck += OnLayoutCheck;
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            refresh.Click += delegate { app.ReloadLayouts(true); RefreshState(); };
            var langSettings = new Button { Text = "Open Windows language settings", AutoSize = true };
            langSettings.Click += delegate { Process.Start("ms-settings:regionlanguage"); };
            AddRow(Group("Keyboard layouts", list,
                Hint("Checked layouts take part in the cycle. Add or remove layouts in Windows settings; they show up here automatically."),
                Row(refresh, langSettings)));

            // Shortcut
            shortcutBox = new ShortcutBox { ReadOnly = true, BackColor = SystemColors.Window, Width = 230, Cursor = Cursors.Hand };
            shortcutBox.Enter += delegate { app.CaptureShortcut(true); Info("Press the new combination, then Apply.", SystemColors.GrayText); };
            shortcutBox.Leave += delegate { app.CaptureShortcut(false); };
            shortcutBox.KeyDown += OnShortcutKey;
            var apply = new Button { Text = "Apply", AutoSize = true };
            apply.Click += delegate
            {
                if (pending != null) ApplyShortcut(pending);
                else Info("Click the field and press a key combination first.", SystemColors.GrayText);
            };
            var reset = new Button { Text = "Default", AutoSize = true };
            reset.Click += delegate { ApplyShortcut(Settings.DefaultShortcut); };
            shortcutInfo = WrapLabel();
            AddRow(Group("Shortcut", Row(shortcutBox, apply, reset), shortcutInfo));

            // Behaviour
            selectAll = Option("With nothing selected, convert the whole text field (Ctrl+A)", v => app.Settings.SelectAll = v);
            switchLayout = Option("Switch the window's layout to the new language", v => app.Settings.SwitchLayout = v);
            reselect = Option("Keep the converted text selected (press again to undo / cycle)", v => app.Settings.Reselect = v);
            autostart = new CheckBox { Text = "Start automatically on login", AutoSize = true };
            autostart.CheckedChanged += delegate
            {
                if (updating) return;
                try
                {
                    SettingsStore.Autostart = autostart.Checked;
                }
                catch (Exception e)
                {
                    ShowMessage("Could not change autostart: " + e.Message);
                }
            };
            AddRow(Group("Behaviour", selectAll, switchLayout, reselect, autostart));

            // Try it
            tryIn = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI", 11F) };
            tryIn.TextChanged += delegate { Preview(); };
            tryRoute = WrapLabel();
            tryRoute.ForeColor = SystemColors.GrayText;
            tryOut = WrapLabel();
            tryOut.Font = new Font("Segoe UI", 14F);
            AddRow(Group("Try it", Hint("Type something with the wrong layout, e.g. sghl o,fd?"), tryIn, tryRoute, tryOut));

            messageTimer.Tick += delegate { message.Text = ""; messageTimer.Stop(); };
            Resize += delegate { FitLabels(); };
            Activated += delegate { RefreshState(); };
            FitLabels();
            RefreshState();
        }

        // ------------------------------------------------------------------ building blocks

        void AddRow(Control c)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(c);
        }

        Label WrapLabel()
        {
            var label = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            wrapping.Add(label);
            return label;
        }

        Label Hint(string text)
        {
            Label label = WrapLabel();
            label.Text = text;
            label.ForeColor = SystemColors.GrayText;
            return label;
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.AddRange(controls);
            return row;
        }

        static GroupBox Group(string title, params Control[] children)
        {
            var box = new GroupBox
            {
                Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 10, 0, 0),
            };
            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (Control c in children)
            {
                inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                inner.Controls.Add(c);
            }
            box.Controls.Add(inner);
            return box;
        }

        CheckBox Option(string text, Action<bool> set)
        {
            var box = new CheckBox { Text = text, AutoSize = true };
            box.CheckedChanged += delegate
            {
                if (updating) return;
                set(box.Checked);
                app.Save();
            };
            return box;
        }

        void FitLabels()
        {
            int width = Math.Max(200, root.ClientSize.Width - 60);
            foreach (Label label in wrapping) label.MaximumSize = new Size(width, 0);
        }

        // ------------------------------------------------------------------ state

        string ActiveId()
        {
            return app.SystemLayouts.ActiveId(Handle);
        }

        public void RefreshState()
        {
            updating = true;
            try
            {
                Settings s = app.Settings;
                shownActive = ActiveId();
                list.Items.Clear();
                foreach (Layout layout in app.Engine.Layouts)
                {
                    var item = new LayoutItem { Layout = layout, Active = layout.Id == shownActive };
                    list.Items.Add(item, !s.DisabledLayouts.Contains(layout.Id));
                }
                list.Height = Math.Max(3, list.Items.Count) * list.ItemHeight + 6;
                if (pending == null) shortcutBox.Text = s.Shortcut;
                selectAll.Checked = s.SelectAll;
                switchLayout.Checked = s.SwitchLayout;
                reselect.Checked = s.Reselect;
                autostart.Checked = SettingsStore.Autostart;
            }
            finally
            {
                updating = false;
            }
            UpdateStatus();
            Preview();
        }

        void UpdateStatus()
        {
            if (app.ShortcutError != "")
            {
                status.ForeColor = Bad;
                status.Text = "Shortcut problem: " + app.ShortcutError;
            }
            else if (app.Engine.Enabled().Count < 2)
            {
                status.ForeColor = Bad;
                status.Text = "Setup needed: enable at least two keyboard layouts.";
            }
            else
            {
                status.ForeColor = Ok;
                status.Text = "Ready. Select text anywhere and press " + app.Settings.Shortcut + ".";
            }
        }

        /// <summary>Called by the tray app twice a second.</summary>
        public void Poll(bool layoutsChanged)
        {
            if (!Visible) return;
            if (layoutsChanged || (ActiveForm == this && ActiveId() != shownActive)) RefreshState();
        }

        void OnLayoutCheck(object sender, ItemCheckEventArgs e)
        {
            if (updating) return;
            string id = ((LayoutItem)list.Items[e.Index]).Layout.Id;
            app.Settings.DisabledLayouts.Remove(id);
            if (e.NewValue != CheckState.Checked) app.Settings.DisabledLayouts.Add(id);
            app.Save();
            BeginInvoke(new Action(delegate { UpdateStatus(); Preview(); }));
        }

        void Preview()
        {
            string text = tryIn.Text;
            if (text == "" || app.Engine.Enabled().Count < 2)
            {
                tryRoute.Text = tryOut.Text = "";
                return;
            }
            Plan plan = app.Engine.MakePlan(text, ActiveId());
            tryRoute.Text = app.Engine.Layouts[plan.Src].Name + "  →  " + app.Engine.Layouts[plan.Dst].Name;
            tryOut.Text = plan.Result;
        }

        // ------------------------------------------------------------------ shortcut

        void OnShortcutKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Escape)
            {
                pending = null;
                shortcutBox.Text = app.Settings.Shortcut;
                ActiveControl = null;
                return;
            }
            if (Shortcut.IsModifier(e.KeyCode)) return;
            bool win = Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
            // Plain keys would steal normal typing; only F-keys may go without modifiers.
            if (!e.Control && !e.Alt && !e.Shift && !win && !Shortcut.IsFunctionKey(e.KeyCode)) return;
            pending = Shortcut.Format(e.Control, e.Alt, e.Shift, win, e.KeyCode);
            shortcutBox.Text = pending;
        }

        void ApplyShortcut(string shortcut)
        {
            string error = app.SetShortcut(shortcut);
            pending = null;
            ActiveControl = null;
            if (error == "") Info("Saved: " + shortcut, Ok);
            else Info(error, Bad);
            RefreshState();
        }

        void Info(string text, Color color)
        {
            shortcutInfo.ForeColor = color;
            shortcutInfo.Text = text;
        }

        // ------------------------------------------------------------------ window

        public void ShowMessage(string text)
        {
            message.Text = text;
            messageTimer.Stop();
            messageTimer.Start();
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void Quit()
        {
            quitting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window keeps the app running in the tray.
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }
    }
}
