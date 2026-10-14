using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The list behind LaunchWatch and the plain cuts: mission id -> the moment a game must cut for it. Every game feeds
    // it from the same missions, so every game finds the same earliest event; a tie goes to the lower mission id, never
    // to insertion order (which differs between games).
    public sealed class MissionWatch
    {
        readonly Dictionary<long, long> events = new Dictionary<long, long>();

        public void Clear() { events.Clear(); }

        // Adding an id again replaces its event (a mission moved).
        public void Add(long missionId, long eventTicks)
        {
            if (missionId < 0) throw new ArgumentOutOfRangeException("missionId", "a mission id is 0 or more");
            events[missionId] = eventTicks;
        }

        public void Remove(long missionId) { events.Remove(missionId); }

        public bool Contains(long missionId) { return events.ContainsKey(missionId); }

        public long NextTicks
        {
            get
            {
                long ticks, id;
                return Earliest(out ticks, out id) ? ticks : long.MaxValue;
            }
        }

        public long NextMission
        {
            get
            {
                long ticks, id;
                return Earliest(out ticks, out id) ? id : -1;
            }
        }

        public int Count { get { return events.Count; } }

        // The events as parallel arrays, in no order: how many were copied (at most what the buffers hold). The
        // client's cut walks them in time order.
        public int CopyTo(long[] ids, long[] ticks)
        {
            int n = 0;
            foreach (KeyValuePair<long, long> e in events)
            {
                if (n >= ids.Length || n >= ticks.Length) break;
                ids[n] = e.Key;
                ticks[n] = e.Value;
                n++;
            }
            return n;
        }

        bool Earliest(out long ticks, out long id)
        {
            ticks = long.MaxValue; id = -1;
            bool any = false;
            foreach (KeyValuePair<long, long> e in events)
            {
                if (!any || e.Value < ticks || (e.Value == ticks && e.Key < id))
                {
                    ticks = e.Value; id = e.Key; any = true;
                }
            }
            return any;
        }
    }
}
