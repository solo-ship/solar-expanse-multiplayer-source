using System;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // Drift numbers for the log: once a period, the mean and worst gap and how much of the time the client spent
    // catching up or holding. OutOfStep says when the games have been apart for a while despite the corrections,
    // so drift is never silent.
    public sealed class DriftStats
    {
        public const double WarnSeconds = 5.0;
        public const long WarnAfterMs = 10000;
        readonly long periodMs;
        long startMs = -1, lastMs, catchUpMs, holdMs, apartSinceMs = -1;
        int n;
        double sum, worst;
        ClockMode lastMode = ClockMode.Follow;

        public DriftStats(long periodMs) { this.periodMs = periodMs; }

        // A summary line once per period, otherwise null.
        public string Add(long nowMs, double gapSeconds, ClockMode mode)
        {
            if (startMs < 0) startMs = lastMs = nowMs;
            long dt = Math.Max(0, nowMs - lastMs);
            lastMs = nowMs;
            if (lastMode == ClockMode.CatchUp) catchUpMs += dt;
            else if (lastMode == ClockMode.Hold) holdMs += dt;
            lastMode = mode;
            if (!double.IsNaN(gapSeconds))
            {
                n++;
                sum += gapSeconds;
                if (Math.Abs(gapSeconds) > Math.Abs(worst)) worst = gapSeconds;
                if (Math.Abs(gapSeconds) > WarnSeconds) { if (apartSinceMs < 0) apartSinceMs = nowMs; }
                else apartSinceMs = -1;
            }
            long span = nowMs - startMs;
            if (span < periodMs) return null;
            string line = string.Format(CultureInfo.InvariantCulture,
                "clock: {0} samples in {1:0} s; gap mean {2:+0.000;-0.000;0.000} s, worst {3:+0.000;-0.000;0.000} s; catching up {4:0}%, holding {5:0}%",
                n, span / 1000.0, n > 0 ? sum / n : 0.0, worst, 100.0 * catchUpMs / span, 100.0 * holdMs / span);
            startMs = nowMs; n = 0; sum = 0; worst = 0; catchUpMs = 0; holdMs = 0;
            return line;
        }

        // True once the gap has stayed over WarnSeconds for WarnAfterMs.
        public bool OutOfStep(long nowMs) { return apartSinceMs >= 0 && nowMs - apartSinceMs >= WarnAfterMs; }
    }
}
