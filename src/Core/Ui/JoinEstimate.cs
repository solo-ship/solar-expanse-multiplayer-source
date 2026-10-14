namespace SolarExpanseMultiplayer
{
    public static class JoinEstimate
    {
        public const int MaxSeconds = 86400;   // Roster's JoinSecsLeft cap: a longer wait is shown as the cap

        // Seconds until the join finishes at the given rate, rounded up; -1 when it is unknown (no rate yet, a rate
        // that is not a positive number, a negative amount). The caller smooths the rate (Smooth) before asking.
        public static int SecondsLeft(long bytesLeft, double bytesPerSecond)
        {
            if (bytesLeft < 0) return -1;
            if (bytesLeft == 0) return 0;
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0) return -1;
            double secs = System.Math.Ceiling(bytesLeft / bytesPerSecond);
            return secs >= MaxSeconds ? MaxSeconds : (int)secs;
        }

        // The rate the estimate uses: the first sample as it is, then old * 0.8 + sample * 0.2 (as the client's own
        // round trip is smoothed), so one fast or slow second does not swing the time left. A bad sample is ignored.
        public static double Smooth(double old, double sample)
        {
            if (double.IsNaN(sample) || double.IsInfinity(sample) || sample < 0) return old > 0 ? old : 0;
            if (!(old > 0)) return sample;
            return old * 0.8 + sample * 0.2;
        }
    }
}
