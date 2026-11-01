// End-to-end test with real input, clipboard and layout switching, against fields in our own
// window (Edit, RichEdit, and a VS Code-like field). Aborts if that window isn't in the foreground,
// so no keys ever reach other applications.
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeyboardSwitch.Tests
{
    /// <summary>Like VS Code: Ctrl+C with no selection copies the line and marks the copy.</summary>
    class VsCodeLikeBox : TextBox
    {
        protected override void WndProc(ref Message m)
        {
            const int WM_COPY = 0x0301;
            if (m.Msg == WM_COPY && SelectionLength == 0)
            {
                var data = new DataObject();
                data.SetText(Text + "\r\n");
                data.SetData("vscode-editor-data", "{\"version\":1,\"isFromEmptySelection\":true}");
                Clipboard.SetDataObject(data, true);
                return;
            }
            base.WndProc(ref m);
        }
    }

    class E2E : Form
    {
        readonly TextBox edit = new TextBox { Width = 460, Font = new System.Drawing.Font("Segoe UI", 12F) };
        readonly RichTextBox rich = new RichTextBox { Width = 460, Height = 50, Font = new System.Drawing.Font("Segoe UI", 12F) };
        readonly VsCodeLikeBox vscode = new VsCodeLikeBox { Width = 460, Font = new System.Drawing.Font("Segoe UI", 12F) };
        // Like a chat app: Enter sends, Shift+Enter starts a new line.
        readonly TextBox chat = new TextBox { Width = 460, Height = 60, Multiline = true, AcceptsReturn = true, Font = new System.Drawing.Font("Segoe UI", 12F) };
        int sent;
        readonly WinLayouts system = new WinLayouts();
        readonly Engine engine;
        int failures;

        [STAThread]
        static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var form = new E2E();
            Application.Run(form);
            return form.failures;
        }

        E2E()
        {
            Text = "Keyboard Switch E2E";
            TopMost = true;
            ClientSize = new System.Drawing.Size(480, 270);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
            panel.Controls.AddRange(new Control[] { edit, rich, vscode, chat });
            chat.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    sent++;
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(panel);
            engine = new Engine(new WinInput(), new WinClipboard(), system, new Settings(), m => Console.WriteLine("  NOTIFY: " + m));
            engine.Layouts = WinLayouts.LoadAll();
            Shown += async delegate
            {
                try
                {
                    await RunAll();
                }
                catch (Exception e)
                {
                    failures++;
                    Console.WriteLine("ABORT " + e.Message);
                }
                Close();
            };
        }

        void Check(string what, object actual, object expected)
        {
            bool ok = Equals(actual, expected);
            if (!ok) failures++;
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + what + ": " + actual + (ok ? "" : "   (expected " + expected + ")"));
        }

        void Guard()
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                throw new Exception("test window is not in the foreground, aborting");
            }
        }

        string LayoutId(string klid)
        {
            return engine.Layouts.First(l => l.Klid == klid).Id;
        }

        async Task Focus(Control c)
        {
            Activate();
            c.Focus();
            await Task.Delay(300);
            Guard();
        }

        async Task RoundTrip(TextBoxBase box, string name, bool selectFirst, Func<Task> press)
        {
            await Focus(box);
            var seed = new DataObject();
            seed.SetText("ORIGINAL");
            seed.SetData(DataFormats.Html, "<b>ORIGINAL</b>");
            Clipboard.SetDataObject(seed, true);

            system.SwitchTo(Handle, engine.Layouts.First(l => l.Klid == "00000409").Hkl);
            await Task.Delay(200);
            await new WinInput().TypeText("sghl o,fd?");
            await Task.Delay(200);

            Console.WriteLine(name + ":");
            Guard();
            if (selectFirst) box.SelectAll();   // else nothing selected -> select all
            await press();
            await Task.Delay(300);
            Check("first press text", box.Text, "سلام خوبی؟");
            Check("first press selection", box.SelectionStart + "+" + box.SelectionLength, "0+10");
            Check("layout after first press", system.ActiveId(Handle), LayoutId("00000429"));
            Guard();
            await press();
            await Task.Delay(300);
            Check("second press text", box.Text, "sghl o,fd?");
            Check("second press selection", box.SelectionStart + "+" + box.SelectionLength, "0+10");
            Check("layout after second press", system.ActiveId(Handle), LayoutId("00000409"));
            Check("clipboard text restored", Clipboard.GetText(), "ORIGINAL");
            Check("clipboard HTML restored", Clipboard.ContainsData(DataFormats.Html), true);
        }

        async Task RunAll()
        {
            // WinForms TextBox has no Ctrl+A, so select the text here; RichEdit covers select all.
            await RoundTrip(edit, "Edit control, text selected (engine.Trigger)", true, () => engine.Trigger());

            // RichEdit, triggered by a real global hotkey pressed with SendInput.
            using (var hotkey = new HotkeyWindow())
            {
                Check("register Ctrl+Alt+F11", hotkey.Register("Ctrl+Alt+F11"), "");
                Task<Plan> running = null;
                hotkey.Pressed += () => running = engine.Trigger();
                await RoundTrip(rich, "RichEdit control, nothing selected (global hotkey)", false, async () =>
                {
                    running = null;
                    await WinInput.Combo(new ushort[] { 0x11, 0x12 }, 0x7A, 1);   // Ctrl+Alt+F11
                    for (int i = 0; i < 50 && running == null; i++) await Task.Delay(10);
                    if (running == null) throw new Exception("hotkey did not fire");
                    await running;
                });
            }

            // VS Code-like field: line copy without selection must count as "nothing selected".
            engine.Settings.SelectAll = false;
            await Focus(vscode);
            vscode.Text = "sghl";
            vscode.Select(4, 0);
            Console.WriteLine("VS Code-like field, nothing selected, select all off:");
            Plan plan = await engine.Trigger();
            Check("no conversion", plan == null, true);
            Check("text unchanged", vscode.Text, "sghl");

            // Chat-like field with two lines: the line break must not send the message.
            await Focus(chat);
            system.SwitchTo(Handle, engine.Layouts.First(l => l.Klid == "00000409").Hkl);
            chat.Text = "sghl\r\no,fd?";
            chat.SelectAll();
            Console.WriteLine("Chat-like field, two lines:");
            await engine.Trigger();
            await Task.Delay(300);
            Check("first press text", chat.Text, "سلام\r\nخوبی؟");
            Check("first press selection", chat.SelectionStart + "+" + chat.SelectionLength, "0+11");
            await engine.Trigger();
            await Task.Delay(300);
            Check("second press text", chat.Text, "sghl\r\no,fd?");
            Check("messages sent by Enter", sent, 0);
        }
    }
}
