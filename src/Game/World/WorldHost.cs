using System;
using System.Collections.Generic;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // The host's game as the world part of the command driver sees it (IWorldWorld, IWorldHostSide; M7 DD-W9, DD-W10):
    // the world print read at the day-event point through M3's state reader (the hasher into a DaySnapshot), the items of
    // every area as text rows for group prints and heals (GroupSink, one more visit of the same reader), the agencies'
    // wire names, a fingerprint for a resume's release and the due test of a held contract note. One instance owned by
    // the command session; OnHostLoaded is its reset per load. Main thread.
    public sealed class WorldHost : IWorldWorld, IWorldHostSide
    {
        readonly HostTime time;
        int preparesAtLoad = -1, agencyCount = -1;
        long hasherOrigin;
        AreaHasher hasher;
        DaySnapshot snapshot;
        GroupSink items;          // the rows of the visit just made, until the next ReadWorld
        string[] names = new string[0];

        public WorldHost(HostTime time)
        {
            if (time == null) throw new ArgumentNullException("time");
            this.time = time;
        }

        public void OnHostLoaded()
        {
            preparesAtLoad = StateReader.Prepares;
            hasher = null;
            items = null;
        }

        // The reader prepared for this load (once, unless Drift did), and the hasher and snapshot kept for the origin
        // and the agency count they were made for.
        bool Ready(out long origin)
        {
            origin = time.OriginTicks;
            if (origin < 1) return false;
            if (PrintCheck.NeedsPrepare(preparesAtLoad, StateReader.Prepares, StateReader.IsPrepared))
            {
                StateReader.Prepare();
                if (!StateReader.IsPrepared) return false;
                hasher = null;
            }
            int n = StateReader.Agencies.Length;
            if (hasher == null || hasherOrigin != origin || agencyCount != n)
            {
                hasher = new AreaHasher(origin, null, n);
                snapshot = new DaySnapshot(DriftAreas.Blocks(n), DriftAreas.FieldSums(n), DriftFields.MaxScalars(n));
                hasherOrigin = origin;
                agencyCount = n;
            }
            names = new string[n];
            for (int i = 0; i < n; i++) names[i] = AgencyWire.From(StateReader.Agencies[i]);
            return true;
        }

        // ---- IWorldWorld ----

        public DaySnapshot ReadWorld()
        {
            long origin;
            if (!Ready(out origin)) throw new InvalidOperationException("the state reader is not ready");
            items = null;
            int failures = StateReader.AreaFailures;
            hasher.BeginDay(snapshot, null);
            StateReader.Visit(hasher);
            hasher.EndDay();
            if (StateReader.AreaFailures != failures || hasher.SnapshotOverflow)
                throw new InvalidOperationException("an area of the world print failed to read");
            return snapshot;
        }

        // The text rows of the visit at the moment of the last ReadWorld (the day-event point); read once, served to
        // every area and agency asked for in the same close.
        GroupSink Items()
        {
            if (items != null) return items;
            long origin;
            if (!Ready(out origin)) throw new InvalidOperationException("the state reader is not ready");
            var sink = new GroupSink(names);
            StateReader.Visit(sink);
            items = sink;
            return sink;
        }

        public IList<ItemRow> ReadItems(int area, string agency) { return Items().Items(area, agency); }

        // One group's rows in pieces of at most maxRows; Code TooBig when a row is too wide to send or the group is over
        // MaxHealPieces pieces. A group that is not there answers NoPrint.
        public HealData[] ReadGroup(int area, string agency, string group, int maxRows)
        {
            IList<ItemRow> all = ReadItems(area, agency);
            var rows = new List<HealRow>();
            foreach (ItemRow r in all)
            {
                if (WorldPrint.GroupOf(area, r.Key) != group) continue;
                if (r.Values != null && r.Values.Length > ActionLimits.MaxHealValues)
                    return Code(HealCodes.TooBig, area, agency, group);
                rows.Add(new HealRow { Key = r.Key, Values = WorldRows.ToWire(r.Values) });
            }
            if (rows.Count == 0) return Code(HealCodes.NoPrint, area, agency, group);
            int pieces = (rows.Count + maxRows - 1) / maxRows;
            if (pieces > ActionLimits.MaxHealPieces) return Code(HealCodes.TooBig, area, agency, group);
            var list = new HealData[pieces];
            for (int k = 0; k < pieces; k++)
            {
                int from = k * maxRows;
                list[k] = new HealData { Code = HealCodes.Ok, Area = area, Agency = agency, Group = group, Piece = k,
                    Pieces = pieces, Rows = rows.GetRange(from, Math.Min(maxRows, rows.Count - from)).ToArray() };
            }
            return list;
        }

        static HealData[] Code(int code, int area, string agency, string group)
        {
            return new[] { new HealData { Code = code, Area = area, Agency = agency, Group = group, Pieces = 1 } };
        }

        // The host restores nothing: its world is the truth.
        public string Restore(HealData[] pieces) { return "the host restores nothing"; }

        public bool YearEventDue(long nextDayTicks) { return time.YearEventDue(nextDayTicks); }

        // ---- IWorldHostSide ----

        public bool Due(WorldNote note) { return WorldContracts.Due(note); }

        public string[] AgencyNames()
        {
            long origin;
            if (!Ready(out origin)) return null;
            return names;
        }

        // M4's fingerprint of the host's world now, for a resume's release check. The game is paused then.
        public Fingerprint Print()
        {
            long origin;
            if (!Ready(out origin)) return null;
            int failures = StateReader.AreaFailures;
            hasher.BeginDay(snapshot, null);
            StateReader.Visit(hasher);
            hasher.EndDay();
            items = null;
            return PrintCheck.From(snapshot, origin, failures, StateReader.AreaFailures, hasher.SnapshotOverflow);
        }
    }
}
