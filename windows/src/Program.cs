// Entry point. Usage: KeyboardSwitch.exe [--hidden] [--convert] [--dump [--standard]]
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KeyboardSwitch
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--dump")) return Dump(args.Contains("--standard"));

            bool created;
            using (var mutex = new Mutex(true, @"Local\KeyboardSwitch.Instance", out created))
            {
                if (!created)
                {
                    // Already running: ask that instance to convert or to show its window.
                    string name = args.Contains("--convert") ? TrayApp.ConvertEventName : args.Contains("--hidden") ? null : TrayApp.ShowEventName;
                    EventWaitHandle ev;
                    if (name != null && EventWaitHandle.TryOpenExisting(name, out ev)) ev.Set();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(args.Contains("--hidden"), args.Contains("--convert")));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Print the diagnostic report (for --dump).</summary>
        static int Dump(bool standard)
        {
            Native.AttachConsole(-1);
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            output.Write(Diagnostics.Report(standard, null));
            return 0;
        }
    }
}
