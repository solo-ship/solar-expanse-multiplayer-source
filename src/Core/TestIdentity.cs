namespace SolarExpanseMultiplayer
{
    // tools\isolate-test-copy.ps1 renames a test copy's Unity company from SpaceOps to SEMPTest, which moves its data
    // folder and registry settings away from the real game's.
    public static class TestIdentity
    {
        public const string Company = "SEMPTest";

        // True when Application.persistentDataPath is the test copies' own ...\LocalLow\SEMPTest\Solar Expanse.
        public static bool InTestDataFolder(string persistentDataPath)
        {
            if (string.IsNullOrEmpty(persistentDataPath)) return false;
            string[] parts = persistentDataPath.Replace('\\', '/').TrimEnd('/').Split('/');
            int n = parts.Length;
            return n >= 2 && parts[n - 2] == Company && parts[n - 1] == "Solar Expanse";
        }
    }
}
