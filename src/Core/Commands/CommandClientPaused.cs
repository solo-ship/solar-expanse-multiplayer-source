using System;

namespace SolarExpanseMultiplayer
{
    // The host's pause as this client follows it (PausedLanding): its last Clock stood still, so its date is where this
    // game may run to, entries and all.
    public sealed partial class CommandClient
    {
        bool hostPaused;

        // Where this game runs to while the host is paused (PausedLanding.Target), or 0: the host runs, its clock is
        // not known yet, this game is outside the stream, or a catch-up after a drop has the clock (CatchUpTo).
        public long PausedLandingTo()
        {
            if (!hostPaused || !inStream || outOfStep || suspended || catchingUp || hostTicksSeen == long.MinValue)
                return 0;
            long day;
            if (!LocalDayNow(out day)) return 0;
            // the host closed this game's day: it stands on or past the boundary
            bool closed = gate.Disabled || order.StartedAfter(day) || order.ClosedFor(day) != null;
            return PausedLanding.Target(hostTicksSeen, world.NextDayTicks, closed);
        }

        // Game seconds this game still has to run to stand at the paused host's date; 0 when there is nothing to run.
        public double PausedLandingLeft()
        {
            long to = PausedLandingTo();
            if (to == 0) return 0;
            long now = world.GameTicks;
            if (!PausedLanding.Due(now, to, world.NextDayTicks)) return 0;
            return (to - now) / (double)TimeSpan.TicksPerSecond;
        }
    }
}
