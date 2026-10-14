using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // Where drift files live and how big they may get. Everything is under <dataDir>\drift, where dataDir is
    // ModData.Dir(modDir, profile) (<mod>\data\test-<profile>), never the game's Saves. The caps are enforced by the
    // recorder (Task 8) and the writer: a cap that is reached is logged once.
    public static class DriftPaths
    {
        public const long MaxRecordBytes = 64L << 20, MaxFolderBytes = 1L << 30;
        public const long MaxDumpBytes = 16L << 20;     // the spike's text dumps were about 4.7 MB (truncated) and an estimated 5.9 MB full; binary is smaller
        public const int MaxDumps = 32, CheckpointEvery = 30, AiCheckEvery = 30;

        static readonly Regex Role = new Regex(@"^[A-Za-z0-9_-]{1,32}\z");

        // <dataDir>\drift; null when dataDir is null, empty or not a usable path.
        public static string Dir(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return null;
            try { return Path.Combine(dataDir, "drift"); }
            catch (ArgumentException) { return null; }
        }

        // drift-<role>-<yyyyMMdd-HHmmss>.semd; null when the role is not a plain name.
        public static string RecordFile(string dataDir, string role, DateTime startedLocal)
        {
            string dir = Dir(dataDir);
            if (dir == null || role == null || !Role.IsMatch(role)) return null;
            return Path.Combine(dir, "drift-" + role + "-" + Stamp(startedLocal) + ".semd");
        }

        // dump-<yyyyMMdd-HHmmss>-day<NNNN>.semp-dump; null for a negative day.
        public static string DumpFile(string dataDir, DateTime startedLocal, int day)
        {
            string dir = Dir(dataDir);
            if (dir == null || day < 0) return null;
            return Path.Combine(dir, "dump-" + Stamp(startedLocal) + "-day" + day.ToString("D4", CultureInfo.InvariantCulture) + ".semp-dump");
        }

        static string Stamp(DateTime t) { return t.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture); }

        // path resolves (.. removed) to somewhere strictly under Dir(dataDir); case does not matter.
        public static bool Inside(string dataDir, string path)
        {
            string dir = Dir(dataDir);
            return dir != null && PathRules.IsInside(dir, path);
        }

        // The total size of every file under Dir(dataDir), the spike's leftovers included. 0 when the folder is missing.
        // Fails closed: when the total cannot be worked out (no data folder, or a read error) it is long.MaxValue, so
        // "recording refuses to start above 1 GiB" refuses.
        public static long FolderBytes(string dataDir)
        {
            string dir = Dir(dataDir);
            if (dir == null) return long.MaxValue;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                long total = 0;
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) total += new FileInfo(file).Length;
                return total;
            }
            catch (Exception) { return long.MaxValue; }
        }
    }
}
