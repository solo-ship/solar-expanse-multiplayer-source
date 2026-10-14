namespace SolarExpanseMultiplayer
{
    // The PLAYERS screen's connection bars (DD27): four bars for a ping under 80 ms, three under 150, two under 300,
    // else one; 0 when no ping is known (the bars show as an empty outline with "-", never colour alone).
    public static class SignalBars
    {
        public const int Max = 4;

        public static int For(int pingMs)
        {
            if (pingMs < 0) return 0;
            if (pingMs < 80) return 4;
            if (pingMs < 150) return 3;
            if (pingMs < 300) return 2;
            return 1;
        }
    }
}
