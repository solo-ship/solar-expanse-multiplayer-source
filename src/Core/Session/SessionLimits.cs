using System;

namespace SolarExpanseMultiplayer
{
    public static class SessionLimits
    {
        public const int PlayerLimitNoCoop = 5;     // the lobby option ranges 1..5 without co-op
        public const int TechnicalCeiling = 16;     // seats, ids, roster size; M8b lifts PlayerLimit up to this

        // Every playable agency can have a human; a world that reports a negative count seats none.
        public static int HumanAgencyLimit(int playableAgencies)
        {
            return Math.Max(0, playableAgencies);
        }
    }
}
