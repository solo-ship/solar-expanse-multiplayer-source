using System;
using System.Globalization;
using System.IO;

namespace SolarExpanseMultiplayer
{
    // Writes a file through a temp file in the same folder, flushed to disk, then swapped in. A crash leaves the old file or the new
    // one, never half of one. Null = ok, else a fixed reason (never throws).
    public static class AtomicFile
    {
        public static string Write(string path, byte[] data)
        {
            if (string.IsNullOrEmpty(path) || data == null) return "nothing to write";
            string tmp = path + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(tmp)) File.Delete(tmp);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                return null;
            }
            catch (Exception e)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return "the file could not be written (" + e.GetType().Name + ")";
            }
        }

        // Moves a file we could not use aside as <name>.bad-<time> (never deleted). Returns the new path, or null if it failed.
        public static string MoveAside(string path)
        {
            try
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
                string target = path + ".bad-" + stamp;
                for (int n = 2; File.Exists(target); n++) target = path + ".bad-" + stamp + "-" + n;
                File.Move(path, target);
                return target;
            }
            catch (Exception) { return null; }
        }
    }
}
