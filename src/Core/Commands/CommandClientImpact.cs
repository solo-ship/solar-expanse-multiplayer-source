using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The client's side of a landing: an asteroid impact (the host's 59) or an orbit pull (72) (plan DD15, DD-F7, "The
    // impact step on a client"). The landing handler of a ship reads that ship's own state, which is valid only inside
    // the landing step, so the client's landing step never runs before the host's entry: the client knows its landings
    // in advance, stops just short of each arrival until the host's entry for that mission has come with its whole
    // group (ImpactReady), and then applies it inside its own landing (ApplyImpactNow), with the game's handler as the
    // branch the world runs under the entry's spawn and draw replay. A landing entry is never applied by Update: it,
    // and every entry behind it, waits at the head of the stream until then. The glue (the watch of arrivals, the
    // step cut, the handler prefix) is the game side's.
    //
    // A landing entry's group is the Count entries behind it: its re-plans (57), then the cancels of the flights to
    // the asteroid it destroyed (71, reason 5). They apply in the same call, so no step runs with part of a landing's
    // consequences applied. Everything here is keyed on the entry's own day: an entry the host stamped with the next
    // day (a landing in the last minute) is ready once the gate may cross the client's day.
    public sealed partial class CommandClient
    {
        // Runs the game's impact handler for the world and remembers whether it ran and what it said. It runs once,
        // inside the apply: a second run, or one after the apply has returned (a world that kept the delegate), would
        // act on a ship whose state has moved on, so it is refused and the client goes out of step.
        sealed class BranchRun
        {
            readonly Func<string> inner;
            bool closed;
            public bool Ran;
            public string Said;
            public string Misused;

            public BranchRun(Func<string> inner) { this.inner = inner; }

            public void Close() { closed = true; }

            public string Run()
            {
                if (closed || Ran)
                {
                    if (Misused == null)
                        Misused = closed ? "the branch was run after the apply" : "the branch ran twice";
                    return Misused;
                }
                Ran = true;
                Said = inner();
                return Said;
            }
        }

        // The landings and launches applied in this stream, so a wait the glue still asks about afterwards is no wait.
        readonly HashSet<long> impactsApplied = new HashSet<long>();

        // The mission a landing entry (an impact's 59, a pull's 72) at the head of the stream (waiting to be applied)
        // belongs to, or -1 when the head is anything else or nothing is queued. A head landing for a mission the glue
        // does not expect is out of step at once, rather than a stream that stalls.
        public long HeadImpactMission()
        {
            if (!inStream) return -1;
            ActionResult head = order.PeekEntry();
            return head != null && FlightEvents.IsLanding(head.Action) ? head.Action.ItemId : -1;
        }

        // That mission's landing entry has come with its whole group and the stream is at it: every entry before it is
        // applied (the entry's own day is the client's), or, for an entry stamped with the next day, the gate says the
        // client's day may be crossed (a pure read, never MayCross: the comparison is the cut's) and the group has
        // arrived. Then the client's landing step may run, and applies the group in one call.
        public bool ImpactReady(long missionId) { return EventReady(missionId, true); }

        bool EventReady(long missionId, bool landing)
        {
            if (missionId < 0 || !inStream) return false;
            int at = order.IndexOfEvent(missionId, landing);
            if (at < 0) return false;
            ActionResult e = order.EntryAt(at);
            if (order.QueuedEntries < at + 1 + FlightEvents.GroupSize(e.Action)) return false;
            long day;
            if (!TryDay(out day)) return false;
            if (e.Day == day) return at == 0;
            return e.Day == day + 1 && gate.MayCross(day);
        }

        // A cluster (events chained less than a game second apart, one cut each): the first member is ready when its
        // own entry is at the stream's head or past the gate, and every member's whole group has arrived, so none of
        // them can wait for another inside the step. landing[i] says whether ids[i] is a landing or a launch.
        public bool ClusterReady(long[] ids, bool[] landing, int count)
        {
            if (count < 1 || ids == null || landing == null) return false;
            if (!EventReady(ids[0], landing[0])) return false;
            for (int i = 1; i < count; i++)
            {
                int at = order.IndexOfEvent(ids[i], landing[i]);
                if (at < 0 || order.QueuedEntries < at + 1 + FlightEvents.GroupSize(order.EntryAt(at).Action))
                    return false;
            }
            return true;
        }

        // Every entry before that mission's landing (or launch) entry is applied: the ships-first hold at the day's
        // start waits for Update to get there.
        public bool AheadApplied(long missionId)
        {
            int at = order.IndexOfEvent(missionId, true);
            if (at < 0) at = order.IndexOfEvent(missionId, false);
            return at == 0;
        }

        // M5's form: the client's own current day.
        public string ApplyImpactNow(long missionId, Func<string> runBranch)
        {
            if (outOfStep) return outWhy;
            long day;
            try
            {
                if (!DayKey.TryCurrent(world.NextDayTicks, world.OriginTicks, out day))
                    return Stop("the client's own day has no key");
            }
            catch (Exception e)
            {
                Log.Error("command: the impact of mission " + missionId + " threw: " + ExceptionText.Chain(e));
                return Stop("the impact of mission " + missionId + " threw " + e.GetType().Name);
            }
            return ApplyImpactNow(missionId, day, runBranch);
        }

        bool TryDay(out long day)
        {
            day = 0;
            try { return DayKey.TryCurrent(world.NextDayTicks, world.OriginTicks, out day); }
            catch (Exception) { return false; }
        }

        // Called from the client's own landing, inside the game's call, with the client's day as it is now (a landing in
        // the crossing step, after the day event, is for the next day): applies, in order, every received entry of
        // that day before the landing entry (each must apply within the step, a host-decided entry; anything else is a
        // reason), then the landing entry under Applying, with runBranch (the game's original handler) run by the
        // world inside the apply, and then the Count entries of its group, in the same call. null = done; otherwise the
        // reason, and the client is out of step (the caller lets the game carry on with whatever it does then).
        public string ApplyImpactNow(long missionId, long localDay, Func<string> runBranch)
        {
            if (outOfStep) return outWhy;
            // the game's handler firing again from inside an apply (this one's branch, or another entry's) would apply
            // the same entry twice
            if (applying)
                return Stop("the impact of mission " + missionId + " landed while an entry was being applied");
            try { return ApplyImpact(missionId, localDay, runBranch); }
            catch (Exception e)
            {
                Log.Error("command: the impact of mission " + missionId + " threw: " + ExceptionText.Chain(e));
                return Stop("the impact of mission " + missionId + " threw " + e.GetType().Name);
            }
        }

        string ApplyImpact(long missionId, long localDay, Func<string> runBranch)
        {
            if (runBranch == null) return Stop("no impact branch given for mission " + missionId);
            if (!inStream) return Stop("the impact of mission " + missionId + " came with no stream");
            int at = order.IndexOfEvent(missionId, true);
            if (at < 0 || order.QueuedEntries < at + 1 + FlightEvents.GroupSize(order.EntryAt(at).Action))
                return Stop("the impact of mission " + missionId + " landed with no entry for it at the head");
            ActionResult impact = order.EntryAt(at);
            if (impact.Day != localDay)
                return Stop("the impact of mission " + missionId + " is for day " + impact.Day + ", not " + localDay);
            string ahead = AheadOf(impact.Seq, localDay);
            if (ahead != null) return ahead;
            if (!ReferenceEquals(order.PeekEntry(), impact))
                return Stop("the impact of mission " + missionId + " landed with no entry for it at the head");
            if (!ApplyEntry(impact, runBranch, true)) return outWhy;
            impactsApplied.Add(missionId);
            gate.EndWait();
            // exactly the entries the landing announced, each of its day: the re-plans, then the cancels
            int group = FlightEvents.GroupSize(impact.Action);
            for (int i = 0; i < group; i++)
            {
                ActionResult next = order.PeekEntry();
                if (next == null || next.Day != impact.Day || !FlightEvents.InGroup(next.Action))
                    return Stop("the entry after the impact of mission " + missionId + " is not re-plan or cancel "
                        + (i + 1));
                if (!ApplyEntry(next, null, true)) return outWhy;
            }
            return null;
        }

        // Applies, in order, every received entry of localDay ahead of entry `seq` that applies within a step: a
        // host-decided (or rival-decided) entry; also the year event's way to the 91 (CommandClientWorld.OpenYear). Anything else (a player's action, another landing or launch, an apply that goes on over
        // frames) is a reason, and the client is out of step. null = every entry ahead is applied.
        public string ApplyAheadOf(uint seq, long localDay)
        {
            if (outOfStep) return outWhy;
            if (applying) return Stop("entries were applied ahead of #" + seq + " while an entry was being applied");
            try { return AheadOf(seq, localDay); }
            catch (Exception e)
            {
                Log.Error("command: applying ahead of #" + seq + " threw: " + ExceptionText.Chain(e));
                return Stop("applying ahead of #" + seq + " threw " + e.GetType().Name);
            }
        }

        string AheadOf(uint seq, long localDay)
        {
            while (true)
            {
                ActionResult head = order.PeekEntry();
                if (head == null || head.Seq >= seq) return null;
                ActionRequest a = head.Action;
                if (a == null) return Stop("an entry with no action");
                if (head.Day != localDay)
                    return Stop("entry #" + head.Seq + " ahead of #" + seq + " is for day " + head.Day + ", not "
                        + localDay);
                if (FlightEvents.IsLanding(a) || FlightEvents.IsLaunch(a))
                    return Stop("entry #" + head.Seq + " (" + ActionKind.Name(a.Kind) + ") is ahead of #" + seq
                        + ": not next in this game's own order");
                // a host-decided kind, or any kind the host or a rival decided (M7 origins): never a person's request
                if (!ActionKind.HostOnly(a.Kind) && head.Origin == EntryOrigin.Player)
                    return Stop("entry #" + head.Seq + " (" + ActionKind.Name(a.Kind) + ") ahead of #" + seq
                        + " does not apply within a step");
                if (a.Kind == ActionKind.LandingCraft && !LandingRecorded(head))
                    return outOfStep ? outWhy : Stop("entry #" + head.Seq + " is ahead of #" + seq
                        + " but this game has not landed its mission");
                if (!ApplyEntry(head, null, true)) return outWhy;
            }
        }

        // The wait after the client has reached an arrival: true (and out of step) when the host's own clock is a
        // whole day past the arrival, and has been for FetchDeadlineMs, with the impact still not applied: its entry not
        // at hand, or at hand but the landing never coming. Only the host's clock counts, so a pause harms nobody.
        public bool ImpactOverdue(long missionId, long arrivalTicks, long nowMs)
        {
            // while catching up the host's clock is paused far ahead (M7 DD-W12)
            if (catchingUp) return false;
            if (!inStream || impactsApplied.Contains(missionId))
            {
                gate.EndWait();
                return false;
            }
            gate.BeginWait(missionId);
            if (!gate.WaitOverdue(arrivalTicks, nowMs)) return false;
            // the entry may be here and the client's own landing the part that never came
            Fail(HeadImpactMission() == missionId
                ? "the landing of mission " + missionId + " never came (its impact entry is here)"
                : "the impact of mission " + missionId + " never came");
            return true;
        }

        string Stop(string why)
        {
            Fail(why);
            return why;
        }
    }
}
