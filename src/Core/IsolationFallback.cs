using System.Text;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    public static class IsolationFallback
    {
        // Windows device names, with or without an extension ("nul.txt" is still the NUL device): a write there would vanish
        // (NUL) or could block (COMn). LaunchArgs has the same list for whole names; here the part before the first dot counts.
        static readonly Regex Device = new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z", RegexOptions.IgnoreCase);

        // Where a data path goes in a test profile when it can't be mapped properly: always inside the profile, never the real folder.
        public static string Unmatched(string profileRoot, string originalPath)
        {
            string last = "";
            if (originalPath != null)
                foreach (string part in originalPath.Split('/', '\\'))
                    if (part.Length > 0) last = part;
            var sb = new StringBuilder();
            foreach (char c in last)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                sb.Append(ok ? c : '_');
                if (sb.Length >= 64) break;
            }
            string name = sb.ToString();
            while (name.Contains("..")) name = name.Replace("..", "_");
            if (name.Length == 0 || name.Trim('.').Length == 0) name = "path";
            if (Device.IsMatch(name)) { name = "_" + name; if (name.Length > 64) name = name.Substring(0, 64); }   // _CON, _nul.txt: an ordinary file name
            return profileRoot.TrimEnd('\\', '/') + "\\_unmatched\\" + name;
        }
    }
}
