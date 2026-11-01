// Clipboard access for the engine (must run on the STA UI thread).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KeyboardSwitch
{
    class WinClipboard : IClipboard
    {
        // Where Chromium-based apps put custom web types, such as VS Code's editor data.
        const string ChromiumCustom = "Chromium Web Custom MIME Data Format";
        const string VsCodeData = "vscode-editor-data";

        class Saved
        {
            public uint Seq;
            public readonly Dictionary<string, object> Data = new Dictionary<string, object>();
        }

        /// <summary>The clipboard can be briefly locked by the app that is writing to it.</summary>
        static T Retry<T>(Func<T> action)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    return action();
                }
                catch (ExternalException)
                {
                    if (i >= 5) throw;
                    Thread.Sleep(20);
                }
            }
        }

        public uint Sequence()
        {
            return Native.GetClipboardSequenceNumber();
        }

        public string ReadText()
        {
            return Retry(() => Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null);
        }

        /// <summary>VS Code copies the whole line when nothing is selected, and marks the copy.</summary>
        public bool EmptySelectionCopy()
        {
            try
            {
                IDataObject data = Retry(() => Clipboard.GetDataObject());
                if (data == null) return false;
                foreach (string format in new[] { VsCodeData, ChromiumCustom })
                {
                    if (!data.GetDataPresent(format, false)) continue;
                    object value = data.GetData(format, false);
                    string text = value as string;
                    var stream = value as MemoryStream;
                    if (text == null && stream != null) text = Encoding.Unicode.GetString(stream.ToArray()) + Encoding.UTF8.GetString(stream.ToArray());
                    if (text != null && text.Replace(" ", "").Contains("\"isFromEmptySelection\":true")) return true;
                }
            }
            catch (Exception)
            {
                // Unknown clipboard content: assume a real selection.
            }
            return false;
        }

        /// <summary>Every format that can be copied back (text, HTML, RTF, images, files, custom data).</summary>
        public object Save()
        {
            var saved = new Saved { Seq = Sequence() };
            try
            {
                IDataObject data = Retry(() => Clipboard.GetDataObject());
                if (data == null) return saved;
                foreach (string format in data.GetFormats(false))
                {
                    try
                    {
                        object value = data.GetData(format, false);
                        var stream = value as MemoryStream;
                        if (stream != null) saved.Data[format] = new MemoryStream(stream.ToArray());
                        else if (value is string || value is string[] || value is Image) saved.Data[format] = value;
                    }
                    catch (Exception)
                    {
                        // Skip formats that can't be read back.
                    }
                }
            }
            catch (Exception)
            {
                // Nothing restorable.
            }
            return saved;
        }

        public void Restore(object state)
        {
            var saved = (Saved)state;
            if (Sequence() == saved.Seq) return;   // untouched
            if (saved.Data.Count == 0)
            {
                Retry(() => { Clipboard.Clear(); return 0; });
                return;
            }
            var obj = new DataObject();
            foreach (var kv in saved.Data) obj.SetData(kv.Key, false, kv.Value);
            Clipboard.SetDataObject(obj, true, 5, 50);
        }
    }
}
