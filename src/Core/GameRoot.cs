using System.IO;

namespace SolarExpanseMultiplayer
{
    public static class GameRoot
    {
        // The mod sits in <game>\SolarExpanseMultiplayer (Doorstop) or <game>\BepInEx\plugins\SolarExpanseMultiplayer.
        public static string FromModDir(string modDir)
        {
            DirectoryInfo d = string.IsNullOrEmpty(modDir) ? null : new DirectoryInfo(modDir);
            for (int i = 0; d != null && i < 5; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "Solar Expanse_Data"))) return d.FullName;
            return null;
        }
    }
}
