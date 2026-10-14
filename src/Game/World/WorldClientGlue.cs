using System;
using System.Collections.Generic;
using HarmonyLib;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The client's game side of the shared world (M7 Task 12): IWorldWorld for a joiner. The world print is read with
    // the same visit and hasher as the host's and the quick-rejoin print (M3's StateReader into AreaHasher), keyed
    // from the host's origin; the item rows with the host's own row writer (GroupSink), so the two sides' group prints
    // and heal rows are one layout (WorldRows reads it back). A client never answers a heal (ReadGroup is the host's
    // question). Main thread only.
    public sealed class WorldClientGlue : IWorldWorld
    {
        static readonly AccessTools.FieldRef<TimeController, DateTime> NextYearOf =
            AccessTools.FieldRefAccess<TimeController, DateTime>("nextYear");

        readonly Func<long> origin;
        int preparesAtLoad = -1, agencies = -1;   // StateReader.Prepares when this load began; -1 = no load seen
        long hasherOrigin;
        AreaHasher hasher;
        DaySnapshot snapshot;

        public WorldClientGlue(Func<long> origin)
        {
            if (origin == null) throw new ArgumentNullException("origin");
            this.origin = origin;
        }

        // A snapshot is about to load: a Prepare after this point is this load's (Drift's included), and nothing read
        // before it is kept.
        public void OnLoadStarting()
        {
            preparesAtLoad = StateReader.Prepares;
            hasher = null;
        }

        // ---- IWorldWorld ----

        public DaySnapshot ReadWorld()
        {
            if (!Ensure()) return null;
            int failures0 = StateReader.AreaFailures;
            hasher.BeginDay(snapshot, null);
            StateReader.Visit(hasher);
            hasher.EndDay();
            return hasher.SnapshotOverflow || StateReader.AreaFailures != failures0 ? null : snapshot;
        }

        // Every item of the area (all agencies when agency is null), as the host lists them: the host's own writer
        // (GroupSink) over one more visit of the reader.
        public IList<ItemRow> ReadItems(int area, string agency)
        {
            if (!Ensure()) throw new InvalidOperationException("the state reader is not ready");
            int failures0 = StateReader.AreaFailures;
            var names = new string[StateReader.Agencies.Length];
            for (int i = 0; i < names.Length; i++) names[i] = AgencyWire.From(StateReader.Agencies[i]);
            var sink = new GroupSink(names);
            StateReader.Visit(sink);
            if (StateReader.AreaFailures != failures0) throw new InvalidOperationException("an area failed to read");
            return sink.Items(area, agency);
        }
        public string[] AgencyNames()
        {
            if (!Ensure()) return null;
            string[] ids = StateReader.Agencies;
            var names = new string[ids.Length];
            for (int i = 0; i < names.Length; i++) names[i] = AgencyWire.From(ids[i]);
            return names;
        }

        public HealData[] ReadGroup(int area, string agency, string group, int maxRows)
        {
            throw new NotSupportedException("the host's question");
        }

        public string Restore(HealData[] pieces) { return WorldRestore.Restore(pieces); }

        // The year event runs at the day event whose old nextDay has reached nextYear (D 88912: the same on every
        // game).
        public bool YearEventDue(long nextDayTicks)
        {
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            return tc != null && NextYearOf(tc).Ticks <= nextDayTicks;
        }

        // ---- the visit ----

        // The reader prepared once per load and the hasher kept until the origin or the agency count changes.
        bool Ensure()
        {
            long originTicks = origin();
            if (originTicks < 1) return false;
            if (PrintCheck.NeedsPrepare(preparesAtLoad, StateReader.Prepares, StateReader.IsPrepared))
            {
                StateReader.Prepare();
                if (!StateReader.IsPrepared) return false;
                hasher = null;
            }
            int n = StateReader.Agencies.Length;
            if (hasher == null || hasherOrigin != originTicks || agencies != n)
            {
                hasher = new AreaHasher(originTicks, null, n);
                snapshot = new DaySnapshot(DriftAreas.Blocks(n), DriftAreas.FieldSums(n), DriftFields.MaxScalars(n));
                hasherOrigin = originTicks;
                agencies = n;
            }
            return true;
        }

    }
}
