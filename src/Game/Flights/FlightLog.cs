using System;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using Spacecraft = CustomUpdate.Spacecraft;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The four flight lines tools\command-compare.ps1 compares between the host's log and a client's (plan Task 13):
    //   flight: ids #<seq> sc <n> max <id> lv <n>        after each flight entry: the ship counters and the highest id
    //   flight: launched <mission> at <ticks>            a mission's launch, once
    //   flight: landed <mission> at <ticks> day <d> before|after day event   a landing, once, and which side of the
    //                                                    day event of its step it fell
    //   flight: step end <ticks> day <d>                 the end of each day-event step
    // Every game writes them from its own world, so a split between games shows as lines that differ. Nothing here
    // changes the world; all of it is a no-op outside a session.
    internal static class FlightLog
    {
        static readonly HarmonyLib.AccessTools.FieldRef<TimeController, DateTime> NextDayOf =
            HarmonyLib.AccessTools.FieldRefAccess<TimeController, DateTime>("nextDay");

        static FlightState State
        {
            get { RuleState st = HumanAgencies.State; return st != null ? st.Flights : null; }
        }

        static long Origin
        {
            get
            {
                CommandSession s = Session.Commands;
                return s != null && s.FlightRead != null && s.FlightRead.OriginTicks != null
                    ? s.FlightRead.OriginTicks() : 0;
            }
        }

        // The day the game is in, as the session counts it (the day key of nextDay), or -1 before there is one.
        static long Day(TimeController tc)
        {
            long day;
            return DayKey.TryCurrent(NextDayOf(tc).Ticks, Origin, out day) ? day : -1;
        }

        public static void Ids(ActionResult r)
        {
            if (!HumanAgencies.Active || r == null || r.Action == null) return;
            int kind = r.Action.Kind;
            if (!FlightApply.Handles(kind) && !FlightClientEntries.Handles(kind)) return;
            int max = -1;
            foreach (Spacecraft s in ShipManager.Instance.ListAllSpaceShip) if (s != null && s.ID > max) max = s.ID;
            Log.Info("flight: ids #" + r.Seq + " sc " + Spacecraft.count + " max " + max + " lv " + LaunchVehicle.count);
        }

        public static void Launched(MissionInfo mi)
        {
            FlightState f = State;
            if (f == null || mi == null || !f.LaunchLogged.Add(mi.id)) return;
            Log.Info("flight: launched " + mi.id + " at " + mi.DateLaunch.Ticks);
        }

        public static void Landed(Spacecraft ship)
        {
            FlightState f = State;
            if (f == null || ship == null) return;
            MissionInfo mi = ship.GetMissionInfo();
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            if (mi == null || tc == null || !f.LandedLogged.Add(mi.id)) return;
            Log.Info("flight: landed " + mi.id + " at " + mi.DateArrive.Ticks + " day " + Day(tc)
                + (f.DayEventRan ? " after day event" : " before day event"));
        }

        public static void StepStarted()
        {
            FlightState f = State;
            if (f != null) f.DayEventRan = false;
        }

        public static long NextDayTicks(TimeController tc) { return NextDayOf(tc).Ticks; }

        // After DoThePeriodicEvents, which runs every fixed step: only a step whose day event ran (nextDay moved on
        // from nextDayBefore) writes the line.
        public static void DayEventDone(TimeController tc, long nextDayBefore)
        {
            FlightState f = State;
            if (f == null || NextDayOf(tc).Ticks == nextDayBefore) return;
            f.DayEventRan = true;
            Log.Info("flight: step end " + tc.CurrentTime.Ticks + " day " + Day(tc));
        }
    }
}
