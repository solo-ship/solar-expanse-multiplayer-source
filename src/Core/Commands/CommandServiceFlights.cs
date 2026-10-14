using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host driver's flight part (M6 Task 5; plan DD-F3, DD-F9): a player's flight is accepted at once and its
    // entry waits until the host's own copy is created and checked (deferred entries); the large flight kinds share a
    // per-player bucket; a join batch's snapshot waits for the creations accepted before it; an entry made after the
    // snapshot is held for everyone until the batch ends, so the stream's order never differs between recipients.
    // With Flights null none of this runs and the driver is M5's.
    public sealed partial class CommandService
    {
        sealed class DeferredFlight
        {
            public int Token, Player;
            public ActionRequest Req;       // the player's request
            public long MissionId;
            public int Frames;
            public bool Orphan;             // its player's link was replaced or left: the entry goes out as the host's
        }

        public IFlightWorld Flights { get; set; }

        readonly List<DeferredFlight> pending = new List<DeferredFlight>();
        // DeferredDone called inside an apply: published after it, so the apply's own entries keep their order
        readonly List<KeyValuePair<DeferredFlight, ApplyOutcome>> doneLater =
            new List<KeyValuePair<DeferredFlight, ApplyOutcome>>();
        readonly Dictionary<int, TokenBucket> flightBuckets = new Dictionary<int, TokenBucket>();
        // entries made after the snapshot of a join batch: held for every recipient until the batch ends
        readonly List<Item> afterSnapshot = new List<Item>();
        bool snapshotDone;

        // Deferred entries outstanding, or a plan in the game's planner queue: the host's daily cut waits for them.
        public bool PlansPending
        {
            get { return pending.Count > 0 || doneLater.Count > 0 || (Flights != null && Flights.PlansPending); }
        }

        // ---- the limits ----

        // DD-F9: kinds 60, 62, 65, 67 per player, the same numbers as the client's own bucket. Past it: TooMany, the
        // link kept.
        bool FlightBucketAllows(int player, ActionRequest a, long nowMs)
        {
            if (Flights == null || !ActionKind.IsFlight(a.Kind)) return true;
            TokenBucket b;
            if (!flightBuckets.TryGetValue(player, out b))
                flightBuckets[player] = b = new TokenBucket(ActionLimits.FlightsPerMinute / 60.0,
                    ActionLimits.FlightBurst);
            if (!b.Has(1, nowMs)) return false;
            b.Take(1, nowMs);
            return true;
        }

        // Only a schedule or a supply flight is deferred; over the caps it is Busy, before the game is touched.
        int DeferredBusy(int player, ActionRequest a)
        {
            if (Flights == null || (a.Kind != ActionKind.FlightSchedule && a.Kind != ActionKind.SupplyFlight)) return 0;
            if (pending.Count >= ActionLimits.MaxDeferred) return RefuseCodes.Busy;
            int mine = 0;
            for (int i = 0; i < pending.Count; i++) if (pending[i].Player == player) mine++;
            return mine >= ActionLimits.MaxDeferredPerPlayer ? RefuseCodes.Busy : 0;
        }

        // The entry as the host will send it, at its widest: the request's own crafts and cargo with the host's
        // mission id, name, flags and the longest removal list. The encode check at acceptance uses this.
        internal static ActionRequest FlightWidest(ActionRequest a)
        {
            if ((a.Kind != ActionKind.FlightSchedule && a.Kind != ActionKind.SupplyFlight) || a.Mission == null)
                return a;
            ActionRequest w = ActionCopy.Of(a);
            MissionData m = w.Mission;
            m.MissionId = int.MaxValue;
            m.Container = 1;
            m.Flags = MissionFlags.All;
            m.Name = new string('"', ActionLimits.MaxMissionNameChars);
            m.Removals = new RemovalRow[ActionLimits.MaxRemovals];
            for (int i = 0; i < m.Removals.Length; i++)
                m.Removals[i] = new RemovalRow { What = RemovalKinds.Resource, Agency = a.Agency ?? "agency",
                    Body = ActionLimits.MaxBodyId, Resource = CounterRows.WideId, Amount = 1.2345678901234568E-300 };
            return w;
        }

        // ---- deferred entries ----

        // The world accepted a flight and queued its own copy: nothing is sent, nothing answered, until DeferredDone.
        void Defer(int player, ActionRequest req, ApplyOutcome o)
        {
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].Token == o.Token)
                {
                    Fault("two deferred flights share token " + o.Token);
                    return;
                }
            ActionRequest shown = o.Applied ?? req;
            long id = shown.Mission != null ? shown.Mission.MissionId : -1;
            pending.Add(new DeferredFlight { Token = o.Token, Player = player, Req = req, MissionId = id });
            Log.Info("flight: player " + player + " req " + req.Req + " " + ActionKind.Name(req.Kind)
                + " accepted mission " + id + " token " + o.Token);
        }

        // The host's copy was created and checked (outcome applied) or stopped and rolled back (Code set). An
        // applied one takes the next Seq now, is validated and sent as any entry; a refused one answers PlanFailed
        // (or the code) and sends nothing.
        public void DeferredDone(int token, ApplyOutcome outcome)
        {
            DeferredFlight d = null;
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].Token == token) { d = pending[i]; pending.RemoveAt(i); break; }
            if (d == null)
            {
                Log.Warn("flight: token " + token + " is not outstanding (dropped by a load, or done twice)");
                return;
            }
            if (outcome == null || outcome.Code != 0 || outcome.Deferred)
            {
                int code = outcome != null && outcome.Code != 0 ? outcome.Code : RefuseCodes.PlanFailed;
                Log.Info("flight: token " + token + " refunded " + RefuseCodes.Name(code));
                if (!d.Orphan) Refuse(d.Player, d.Req, code);
                return;
            }
            if (applying) { doneLater.Add(new KeyValuePair<DeferredFlight, ApplyOutcome>(d, outcome)); return; }
            PublishDeferred(d, outcome);
        }

        void PublishDeferred(DeferredFlight d, ApplyOutcome outcome)
        {
            try
            {
                long day;
                if (!StampDay(out day)) { Fault("no day key for nextDay " + world.NextDayTicks); return; }
                uint before = seq;
                // an orphan is still a person's request (protocol 6 allows Host only on host kinds): numbered 0, origin
                // Player
                Publish(d.Player, d.Req, outcome, day, d.Orphan, EntryOrigin.Player);
                if (seq != before)
                    Log.Info("flight: token " + d.Token + " created in " + d.Frames + " frames, sent #" + seq);
            }
            catch (Exception e)
            {
                Log.Error("flight: publishing token " + d.Token + " failed: " + ExceptionText.Chain(e));
                Fault("the game threw sending flight token " + d.Token + ": " + e.GetType().Name);
            }
        }

        void DrainDone()
        {
            while (doneLater.Count > 0)
            {
                KeyValuePair<DeferredFlight, ApplyOutcome> next = doneLater[0];
                doneLater.RemoveAt(0);
                PublishDeferred(next.Key, next.Value);
            }
        }

        void FlightFrame()
        {
            for (int i = 0; i < pending.Count; i++) pending[i].Frames++;
        }

        // A link replaced or a player left: the flight's removals are made, so it finishes; its entry then goes out as
        // the host's, because the player's request number means nothing to a link that has since restarted.
        void OrphanFlightsOf(int player)
        {
            for (int i = 0; i < pending.Count; i++) if (pending[i].Player == player) pending[i].Orphan = true;
            for (int i = 0; i < doneLater.Count; i++) if (doneLater[i].Key.Player == player) doneLater[i].Key.Orphan = true;
        }

        // A load replaces the world the queued creations belong to.
        void DropFlights()
        {
            pending.Clear();
            doneLater.Clear();
            afterSnapshot.Clear();
            if (Flights != null) Flights.DropQueued();
        }

        // ---- join batches (Y-4) ----

        // The snapshot waits until every creation accepted before the batch has gone out (new requests are held, so
        // none is accepted meanwhile).
        bool FlightsReadyToSnapshot() { return pending.Count == 0 && doneLater.Count == 0; }

        // After the snapshot the stream is held for everyone until the batch ends: an entry made then reaches the old
        // recipients and the joiner in one order, after the joiner's AgencyState.
        bool HoldsAfterSnapshot(Item it)
        {
            if (Flights == null || !held || !snapshotDone) return false;
            afterSnapshot.Add(it);
            if (afterSnapshot.Count > MaxOutQueue)
                Fault("more than " + MaxOutQueue + " entries wait for a join batch to end");
            return true;
        }

        bool KeepsForMembers { get { return Flights == null || !snapshotDone; } }

        List<Item> TakeAfterSnapshot()
        {
            var list = new List<Item>(afterSnapshot);
            afterSnapshot.Clear();
            return list;
        }

        // ---- the day ----

        IList<PrintEntry> FlightTouchesFor(long day)
        {
            return Flights == null ? null : Flights.FlightTouches(day);
        }
    }
}
