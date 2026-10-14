using System.Text;

namespace SolarExpanseMultiplayer
{
    // The text of a dev trigger file. Agents write these files by hand (Set-Content adds a BOM or a newline, cmd's echo a
    // space and CR LF), so the bytes are cleaned here: a leading byte order mark is dropped (UTF-8, or UTF-16 as
    // PowerShell's -Encoding Unicode writes it) and surrounding whitespace, newlines and stray NULs are trimmed.
    public static class DevTriggerText
    {
        static readonly char[] Trimmed = { '\0', ' ', '\t', '\r', '\n', '\v', '\f', ' ' };

        public static string Clean(byte[] buf, int count)
        {
            if (buf == null || count <= 0) return "";
            if (count > buf.Length) count = buf.Length;
            Encoding enc = Encoding.UTF8;
            int skip = 0;
            if (count >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) { enc = Encoding.Unicode; skip = 2; }
            else if (count >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) { enc = Encoding.BigEndianUnicode; skip = 2; }
            else if (count >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) skip = 3;
            return enc.GetString(buf, skip, count - skip).Trim(Trimmed);
        }

        // A trigger's id argument: plain digits only (no sign, no spaces, no thousands marks), at most 18 of them.
        public static bool TryLong(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 18) return false;
            foreach (char ch in text) if (ch < '0' || ch > '9') return false;
            value = long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
    }
}
