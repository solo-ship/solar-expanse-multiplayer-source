using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SolarExpanseMultiplayer
{
    // Where a build unit's progress starts counting. The game adds a frame's progress from the site's last render
    // frame (ObjectInfoData.MyUpdate), so a unit that an entry, a rival's action or the day event starts between two
    // frames is credited from the frame before: each game from its own frames, which part the two games' build
    // progress by up to a frame for the unit's whole life. In a session a unit's first credit counts from the time of
    // what started it instead: the host's tick of the entry (a client applying it), this game's clock (the host
    // acting, the day event at its boundary). Every game then credits it from the same time. A stamp is the latest
    // such time for an agency (or every agency), noted when it happened; it counts for a unit that has no progress yet
    // and gets its first credit in the first frame after the stamp. A stamp still ahead of the clock (an entry the
    // host applied later than this game did) holds the unit at no progress until the clock passes it.
    public sealed class BuildStarts
    {
        public const int MaxWaiting = 4096;

        struct Stamp { public long Start, SetAt; public bool Set; }

        struct Unit
        {
            public object Item;
            public int Index;
        }

        sealed class SameUnit : IEqualityComparer<Unit>
        {
            public bool Equals(Unit a, Unit b) { return ReferenceEquals(a.Item, b.Item) && a.Index == b.Index; }
            public int GetHashCode(Unit u) { return RuntimeHelpers.GetHashCode(u.Item) * 31 + u.Index; }
        }

        readonly Dictionary<string, Stamp> stamps = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        readonly Dictionary<Unit, long> waiting = new Dictionary<Unit, long>(new SameUnit());
        Stamp every;

        public int Waiting { get { return waiting.Count; } }

        // Fresh units since the load: credited from a stamp, or from the site's last frame because no stamp covered
        // them (a start each game's own frames decide); LastUnstamped: the last Credit was such a unit.
        public long Stamped { get; private set; }
        public long Unstamped { get; private set; }
        public bool LastUnstamped { get; private set; }

        // Something that can start an agency's builds happened at `start` (agency null: every agency), noted at this
        // game's clock `setAt`. A start that is no time (unset ticks, long.MinValue outside an apply, before the
        // game's start `earliest`) is no stamp: a unit credited from it would get its whole life's progress at once.
        public void Note(string agency, long start, long setAt, long earliest = 0)
        {
            if (start <= 0 || start < earliest || setAt <= 0) return;
            var s = new Stamp { Start = start, SetAt = setAt, Set = true };
            if (agency == null) every = s;
            else stamps[agency] = s;
        }

        public void Reset()
        {
            stamps.Clear();
            waiting.Clear();
            every = new Stamp();
            Stamped = Unstamped = 0;
            LastUnstamped = false;
        }

        // The days of progress to credit unit `index` of `item` (an agency's) in this update instead of the game's
        // daysTimeDelta: fresh = the unit has no progress yet; now and lastUpdate (the site's last frame) are ticks.
        public double Credit(object item, int index, string agency, bool fresh, long now, long lastUpdate,
            double daysTimeDelta)
        {
            LastUnstamped = false;
            if (item == null) return daysTimeDelta;
            var key = new Unit { Item = item, Index = index };
            long start;
            if (waiting.TryGetValue(key, out start))
            {
                if (now <= start) return 0;
                waiting.Remove(key);
                return Days(now - start);
            }
            if (!fresh) return daysTimeDelta;
            // a site with no frame yet: every stamp ever noted would cover it
            if (lastUpdate <= 0) return daysTimeDelta;
            if (!Latest(agency, lastUpdate, out start))
            {
                Unstamped++;
                LastUnstamped = true;
                return daysTimeDelta;
            }
            Stamped++;
            if (now <= start)
            {
                if (waiting.Count >= MaxWaiting) waiting.Clear();
                waiting[key] = start;
                return 0;
            }
            return Days(now - start);
        }

        // The latest stamp for the agency noted at or after the site's last frame.
        bool Latest(string agency, long lastUpdate, out long start)
        {
            start = 0;
            Stamp own;
            if (agency == null || !stamps.TryGetValue(agency, out own)) own = new Stamp();
            bool has = own.Set && own.SetAt >= lastUpdate;
            bool all = every.Set && every.SetAt >= lastUpdate;
            if (!has && !all) return false;
            start = has && (!all || own.SetAt >= every.SetAt) ? own.Start : every.Start;
            return true;
        }

        static double Days(long ticks) { return ticks / (double)TimeSpan.TicksPerDay; }
    }
}
