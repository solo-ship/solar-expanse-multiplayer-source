using System;

namespace SolarExpanseMultiplayer
{
    public static class PathRedirect
    {
        // Moves a path under fromBase to the same place under toBase. Null means "not ours, leave it alone".
        public static string Rewrite(string path, string fromBase, string toBase)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(fromBase) || string.IsNullOrEmpty(toBase)) return null;
            string p = Norm(path), b = Norm(fromBase).TrimEnd('\\');
            if (!p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return null;
            if (p.Length > b.Length && p[b.Length] != '\\') return null;   // "Solar Expanse Demo" is not under "Solar Expanse"
            string rest = p.Substring(b.Length).Trim('\\');
            foreach (string part in rest.Split('\\'))
                if (part == "..") return null;
            string to = Norm(toBase).TrimEnd('\\');
            return rest.Length == 0 ? to : to + "\\" + rest;
        }

        static string Norm(string s) { return s.Replace('/', '\\'); }
    }
}
