using System;

namespace SolarExpanseMultiplayer
{
    // I-13: a character cap is not a byte cap. A line of 160 CJK characters is 480 bytes of UTF-8 and an emoji is 4, so text
    // that passes "160 characters" can still push a message past its payload cap. Everything peer-derived that goes into a
    // message is cut to a byte budget first, at a character boundary (never inside a surrogate pair), so no send can throw.
    // Lists of lines (FitLines) are budgeted at their JSON size, where a quote or a backslash costs two bytes and a control
    // character six, so a report of quotes still encodes. Envelope.TryEncode and Link.Send stay as the backstop: they say no
    // instead of throwing, and Send returns false, so every caller must look at what Send returns.
    public static class TextBudget
    {
        const string MorePrefix = "... and ", MoreSuffix = " more";

        // The bytes UTF-8 uses for s: 1 to 3 per UTF-16 unit, 4 for a surrogate pair, 3 for a lone surrogate (the encoder's
        // replacement character). Agrees with Encoding.UTF8.GetByteCount; allocates nothing.
        public static int Utf8Bytes(string s)
        {
            if (s == null) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80) n += 1;
                else if (c < 0x800) n += 2;
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { n += 4; i++; }
                else n += 3;
            }
            return n;
        }

        // The longest prefix of s whose UTF-8 form is at most maxBytes, ending on a character boundary. null stays null.
        public static string FitBytes(string s, int maxBytes)
        {
            if (s == null) return null;
            if (maxBytes <= 0) return "";
            int bytes = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int size, units = 1;
                if (c < 0x80) size = 1;
                else if (c < 0x800) size = 2;
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { size = 4; units = 2; }
                else size = 3;
                if (bytes + size > maxBytes) return s.Substring(0, i);
                bytes += size;
                i += units - 1;
            }
            return s;
        }

        // The bytes s takes inside a JSON string: UTF-8, except that a quote and a backslash are two bytes and a control
        // character is six (as \u00XX; the short forms like \n are shorter, so this never undercounts).
        public static int JsonBytes(string s)
        {
            if (s == null) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') n += 2;
                else if (c < 0x20 || c == 0x85 || c == 0x2028 || c == 0x2029) n += 6;   // Newtonsoft writes U+0085, U+2028 and U+2029 as \uXXXX too
                else if (c < 0x80) n += 1;
                else if (c < 0x800) n += 2;
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { n += 4; i++; }
                else n += 3;
            }
            return n;
        }

        // At most maxChars characters of s, never splitting a surrogate pair.
        static string CutChars(string s, int maxChars)
        {
            if (s.Length <= maxChars) return s;
            int cut = maxChars < 0 ? 0 : maxChars;
            if (cut > 0 && char.IsHighSurrogate(s[cut - 1]) && char.IsLowSurrogate(s[cut])) cut--;
            return s.Substring(0, cut);
        }

        static string More(int left) { return MorePrefix + left + MoreSuffix; }

        // Cuts a list of lines to a line count, a character count per line and a byte total counted as JSON (JsonBytes). When some lines are left
        // out, the last line says "... and N more" and counts toward both the line count and the byte total. A null line
        // becomes an empty one. Returns an empty array when nothing fits (not even the note).
        public static string[] FitLines(string[] lines, int maxLines, int maxCharsPerLine, int maxBytesTotal)
        {
            if (lines == null || lines.Length == 0 || maxLines <= 0 || maxBytesTotal <= 0) return new string[0];
            int n = lines.Length;
            int look = n < maxLines ? n : maxLines;   // no more than this many lines can ever be shown
            var cut = new string[look];
            var prefix = new long[look + 1];          // prefix[k] = the bytes of the first k lines
            for (int i = 0; i < look; i++)
            {
                cut[i] = CutChars(lines[i] ?? "", maxCharsPerLine);
                prefix[i + 1] = prefix[i] + JsonBytes(cut[i]);
            }
            for (int k = look; k >= 0; k--)
            {
                bool all = k == n;
                string note = all ? null : More(n - k);
                int count = k + (all ? 0 : 1);
                long total = prefix[k] + (all ? 0 : JsonBytes(note));
                if (count > maxLines || total > maxBytesTotal) continue;
                var result = new string[count];
                for (int i = 0; i < k; i++) result[i] = cut[i];
                if (!all) result[k] = note;
                return result;
            }
            return new string[0];
        }
    }
}
