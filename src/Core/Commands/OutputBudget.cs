using System;

namespace SolarExpanseMultiplayer
{
    // A token bucket on a caller-supplied millisecond clock. The shared arithmetic of the host's and the client's
    // limits, so a client that keeps to its own numbers is, to the last rounding, inside the host's. It starts full. A
    // clock going backwards refills nothing and does not move the reference time, so it cannot be used for credit.
    internal sealed class TokenBucket
    {
        readonly double rate, burst;   // tokens a second; the most it holds
        double tokens;
        long last;
        bool started;

        public TokenBucket(double perSecond, double burst)
        {
            if (perSecond <= 0) throw new ArgumentOutOfRangeException("perSecond", "a rate is above zero");
            if (burst <= 0) throw new ArgumentOutOfRangeException("burst", "a burst is above zero");
            rate = perSecond;
            this.burst = burst;
            tokens = burst;
        }

        void Refill(long nowMs)
        {
            if (started && nowMs > last) tokens = Math.Min(burst, tokens + (nowMs - last) * rate / 1000.0);
            if (!started || nowMs > last) last = nowMs;
            started = true;
        }

        // Room for n tokens now. A request above the burst can never fit, so it counts as the burst (a full bucket
        // takes it) rather than waiting for ever.
        public bool Has(double n, long nowMs)
        {
            Refill(nowMs);
            return tokens >= Math.Min(n, burst);
        }

        // Takes n; the bucket may go into debt (never below minus one burst), which is how reserved traffic is counted.
        public void Take(double n, long nowMs)
        {
            Refill(nowMs);
            tokens = Math.Max(-burst, tokens - n);
        }
    }

    // The host's output to ONE client, every message kind counted (N6). Reserved kinds (Clock, pings, join messages)
    // are Spent: counted and never delayed. Everything else (entries, DayClosed, ObjectData, AgencyState) is Taken
    // only when CanTake says there is room, so the host's output stays under what the client's inbound limiter on its
    // host link takes (ActionLimits.HostOut* against HostLink*) and an honest host never gets a client to cut the link.
    public sealed class OutputBudget
    {
        readonly TokenBucket msgs, bytes;

        public OutputBudget(int msgsPerSecond, int msgBurst, int bytesPerSecond, int byteBurst)
        {
            msgs = new TokenBucket(msgsPerSecond, msgBurst);
            bytes = new TokenBucket(bytesPerSecond, byteBurst);
        }

        // A reserved message of this size was sent: counted, the debt bounded by one burst.
        public void Spend(int byteCount, long nowMs)
        {
            msgs.Take(1, nowMs);
            bytes.Take(byteCount, nowMs);
        }

        // Room for a deferrable message of this size now (one message and its bytes).
        public bool CanTake(int byteCount, long nowMs)
        {
            bool m = msgs.Has(1, nowMs);
            bool b = bytes.Has(byteCount, nowMs);
            return m && b;
        }

        // A deferrable message was sent (after CanTake said yes).
        public void Take(int byteCount, long nowMs) { Spend(byteCount, nowMs); }
    }
}
