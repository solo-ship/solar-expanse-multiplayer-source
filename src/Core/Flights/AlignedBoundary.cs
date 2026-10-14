using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Where every game cuts its step near a day boundary, so a watched event lands in the same step on all of them
    // (I-A, I-B, I-C, V2, V4). One function for the host and the clients: a host-only boundary in the last minute would
    // split a landing between games.
    public static class AlignedBoundary
    {
        public const long MarginTicks = 60L * TimeSpan.TicksPerSecond;   // M5's daily cut margin
        // a watched event's cut: one game second before it
        public const long LeadTicks = TimeSpan.TicksPerSecond;

        public static long Cut(long nextDayTicks) { return nextDayTicks - MarginTicks; }

        // Strictly Cut < event < nextDay (M-a): an event exactly at the cut is an ordinary one.
        public static bool InLastMinute(long eventTicks, long nextDayTicks)
        {
            return eventTicks > Cut(nextDayTicks) && eventTicks < nextDayTicks;
        }

        // Where a game cuts before a watched event: the aligned cut for an event in the last minute; nextDay for an
        // event in [nextDay, nextDay + LeadTicks) (never earlier than nextDay for an event at or after it, I-B); the
        // event minus LeadTicks otherwise.
        public static long HoldPoint(long eventTicks, long nextDayTicks)
        {
            if (InLastMinute(eventTicks, nextDayTicks)) return Cut(nextDayTicks);
            // written as a difference so an event near the top of the range cannot overflow
            if (eventTicks >= nextDayTicks && eventTicks - nextDayTicks < LeadTicks) return nextDayTicks;
            return eventTicks - LeadTicks;
        }

        // Where a host-only scheduled action goes in a session: nextDay when Cut < time < nextDay, else unchanged
        // (I-C). A time exactly at the cut adds no step boundary the clients lack, so it stays.
        public static long MoveHostOnly(long timeTicks, long nextDayTicks)
        {
            return InLastMinute(timeTicks, nextDayTicks) ? nextDayTicks : timeTicks;
        }

        // Watched events chained less than LeadTicks apart form one cluster (transitive, V4): the index where each
        // cluster starts, in time order. Every member is cut at its own hold point; only the first waits. The events
        // must be sorted: an unsorted list would give clusters that differ between games, so it throws.
        public static int[] Groups(long[] sortedEventTicks)
        {
            if (sortedEventTicks == null) throw new ArgumentNullException("sortedEventTicks");
            var starts = new List<int>();
            for (int i = 0; i < sortedEventTicks.Length; i++)
            {
                if (i == 0) { starts.Add(0); continue; }
                if (sortedEventTicks[i] < sortedEventTicks[i - 1])
                    throw new ArgumentException("the events are not in time order", "sortedEventTicks");
                // unsigned, so the gap between two far-apart events cannot overflow
                ulong gap = unchecked((ulong)sortedEventTicks[i] - (ulong)sortedEventTicks[i - 1]);
                if (gap >= (ulong)LeadTicks) starts.Add(i);
            }
            return starts.ToArray();
        }
    }
}
