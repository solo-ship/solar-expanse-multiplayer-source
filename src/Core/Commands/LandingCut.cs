using System;

namespace SolarExpanseMultiplayer
{
    // Where the Short hold ends its cut step while a landing wait is open: the day gate stops the client 60 game
    // seconds short of nextDay, but the awaited mission's own arrival must not be cut off by that margin. So the cut
    // is the arrival when it falls in the last 60 seconds before nextDay, and nextDay minus 60 seconds otherwise (no
    // date, an earlier date, or one past the boundary).
    public static class LandingCut
    {
        public const long MarginTicks = 60L * TimeSpan.TicksPerSecond;

        public static long Of(long arrivalTicks, long nextDayTicks)
        {
            long usual = nextDayTicks - MarginTicks;
            return arrivalTicks >= usual && arrivalTicks < nextDayTicks ? arrivalTicks : usual;
        }
    }
}
