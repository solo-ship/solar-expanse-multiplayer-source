using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Company = Game.Company;
using MissionInfo = Game.Info.MissionInfo;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;

namespace SolarExpanseMultiplayer
{
    // What a saved flight's record says the load must put back on the mission it makes (FlightRules, the load rule).
    public sealed class SavedFigures
    {
        public double CostFuel, OptimalCostFuel;
        public int CostType;
        // false: a load plan noted without its record (the host's first load); the mission keeps what the game made
        public bool Recorded = true;
    }

    // A ship a landing would have made, kept by a client until the host's LandingCraft entry (S4, DD-F7).
    public sealed class HeldLanding
    {
        public string Agency, Type;
        public int Body;
        public long Mission;
    }

    // Task 9's FlightRead gives the host this: a game-made plan, read after its CreateFly, as the FlightPlanned entry
    // and its counter rows. Null from Planned = the plan cannot be read (the host faults, never skips it silently).
    public interface IFlightPlanReader
    {
        ActionRequest Planned(PMMissionParameter plan, MissionInfo created, out ApplyNumbers numbers);
    }

    // Everything the flight rules and flight sources remember between one game call and the next, in one object the
    // RuleState owns (so "no loose static state" holds). A load starts it again, except what a load itself fills
    // (the parameters its own mission load queued: see LoadQueued).
    public sealed class FlightState
    {
        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        // The agency whose schedule step the host is running (FlightHost's acceptance); null = none. The three
        // `FlyCompany == Player` tests, the container swap and the buy-fuel popups read it.
        public Company Acting;

        // The parameters an applied entry queued (by object), and those whose creation the host gave up on.
        readonly HashSet<object> expecting = new HashSet<object>(new SameObject());
        readonly HashSet<object> abandoned = new HashSet<object>(new SameObject());

        // The parameters Company.LoadMission queued, with the figures their record saved. Weak, and not cleared by a
        // load: the host's own load fills it before CommandSession.OnHostLoaded resets the rules, and the parameters
        // are only created over the frames after.
        readonly ConditionalWeakTable<object, SavedFigures> loadQueued =
            new ConditionalWeakTable<object, SavedFigures>();

        // A4: the plans M7's end-game copy registered (host).
        readonly HashSet<object> endGame = new HashSet<object>(new SameObject());

        // The host is inside a launch, a stalled-build check or a route's own cancel: the reason of a cancel made
        // there (CancelReasons), or 0. A cancel inside a target-gone or impact call is quiet: that call raises it.
        public int CancelReason;
        int quiet;
        readonly List<KeyValuePair<string, long>> cancelLog = new List<KeyValuePair<string, long>>();
        public bool CancelsAreQuiet { get { return quiet > 0; } }
        // ScheduleNextPriceChange is running (its scheduled time is rewritten out of the last minute)
        public bool PriceChange;

        // A landing is running: the mission whose cargo is being delivered (-1 = none). A client keeps the ship the
        // landing would make; the host reports it.
        public long LandingMission = -1;
        public bool ReleasingLanding;
        public readonly List<HeldLanding> HeldLandings = new List<HeldLanding>();

        // The orbit pull: the host's record of the mission being run, and what a client was given (the 72's Pull).
        public AsteroidPull PullMade;
        public AsteroidPull PullGiven;
        public long PullMission = -1;

        public IFlightPlanReader Reader;
        // Task 9's watch lists hear of every cancel the host's rules make here (target-gone, launch, stalled).
        public Action<long> Cancelled;

        // A host call that cancels missions and raises their entries itself: the cancels it makes are logged here
        // (agency wire id, mission id) in the order they happen, and EndQuiet hands them back.
        public void BeginQuiet()
        {
            if (quiet++ == 0) cancelLog.Clear();
        }

        public KeyValuePair<string, long>[] EndQuiet()
        {
            if (quiet == 0) return new KeyValuePair<string, long>[0];
            if (--quiet > 0) return new KeyValuePair<string, long>[0];
            KeyValuePair<string, long>[] taken = cancelLog.ToArray();
            cancelLog.Clear();
            return taken;
        }

        public void LogCancel(string agency, long mission)
        {
            cancelLog.Add(new KeyValuePair<string, long>(agency, mission));
        }

        // holds are expected every day for every route on a client; the log shows the first few of each kind
        readonly Dictionary<string, int> holds = new Dictionary<string, int>();

        public bool ShouldLogHold(string what)
        {
            int n;
            holds.TryGetValue(what, out n);
            holds[what] = ++n;
            return n <= 3 || n % 500 == 0;
        }

        public void Expect(object plan) { if (plan != null) { abandoned.Remove(plan); expecting.Add(plan); } }
        public bool IsExpecting(object plan) { return plan != null && expecting.Contains(plan); }
        public void Done(object plan) { if (plan != null) { expecting.Remove(plan); abandoned.Remove(plan); } }
        public void Abandon(object plan) { if (plan != null && expecting.Contains(plan)) abandoned.Add(plan); }
        public bool IsAbandoned(object plan) { return plan != null && abandoned.Contains(plan); }
        public int ExpectingCount { get { return expecting.Count; } }

        public void NoteLoadQueued(object plan, SavedFigures figures)
        {
            if (plan == null) return;
            loadQueued.Remove(plan);
            loadQueued.Add(plan, figures ?? new SavedFigures { Recorded = false });
        }

        public bool IsLoadQueued(object plan)
        {
            SavedFigures f;
            return plan != null && loadQueued.TryGetValue(plan, out f);
        }

        // The saved figures of a load-queued plan, once: its CreateFly has run.
        public SavedFigures TakeLoadQueued(object plan)
        {
            SavedFigures f;
            if (plan == null || !loadQueued.TryGetValue(plan, out f)) return null;
            loadQueued.Remove(plan);
            return f;
        }

        public void MarkEndGame(object plan) { if (plan != null) endGame.Add(plan); }
        public bool IsMarkedEndGame(object plan) { return plan != null && endGame.Contains(plan); }
        public void ClearEndGame(object plan) { if (plan != null) endGame.Remove(plan); }

        // FlightLog: the launches and landings already written, and whether this step's day event has run
        public readonly HashSet<long> LaunchLogged = new HashSet<long>(), LandedLogged = new HashSet<long>();
        public bool DayEventRan;

        public void Reset()
        {
            Acting = null;
            expecting.Clear();
            abandoned.Clear();
            endGame.Clear();
            CancelReason = 0;
            quiet = 0;
            cancelLog.Clear();
            holds.Clear();
            PriceChange = false;
            LandingMission = -1;
            ReleasingLanding = false;
            HeldLandings.Clear();
            LaunchLogged.Clear();
            LandedLogged.Clear();
            DayEventRan = false;
            PullMade = null;
            PullGiven = null;
            PullMission = -1;
        }
    }
}
