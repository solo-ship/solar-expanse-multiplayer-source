using System;
using System.IO;

namespace SolarExpanseMultiplayer
{
    // Test sessions only: tests act on a running game by dropping a small file into <data>\dev\ (the game picks it up
    // within a second and deletes it), so nobody has to click inside the game. Files: "disconnect" (client drops its
    // link, then reconnects), "speed" (contains a speed; set as if the player had clicked it), "planner" (open or close:
    // the game's own mission-planner methods, so the pause-with-a-reason path runs), "clock" (the host only: "stop" makes
    // it send no Clock messages, as at its main menu; "go" sends them again).
    // M5 (CommandSession reads them): "act <n>" (line n of the -semp-actions script as this player's click),
    // "click <n>" (the same through the window's own handler; refused until a capture is registered), "dayhold on|off"
    // (the host holds DayClosed back, then sends it), "fault" (the host arms its -semp-command-fault again) and
    // "reload [save name]" (the host loads that save, or its newest, mid-session: the host-load path).
    public static class DevTriggers
    {
        // True about once a second per caller (each keeps its own next): files are checked only then.
        public static bool Every(ref float next, float now)
        {
            if (now < next) return false;
            next = now + 1f;
            return true;
        }

        static readonly System.Collections.Generic.HashSet<string> deleteWarned = new System.Collections.Generic.HashSet<string>();

        // The file's text (BOM and surrounding whitespace removed by DevTriggerText, at most 64 bytes) if it exists, after deleting it; otherwise null. The file is deleted
        // before the trigger acts: if that fails the trigger is skipped (one warning per name), never retried every second.
        // A file over 64 bytes is dropped unread, with one warning.
        public static string Take(string name)
        {
            if (Boot.Profile == null || Boot.DataDir == null) return null;
            if (!TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath)) return null;   // never on the player-path profile
            try
            {
                string file = Path.Combine(Path.Combine(Boot.DataDir, "dev"), name);
                var info = new FileInfo(file);
                if (!info.Exists) return null;
                bool tooBig = info.Length > 64;
                string text = "";
                if (!tooBig)
                {
                    var buf = new byte[64];
                    int n = 0, got;
                    using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        while (n < buf.Length && (got = s.Read(buf, n, buf.Length - n)) > 0) n += got;
                    text = DevTriggerText.Clean(buf, n);
                }
                try { File.Delete(file); }
                catch (Exception e)
                {
                    if (deleteWarned.Add(name)) Log.Warn("dev trigger " + name + " could not be deleted, skipped: " + e.Message);
                    return null;
                }
                deleteWarned.Remove(name);   // a delete that works again may warn again if it later fails
                if (tooBig) { Log.Warn("dev trigger " + name + " dropped: the file is over 64 bytes"); return null; }
                Log.Info("dev trigger: " + name + (text.Length > 0 ? " " + text : ""));
                return text;
            }
            catch (Exception e) { Log.Warn("dev trigger " + name + " unreadable: " + e.Message); return null; }
        }
    }
}
