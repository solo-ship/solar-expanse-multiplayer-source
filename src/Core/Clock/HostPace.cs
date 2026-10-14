using System;

namespace SolarExpanseMultiplayer
{
    // The host's achieved pace (game seconds per real second) from its Clock stream: the date each Clock carries
    // against the host's own clock when it was sent (Clock.HostMs), over the last second at one speed. The host's
    // measured rate (Clock.RatePer1x) lags behind it and is not sent at all until three steady stretches agree, which
    // a host still warming up at the top speed does not give for about 20 s (the first two-device session: 260k
    // climbing to 740k game s per real s at 200000, where a game that keeps up runs about 1.05M). Network delay does
    // not enter: both ends of the span are the host's own, and each Clock's date and time are the same host frame's.
    public sealed class HostPace
    {
        // Known from the second Clock at a speed (the heartbeat is 250 ms), smoothed over the last WindowMs.
        public const long SpanMs = 250, WindowMs = 1000;
        const int Size = 64;   // a second of Clocks at the flood bound's worst is cut to the newest

        readonly long[] ms = new long[Size];
        readonly double[] game = new double[Size];
        int first, count;
        float scale = -1f;

        // Forgets the Clocks so far (the host lost or silent).
        public void Reset() { first = count = 0; scale = -1f; }

        public void Add(double hostGame, long hostMs, float hostScale)
        {
            if (double.IsNaN(hostGame) || double.IsInfinity(hostGame)) { Reset(); return; }
            if (count > 0)
            {
                int last = (first + count - 1) % Size;
                if (hostScale != scale || hostGame < game[last] || hostMs < ms[last]) Reset();
                else if (hostMs == ms[last]) return;   // the same host frame: nothing new
            }
            scale = hostScale;
            if (count == Size) { first = (first + 1) % Size; count--; }
            int at = (first + count) % Size;
            ms[at] = hostMs;
            game[at] = hostGame;
            count++;
            // Keep the window just over WindowMs: drop the oldest while the next one still spans it.
            while (count > 2 && hostMs - ms[(first + 1) % Size] >= WindowMs) { first = (first + 1) % Size; count--; }
        }

        // Game seconds per real second, or NaN while unknown (paused, or under SpanMs of Clocks at this speed).
        public double Pace
        {
            get
            {
                if (count < 2 || !(scale > 0f)) return double.NaN;
                int last = (first + count - 1) % Size;
                long span = ms[last] - ms[first];
                if (span < SpanMs) return double.NaN;
                return (game[last] - game[first]) / (span / 1000.0);
            }
        }

        // The pace as a rate per 1x (DriftController.BaseScale), or 0 while unknown.
        public double RatePer1x
        {
            get
            {
                double p = Pace;
                return double.IsNaN(p) || !(p > 0) ? 0 : p * DriftController.BaseScale / scale;
            }
        }
    }
}
