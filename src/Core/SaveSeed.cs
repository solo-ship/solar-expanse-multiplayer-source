using System.Collections.Generic;
using System.IO;

namespace SolarExpanseMultiplayer
{
    public static class SaveSeed
    {
        static readonly string[] Parts = { ".json.gz", ".info.gz" };

        // Files to copy so a fresh test profile has the named save. Only whole pairs: the game deletes unpaired files.
        public static List<KeyValuePair<string, string>> Plan(string realSaves, string profileSaves, string name)
        {
            var copies = new List<KeyValuePair<string, string>>();
            if (name == null) return copies;
            foreach (string ext in Parts)
                if (!File.Exists(Path.Combine(realSaves, name + ext))) return copies;
            foreach (string ext in Parts)
            {
                string dst = Path.Combine(profileSaves, name + ext);
                if (!File.Exists(dst)) copies.Add(new KeyValuePair<string, string>(Path.Combine(realSaves, name + ext), dst));
            }
            return copies;
        }
    }
}
