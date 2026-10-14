using System;

namespace SolarExpanseMultiplayer
{
    // Measures how many game seconds pass per real second at speed 1000, from (real ms, game seconds, speed) samples.
    // Only steady stretches count (the same non-zero speed for at least MinWindowMs); the last value is kept while
    // paused. Measured, not assumed: the research didn't pin down the engine's rate, and mods could change it.
    public sealed class RateMeter
    {
        public const long MinWindowMs = 1000;
        long startMs = -1;
        double startGame;
        float scale;

        public const double MaxJumpFactor = 3.0;   // a stretch that advanced over 3x the expected is not believed...
        public const int AcceptAfter = 5;          // ...unless it happens this many times in a row (the rate really changed)
        int ignoredRun;

        public double RatePer1x { get; private set; }   // 0 until first measured
        public int Ignored { get; private set; }         // stretches thrown away (a frozen game, or an implausible jump)
        public string LastIgnored { get; private set; }  // why, for the log

        // After a save load or anything else that moves the date: forget the stretch in progress (the rate is kept).
        public void Reset() { startMs = -1; ignoredRun = 0; firstCount = 0; }

        // The very first value is not believed on one stretch: it is adopted when this many in a row agree (within 10%).
        public const int FirstNeeds = 3;
        const double FirstAgree = 0.1;
        int firstCount;
        double firstMean;

        public void Add(long nowMs, double game, float currentScale)
        {
            if (double.IsNaN(game) || double.IsInfinity(game)) { startMs = -1; Ignore("the date was not a number"); return; }
            if (startMs < 0 || currentScale != scale || game < startGame || nowMs < startMs)
            {
                startMs = nowMs; startGame = game; scale = currentScale;
                return;
            }
            if (scale <= 0 || nowMs - startMs < MinWindowMs) return;
            double r = (game - startGame) / ((nowMs - startMs) / 1000.0) * DriftController.BaseScale / scale;
            startMs = nowMs; startGame = game;
            // A game that did not advance while running (a freeze, a menu, an unfocused window) says nothing about the
            // rate; neither does a jump far beyond what the speed allows (a load, a stutter catching up).
            if (!(r > 0) || double.IsInfinity(r)) { Ignore("the date did not advance at speed " + scale); return; }
            if (RatePer1x <= 0)
            {
                if (firstCount > 0 && Math.Abs(r - firstMean) <= FirstAgree * firstMean) { firstMean = (firstMean * firstCount + r) / (firstCount + 1); firstCount++; }
                else { firstMean = r; firstCount = 1; }
                if (firstCount >= FirstNeeds) RatePer1x = firstMean;
                return;
            }
            if (RatePer1x > 0 && r > RatePer1x * MaxJumpFactor && ignoredRun < AcceptAfter - 1)
            {
                Ignore("the date advanced " + (r / RatePer1x).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x the expected at speed " + scale);
                return;
            }
            ignoredRun = 0;
            RatePer1x = RatePer1x > 0 ? RatePer1x * 0.7 + r * 0.3 : r;
        }

        void Ignore(string why) { Ignored++; ignoredRun++; LastIgnored = why; }
    }
}
