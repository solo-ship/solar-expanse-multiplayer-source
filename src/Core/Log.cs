using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The mod's own log. Never throws: logging must not be able to break the game.
    public static class Log
    {
        const int RotateTries = 5;
        const int RotatePauseMs = 100;
        const int KeepOld = 3;

        static readonly object Gate = new object();
        public static string CurrentFile { get; private set; }

        // Starts a log run and keeps the previous log as <file>.prev. Logging must always end up somewhere: if the
        // old log is held open by another process (a tool polling it, a virus scanner) and cannot be renamed, the
        // run appends to the same file after a separator, so tools that read the fixed name still see it. Only if
        // that file cannot be written either does it use <name>.<pid>.log, and if nothing can be opened the reason
        // goes to init-error-<pid>.txt beside it. Any failure is named on the first line this run writes.
        public static void Init(string file)
        {
            lock (Gate)
            {
                CurrentFile = null;
                string dir = null, reason = null;
                bool rotateFailed = false;
                try
                {
                    dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    try { Rotate(file); }
                    catch (Exception e) { rotateFailed = true; reason = Describe(e); }
                }
                catch (Exception e) { reason = Describe(e); }

                string used = null;
                Exception openFailure = TryOpen(file);
                if (openFailure == null) used = file;
                else
                {
                    if (reason == null) reason = Describe(openFailure);
                    string alt = dir == null ? null
                        : Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "." + ProcessId() + ".log");
                    if (alt != null && TryOpen(alt) == null) used = alt;
                }

                if (used == null) { WriteInitError(dir, file, reason); return; }
                CurrentFile = used;
                if (reason == null) return;
                if (rotateFailed && used == file)
                    Append(used, "---- the previous log could not be moved aside; this run starts below ----"
                        + Environment.NewLine);
                Write("WARN", "log: could not " + (rotateFailed ? "rotate the previous log" : "open the log")
                    + " (" + reason + "); writing to " + used);
            }
        }

        // The main log is renamed first and .prev replaced only afterwards, so a failed rename loses nothing. The
        // temporary name carries the process id so two processes on one profile never share it.
        static void Rotate(string file)
        {
            RecoverLeftovers(file);
            if (!File.Exists(file)) return;
            string prev = file + ".prev", pending = file + ".rotating-" + ProcessId();
            for (int i = 1; ; i++)
            {
                try { File.Move(file, pending); break; }
                catch (Exception) { if (i >= RotateTries) throw; }
                Thread.Sleep(RotatePauseMs);
            }
            TryDelete(prev);
            try { File.Move(pending, prev); }
            catch (Exception)
            {
                // .prev is held open: put the log back so it is not stranded under the temporary name.
                try { File.Move(pending, file); } catch (Exception) { }
                throw;
            }
        }

        // A crash (or a failed put-back) can leave a log under its temporary name. It is kept under a unique
        // .old name, never deleted, and only the newest few .old files stay so the folder cannot fill up.
        static void RecoverLeftovers(string file)
        {
            try
            {
                string dir = Path.GetDirectoryName(file), name = Path.GetFileName(file);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                foreach (string left in Directory.GetFiles(dir, name + ".rotating*"))
                {
                    int pid;
                    string tail = left.Substring(left.LastIndexOf(".rotating", StringComparison.Ordinal) + 9);
                    if (tail.StartsWith("-") && int.TryParse(tail.Substring(1), out pid) && IsRunning(pid)) continue;
                    string target = file + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    string dest = target + ".old";
                    for (int n = 1; File.Exists(dest); n++) dest = target + "-" + n + ".old";
                    try { File.Move(left, dest); } catch (Exception) { }
                }
                string[] olds = Directory.GetFiles(dir, name + ".*.old");
                Array.Sort(olds, StringComparer.Ordinal);
                for (int i = 0; i < olds.Length - KeepOld; i++) TryDelete(olds[i]);
            }
            catch (Exception) { }
        }

        static bool IsRunning(int pid)
        {
            if (pid == ProcessId()) return false;
            try { System.Diagnostics.Process.GetProcessById(pid).Dispose(); return true; }
            catch (Exception) { return false; }
        }

        static Exception TryOpen(string file)
        {
            try
            {
                using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return null;
            }
            catch (Exception e) { return e; }
        }

        static void TryDelete(string file) { try { File.Delete(file); } catch (Exception) { } }

        static void Append(string file, string text) { try { File.AppendAllText(file, text); } catch (Exception) { } }

        static string Describe(Exception e)
        {
            return e.GetType().Name + ": " + (e.Message ?? "").Replace("\r", " ").Replace("\n", " ");
        }

        static int ProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; } catch (Exception) { return 0; }
        }

        static void WriteInitError(string dir, string file, string reason)
        {
            if (dir == null) return;
            Append(Path.Combine(dir, "init-error-" + ProcessId() + ".txt"),
                Stamp() + " log: no log could be opened for " + file + " (" + reason + ")" + Environment.NewLine);
        }

        static string Stamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        // A Steam id is 17 digits and names a person's account: it never reaches a log whole (SteamIdText.Short is the form
        // to log). Whatever slipped through is blanked here.
        static readonly Regex SteamIdRun = new Regex(@"(?<!\d)\d{17}(?!\d)", RegexOptions.CultureInvariant);
        public static string Redact(string message) { return message == null ? "" : SteamIdRun.Replace(message, "s:<hidden>"); }

        static void Write(string level, string message)
        {
            message = Redact(message);
            lock (Gate)
            {
                if (CurrentFile == null) return;
                try
                {
                    File.AppendAllText(CurrentFile, Stamp() + " [" + level + "] " + message + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
