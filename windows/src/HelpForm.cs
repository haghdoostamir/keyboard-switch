// Help window: how to use it, setting it up on another computer, limitations, diagnostics.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace KeyboardSwitch
{
    class HelpForm : Form
    {
        // Each section: title, then its points.
        static readonly string[][] Sections =
        {
            new[]
            {
                "How to use",
                "Select text you typed with the wrong keyboard layout and press the shortcut. The text is retyped as if the next layout had been active, and that window switches to the new layout so you can keep typing.",
                "Press the shortcut again to get the original back (or to go on to the next layout when more than two are enabled).",
                "With nothing selected, the whole text field is converted (you can turn this off).",
                "Line breaks are typed as Shift+Enter, so chat apps don't send the message halfway.",
            },
            new[]
            {
                "Using it on another computer",
                "Only KeyboardSwitch.exe is needed, nothing has to be installed. It runs on Windows 10 and 11 (.NET Framework is part of Windows).",
                @"Put it in a permanent folder first, e.g. %LOCALAPPDATA%\Programs\Keyboard Switch, then start it and tick ""Start automatically on login"". If you move the file later, untick and tick that option again.",
                @"""Windows protected your PC"" (SmartScreen) appears because the program isn't digitally signed: click ""More info"", then ""Run anyway"".",
                @"If the file came from a browser or a messenger and doesn't start: right-click it, Properties, tick ""Unblock"", OK.",
                "Some antivirus programs distrust apps that simulate key presses and register a global shortcut. Add an exception if yours blocks it.",
                "The keyboard layouts installed on that computer are detected automatically; the settings start from the defaults.",
            },
            new[]
            {
                "Where it works only partly",
                "Terminals (Windows Terminal, cmd, PowerShell): select the text with the mouse. The result is typed at the cursor; the selected text stays.",
                "Programs running as administrator: Windows blocks input from normal programs. Run Keyboard Switch as administrator too if you need it there.",
                "Text fields without Ctrl+A: select the text before pressing the shortcut.",
                "Word: a line break in the converted text becomes a line break inside the paragraph.",
                "The temporary copies go through the clipboard and can show up in clipboard history (Win+V). Your clipboard content is restored.",
            },
            new[]
            {
                "Removing it",
                @"Untick ""Start automatically on login"", choose Quit in the tray icon menu and delete the file. Settings are in %APPDATA%\Keyboard Switch.",
            },
            new[]
            {
                "Problems?",
                @"Click ""Copy diagnostic info"" and send the text. It contains the Windows version, the detected keyboard layouts and a conversion self-test, no text you typed.",
            },
        };

        readonly Func<string> extraInfo;
        readonly Label copied;

        public HelpForm(Func<string> extraInfo)
        {
            this.extraInfo = extraInfo;
            Text = "Keyboard Switch - Help";
            Icon = AppIcon.Get(new Size(32, 32));
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(600, 640);
            MinimumSize = new Size(420, 360);
            StartPosition = FormStartPosition.CenterScreen;

            var text = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
            };
            var heading = new Font("Segoe UI Semibold", 12F);
            var body = new Font("Segoe UI", 10F);
            // Formatting needs the control's window handle, so fill it on Load.
            Load += delegate
            {
                foreach (string[] section in Sections)
                {
                    Append(text, (text.TextLength > 0 ? "\n" : "") + section[0] + "\n", heading, false);
                    for (int i = 1; i < section.Length; i++) Append(text, section[i] + "\n", body, true);
                }
                text.Select(0, 0);
            };

            var padding = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 8, 4), BackColor = SystemColors.Window };
            padding.Controls.Add(text);

            var copy = new Button { Text = "Copy diagnostic info", AutoSize = true };
            copy.Click += delegate { CopyDiagnostics(); };
            var close = new Button { Text = "Close", AutoSize = true };
            close.Click += delegate { Close(); };
            copied = new Label { AutoSize = true, ForeColor = Color.FromArgb(21, 128, 61), Margin = new Padding(8, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 8, 10, 10) };
            buttons.Controls.AddRange(new Control[] { copy, close, copied });
            CancelButton = close;

            Controls.Add(padding);
            Controls.Add(buttons);
        }

        /// <summary>Formatting only sticks when it's set on an empty selection at the end.</summary>
        static void Append(RichTextBox box, string s, Font font, bool bullet)
        {
            box.Select(box.TextLength, 0);
            box.SelectionFont = font;
            box.SelectionBullet = bullet;
            box.SelectionIndent = bullet ? 8 : 0;
            box.SelectionHangingIndent = bullet ? 14 : 0;
            box.SelectedText = s;
        }

        void CopyDiagnostics()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                Clipboard.SetText(Diagnostics.Report(false, extraInfo()));
                copied.Text = "Copied. Paste it into a message.";
            }
            catch (Exception e)
            {
                copied.Text = "Could not copy: " + e.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
    }
}
