using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What a limiter says about one request: act on it now, keep it for a later TakeDue, or refuse it (the host answers
    // TooMany; the client shows it locally and sends nothing).
    public static class LimitVerdict { public const int Now = 0, Held = 1, Refused = 2; }

    // The host's limits per player (D7): defence in depth, since an honest client's ClientSendBudget keeps it far
    // inside them. "Do" actions pass through a token bucket (DoPerSecond, bursts of DoBurst) and are refused beyond
    // it, never queued. "Set" actions say what a value should be, so only the latest per target (the set key) matters:
    // at most SetPerSecond of them are acted on in all, the rest wait here, newer replacing older of the same key, up
    // to maxHeldSetKeys distinct keys (then Refused). Time is plain milliseconds from any monotonic clock.
    public sealed class ActionLimiter
    {
        sealed class Slot
        {
            public TokenBucket Do;
            public bool HasActed;
            public long ActedMs;                            // the last set action acted on
            public readonly List<string> Order = new List<string>();   // held keys, oldest first
            public readonly Dictionary<string, ActionRequest> Held =
                new Dictionary<string, ActionRequest>(StringComparer.Ordinal);
        }

        readonly int doPerSecond, doBurst, maxHeldSetKeys;
        readonly long setWindowMs;
        readonly Dictionary<int, Slot> slots = new Dictionary<int, Slot>();

        public ActionLimiter(int setPerSecond, int doPerSecond, int doBurst, int maxHeldSetKeys)
        {
            if (setPerSecond < 1) throw new ArgumentOutOfRangeException("setPerSecond");
            if (maxHeldSetKeys < 0) throw new ArgumentOutOfRangeException("maxHeldSetKeys");
            this.doPerSecond = doPerSecond;
            this.doBurst = doBurst;
            this.maxHeldSetKeys = maxHeldSetKeys;
            setWindowMs = (1000 + setPerSecond - 1) / setPerSecond;
            new TokenBucket(doPerSecond, doBurst);   // the numbers are checked here, not on a player's first request
        }

        Slot SlotOf(int player)
        {
            Slot s;
            if (!slots.TryGetValue(player, out s))
            {
                s = new Slot { Do = new TokenBucket(doPerSecond, doBurst) };
                slots[player] = s;
            }
            return s;
        }

        // A request arrived from this player. superseded is the held set request this one replaced (the host answers
        // it Superseded), else null.
        public int Offer(int player, ActionRequest a, long nowMs, out ActionRequest superseded)
        {
            if (a == null) throw new ArgumentNullException("a");
            superseded = null;
            Slot s = SlotOf(player);
            string key = ActionShapes.Key(a);
            if (key == null)
            {
                if (!s.Do.Has(1, nowMs)) return LimitVerdict.Refused;
                s.Do.Take(1, nowMs);
                return LimitVerdict.Now;
            }
            ActionRequest old;
            if (s.Held.TryGetValue(key, out old))
            {
                s.Held[key] = a;
                superseded = old;
                return LimitVerdict.Held;
            }
            if (s.Order.Count == 0 && WindowOpen(s, nowMs))
            {
                Acted(s, nowMs);
                return LimitVerdict.Now;
            }
            if (s.Order.Count >= maxHeldSetKeys) return LimitVerdict.Refused;
            s.Held[key] = a;
            s.Order.Add(key);
            return LimitVerdict.Held;
        }

        bool WindowOpen(Slot s, long nowMs) { return !s.HasActed || nowMs - s.ActedMs >= setWindowMs; }

        static void Acted(Slot s, long nowMs)
        {
            s.HasActed = true;
            s.ActedMs = nowMs;
        }

        // From the owner's frame: the oldest held set request, once the window since the last acted one has passed
        // (one per window), else null. Call until it returns null.
        public ActionRequest TakeDue(int player, long nowMs)
        {
            Slot s;
            if (!slots.TryGetValue(player, out s) || s.Order.Count == 0 || !WindowOpen(s, nowMs)) return null;
            string key = s.Order[0];
            ActionRequest a = s.Held[key];
            s.Order.RemoveAt(0);
            s.Held.Remove(key);
            Acted(s, nowMs);
            return a;
        }

        // The link was replaced or closed: its buckets and held requests go (the host answers none of them).
        public void Forget(int player) { slots.Remove(player); }
    }
}
