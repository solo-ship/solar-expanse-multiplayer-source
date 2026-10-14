using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The client half of IFlightWorld (plan Task 10) and the client's launch step. Owned by CommandClientGlue for the
    // length of the session; each snapshot load starts it again (Loading). Main thread only.
    //
    // The apply over frames: the client's apply of a flight entry (FlightApply) queues the game's own planner request
    // and returns Deferred; this object holds what the frames then do. Begin() when the request is queued, Started()
    // when the plan leaves the game's queue (the deadline counts from there), Finish() with the outcome once the
    // mission is made and checked. CommandClient polls it each step.
    //
    // The launch step: a checked launch (an elevator or catapult flight) is decided by the host. The game's own
    // OnLaunch is entered through FlightLaunchHook, which applies the host's outcome entry (74 or 71) inside the call
    // and runs the game's original under that outcome; the two facility tests inside OnLaunch read OutcomeFor.
    public sealed class FlightClientGlue : IFlightWorld
    {
        static readonly MethodBase OnLaunchMethod = FindOnLaunch();

        readonly CommandSession session;
        bool running;
        long started = -1;
        ApplyOutcome done;
        // the host's counters after its copy, checked once this game's copy is made
        CounterRow[] after;
        // this game's own landings by mission id since the load, and the host's launch outcomes applied since it
        readonly HashSet<long> landed = new HashSet<long>();
        readonly Dictionary<long, bool> outcomes = new Dictionary<long, bool>();
        // the late launches held for the host's verdict: mission id -> the launcher whose OnLaunch was held
        readonly Dictionary<long, object> late = new Dictionary<long, object>();
        bool subscribed;

        public FlightClientGlue(CommandSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            this.session = session;
        }

        // ---- the apply over frames ----

        // An apply queued its request: Poll answers null until Finish.
        public void Begin()
        {
            running = true;
            started = -1;
            done = null;
            after = null;
        }

        // The client world's apply returned Deferred (FlightApply.StartClient queued the copy through the load path):
        // the entry's counters are checked when the copy is made, not now.
        public void Begin(CounterRow[] counters)
        {
            Begin();
            after = counters;
        }

        // What the load path's copy (FlightApply) has to say: when its plan left the game's queue, and its outcome.
        void Pump()
        {
            if (!running || done != null) return;
            FlightApply apply = session.FlightApply;
            if (started < 0)
            {
                long s = apply.ClientStartedMs;
                if (s >= 0) Started(s);
            }
            ApplyOutcome o = apply.PollClient();
            if (o != null) Finish(o);
        }

        // The plan left the game's queue now (nowMs is NetClock's).
        public void Started(long nowMs) { if (running) started = nowMs; }

        // The mission is made and checked (or could not be): Poll gives this next.
        public void Finish(ApplyOutcome outcome)
        {
            if (!running) return;
            done = outcome ?? new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
        }

        // Whether an apply is waiting on the game (the planner queue must not be asked for more meanwhile).
        public bool Waiting { get { return running; } }

        public ApplyOutcome Poll()
        {
            Pump();
            if (!running || done == null) return null;
            ApplyOutcome o = done;
            CounterRow[] rows = after;
            running = false;
            started = -1;
            done = null;
            after = null;
            CommandClientGlue g = session.ClientGlue;
            if (o.Code == 0 && rows != null && g != null)
            {
                string why = g.Counters.After(rows);
                if (why != null)
                {
                    Log.Warn("flight: the copy's counters are not the host's: " + why);
                    return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
                }
            }
            return o;
        }

        public long ApplyStarted
        {
            get
            {
                Pump();
                return running ? started : -1;
            }
        }

        // The link was lost or a full rejoin begins: nothing queued for the old stream is waited for, and the copies
        // it queued leave the game's queue.
        public void Dropped()
        {
            running = false;
            started = -1;
            done = null;
            after = null;
            session.FlightApply.DropAll();
        }

        // ---- landings ----

        // 73 only: this game's own landing of that mission is recorded. A mission this game never had is out of step.
        public bool Landed(long missionId)
        {
            if (landed.Contains(missionId)) return true;
            MissionInfoManager m = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            if (m != null && m.ListMissionInfo != null)
                foreach (MissionInfo mi in m.ListMissionInfo)
                    if (mi != null && mi.id == missionId) return false;
            throw new InvalidOperationException("this game never had mission " + missionId);
        }

        void OnRocketLand(Spacecraft ship)
        {
            try
            {
                MissionInfo mi = ship != null ? ship.GetMissionInfo() : null;
                if (mi != null) landed.Add(mi.id);
            }
            catch (Exception e) { Log.Warn("flight: could not record a landing: " + e.GetType().Name); }
        }

        // ---- the load ----

        // A snapshot is about to load: nothing of the old world carries over.
        public void Loading()
        {
            Dropped();
            landed.Clear();
            outcomes.Clear();
            late.Clear();
        }

        // The loaded world runs: this game's landings are recorded from here.
        public void LoadDone()
        {
            if (subscribed) return;
            Spacecraft.rocketLand += OnRocketLand;
            subscribed = true;
        }

        public void AtMenu()
        {
            Dropped();
            if (!subscribed) return;
            Spacecraft.rocketLand -= OnRocketLand;
            subscribed = false;
        }

        // ---- the host half of IFlightWorld: a client has none ----

        public bool PlansPending { get { return false; } }
        public void DropQueued() { }
        public IList<PrintEntry> FlightTouches(long day) { return new PrintEntry[0]; }

        // ---- the launch step ----

        // The host's outcome for a checked launch this game has applied: 1 launched, 0 cancelled, -1 none. The two
        // facility tests in OnLaunch read it (the outcome, not this game's own check), also for a second ship of the same
        // mission that reaches OnLaunch after the first applied the entry.
        public int OutcomeFor(long missionId)
        {
            bool launched;
            if (!outcomes.TryGetValue(missionId, out launched)) return LaunchVerdictRule.None;
            return launched ? LaunchVerdictRule.Launched : LaunchVerdictRule.Cancelled;
        }

        // The prefix of the game's OnLaunch on a client. False: the original must not run again (it ran inside the
        // entry); true: not a launch the session decides. A mission the launch watch does not have launches as vanilla
        // (an ordinary rocket has no check to split on).
        public bool Launching(object launcher, Spacecraft ship, bool cancel, bool ready, bool wasLaunch)
        {
            CommandClientGlue g = session.ClientGlue;
            if (g == null || !g.Loaded || !HumanAgencies.Active || ship == null) return true;
            if (cancel || !ready || wasLaunch) return true;
            MissionInfo mission = ship.GetMissionInfo();
            if (mission == null) return true;
            long id = mission.id;
            FlightWatches watches = session.FlightWatches;
            long gameNow = MonoBehaviourSingleton<Manager.TimeController>.Instance.CurrentTime.Ticks;
            // DD-F6, the late launch: a checked launch of a ship built after its date is the host's verdict to apply,
            // from Update once it heads the stream; this game's own facility says nothing
            if (LateLaunchRule.ShouldHold(FlightWatches.IsChecked(mission), watches.Launches.Contains(id),
                    outcomes.ContainsKey(id), gameNow, mission.DateLaunch.Ticks)
                || (watches.IsLate(id) && !late.ContainsKey(id)))
            {
                late[id] = launcher;
                if (!watches.IsLate(id)) watches.AdmitLate(id, gameNow);
                Log.Info("flight: late launch " + id + " held for the host's verdict");
                return false;
            }
            if (!watches.Launches.Contains(id)) return true;
            long day;
            if (!g.LocalDay(out day)) return true;
            string why = g.Client.ApplyLaunchNow(id, day, launched => RunOriginal(launcher, id, launched));
            session.Steps.Launches.Remove(id);
            if (why != null) Log.Warn("command: the launch of mission " + id + " did not apply: " + why);
            else Log.Info("flight: launch cut " + id + " " + Math.Max(0, session.Steps.TakeEventHold(id)) + " ms");
            return false;
        }

        // A launch outcome heads the stream for a mission nobody watches: if this game has just built the ship of a
        // late checked launch, the mission goes on the watch (before the cut judges the head). Called each fixed step.
        public void AdmitHead(CommandClient client, long gameNow)
        {
            long head = client.HeadLaunchMission();
            if (head < 0) return;
            FlightWatches watches = session.FlightWatches;
            if (watches.Launches.Contains(head)) return;
            MissionInfo mi = Find(head);
            bool live = mi != null && !mi.cancel && !mi.complete && FlightWatches.IsBuilt(mi);
            if (!live) return;
            if (LateLaunchRule.AdmitsHead(FlightWatches.IsChecked(mi), false, outcomes.ContainsKey(head), true, gameNow,
                    mi.DateLaunch.Ticks))
            {
                watches.AdmitLate(head, gameNow);
                Log.Info("flight: late launch " + head + " is the host's to decide");
            }
        }

        // Each frame: a held late launch whose outcome heads the stream is applied (the held launcher is the branch);
        // one whose mission is gone is dropped (the host's own cancel entry ended it).
        public void UpdateLate()
        {
            if (late.Count == 0) return;
            CommandClientGlue g = session.ClientGlue;
            if (g == null || !g.Loaded || g.Client.OutOfStep) return;
            var ids = new List<long>(late.Keys);
            foreach (long id in ids)
            {
                MissionInfo mi = Find(id);
                if (mi == null || mi.cancel || mi.complete)
                {
                    late.Remove(id);
                    session.FlightWatches.DropLate(id);
                    Log.Info("flight: late launch " + id + " dropped, the mission is gone");
                    continue;
                }
                long day;
                if (!g.Client.LaunchReady(id) || !g.LocalDay(out day)) continue;
                object launcher = late[id];
                late.Remove(id);
                string why = g.Client.ApplyLaunchNow(id, day, launched => RunLate(launcher, id, launched));
                session.FlightWatches.DropLate(id);
                if (why != null) Log.Warn("command: the late launch of mission " + id + " did not apply: " + why);
                else Log.Info("flight: late launch " + id + " applied");
            }
        }

        static MissionInfo Find(long id)
        {
            MissionInfoManager m = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null) return null;
            foreach (MissionInfo mi in m.ListMissionInfo) if (mi != null && mi.id == id) return mi;
            return null;
        }

        // The held OnLaunch under the host's outcome, then what StartOnLaunch does after it (the planned launch time
        // for the save, which the held call's own coroutine did before the ship had launched).
        string RunLate(object launcher, long missionId, bool launched)
        {
            string why = RunOriginal(launcher, missionId, launched);
            try
            {
                var ship = AccessTools.Field(launcher.GetType(), "spacecraft").GetValue(launcher) as Spacecraft;
                if (ship != null) ship.SetLaunchTimeForSave();
            }
            catch (Exception e) { Log.Warn("flight: late launch time not set: " + e.GetType().Name); }
            return why;
        }

        // The game's OnLaunch, through its patched entry, under the host's outcome: this prefix lets it run while the
        // entry is applied.
        string RunOriginal(object launcher, long missionId, bool launched)
        {
            if (OnLaunchMethod == null) return "the game has no OnLaunch";
            outcomes[missionId] = launched;
            try
            {
                OnLaunchMethod.Invoke(launcher, null);
                return null;
            }
            catch (TargetInvocationException e)
            {
                return "OnLaunch threw " + (e.InnerException ?? e).GetType().Name;
            }
        }

        internal static MethodBase FindOnLaunch()
        {
            Type t = AccessTools.Inner(typeof(RocketLauncher), "LaunchSpaceCraft");
            return t == null ? null : AccessTools.Method(t, "OnLaunch");
        }
    }

    // RocketLauncher.LaunchSpaceCraft.OnLaunch (private nested class): on a client in a session, the host's outcome for
    // a checked launch is applied inside this call. Needs a [HarmonyPatch] of its own (HarmonyX skips a patch class
    // without one); a no-op outside a session and on the host.
    [HarmonyPatch]
    public static class FlightLaunchHook
    {
        [HarmonyTargetMethod]
        static MethodBase Target() { return FlightClientGlue.FindOnLaunch(); }

        [HarmonyPrefix]
        static bool Prefix(object __instance, Spacecraft ___spacecraft, bool ___cancel, bool ___readyToCountDown,
                           bool ___wasLaunch)
        {
            CommandSession s = Session.Commands;
            if (s == null || s.IsHost || s.Applying) return true;
            CommandClientGlue g = s.ClientGlue;
            try
            {
                return g == null || g.Flights.Launching(__instance, ___spacecraft, ___cancel, ___readyToCountDown,
                    ___wasLaunch);
            }
            catch (Exception e)
            {
                s.Steps.Failed(e);
                return true;
            }
        }
    }
}
