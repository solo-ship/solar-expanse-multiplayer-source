using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SolarExpanseMultiplayer
{
    // A test profile's dump of the content summary a game scanned (-semp-dump-summary), as indented JSON in the
    // profile's data folder. Two machines' files can be compared by hand, and the fake client's `join --summary-file`
    // reads one (Read), so its content check says "same". Never reaches a normal player: it needs a test profile.
    public static class ContentSummaryFile
    {
        public const string Name = "content-summary.json";
        // Indented JSON is about a third bigger than the wire form, so the file's own cap is twice the wire cap; the
        // summary in it must still pass the wire cap (Read checks it).
        public const int FileMaxBytes = 2 * JoinLimits.SummaryMaxBytes;

        // The whole decision for Session: null when the flag is off, else the log line (ok tells which kind).
        public static string Dump(DevFlags dev, string dir, ContentSummary summary, out bool ok)
        {
            ok = true;
            if (dev == null || !dev.DumpSummary) return null;
            string why = Write(dir, summary);
            ok = why == null;
            return ok ? "join: content summary written to " + Path.Combine(dir, Name)
                : "join: content summary not written: " + why;
        }

        // Null when written, else why not (never throws). A scan with no summary, a summary that fails its own checks
        // and any failure before the swap all delete an older dump: one from another build or mod set must not be fed
        // as the current one. A failed swap keeps the old file.
        public static string Write(string dir, ContentSummary summary)
        {
            if (string.IsNullOrEmpty(dir)) return "no data folder";
            string temp = null;
            bool swapping = false;
            try
            {
                if (summary == null) return Refuse(dir, "no summary was scanned");
                string json = JsonConvert.SerializeObject(summary, Formatting.Indented);
                // Validate cleans in place, and the scan keeps its own summary: check a copy
                string bad = JsonConvert.DeserializeObject<ContentSummary>(json).Validate();
                if (bad != null) return Refuse(dir, "the summary fails its own checks: " + bad);
                Directory.CreateDirectory(dir);
                // through a temp file, so a reader never sees half a file
                temp = Path.Combine(dir, Name + ".tmp");
                File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(json));
                string path = Path.Combine(dir, Name);
                // Replace keeps the old file when the swap fails; deleting first would lose it
                swapping = true;
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return null;
            }
            catch (Exception e)
            {
                try { if (temp != null && File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                // any failure before the swap leaves no older dump either; a failed swap keeps it
                string reason = e.GetType().Name + ": " + e.Message;
                return swapping ? reason : Refuse(dir, reason);
            }
        }

        static string Refuse(string dir, string why)
        {
            string path = Path.Combine(dir, Name);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { return why + " (and the older file could not be removed: " + e.GetType().Name + ")"; }
            return why;
        }

        // The fake client's read of a dump: null summary and the reason when it will not do.
        public static ContentSummary Read(string path, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > FileMaxBytes)
                {
                    error = "missing or over " + FileMaxBytes / 1024 + " KiB";
                    return null;
                }
                ContentSummary s = JsonConvert.DeserializeObject<ContentSummary>(File.ReadAllText(path));
                string bad = s == null ? "empty" : s.Validate();
                if (bad != null) { error = "does not pass the summary's own checks: " + bad; return null; }
                byte[] body;
                string why;
                // what it will send must fit the wire cap, as the game's own summary does
                if (!Envelope.TryEncode(s, 0, out body, out why)) { error = "over the wire cap: " + why; return null; }
                return s;
            }
            catch (Exception e)
            {
                error = "is not a content summary (" + e.GetType().Name + ")";
                return null;
            }
        }
    }
}
