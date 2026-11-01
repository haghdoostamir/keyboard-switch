// Pure conversion logic. Note: built with the C# 5 compiler that ships with Windows.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KeyboardSwitch
{
    /// <summary>A keyboard layout and its character tables.</summary>
    public class Layout
    {
        public string Id;      // HKL as 16 hex digits
        public IntPtr Hkl;
        public string Klid;    // e.g. "00000429"
        public string Name;
        public readonly Dictionary<int, string> Fwd = new Dictionary<int, string>();  // key -> text
        public readonly Dictionary<string, int> Rev = new Dictionary<string, int>();  // char -> key

        /// <summary>Add a key; the first (simplest) key typing a character wins in Rev.</summary>
        public void Add(int key, string text)
        {
            Fwd[key] = text;
            if (Converter.IsSingleCodePoint(text) && !Rev.ContainsKey(text)) Rev[text] = key;
        }
    }

    public static class Converter
    {
        /// <summary>Modifier states, simplest first.</summary>
        public static readonly string[] States = { "", "Shift", "AltGr", "Shift+AltGr" };

        public static int KeyId(int vk, int state)
        {
            return vk * States.Length + state;
        }

        public static bool IsSingleCodePoint(string s)
        {
            return s.Length == 1 || (s.Length == 2 && char.IsSurrogatePair(s, 0));
        }

        /// <summary>The code points of `text`, as strings (surrogate pairs kept together).</summary>
        public static IEnumerable<string> CodePoints(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsSurrogatePair(text, i))
                {
                    yield return text.Substring(i, 2);
                    i++;
                }
                else
                {
                    yield return text.Substring(i, 1);
                }
            }
        }

        /// <summary>
        /// Re-interpret `text` as keys pressed in layout `src` and return what `dst` types with them.
        /// Characters with no equivalent are kept unchanged.
        /// </summary>
        public static string Convert(string text, Layout src, Layout dst)
        {
            var sb = new StringBuilder(text.Length);
            foreach (string ch in CodePoints(text))
            {
                int key;
                string outText;
                if (src.Rev.TryGetValue(ch, out key) && dst.Fwd.TryGetValue(key, out outText)) sb.Append(outText);
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>How many non-space characters of `text` the layout can type.</summary>
        public static int Coverage(string text, Layout layout)
        {
            int n = 0;
            foreach (string ch in CodePoints(text))
            {
                if (!char.IsWhiteSpace(ch, 0) && layout.Rev.ContainsKey(ch)) n++;
            }
            return n;
        }

        /// <summary>
        /// Index of the layout `text` was most likely typed with: the active one (`current`, -1 if
        /// none) unless another layout can type strictly more of it.
        /// </summary>
        public static int DetectSource(string text, IList<Layout> layouts, int current)
        {
            int best = 0, bestIndex = 0;
            var scores = new int[layouts.Count];
            for (int i = 0; i < layouts.Count; i++)
            {
                scores[i] = Coverage(text, layouts[i]);
                if (scores[i] > best)
                {
                    best = scores[i];
                    bestIndex = i;
                }
            }
            if (current >= 0 && current < layouts.Count && scores[current] == best) return current;
            return bestIndex;
        }

        /// <summary>The layout index after `src` in the cycle of enabled indices.</summary>
        public static int NextLayout(int src, IList<int> enabled)
        {
            if (enabled.Count == 0) return src;
            int i = enabled.IndexOf(src);
            return i < 0 ? enabled[0] : enabled[(i + 1) % enabled.Count];
        }

        /// <summary>
        /// Number of cursor positions (grapheme clusters) in `text`. Combining marks (Persian
        /// diacritics), ZWNJ/ZWJ, variation selectors and emoji modifiers don't start a new one.
        /// </summary>
        public static int GraphemeCount(string text)
        {
            int n = 0, prev = -1, regional = 0;
            bool emojiBase = false;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
                bool join;
                if (prev == '\r' && cp == '\n') join = true;
                else if (IsExtend(text, i, cp)) join = true;
                else if (prev == 0x200D && emojiBase) join = true;            // emoji ZWJ sequence
                else if (IsRegional(cp) && regional % 2 == 1) join = true;    // flag = 2 indicators
                else join = false;

                if (!join || n == 0)
                {
                    n++;
                    emojiBase = cp >= 0x2600;
                }
                regional = IsRegional(cp) ? regional + 1 : 0;
                prev = cp;
                if (cp > 0xFFFF) i++;
            }
            return n;
        }

        static bool IsRegional(int cp)
        {
            return cp >= 0x1F1E6 && cp <= 0x1F1FF;
        }

        static bool IsExtend(string text, int i, int cp)
        {
            if (cp == 0x200C || cp == 0x200D) return true;                 // ZWNJ, ZWJ
            if (cp >= 0xFE00 && cp <= 0xFE0F) return true;                 // variation selectors
            if (cp >= 0x1F3FB && cp <= 0x1F3FF) return true;               // skin tones
            if (cp >= 0xE0020 && cp <= 0xE007F) return true;               // tags
            UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(text, i);
            return cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark
                || cat == UnicodeCategory.EnclosingMark;
        }
    }
}
