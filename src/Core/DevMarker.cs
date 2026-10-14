using System;
using System.Collections.Generic;
using System.IO;

namespace SolarExpanseMultiplayer
{
    // The -semp-* flags (test profiles, dev flags, test seams) count only in a test copy: a marker file beside the mod
    // that only the developer's tools write (tools\deploy-test.ps1, tools\run-test-game.ps1, the device kit), never the
    // release installer. A command line alone is not enough: Steam can start a player's game with one a friend's
    // modified client chose (rich presence "connect"), and it must not open a test profile or a test seam.
    public static class DevMarker
    {
        public const string FileName = "semp-test-copy.marker";
        public const string FlagPrefix = "-semp-";

        public static bool Present(string modDir)
        {
            try { return !string.IsNullOrEmpty(modDir) && File.Exists(Path.Combine(modDir, FileName)); }
            catch (Exception) { return false; }
        }

        // The command line the mod reads. In a test copy it is unchanged; elsewhere every -semp- flag is dropped with
        // the value after it (the next argument, unless it is itself a flag: '-' or '+'), so the rest of the mod sees
        // a player's command line. dropped: how many -semp- flags were dropped.
        public static string[] Args(string[] args, string modDir, out int dropped)
        {
            dropped = 0;
            if (args == null) return new string[0];
            if (Present(modDir)) return args;
            var kept = new List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (i > 0 && a != null && a.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    dropped++;
                    if (i + 1 < args.Length && !IsFlag(args[i + 1])) i++;
                    continue;
                }
                kept.Add(a);
            }
            return kept.ToArray();
        }

        static bool IsFlag(string a)
        {
            return a != null && a.Length > 0 && (a[0] == '-' || a[0] == '+');
        }
    }
}
