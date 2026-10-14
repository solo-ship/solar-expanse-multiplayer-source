using System;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The names of Multiplayer saves (DD24, "Names"). Every name starts with "MP ", never with "autosave", and holds only letters,
    // digits, space, '-', '_', '(' and ')': no separator, no dot, no leading or trailing space, never a device name.
    public static class SaveNames
    {
        public const string Prefix = "MP ";
        public const int MaxTitleChars = 48, MaxHostChars = 20;

        public static bool IsSaveId(string s)
        {
            if (s == null || s.Length != 16) return false;
            for (int i = 0; i < 16; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        static bool Allowed(char c)
        {
            if (char.IsSurrogate(c)) return false;
            return char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')';
        }

        static string Filter(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            bool space = true;   // leading spaces dropped, runs collapsed
            for (int i = 0; i < s.Length && i < 4096 && sb.Length < max; i++)
            {
                char c = s[i];
                if (!Allowed(c)) continue;
                if (c == ' ') { if (space) continue; space = true; } else space = false;
                sb.Append(c);
            }
            return sb.ToString().Trim(' ', '.');
        }

        // "MP <title>". hostTitle is the host's save title (ignored for an autosave); an empty or reserved title becomes the date.
        // An autosave is "MP <host> autosave <yyyy-MM-dd HH-mm>". Never throws.
        public static string Clean(string hostTitle, string hostName, DateTime date, bool wasAutosave)
        {
            string stamp = date.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
            string title;
            if (wasAutosave)
            {
                string host = Filter(hostName, MaxHostChars);
                title = (host.Length > 0 ? host + " " : "") + "autosave " + stamp;
            }
            else
            {
                title = Filter(hostTitle, MaxTitleChars);
                if (title.Length == 0 || IsReservedDeviceName(title)) title = stamp;
            }
            if (title.Length > MaxTitleChars) title = title.Substring(0, MaxTitleChars).TrimEnd(' ');
            string name = Prefix + title;
            return IsLegal(name) ? name : Prefix + stamp;
        }

        // The checks a stored or incoming name must pass again (the index's Read uses it): "MP " + a clean title, a number suffix allowed.
        public static bool IsLegal(string name)
        {
            if (name == null || name.Length <= Prefix.Length || name.Length > Prefix.Length + MaxTitleChars + 6) return false;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (name.StartsWith("autosave", StringComparison.OrdinalIgnoreCase)) return false;
            if (name[name.Length - 1] == ' ' || name[name.Length - 1] == '.' || name[Prefix.Length] == ' ') return false;
            for (int i = 0; i < name.Length; i++) if (!Allowed(name[i])) return false;
            return !IsReservedDeviceName(name.Substring(Prefix.Length));
        }

        public static bool IsReservedDeviceName(string name)
        {
            if (name == null) return false;
            int dot = name.IndexOf('.');
            string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ').ToUpperInvariant();
            switch (stem)
            {
                case "CON": case "PRN": case "AUX": case "NUL": return true;
            }
            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
                return stem[3] >= '1' && stem[3] <= '9';
            return false;
        }

        // "MP <title> 2" for n >= 2; n <= 1 is the name itself.
        public static string Numbered(string name, int n)
        {
            return n <= 1 ? name : name + " " + n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
