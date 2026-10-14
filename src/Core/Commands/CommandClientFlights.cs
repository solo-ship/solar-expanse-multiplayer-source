using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The client's flight parts (plan M6 Task 6, DD-F3, DD-F6): an entry whose apply goes on over frames, and the launch
    // step. A flight entry is applied through the game's own mission planner, which makes the mission over a few
    // frames; the stream holds at that entry until the game says it is made (Poll), and the day gate holds with it,
    // since the entry is not yet applied. A checked launch (an elevator or catapult flight) is decided by the host: the
    // client cuts its step before the launch until the host's outcome is in (LaunchReady), and the game's own OnLaunch
    // then applies it (ApplyLaunchNow), as an impact's landing does.
    public sealed partial class CommandClient
    {
        // The flight side of the game; null = M5's behaviour exactly.
        public IFlightWorld Flights { get; set; }

        // The entry being applied over frames, and how long it has run: the stream waits for it.
        ActionResult running;
        int runFrames;

        // The launches applied in this stream, so a wait the glue still asks about afterwards is no wait.
        readonly HashSet<long> launchesApplied = new HashSet<long>();

        // Twin of the host's bucket for the large flight kinds (DD-F9): the same numbers on both sides, so a client that
        // keeps to them never meets the host's TooMany.
        readonly TokenBucket flightBucket = new TokenBucket(ActionLimits.FlightsPerMinute / 60.0,
            ActionLimits.FlightBurst);

        // An entry is being applied over frames; the stream waits.
        public bool ApplyRunning { get { return running != null; } }

        void ForgetEvents()
        {
            impactsApplied.Clear();
            launchesApplied.Clear();
            running = null;
        }

        void BeginRunning(ActionResult entry)
        {
            running = entry;
            runFrames = 0;
            if (Logging) Say("flight: applying #" + entry.Seq + " mission " + MissionOf(entry.Action));
        }

        static long MissionOf(ActionRequest a)
        {
            return a.Mission != null && a.Mission.MissionId >= 0 ? a.Mission.MissionId : a.ItemId;
        }

        // One step of the running apply: done, still going, or too long (counted from when its plan left the game's
        // queue; a plan still queued is not late, whatever the wait).
        void PollRunning(long now)
        {
            ActionResult entry = running;
            IFlightWorld f = Flights;
            ApplyOutcome outcome;
            long started;
            runFrames++;
            try
            {
                started = f.ApplyStarted;
                outcome = f.Poll();
            }
            catch (Exception e)
            {
                Log.Error("command: polling #" + entry.Seq + " threw: " + ExceptionText.Chain(e));
                Fail("applying #" + entry.Seq + " threw " + e.GetType().Name);
                return;
            }
            if (outcome == null)
            {
                if (started >= 0 && now - started > ActionLimits.FlightApplyMs)
                    Fail("applying #" + entry.Seq + " took more than " + ActionLimits.FlightApplyMs
                        + " ms once its plan left the queue");
                return;
            }
            running = null;
            if (!Complete(entry, outcome, null)) return;
            if (Logging)
                Say("flight: applied #" + entry.Seq + " mission " + MissionOf(entry.Action) + " in " + runFrames
                    + " frames " + (started >= 0 ? now - started : 0) + " ms");
        }

        // A landing craft's entry (73) waits for this game's own landing of its mission; a mission this game never had
        // is out of step.
        bool LandingRecorded(ActionResult entry)
        {
            IFlightWorld f = Flights;
            if (f == null) return true;
            try { return f.Landed(entry.Action.ItemId); }
            catch (Exception e)
            {
                Fail("the craft entry #" + entry.Seq + " names mission " + entry.Action.ItemId + ", which this game"
                    + " never had (" + e.GetType().Name + ")");
                return false;
            }
        }

        // ---- the launch step ----

        // The mission a launch outcome (74, or a 71 of reason 1 or 2) at the head of the stream belongs to, or -1.
        public long HeadLaunchMission()
        {
            if (!inStream) return -1;
            ActionResult head = order.PeekEntry();
            return head != null && FlightEvents.IsLaunch(head.Action) ? head.Action.ItemId : -1;
        }

        // The host's outcome for that mission's launch has come and the stream is at it, or (an entry stamped with the
        // next day, a launch just after midnight) the gate may cross the client's day: the client's launch step may
        // run. An elevator switch the host made before the launch is an earlier entry, applied first.
        public bool LaunchReady(long missionId) { return EventReady(missionId, false); }

        // Called from the game's own OnLaunch, inside its call: applies every host-decided entry of localDay ahead of
        // the outcome, then the outcome under Applying. For a 74 runOnLaunch(true) lets the launch go ahead past the
        // check; for a 71 runOnLaunch(false) lets the game's own cancel run, as it did on the host. null = done;
        // otherwise the reason, and the client is out of step.
        public string ApplyLaunchNow(long missionId, long localDay, Func<bool, string> runOnLaunch)
        {
            if (outOfStep) return outWhy;
            if (applying)
                return Stop("the launch of mission " + missionId + " ran while an entry was being applied");
            try { return ApplyLaunch(missionId, localDay, runOnLaunch); }
            catch (Exception e)
            {
                Log.Error("command: the launch of mission " + missionId + " threw: " + ExceptionText.Chain(e));
                return Stop("the launch of mission " + missionId + " threw " + e.GetType().Name);
            }
        }

        string ApplyLaunch(long missionId, long localDay, Func<bool, string> runOnLaunch)
        {
            if (runOnLaunch == null) return Stop("no launch branch given for mission " + missionId);
            if (!inStream) return Stop("the launch of mission " + missionId + " came with no stream");
            int at = order.IndexOfEvent(missionId, false);
            if (at < 0) return Stop("the launch of mission " + missionId + " ran with no entry for it at the head");
            ActionResult outcome = order.EntryAt(at);
            if (outcome.Day != localDay)
                return Stop("the launch of mission " + missionId + " is for day " + outcome.Day + ", not " + localDay);
            string ahead = AheadOf(outcome.Seq, localDay);
            if (ahead != null) return ahead;
            if (!ReferenceEquals(order.PeekEntry(), outcome))
                return Stop("the launch of mission " + missionId + " ran with no entry for it at the head");
            bool launched = outcome.Action.Kind == ActionKind.MissionLaunched;
            if (!ApplyEntry(outcome, () => runOnLaunch(launched), true)) return outWhy;
            launchesApplied.Add(missionId);
            gate.EndWait();
            return null;
        }

        // The wait after the client has reached a launch: out of step once the host's own clock is a whole day past
        // the launch for FetchDeadlineMs and its outcome is still not applied (never real time alone, so a pause harms
        // nobody).
        public bool LaunchOverdue(long missionId, long launchTicks, long nowMs)
        {
            if (!inStream || launchesApplied.Contains(missionId))
            {
                gate.EndWait();
                return false;
            }
            gate.BeginWait(missionId);
            if (!gate.WaitOverdue(launchTicks, nowMs)) return false;
            Fail(HeadLaunchMission() == missionId
                ? "the launch of mission " + missionId + " never ran (its outcome is here)"
                : "the launch of mission " + missionId + " never came");
            return true;
        }
    }
}
