namespace SolarExpanseMultiplayer
{
    // The ONE answer to "may a test seam act?": a valid test profile that is not the player-path profile. Every seam
    // (dev flags beyond the two profile flags, dev triggers, planted faults, the simulated network, the test window,
    // auto-load, files such as join-code.txt) asks this and nothing else, so a player's game cannot reach one by
    // any other route.
    public static class TestSeams
    {
        public static bool Open(string profile, bool playerPath)
        {
            return !playerPath && Valid(profile);
        }

        // The profile name rule is the launch argument's own, so a name the boot would reject never opens a seam.
        internal static bool Valid(string profile)
        {
            return profile != null && LaunchArgs.SafeName(profile);
        }
    }
}
