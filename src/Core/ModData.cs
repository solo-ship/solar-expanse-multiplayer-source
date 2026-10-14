using System.IO;

namespace SolarExpanseMultiplayer
{
    // The mod's own data (host certificate, join codes): <mod>\data\player for a normal player,
    // <mod>\data\test-<profile> for a test profile, so test runs never share keys with real play.
    public static class ModData
    {
        public static string Dir(string modDir, string profile)
        {
            if (string.IsNullOrEmpty(modDir)) return null;
            string name;
            if (profile == null) name = "player";
            else if (LaunchArgs.SafeName(profile)) name = "test-" + profile;
            else return null;
            return Path.Combine(Path.Combine(modDir, "data"), name);
        }

        // The player's protected keys: a folder inside the profile's data folder (which is already data\player).
        public static string KeysDir(string dataDir)
        {
            return string.IsNullOrEmpty(dataDir) ? null : Path.Combine(dataDir, "keys");
        }
    }
}
