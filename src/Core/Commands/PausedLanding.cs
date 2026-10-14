using System;

namespace SolarExpanseMultiplayer
{
    // A client behind a paused host runs on to the host's paused date, so what the host applied there (a player's
    // pick, build or flight made during the pause) shows at once rather than when time runs again. The drift controller
    // stops short of a paused host (its 0.1 s band at 1x is some 500 game s), and a host most often pauses exactly on a
    // day boundary (its steps end there), so the client was left short of a day whose entries were already in. The run
    // never passes the host's date (the step ends there: StepEnd, Parked) and never a boundary the host has not closed
    // (it ends at the day point, where the gate would hold it anyway). The clock (ClockClient) sets the speed, the
    // fixed step (StepGate) the end; CommandClient.PausedLandingTo / PausedLandingLeft give both the same target.
    public static class PausedLanding
    {
        // Close enough to a mid-day date: the Clock's date is a double, a few hundred ticks coarse, and entries apply
        // by day, not by instant.
        public const long ReachTicks = TimeSpan.TicksPerSecond / 2;
        // The run takes about this long (real seconds), at no less than the 1x unit.
        public const double RunSeconds = 0.5;

        // Where this game stands while the host is paused: the host's date, but no further than its day point while
        // the host has not closed the day, and at least its boundary once it has (the Clock's date of a host standing
        // on the boundary can read a few ticks short of it).
        public static long Target(long hostTicks, long nextDay, bool dayClosed)
        {
            if (dayClosed) return Math.Max(hostTicks, nextDay);
            return Math.Min(hostTicks, AlignedBoundary.Cut(nextDay));
        }

        // Still to run: a crossing the host has made, however close, or more than ReachTicks of the day.
        public static bool Due(long now, long target, long nextDay)
        {
            return target > now && (target >= nextDay || target - now > ReachTicks);
        }

        // The speed that covers leftSeconds of game time in about RunSeconds, at ratePer1x game seconds per real
        // second at speed 1000: at least that unit, at most the top speed. The step's end, not the speed, keeps it
        // from passing.
        public static float Speed(double leftSeconds, double ratePer1x)
        {
            double rate = ratePer1x > 0 ? ratePer1x : DriftController.DefaultRatePer1x;
            double s = DriftController.BaseScale * leftSeconds / rate / RunSeconds;
            return (float)Math.Min(DriftController.MaxScale, Math.Max(DriftController.BaseScale, s));
        }

        // The step's end against a paused host's target `to` (0: the host runs, the step is whole). The client's own
        // cut (ClientCut) is decided on this end, so a step ending short of the day point is no approach to it.
        public static long StepEnd(long end, long to) { return to == 0 ? end : Math.Min(end, to); }

        // Standing at (or past) the paused host's target: the step is dropped.
        public static bool Parked(long now, long to) { return to != 0 && now >= to; }
    }
}
