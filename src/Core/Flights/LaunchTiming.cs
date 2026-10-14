using System;

namespace SolarExpanseMultiplayer
{
    // When a shared flight may leave (DD-F5). Every client applies an entry of day N before it may cross N, so a launch
    // inside that day could already be past on a client: the launch must be at or after the day's end.
    public static class LaunchTiming
    {
        // A game day and an hour past the planning game's boundary (Y-6; F3 may raise it): a client plans up to a day
        // behind the host's clock.
        public const long PadTicks = 25L * 3600L * TimeSpan.TicksPerSecond;

        // A code plan's pad covers this many real seconds at the game's speed: plan 3.4 s + copy + lag + a hitch
        // (plan revision 16). ActionLimits.PlanPadSeconds.

        // A flight entry stamped with the day that ends at nextDayTicks may launch only at or after nextDayTicks.
        public static bool TooClose(long launchTicks, long nextDayTicks)
        {
            return launchTicks < nextDayTicks;
        }

        // The earliest departure a session planner may offer: at least nextDayTicks + PadTicks, and never earlier than
        // the game's own earliest.
        public static long PadDeparture(long earliestTicks, long nextDayTicks)
        {
            return Math.Max(earliestTicks, Add(nextDayTicks, PadTicks));
        }

        // The same for a code plan, whose pad also covers PlanPadSeconds of real time at the current speed
        // (game seconds per real second): the larger of the two pads. A speed that is not a positive number adds
        // nothing.
        public static long PadDeparture(long earliestTicks, long nextDayTicks, double gameSecondsPerRealSecond)
        {
            long pad = PadTicks;
            double code = gameSecondsPerRealSecond * ActionLimits.PlanPadSeconds * TimeSpan.TicksPerSecond;
            // a quarter of the range keeps the sum below from overflowing for any realistic nextDay
            if (code > long.MaxValue / 4) code = long.MaxValue / 4;
            if (code > pad) pad = (long)code;
            return Math.Max(earliestTicks, Add(nextDayTicks, pad));
        }

        // a + b for b >= 0, stopping at long.MaxValue
        static long Add(long a, long b)
        {
            return a > long.MaxValue - b ? long.MaxValue : a + b;
        }
    }
}
