using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The client applies the host's own numbers before it sends (I3), so an honest player never reaches the host's
    // ActionLimiter or its per-connection RateLimiter. A "do" is sent now or refused locally (TooMany, nothing sent).
    // A "set" is always kept as the latest for its key and released by TakeDue, at most SetPerSecond in all, oldest key
    // first, so a mouse drag of 144 values a second leaves four and the last value is never lost. Bytes (the encoded
    // size of what is sent) are held under ClientBytesPerSecond for both. Time is plain milliseconds.
    public sealed class ClientSendBudget
    {
        sealed class Held { public ActionRequest Request; public int Bytes; }
        sealed class Sent { public int Value; public int Outstanding; }

        // The client keeps to this share of the host's do rate and set rate, and to DoBurstShare of its do burst.
        // The host sees each request after a network delay that varies, so requests the client spaced out can reach
        // it bunched together (a stall, then everything at once). A bunch of T seconds is at most client burst +
        // 0.9 x 5 x T requests, and the host takes 20 at once: with a burst of 10 that holds for any T up to about
        // 2.2 s with no refusal. A longer stall can get a do refused (TooMany; the link stays up and the player
        // sees a notice), never a disconnect. Sets are held, not refused, by the host; see KeysInFlight.
        public const double HostMargin = 0.9;
        public const double DoBurstShare = 0.5;

        readonly int maxSetKeys;
        readonly long setWindowMs;
        readonly double doPerSecond, doBurst;
        readonly int bytesPerSecond, bytesBurst;
        readonly Dictionary<string, Sent> sent = new Dictionary<string, Sent>(StringComparer.Ordinal);
        TokenBucket doBucket, byteBucket;
        bool hasActed;
        long actedMs;                                       // the last set action released
        readonly List<string> order = new List<string>();   // waiting set keys, oldest first
        readonly Dictionary<string, Held> held = new Dictionary<string, Held>(StringComparer.Ordinal);

        public ClientSendBudget(int setPerSecond, int doPerSecond, int doBurst, int bytesPerSecond, int bytesBurst,
                                int maxSetKeys)
        {
            if (setPerSecond < 1) throw new ArgumentOutOfRangeException("setPerSecond");
            if (maxSetKeys < 0) throw new ArgumentOutOfRangeException("maxSetKeys");
            this.doPerSecond = doPerSecond * HostMargin;
            this.doBurst = doBurst * DoBurstShare;
            this.bytesPerSecond = bytesPerSecond;
            this.bytesBurst = bytesBurst;
            this.maxSetKeys = maxSetKeys;
            setWindowMs = (long)Math.Ceiling(1000.0 / (setPerSecond * HostMargin));
            Reset();
        }

        void Reset()
        {
            doBucket = new TokenBucket(doPerSecond, doBurst);
            byteBucket = new TokenBucket(bytesPerSecond, bytesBurst);
            hasActed = false;
            order.Clear();
            held.Clear();
            sent.Clear();
        }

        // Do: Now (the budget is spent; send it) or Refused (a local TooMany; nothing is spent and nothing is sent).
        // Set: Held, replacing any waiting request for the same key, or Refused for a new key past maxSetKeys.
        public int Offer(ActionRequest a, int encodedBytes, long nowMs)
        {
            if (a == null) throw new ArgumentNullException("a");
            // a message above the burst could never pass the host's limits whatever the wait
            if (encodedBytes > bytesBurst) return LimitVerdict.Refused;
            string key = ActionShapes.Key(a);
            if (key == null)
            {
                if (!doBucket.Has(1, nowMs) || !byteBucket.Has(encodedBytes, nowMs)) return LimitVerdict.Refused;
                doBucket.Take(1, nowMs);
                byteBucket.Take(encodedBytes, nowMs);
                return LimitVerdict.Now;
            }
            Held h;
            if (held.TryGetValue(key, out h))
            {
                h.Request = a;
                h.Bytes = encodedBytes;
                return LimitVerdict.Held;
            }
            if (!sent.ContainsKey(key) && KeysInFlight() >= maxSetKeys) return LimitVerdict.Refused;
            held[key] = new Held { Request = a, Bytes = encodedBytes };
            order.Add(key);
            return LimitVerdict.Held;
        }

        // Distinct set keys the host may be holding for this client: the waiting ones and the sent ones not yet
        // answered. The host holds at most the same number (MaxHeldSetKeys), so past it a new key is refused here and
        // never reaches the host's limit, however long the network stalls.
        int KeysInFlight()
        {
            int n = order.Count;
            foreach (string k in sent.Keys)
                if (!held.ContainsKey(k)) n++;
            return n;
        }

        // From the owner's frame: the oldest waiting set request once the window since the last one has passed and the
        // byte budget has room for it, else null. The caller sends what it returns.
        public ActionRequest TakeDue(long nowMs)
        {
            if (order.Count == 0) return null;
            if (hasActed && nowMs - actedMs < setWindowMs) return null;
            string key = order[0];
            Held h = held[key];
            if (!byteBucket.Has(h.Bytes, nowMs)) return null;
            byteBucket.Take(h.Bytes, nowMs);
            order.RemoveAt(0);
            held.Remove(key);
            Sent s;
            if (!sent.TryGetValue(key, out s)) sent[key] = s = new Sent();
            s.Value = h.Request.Value;
            s.Outstanding++;
            hasActed = true;
            actedMs = nowMs;
            return h.Request;
        }

        // The latest set value for this key that is waiting to be sent or was sent and not yet answered, so the window
        // the player is dragging keeps showing it (DD1).
        public bool TryPendingValue(string key, out int value)
        {
            Held h;
            Sent s;
            value = 0;
            if (key == null) return false;
            if (held.TryGetValue(key, out h)) { value = h.Request.Value; return true; }
            if (sent.TryGetValue(key, out s)) { value = s.Value; return true; }
            return false;
        }

        // The host's result or refusal for a request of this key arrived (one call per answer). When none of the key's
        // sent requests is left unanswered, a sent value stops being pending; a newer waiting value still is.
        public void Answered(string key)
        {
            Sent s;
            if (key == null || !sent.TryGetValue(key, out s)) return;
            if (--s.Outstanding <= 0) sent.Remove(key);
        }

        // The link was lost: nothing waiting will be answered, and the next link starts with a full budget, as the
        // host's limiter does for it.
        public void Clear() { Reset(); }
    }
}
