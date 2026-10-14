namespace SolarExpanseMultiplayer
{
    public static class IsolationGate
    {
        // Null = safe to run. Otherwise the reason the game must quit: a test run must never reach the real save folder.
        public static string Check(bool profileFlagGiven, string profile, string gameRoot, bool patchesOk)
        {
            if (!profileFlagGiven) return null;
            if (profile == null) return "the test profile name isn't a plain name (letters, digits, _ or -, up to 32)";
            if (gameRoot == null) return "the game folder wasn't found, so there is nowhere to keep the test profile";
            if (!patchesOk) return "a save-folder patch failed, so test saves could reach the real save folder";
            return null;
        }
    }
}
