using System;
using System.IO;

namespace SolarExpanseMultiplayer
{
    public static class PathRules
    {
        // True when path lies strictly inside the folder root, after both are resolved (.. and . removed, / accepted for
        // \). Case does not matter (Windows). A sibling that only starts with the same letters ("data-evil" beside "data")
        // is not inside, the folder itself is not inside, and null, empty or unusable paths are not inside.
        // Used for -semp-join-file: it must name a file under the mod's own <mod>\data\ folder.
        public static bool IsInside(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            try
            {
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string p = Path.GetFullPath(path);
                return p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }          // invalid characters
            catch (NotSupportedException) { return false; }      // a colon in the wrong place, and the like
            catch (PathTooLongException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }
    }
}
