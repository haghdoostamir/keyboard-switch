// Unit tests for the converter and the engine flow. Usage: Tests.exe path\to\layouts.json
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KeyboardSwitch.Tests
{
    static class Tests
    {
        static readonly string[][] Vectors =
        {
            new[] { "sghl o,fd?", "سلام خوبی؟" },
            new[] { "hdk d; jsj hsj.", "این یک تست است." },
        };

        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Fixture.Path = args.Length > 0 ? args[0] : Path.Combine("tests", "fixtures", "layouts.json");
            var tests = new List<KeyValuePair<string, Action>>();
            Action<string, Action> add = (name, body) => tests.Add(new KeyValuePair<string, Action>(name, body));

            // ------------------------------------------------------------ converter
            List<Layout> fx = Fixture.Load();
            Layout en = fx[0], fa = fx[1], faStd = fx[2];

            add("US -> Persian test vectors and back", () =>
            {
                foreach (string[] v in Vectors)
                {
                    Eq(Converter.Convert(v[0], en, fa), v[1]);
                    Eq(Converter.Convert(v[1], fa, en), v[0]);
                }
            });
            add("Persian (Standard) round trip", () =>
            {
                Eq(faStd.Klid, "00050429");
                foreach (string[] v in Vectors)
                {
                    string got = Converter.Convert(v[0], en, faStd);
                    True(got != v[0]);
                    Eq(Converter.Convert(got, faStd, en), v[0]);
                }
            });
            add("characters without an equivalent are kept", () =>
                Eq(Converter.Convert("sghl 😀 «x»\n\t", en, fa), "سلام 😀 «ط»\n\t"));
            add("detectSource prefers the active layout on ties, else best coverage", () =>
            {
                var ls = new[] { en, fa };
                Eq(Converter.DetectSource("sghl", ls, 0), 0);
                Eq(Converter.DetectSource("sghl", ls, 1), 0);
                Eq(Converter.DetectSource("سلام", ls, 0), 1);
                Eq(Converter.DetectSource("123 ...", ls, 1), 1);
                Eq(Converter.DetectSource("123", ls, -1), 0);
            });
            add("nextLayout cycles through enabled layouts", () =>
            {
                Eq(Converter.NextLayout(0, new[] { 0, 1 }), 1);
                Eq(Converter.NextLayout(1, new[] { 0, 1 }), 0);
                Eq(Converter.NextLayout(2, new[] { 0, 2, 3 }), 3);
                Eq(Converter.NextLayout(1, new[] { 0, 2 }), 0);
                Eq(Converter.NextLayout(1, new int[0]), 1);
            });
            add("graphemeCount ignores ZWNJ and diacritics", () =>
            {
                Eq(Converter.GraphemeCount("abc"), 3);
                Eq(Converter.GraphemeCount("می‌خواهم"), 7);
                Eq(Converter.GraphemeCount("کِتاب"), 4);
                Eq(Converter.GraphemeCount("👍🏽x"), 2);
                Eq(Converter.GraphemeCount("👨‍👩‍👧"), 1);
                Eq(Converter.GraphemeCount("🇮🇷🇩🇪"), 2);
                Eq(Converter.GraphemeCount("a\nb"), 3);
                Eq(Converter.GraphemeCount("a\r\nb"), 3);
            });

            // ------------------------------------------------------------ engine
            add("converts the selection, switches layout and reselects", () =>
            {
                var d = Run(new FakeOptions { Text = "x sghl o,fd?", SelStart = 2, SelEnd = 12 });
                Eq(d.Text, "x سلام خوبی؟");
                Eq(d.Active, fx[1].Id);
                Eq(d.SelStart + "-" + d.SelEnd, "2-12");
                Eq(d.Notes.Count, 0);
            });
            add("pressing twice gives back the original", () =>
            {
                var o = new FakeOptions { Text = "hdk d; jsj hsj.", SelStart = 0, SelEnd = 15 };
                var texts = new List<string>();
                var d = Run(o, 2, 2, t => texts.Add(t.Text));
                Eq(string.Join("|", texts), "این یک تست است.|hdk d; jsj hsj.");
                Eq(d.Active, fx[0].Id);
            });
            add("three layouts cycle back to the original", () =>
            {
                var seen = new List<string>();
                var d = Run(new FakeOptions { Text = "sghl o,fd?", SelStart = 0, SelEnd = 10 }, 3, 3, t => seen.Add(t.Active));
                Eq(string.Join(",", seen), fx[1].Id + "," + fx[2].Id + "," + fx[0].Id);
                Eq(d.Text, "sghl o,fd?");
            });
            add("unknown characters are kept", () =>
                Eq(Run(new FakeOptions { Text = "sghl 😀!", SelStart = 0, SelEnd = 8 }).Text, "سلام 😀!"));
            add("nothing selected -> select all", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 4, SelEnd = 4 });
                Eq(d.Text, "سلام");
                Eq(string.Join(",", d.Copies), "Ctrl+C,Ctrl+C");
            });
            add("select all impossible -> nothing typed", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 4, SelEnd = 4, SelectAllWorks = false });
                Eq(d.Typed.Count, 0);
                Eq(d.Text, "sghl");
                Eq(d.Notes.Count, 1);
            });
            add("select all disabled -> nothing typed", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 4, SelEnd = 4 }, 2, 1, null, s => s.SelectAll = false);
                Eq(string.Join(",", d.Copies), "Ctrl+C");
                Eq(d.Typed.Count, 0);
            });
            add("the original clipboard is restored", () =>
            {
                Eq(Run(new FakeOptions { Text = "sghl", SelStart = 0, SelEnd = 4 }).Clip, "my clipboard");
                Eq(Run(new FakeOptions { Text = "sghl", SelStart = 4, SelEnd = 4, SelectAllWorks = false }).Clip, "my clipboard");
            });
            add("VS Code line copy without selection counts as nothing selected", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 4, SelEnd = 4, EmptySelectionCopiesLine = true }, 2, 1, null, s => s.SelectAll = false);
                Eq(d.Typed.Count, 0);
            });
            add("terminals use Ctrl+Shift+C and never select all", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 4, SelEnd = 4, Process = "WindowsTerminal.exe" });
                Eq(string.Join(",", d.Copies), "Ctrl+Shift+C");
                Eq(d.Typed.Count, 0);
                True(Engine.IsTerminal(new ForegroundInfo { ProcessName = "conhost.exe" }));
                True(!Engine.IsTerminal(new ForegroundInfo { ProcessName = "notepad.exe", ClassName = "Notepad" }));
            });
            add("elevated window -> notification, no input", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 0, SelEnd = 4, Elevated = true });
                Eq(d.Copies.Count, 0);
                True(d.Notes[0].Contains("administrator"));
            });
            add("options off: no layout switch, no reselect", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 0, SelEnd = 4 }, 2, 1, null, s => { s.SwitchLayout = false; s.Reselect = false; });
                Eq(d.Text, "سلام");
                Eq(d.Active, fx[0].Id);
                Eq(d.SelStart + "-" + d.SelEnd, "4-4");
            });
            add("disabled layouts are left out of the cycle", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 0, SelEnd = 4 }, 3, 1, null, s => s.DisabledLayouts.Add(fx[1].Id));
                Eq(d.Active, fx[2].Id);
            });
            add("modifiers still held -> nothing happens", () =>
            {
                var d = Run(new FakeOptions { Text = "sghl", SelStart = 0, SelEnd = 4, ModifiersReleased = false });
                Eq(d.Copies.Count, 0);
                Eq(d.Notes.Count, 1);
            });

            // ------------------------------------------------------------ run
            int failed = 0;
            foreach (var t in tests)
            {
                try
                {
                    t.Value();
                    Console.WriteLine("ok    " + t.Key);
                }
                catch (Exception e)
                {
                    failed++;
                    Console.WriteLine("FAIL  " + t.Key + "\n      " + e.Message);
                }
            }
            Console.WriteLine("{0} passed, {1} failed", tests.Count - failed, failed);
            return failed == 0 ? 0 : 1;
        }

        /// <summary>Press the shortcut `presses` times on a fake desktop with `layoutCount` layouts.</summary>
        static FakeDesktop Run(FakeOptions o, int layoutCount = 2, int presses = 1, Action<FakeDesktop> after = null, Action<Settings> configure = null)
        {
            List<Layout> layouts = Fixture.Load().Take(layoutCount).ToList();
            var d = new FakeDesktop(layouts, o);
            var settings = new Settings();
            if (configure != null) configure(settings);
            var engine = new Engine(d, d, d, settings, d.Notes.Add) { Layouts = layouts };
            for (int i = 0; i < presses; i++)
            {
                engine.Trigger().GetAwaiter().GetResult();
                if (after != null) after(d);
            }
            return d;
        }

        static void Eq<T>(T actual, T expected)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
            {
                throw new Exception("expected <" + expected + "> but got <" + actual + ">");
            }
        }

        static void True(bool condition)
        {
            if (!condition) throw new Exception("expected true");
        }
    }
}
