using System;

namespace SolarExpanseMultiplayer
{
    // Token buckets for one connection: messages and bytes. Allow() false = over the limit; the caller closes the
    // connection. The clock is passed in (ms), so tests need no waiting.
    public sealed class RateLimiter
    {
        readonly double msgBurst, msgRate, byteBurst, byteRate;
        double msgs, bytes;
        long lastMs;
        bool started;

        public RateLimiter(double msgBurst, double msgsPerSecond, double byteBurst, double bytesPerSecond)
        {
            this.msgBurst = msgBurst; msgRate = msgsPerSecond; this.byteBurst = byteBurst; byteRate = bytesPerSecond;
            msgs = msgBurst; bytes = byteBurst;
        }

        // Generous for M2's traffic (about 6 messages a second each way), tight enough to stop a flood.
        public static RateLimiter Default() { return new RateLimiter(100, 50, 256 * 1024, 64 * 1024); }

        // A client's limiter on its link to the host: the host's output to one client (OutputBudget) can never exceed
        // it, so an honest game is never disconnected for rate (ActionLimits, HostLinkBudgetTests).
        public static RateLimiter HostLink()
        {
            return new RateLimiter(ActionLimits.HostLinkMsgBurst, ActionLimits.HostLinkMsgsPerSecond,
                                   ActionLimits.HostLinkByteBurst, ActionLimits.HostLinkBytesPerSecond);
        }

        public bool Allow(long nowMs, int size)
        {
            if (started && nowMs > lastMs)
            {
                double dt = (nowMs - lastMs) / 1000.0;
                msgs = Math.Min(msgBurst, msgs + dt * msgRate);
                bytes = Math.Min(byteBurst, bytes + dt * byteRate);
            }
            if (!started || nowMs > lastMs) lastMs = nowMs;   // a clock going backwards refills nothing
            started = true;
            if (msgs < 1 || bytes < size) return false;
            msgs -= 1;
            bytes -= size;
            return true;
        }
    }
}
