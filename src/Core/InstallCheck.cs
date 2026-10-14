using System;

namespace SolarExpanseMultiplayer
{
    // Detects the main DLL being loaded from somewhere other than beside the BepInEx shim
    // (a leftover <game>\SolarExpanseMultiplayer\ folder, found by Mono's app-base probing).
    public static class InstallCheck
    {
        // null when both DLLs are in the same folder (or there is nothing to compare); else a message naming both folders.
        public static string Mismatch(string mainDllPath, string shimDllPath)
        {
            string a = Folder(mainDllPath), b = Folder(shimDllPath);
            if (a == null || b == null) return null;
            if (Key(a) == Key(b)) return null;
            return "two installs: the main DLL was loaded from " + a + " but the shim is in " + b + "; delete the stray copy";
        }

        static string Folder(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            if (i < 0) return null;
            string dir = path.Substring(0, i).TrimEnd('\\', '/');
            return dir.Length == 0 ? null : dir;
        }

        // Lexical comparison: 8.3 short names, symlinks and junctions to the same folder read as a mismatch (a warning, or a clear quit in test profiles).
        static string Key(string dir)
        {
            string s = dir.Replace('/', '\\');
            while (s.Contains("\\\\")) s = s.Replace("\\\\", "\\");
            return s.ToLowerInvariant();
        }
    }
}
