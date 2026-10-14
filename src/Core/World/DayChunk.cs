using System;

namespace SolarExpanseMultiplayer
{
    // An observatory works in whole-day chunks: ObservatoryFacility.MyUpdate takes off the time since its own last
    // chunk once a day or more has passed, on whichever render frame that is, and counts the next day from that frame.
    // So the chunks of two games fall on different frames and their days left part by up to a day of work on every
    // value day (the "daysToFinish residual" lines), though neither runs ahead in the long run. In a session every
    // game ends its chunks on the day boundaries instead: the first frame of a day takes off the whole days since the
    // last boundary the chunk counted to, which every game computes alike.
    public static class DayChunk
    {
        public const long Day = TimeSpan.TicksPerDay;

        // The time to hand the game as its last chunk, so that its own test and sum see whole days; `after` is the
        // boundary to keep as the last chunk once the game has run (long.MinValue: the game's own time stays). The
        // grid is nextDay's: the current day began at nextDay - Day. A clock outside the current day is left alone.
        // `earliest` is the game's start date: a last chunk before it was never set (see Unset).
        public static long Before(long prev, long now, long nextDay, long earliest, out long after)
        {
            after = long.MinValue;
            long dayStart = nextDay - Day;
            if (Unset(prev, earliest)) return Started(now, nextDay);
            if (now < dayStart || now >= nextDay) return prev;
            long last = Floor(prev, dayStart);
            if (last >= dayStart) return dayStart;   // already counted to today's boundary: no chunk
            long handed = now - (dayStart - last);
            if (handed < 0) return Started(now, nextDay);
            after = dayStart;
            return handed;
        }

        public static long Before(long prev, long now, long nextDay, out long after)
        {
            return Before(prev, now, nextDay, 0, out after);
        }

        // A new observatory's last chunk is DateTime.MinValue: only a load sets it, and the game's own first frame
        // takes the ~737,000 days since year 1 (nothing, while it is idle). A time before the game's start is no
        // chunk either.
        public static bool Unset(long prev, long earliest)
        {
            return prev <= 0 || prev < earliest;
        }

        // A new observatory counts from the boundary its build day began at, on every game whatever its first frame:
        // no chunk on that day, a whole day of work at each boundary after it. A grid that is not set yet (no day
        // before the clock) starts it at the clock, as the game would.
        static long Started(long now, long nextDay)
        {
            if (now < 0) return 0;
            if (nextDay - Day < 0) return now;
            return AfterHostChunk(0, now, nextDay);
        }

        // The last chunk to keep once a client has applied an entry the host ran inside its own chunk (an examine step
        // done, an asteroid found: the game calls them only from MyUpdate, once the chunk has ended the work). The
        // host's chunk counted to the boundary at or before the entry's tick; the client applies at that tick, often
        // before its own first frame of the day, whose chunk would take that day off the fresh work as well. Never
        // earlier than prev (an unset prev is no time: the boundary stands).
        public static long AfterHostChunk(long prev, long now, long nextDay)
        {
            long dayStart = nextDay - Day;
            long boundary = now >= nextDay ? nextDay + (now - nextDay) / Day * Day : Floor(now, dayStart);
            if (boundary < 0) boundary = Math.Max(now, 0);
            return Math.Max(prev, boundary);
        }

        // The latest boundary of the grid at or before t (dayStart itself for t at or past it).
        public static long Floor(long t, long dayStart)
        {
            if (t >= dayStart) return dayStart;
            long behind = dayStart - t;
            long days = behind / Day + (behind % Day == 0 ? 0 : 1);
            return dayStart - days * Day;
        }
    }
}
