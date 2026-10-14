using System;

namespace SolarExpanseMultiplayer
{
    // The day number both sides stamp on a stream entry: the host's own day boundaries are copied by every joiner
    // (M4), so nextDay on any game is a whole number of days from the host's origin.
    public static class DayKey
    {
        // The day that ends at nextDay: whole days from the host's origin to nextDay, minus one (D 88904). Day 0 ends
        // at origin + 1 day. A nextDay that is not a whole number of days from the origin, is not after it, or gives
        // a day no message may carry has no key: such a key would only be a different number on two games.
        // Game paths use TryCurrent: a failure there is OutOfStep on a client and a new stream on the host, never a
        // throw inside the fixed step.
        public static bool TryCurrent(long nextDayTicks, long originTicks, out long day)
        {
            return Check(nextDayTicks, originTicks, out day) == null;
        }

        public static long Current(long nextDayTicks, long originTicks)
        {
            long day;
            string why = Check(nextDayTicks, originTicks, out day);
            if (why != null) throw new ArgumentException(why);
            return day;
        }

        static string Check(long nextDayTicks, long originTicks, out long day)
        {
            day = 0;
            if (nextDayTicks <= originTicks) return "nextDay is not after the origin";
            // unsigned, so the distance cannot overflow even between the two extremes of long
            ulong span = unchecked((ulong)nextDayTicks - (ulong)originTicks);
            if (span % TimeSpan.TicksPerDay != 0) return "nextDay is not a whole day from the origin";
            ulong d = span / TimeSpan.TicksPerDay - 1;
            if (d > (ulong)ActionLimits.MaxDay) return "the day is past " + ActionLimits.MaxDay;
            day = (long)d;
            return null;
        }
    }
}
