using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // SECURITY.md: no one can flood the host. A player's speed-ups are acted on at most 4 a second (one per WindowMs);
    // while the window is closed the latest request is held, and an older held one is dropped (counted). The held
    // request is acted on when its window has passed (Due), so a newer Choice still counts, just later.
    // A slow-down or pause (a request that lets the session run slower than this player's last acted one) is acted on
    // at once, replacing anything held: the requesting client has already slowed, and every millisecond the host runs
    // on at the old speed is game time that client must later catch up (at 200000, one Wi-Fi click held 190 ms put it
    // 57 s at 1x behind). The flood bound still holds: each at-once act must lower the speed that player's acted
    // request allows, which can happen at most 5 times (6 listed speeds) before a speed-up, so any WindowMs holds at
    // most MaxActsPerWindow acts (24 a second).
    // A request made by the player (a click) must not be lost to a game call that follows it inside the window: the held
    // request keeps the speed, planner flag and Choice of the latest, and is flagged Player if any of them was, so a
    // player's choice still ends a drop hold when it is applied. Times are plain milliseconds from any monotonic clock.
    public sealed class RequestCoalescer
    {
        public const long WindowMs = ClockRules.HeartbeatMs;   // 250 ms: 4 a second
        public const long ReportEveryMs = 1000;                // at most one "requests coalesced" line a second per player
        public const int MaxActsPerWindow = 6;                 // 1 in its window + 5 slow-downs, see above

        sealed class Slot
        {
            public bool HasActed;
            public long ActedMs;
            public SpeedRequest Held;
            public float Allowed;         // the speed the last acted request lets the session run at (0 when planning)
            public int Replaced;          // held requests replaced by a newer one since the last report
            public long ReportedMs = long.MinValue / 2;
        }

        readonly Dictionary<int, Slot> slots = new Dictionary<int, Slot>();

        // A request arrived from this player. Returns it when it may be acted on now; null when it was held for later.
        public SpeedRequest Offer(int player, SpeedRequest r, long nowMs)
        {
            Slot s;
            if (!slots.TryGetValue(player, out s)) { s = new Slot(); slots[player] = s; }
            bool slower = s.HasActed && Allows(r) < s.Allowed;
            if (slower || (s.Held == null && (!s.HasActed || nowMs - s.ActedMs >= WindowMs)))
            {
                if (s.Held != null) Replace(s, r);
                s.Held = null;
                Acted(s, r, nowMs);
                return r;
            }
            if (s.Held != null) Replace(s, r);
            s.Held = r;
            return null;
        }

        static float Allows(SpeedRequest r) { return r.Planning ? 0f : r.Scale; }

        // The held request goes for a newer one; the player's click survives a later game call.
        static void Replace(Slot s, SpeedRequest newer)
        {
            if (s.Held.Player) newer.Player = true;
            s.Replaced++;
        }

        static void Acted(Slot s, SpeedRequest r, long nowMs)
        {
            s.HasActed = true;
            s.ActedMs = nowMs;
            s.Allowed = Allows(r);
        }

        // From the owner's frame: a held request whose window has passed (one per call, any player), else null.
        public SpeedRequest Due(long nowMs, out int player)
        {
            foreach (KeyValuePair<int, Slot> kv in slots)
            {
                Slot s = kv.Value;
                if (s.Held == null || nowMs - s.ActedMs < WindowMs) continue;
                SpeedRequest r = s.Held;
                s.Held = null;
                Acted(s, r, nowMs);
                player = kv.Key;
                return r;
            }
            player = 0;
            return null;
        }

        // How many of this player's requests were dropped for a newer one, at most once a second per player; false when
        // there is nothing to say (or it was said less than a second ago).
        public bool NextReport(long nowMs, out int player, out int count)
        {
            foreach (KeyValuePair<int, Slot> kv in slots)
            {
                Slot s = kv.Value;
                if (s.Replaced == 0 || nowMs - s.ReportedMs < ReportEveryMs) continue;
                player = kv.Key;
                count = s.Replaced;
                s.Replaced = 0;
                s.ReportedMs = nowMs;
                return true;
            }
            player = 0;
            count = 0;
            return false;
        }

        public void Forget(int player) { slots.Remove(player); }
    }
}
