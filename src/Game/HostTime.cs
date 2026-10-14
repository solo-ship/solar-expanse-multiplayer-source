using System;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The host's clock facts for the join: the date, the six private day/month/year boundaries of TimeController
    // (the game's TimeController), the origin (the day-0 key: lastDay at the host's own load, which is the load moment) and
    // the number of loads so far. One instance, owned by Session; OnSaveLoaded runs at every load (it is the explicit
    // reset per load). Main thread.
    public sealed class HostTime
    {
        readonly StateField<TimeController, DateTime> lastDay = new StateField<TimeController, DateTime>("lastDay"),
            nextDay = new StateField<TimeController, DateTime>("nextDay"),
            lastMonth = new StateField<TimeController, DateTime>("lastMonth"),
            nextMonth = new StateField<TimeController, DateTime>("nextMonth"),
            lastYear = new StateField<TimeController, DateTime>("lastYear"),
            nextYear = new StateField<TimeController, DateTime>("nextYear");

        // 0 until a load was seen (no origin: no pause at a day, no header)
        public long OriginTicks { get; private set; }
        public int LoadCount { get; private set; }

        public bool FieldsOk { get { return lastDay.Ok && nextDay.Ok && lastMonth.Ok && nextMonth.Ok && lastYear.Ok
            && nextYear.Ok; } }

        static TimeController Tc { get { return MonoBehaviourSingleton<TimeController>.Instance; } }

        // Inside the game's save-loaded event: lastDay is the load moment then, and day N's key is origin + N days.
        public void OnSaveLoaded()
        {
            LoadCount++;
            TimeController tc = Tc;
            OriginTicks = tc != null && lastDay.Ok ? lastDay.Get(tc).Ticks : 0L;
        }

        // Throw when no game is loaded (CurrentTime reads GravityEngine): the callers turn that into their safe answer.
        public long NowTicks { get { return Tc.CurrentTime.Ticks; } }
        public long NextDay { get { return nextDay.Get(Tc).Ticks; } }

        // The year event runs in the day event that crosses nextDay when nextYear is not after it (D 88912-88917).
        public bool YearEventDue(long nextDayTicks) { return nextYear.Get(Tc).Ticks <= nextDayTicks; }

        // Dev trigger yearnow: the next day event runs the year event too (nextYear is then one of the day keys, as it
        // always is).
        public bool MoveYearToNextDay()
        {
            if (!nextYear.Ok || !nextDay.Ok) return false;
            nextYear.Set(Tc, new DateTime(nextDay.Get(Tc).Ticks));
            return true;
        }

        // The six boundaries into a header; false (and nothing written) when a field is missing in this build.
        public bool Fill(JoinHeader h)
        {
            if (!FieldsOk) return false;
            TimeController tc = Tc;
            h.LastDay = lastDay.Get(tc).Ticks; h.NextDay = nextDay.Get(tc).Ticks;
            h.LastMonth = lastMonth.Get(tc).Ticks; h.NextMonth = nextMonth.Get(tc).Ticks;
            h.LastYear = lastYear.Get(tc).Ticks; h.NextYear = nextYear.Get(tc).Ticks;
            return true;
        }
    }

    // Drives Core's PauseAtDay in the game (the -semp-pause-at-day flag on a host or a solo game, and the host's dev
    // trigger "pause-at-day <N>"): the step-down and the pause are asked the way a click asks (ClockSync.PlayerSets),
    // so on a host they are the host player's own choices. Owned by Session; Update once a frame (no allocation unless
    // it acts).
    public sealed class HostPauseDriver
    {
        readonly PauseAtDay rule = new PauseAtDay();
        readonly HostTime time;

        public HostPauseDriver(HostTime time) { this.time = time; }

        public void Arm(int day, string from)
        {
            if (!rule.Arm(day)) { Log.Warn("join: pause at day " + day + " not armed (" + from
                + "): the day must be 1..800"); return; }
            Log.Info("join: pause at day " + day + " armed (" + from + "; origin " + time.OriginTicks + ", target " +
                     PauseAtDay.TargetTicks(time.OriginTicks, day) + ")");
        }

        public void Update()
        {
            if (!rule.Armed || !ClockSync.Ready) return;
            long now = time.NowTicks;
            switch (rule.Step(time.OriginTicks, now, ClockSync.Scale, NetClock.NowMs))
            {
                case PauseStep.StepDown:
                    Log.Info("join: pause at day " + rule.Day + ": one day before, the speed steps down to "
                        + PauseAtDay.StepDownSpeed);
                    ClockSync.PlayerSets(PauseAtDay.StepDownSpeed);
                    break;
                case PauseStep.Pause:
                    Log.Info("join: pause at day " + rule.Day + ": pausing at " + now);
                    ClockSync.PlayerSets(0f);
                    break;
                case PauseStep.Paused:
                    Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "join: paused at day {0} (game time {1}, day fraction {2:0.####})", rule.Day, now,
                        BoundaryRules.DayFraction(now, time.OriginTicks)));
                    break;
                case PauseStep.Passed:
                    Log.Warn("join: pause at day " + rule.Day + ": day " + rule.Day
                        + " + 0.4 is already behind (game time " + now +
                             "); nothing is armed");
                    break;
            }
        }
    }

    // "join: day event <k> boundary <ticks> at <gameTicks>" for the first 10 day events after each arming (a batch on
    // the host, the join's load on a joiner), which join-compare.ps1 compares between the two games. A handler on the
    // game's own onEachDayChange (no Harmony patch, so the hook counts stay as they are): it runs inside the day step,
    // where nextDay is still the boundary being crossed and CurrentTime is that fixed step's time. It never changes
    // game state. Owned by Session; Attach after every load (removing first, so it is on exactly once).
    public sealed class DayEventLog
    {
        const int PerArm = 10;
        readonly StateField<TimeController, DateTime> nextDay = new StateField<TimeController, DateTime>("nextDay");
        Action<double> handler;
        TimeController attached;
        int left, count;

        public void Attach()
        {
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            if (tc == null) return;
            if (handler == null) handler = OnDay;
            if (attached != null && !ReferenceEquals(attached, tc)) attached.onEachDayChange -= handler;
            tc.onEachDayChange -= handler;
            tc.onEachDayChange += handler;
            attached = tc;
        }

        public void Arm() { left = PerArm; count = 0; }

        void OnDay(double elapsedDays)
        {
            if (left <= 0) return;
            try
            {
                TimeController tc = attached;
                long key = nextDay.Get(tc).Ticks, at = tc.CurrentTime.Ticks;
                left--;
                count++;
                Log.Info("join: day event " + count + " boundary " + key + " at " + at);
            }
            catch (Exception) { left = 0; }   // never into the game's day step
        }
    }
}
