using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What a client may set from the host's copy of an object (DD10) and what it may not. A restore sets the values
    // the game lets change in place (counters, unit counts and progress of a build, units switched on, priorities,
    // selected deposits, the research state); a difference in what exists (an item missing, another item, another
    // order, a built count) cannot be set and puts the client out of step, which the full rejoin heals. Each method
    // answers null when the restore may go on, else why not.
    public static class RestoreRules
    {
        // The unfinished builds this game has against the host's rows: the same items, in the same order.
        public static string BuildsProblem(IList<BuildFact> mine, IList<BuildRow> host)
        {
            int nm = mine == null ? 0 : mine.Count, nh = host == null ? 0 : host.Count;
            if (nm != nh) return nm + " unfinished builds here, " + nh + " on the host";
            for (int i = 0; i < nh; i++)
            {
                BuildFact m = mine[i];
                BuildRow h = host[i];
                if (h == null) return "an empty build row";
                if (m.ItemId != h.ItemId || m.Item != h.Item || m.Craft != h.Craft)
                    return "build " + i + " is " + m.Item + " #" + m.ItemId + " here, " + h.Item + " #" + h.ItemId
                        + " on the host";
            }
            return null;
        }

        // The not-yet-paid queue: the same items in the same order (only the counts may be set).
        public static string QueueProblem(IList<QueueRow> mine, IList<QueueRow> host)
        {
            int nm = mine == null ? 0 : mine.Count, nh = host == null ? 0 : host.Count;
            if (nm != nh) return nm + " queued entries here, " + nh + " on the host";
            for (int i = 0; i < nh; i++)
            {
                if (mine[i] == null || host[i] == null) return "an empty queue row";
                if (mine[i].Item != host[i].Item)
                    return "queue entry " + i + " is " + mine[i].Item + " here, " + host[i].Item + " on the host";
            }
            return null;
        }

        // The built facilities (built only, in the game's order) against the host's: the same items with the same
        // unit counts. Units switched on and priorities may differ; a built count cannot be set.
        public static string FacilitiesProblem(IList<FacilityFact> mine, IList<FacilityRow> host)
        {
            int nh = host == null ? 0 : host.Count, nm = 0;
            for (int i = 0; mine != null && i < mine.Count; i++) if (mine[i].Built) nm++;
            if (nm != nh) return nm + " built facilities here, " + nh + " on the host";
            int j = 0;
            for (int i = 0; mine != null && i < mine.Count; i++)
            {
                FacilityFact m = mine[i];
                if (!m.Built) continue;
                FacilityRow h = host[j++];
                if (h == null) return "an empty facility row";
                if (m.ItemId != h.ItemId || m.Item != h.Item)
                    return "facility " + (j - 1) + " is " + m.Item + " #" + m.ItemId + " here, " + h.Item + " #"
                        + h.ItemId + " on the host";
                if (m.Quantity != h.Count)
                    return "facility " + h.Item + " #" + h.ItemId + " has " + m.Quantity + " units here, " + h.Count
                        + " on the host";
            }
            return null;
        }

        // The units done of a build: never more than it is building (Count 0 is an old save's single unit, of which
        // none counts as done until it is built).
        public static int CountBuild(int count, int countBuild)
        {
            if (countBuild < 0) return 0;
            return Math.Min(countBuild, Math.Max(count, 0));
        }

        // A unit's progress as a restore may set it: never complete (a completion is the host's own entry, DD11).
        public static float UnitProgress(double progress)
        {
            if (!(progress > 0)) return 0f;
            return progress >= CraftHold.HeldProgress ? CraftHold.HeldProgress : (float)progress;
        }

        // Units switched on: between none and the units built.
        public static int Enabled(int enabled, int count)
        {
            if (enabled < 0) return 0;
            return Math.Min(enabled, Math.Max(count, 0));
        }

        // Which deposit rows are selected for mining, from the host's keys (resource and position,
        // ObjectRows.MiningKey) over this game's rows (their resource ids, in order). A key that names no row here:
        // problem, and a null result.
        public static bool[] Mining(IList<string> resources, IList<string> keys, out string problem)
        {
            problem = null;
            int n = resources == null ? 0 : resources.Count;
            var selected = new bool[n];
            for (int k = 0; keys != null && k < keys.Count; k++)
            {
                int at = -1;
                for (int i = 0; i < n && at < 0; i++)
                    if (ObjectRows.MiningKey(resources[i], i) == keys[k]) at = i;
                if (at < 0)
                {
                    problem = "the host mines " + keys[k] + ", which is no deposit here";
                    return null;
                }
                selected[at] = true;
            }
            return selected;
        }

        // The crew-death timer the host's state gives one site, or -1 when it lists none for it (the site's timer is
        // then not running).
        public static long LifeSupportTicks(IList<LifeSupportRow> rows, string agency, int body)
        {
            for (int i = 0; rows != null && i < rows.Count; i++)
            {
                LifeSupportRow r = rows[i];
                if (r != null && r.Agency == agency && r.Body == body) return r.Ticks;
            }
            return -1;
        }

        // A build's unit progress list as the host's copy has it: the units done finished, the one in hand at the
        // host's progress, and no later unit finished (a completion is the host's own entry). count 0 is an old save's
        // single unit, whose progress the item keeps itself (the caller sets it).
        public static void Units(List<float> units, int count, int done, float progress)
        {
            if (units == null || count <= 0) return;
            while (units.Count < count) units.Add(0f);
            for (int i = 0; i < done && i < units.Count; i++) units[i] = 1f;
            if (done < count) units[done] = progress;
            for (int i = done + 1; i < units.Count; i++)
                if (units[i] > CraftHold.HeldProgress) units[i] = CraftHold.HeldProgress;
        }

        // Whether this game's research can take the host's copy. A restore sets the running, queued and paused topics;
        // it cannot finish a topic or take one back. So it is out of step when the host runs or pauses a topic this
        // game has finished, and when it would throw away a topic this game has started (running or paused here) that
        // the host's copy neither runs nor pauses: the host has most likely finished it, which no restore can give.
        public static string ResearchRestoreProblem(ResearchState host, IList<string> startedHere,
            Func<string, bool> finishedHere)
        {
            string bad = ResearchProblem(host);
            if (bad != null) return bad;
            if (host.Running != null && finishedHere(host.Running))
                return host.Running + " is finished here and running on the host";
            for (int i = 0; host.Paused != null && i < host.Paused.Length; i++)
                if (finishedHere(host.Paused[i])) return host.Paused[i] + " is finished here and started on the host";
            for (int i = 0; startedHere != null && i < startedHere.Count; i++)
            {
                string t = startedHere[i];
                if (t == host.Running) continue;
                if (host.Paused != null && Array.IndexOf(host.Paused, t) >= 0) continue;
                return t + " is started here and neither running nor paused on the host";
            }
            return null;
        }

        // The host's research state, as a restore applies it: each topic id once (running, queued and paused may
        // share one: the running topic heads the queue), progress real and not past a topic's end.
        public static string ResearchProblem(ResearchState r)
        {
            if (r == null) return "no research state";
            int np = r.Paused == null ? 0 : r.Paused.Length;
            int nq = r.PausedProgress == null ? 0 : r.PausedProgress.Length;
            if (np != nq) return "paused topics and their progress differ in length";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < np; i++)
            {
                if (string.IsNullOrEmpty(r.Paused[i])) return "an empty paused topic";
                if (!seen.Add(r.Paused[i])) return "the paused topic " + r.Paused[i] + " twice";
                if (r.Running != null && r.Paused[i] == r.Running) return "the running topic is also paused";
            }
            seen.Clear();
            for (int i = 0; r.Queue != null && i < r.Queue.Length; i++)
            {
                if (string.IsNullOrEmpty(r.Queue[i])) return "an empty queued topic";
                if (!seen.Add(r.Queue[i])) return "the queued topic " + r.Queue[i] + " twice";
            }
            return null;
        }
    }

    // The counters an entry or a state carries (DD3): a client sets each to the host's value before the apply and
    // finds it at the host's "after" value when the apply is done; ship and item ids follow from them, so equal
    // counters give equal ids. Pure: the game side reads and writes the counters.
    public static class CounterRules
    {
        // One counter at most once per entry: two rows for one counter would set it twice, and the second would win
        // on one game and not the other.
        public static string Problem(IList<CounterRow> rows)
        {
            for (int i = 0; rows != null && i < rows.Count; i++)
            {
                CounterRow r = rows[i];
                if (r == null) return "an empty counter row";
                for (int j = 0; j < i; j++)
                    if (Same(rows[j], r)) return "the counter " + Name(r) + " twice";
            }
            return null;
        }

        static bool Same(CounterRow a, CounterRow b)
        {
            return a != null && a.Which == b.Which && a.Agency == b.Agency && a.Key == b.Key;
        }

        // What the apply left in a counter, against the host's: null when equal.
        public static string AfterProblem(CounterRow r, long now)
        {
            if (r == null) return "an empty counter row";
            return now == r.After ? null : Name(r) + " is " + now + " after the apply, " + r.After + " on the host";
        }

        // The other agencies whose item counter AgencyState carries: every one that is not human, in id order, at most
        // MaxRivalCounters (beyond them a rival's counter is set by its next finished build's entry). Each once.
        public static List<string> Rivals(IEnumerable<string> all, IList<string> humans, out int left)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string a in all ?? new string[0])
                if (!string.IsNullOrEmpty(a) && (humans == null || !humans.Contains(a))) set.Add(a);
            var rivals = new List<string>();
            foreach (string a in set) if (rivals.Count < ActionLimits.MaxRivalCounters) rivals.Add(a);
            left = set.Count - rivals.Count;
            return rivals;
        }

        public static string Name(CounterRow r)
        {
            switch (r.Which)
            {
                case CounterKind.ProductionCountId: return "the item counter of " + r.Agency;
                case CounterKind.NameCounter: return "the name counter " + r.Key + " of " + r.Agency;
                case CounterKind.SpacecraftCount: return "the ship counter";
                case CounterKind.LaunchVehicleCount: return "the launch-vehicle counter";
                case CounterKind.OfferCount: return "the offer counter";
                case CounterKind.MissionId: return "the mission counter";
                default: return "counter " + r.Which;
            }
        }
    }
}
