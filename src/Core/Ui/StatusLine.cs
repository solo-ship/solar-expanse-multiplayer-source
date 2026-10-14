using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The one status line a screen shows: a UiText key and its arguments. M4-M7 and the clock each report their own
    // state; Pick turns all of them into one, so a screen shows one line, never three.
    public sealed class StatusLine
    {
        public const int Info = 0, Notice = 1, Warning = 2, Blocked = 3;   // Severity, the higher wins

        // The keys M4 reports today. Later milestones add theirs here, so the precedence lives in one place.
        public const string Rejoining = "status.rejoining", OutOfStep = "status.outOfStep",
            WaitingForClock = "status.waitingClock", Joining = "status.joining", Dropped = "status.dropped",
            Planning = "status.planning";

        // A pure lookup, never changed after start-up: key -> severity. An unknown key is Info.
        static readonly Dictionary<string, int> Precedence = new Dictionary<string, int>
        {
            { OutOfStep, Blocked }, { Rejoining, Blocked },
            { WaitingForClock, Warning }, { Dropped, Warning },
            { Joining, Notice }, { Planning, Notice }
        };

        static readonly object[] NoArgs = new object[0];

        public string Key; public int Severity;
        object[] args = NoArgs;
        public object[] Args { get { return args ?? NoArgs; } set { args = value; } }   // never null

        public static int SeverityOf(string key)
        {
            int s;
            return key != null && Precedence.TryGetValue(key, out s) ? s : Info;
        }

        public static StatusLine Make(string key, params object[] args)
        {
            return new StatusLine { Key = key, Args = args, Severity = SeverityOf(key) };
        }

        // The highest severity wins. Among equal severities the EARLIER candidate wins, so the caller lists its
        // sources in priority order, most urgent first. Empty candidates and ones without a key are ignored; null when
        // nothing is left.
        public static StatusLine Pick(IList<StatusLine> candidates)
        {
            StatusLine best = null;
            if (candidates == null) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                StatusLine c = candidates[i];
                if (c == null || string.IsNullOrEmpty(c.Key)) continue;
                if (best == null || c.Severity > best.Severity) best = c;
            }
            return best;
        }
    }
}
