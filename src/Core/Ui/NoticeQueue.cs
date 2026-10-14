using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public sealed class NoticeItem
    {
        public int Id { get; internal set; }
        public string Key { get; internal set; }
        public object[] Args { get; internal set; }
        public int Kind { get; internal set; }       // NoticeQueue.Routine, Warning or Command
        public int Count { get; internal set; }      // how many pushes were merged into this notice
        public long FirstMs { get; internal set; }
        public long LastMs { get; internal set; }
    }

    // The mod's own notice stack (DD11), never the game's NotificationManager.
    public sealed class NoticeQueue
    {
        public const int Routine = 0, Warning = 1, Command = 2;   // Command: the host's console commands (DD19)
        public const long FadeMs = 8000;        // routine and command notices fade this long after the last push
        public const int MaxHistory = 50;
        public const int MaxVisible = 10;
        public const int MaxLiveWarnings = 100;   // live warnings kept; past this the oldest go
        public const int MaxNewPerWindow = 10;  // new routine or command notices accepted per window; more are dropped
        public const long WindowMs = 1000;

        readonly List<NoticeItem> visible = new List<NoticeItem>();
        readonly List<NoticeItem> history = new List<NoticeItem>();
        readonly Queue<long> recentNew = new Queue<long>();
        int nextId = 1;

        // A push of the same key and arguments as a notice still on show merges into it (count up, fade restarted).
        // Otherwise a new notice: a routine or command one that arrives when too many came in the last second goes to
        // the log only (never on screen); a warning never does (a refusal must not vanish in a flood). A bad kind
        // counts as a warning, so it is not lost.
        public void Push(string key, object[] args, int kind, long nowMs)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (kind != Routine && kind != Command) kind = Warning;
            object[] a = args == null ? new object[0] : (object[])args.Clone();
            ExpireRoutine(nowMs);
            foreach (NoticeItem v in visible)
            {
                if (v.Key == key && v.Kind == kind && SameArgs(v.Args, a))
                {
                    v.Count++; v.LastMs = nowMs;
                    return;
                }
            }
            bool show = true;
            if (kind != Warning)
            {
                while (recentNew.Count > 0 && nowMs - recentNew.Peek() >= WindowMs) recentNew.Dequeue();
                if (recentNew.Count >= MaxNewPerWindow) show = false;
                else recentNew.Enqueue(nowMs);
            }
            var item = new NoticeItem
            {
                Id = nextId++, Key = key, Args = a, Kind = kind, Count = 1, FirstMs = nowMs, LastMs = nowMs
            };
            history.Add(item);
            if (history.Count > MaxHistory) history.RemoveAt(0);
            if (!show) return;
            visible.Add(item);
            // Over the cap, the oldest routine or command notice goes first. A warning is never pushed off by the
            // cap: when only warnings are left they all stay live and Visible shows the newest ones.
            while (visible.Count > MaxVisible)
            {
                int i = OldestNonWarning();
                if (i < 0) break;
                visible.RemoveAt(i);
            }
            while (visible.Count > MaxLiveWarnings) visible.RemoveAt(0);
        }

        // The notices on show, oldest first, at most MaxVisible. Routine ones are gone after FadeMs; warnings stay
        // until dismissed. When more than MaxVisible warnings are live, the newest MaxVisible are returned and
        // HiddenWarnings counts the rest; dismissing one brings the next back.
        public List<NoticeItem> Visible(long nowMs)
        {
            ExpireRoutine(nowMs);
            int skip = Math.Max(0, visible.Count - MaxVisible);
            return visible.GetRange(skip, visible.Count - skip);
        }

        // Live warnings the screen has no room for ("+N more").
        public int HiddenWarnings { get { return Math.Max(0, visible.Count - MaxVisible); } }

        public void Dismiss(int id)
        {
            for (int i = 0; i < visible.Count; i++)
                if (visible[i].Id == id) { visible.RemoveAt(i); return; }
        }

        // The last MaxHistory notices, oldest first, shown or not (the PLAYERS screen's log).
        public List<NoticeItem> History() { return new List<NoticeItem>(history); }

        void ExpireRoutine(long nowMs)
        {
            visible.RemoveAll(v => v.Kind != Warning && nowMs - v.LastMs >= FadeMs);
        }

        int OldestNonWarning()
        {
            for (int i = 0; i < visible.Count; i++) if (visible[i].Kind != Warning) return i;
            return -1;
        }

        static bool SameArgs(object[] x, object[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (!Equals(x[i], y[i])) return false;
            return true;
        }
    }
}
