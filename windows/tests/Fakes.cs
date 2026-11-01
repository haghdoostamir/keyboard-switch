// Real Windows tables (exported from this machine) and a fake text field driving the engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KeyboardSwitch.Tests
{
    static class Fixture
    {
        public static string Path;

        /// <summary>Layouts like WinLayouts builds them; Rev is rebuilt with the same preference order.</summary>
        public static List<Layout> Load()
        {
            var json = (object[])new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path));
            var result = new List<Layout>();
            for (int i = 0; i < json.Length; i++)
            {
                var o = (Dictionary<string, object>)json[i];
                var layout = new Layout { Id = (string)o["id"], Klid = (string)o["klid"], Name = (string)o["name"], Hkl = new IntPtr(i + 1) };
                foreach (object[] pair in (object[])o["fwd"]) layout.Add((int)pair[0], (string)pair[1]);
                result.Add(layout);
            }
            return result;
        }
    }

    class FakeOptions
    {
        public string Text = "";
        public int SelStart, SelEnd;
        public bool SelectAllWorks = true;
        public bool EmptySelectionCopiesLine;
        public string Process = "notepad.exe";
        public bool? Elevated = false;
        public bool ModifiersReleased = true;
    }

    /// <summary>A focused text field plus fake input / clipboard / system layers.</summary>
    class FakeDesktop : IInput, IClipboard, ISystemLayouts
    {
        readonly FakeOptions opts;
        readonly List<Layout> layouts;
        public string Text;
        public int SelStart, SelEnd;
        public string Clip = "my clipboard";
        public uint Seq = 1;
        public bool EmptySelCopy;
        public string Active;
        public readonly List<string> Typed = new List<string>();
        public readonly List<string> Copies = new List<string>();
        public readonly List<string> Notes = new List<string>();

        public FakeDesktop(List<Layout> layouts, FakeOptions opts)
        {
            this.layouts = layouts;
            this.opts = opts;
            Text = opts.Text;
            SelStart = opts.SelStart;
            SelEnd = opts.SelEnd;
            Active = layouts[0].Id;
        }

        void SetClip(string text, bool emptySel)
        {
            Seq++;
            Clip = text;
            EmptySelCopy = emptySel;
        }

        static Task Done()
        {
            return Task.FromResult(0);
        }

        // IInput
        public bool SelfElevated { get { return false; } }
        public Task<bool> WaitForModifiersReleased() { return Task.FromResult(opts.ModifiersReleased); }
        public Task Sleep(int ms) { return Done(); }

        public ForegroundInfo Foreground()
        {
            return new ForegroundInfo { Hwnd = new IntPtr(7), ProcessName = opts.Process, ClassName = "Notepad", Elevated = opts.Elevated };
        }

        public Task Copy(bool terminal)
        {
            Copies.Add(terminal ? "Ctrl+Shift+C" : "Ctrl+C");
            if (SelStart != SelEnd) SetClip(Text.Substring(SelStart, SelEnd - SelStart), false);
            else if (opts.EmptySelectionCopiesLine) SetClip(Text + "\r\n", true);
            return Done();
        }

        public Task SelectAll()
        {
            if (opts.SelectAllWorks)
            {
                SelStart = 0;
                SelEnd = Text.Length;
            }
            return Done();
        }

        public Task TypeText(string t)
        {
            Typed.Add(t);
            Text = Text.Substring(0, SelStart) + t + Text.Substring(SelEnd);
            SelStart = SelEnd = SelStart + t.Length;
            return Done();
        }

        public Task SelectLeft(int n)
        {
            // Step back n graphemes from the caret.
            int end = SelEnd, start = end;
            while (start > 0 && Converter.GraphemeCount(Text.Substring(start, end - start)) < n) start--;
            while (start > 0 && Converter.GraphemeCount(Text.Substring(start - 1, end - start + 1)) == n) start--;
            SelStart = start;
            return Done();
        }

        // IClipboard
        public uint Sequence() { return Seq; }
        public string ReadText() { return Clip; }
        public bool EmptySelectionCopy() { return EmptySelCopy; }
        public object Save() { return Clip; }
        public void Restore(object saved) { SetClip((string)saved, false); }

        // ISystemLayouts
        public string ActiveId(IntPtr hwnd) { return Active; }

        public void SwitchTo(IntPtr hwnd, IntPtr hkl)
        {
            Active = layouts.First(l => l.Hkl == hkl).Id;
        }
    }
}
