using System;
using System.IO;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The player's own name for sessions (PlayerNames.Default in M8): the one typed on the host setup or join screen,
    // kept in the mod's data folder for next time, else the Steam persona name read locally, else none (the name rule
    // then gives "A player"). The file is small and cleaned on every read: it is the player's own, but a damaged or
    // edited one must not put anything unclean on screen or on the wire.
    public sealed class NameStore
    {
        public const string FileName = "player-name.txt";
        const int MaxFileBytes = 256;

        readonly string file;
        string saved;
        bool read;

        public NameStore(string dataDir)
        {
            file = dataDir != null ? Path.Combine(dataDir, FileName) : null;
        }

        // The name the screens fill in: the saved one, else Steam's, else null.
        public string Get()
        {
            if (!read) { read = true; saved = ReadFile(); }
            if (!string.IsNullOrEmpty(saved)) return saved;
            return SteamName();
        }

        // Keeps the name the player typed (cleaned by the name rule); an empty one forgets the saved name.
        public void Set(string typed)
        {
            string clean = MenuNames.Stored(typed);
            read = true;
            saved = clean.Length > 0 ? clean : null;
            if (file == null) return;
            try
            {
                if (saved == null) { if (File.Exists(file)) File.Delete(file); return; }
                File.WriteAllText(file, saved, new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Warn("menu: the player's name could not be saved: " + e.Message); }
        }

        string ReadFile()
        {
            if (file == null) return null;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileBytes) return null;
                string clean = MenuNames.Stored(File.ReadAllText(file, Encoding.UTF8));
                return clean.Length > 0 ? clean : null;
            }
            catch (Exception e)
            {
                Log.Warn("menu: the player's saved name could not be read: " + e.Message);
                return null;
            }
        }

        static string SteamName()
        {
            try
            {
                string n = TextSanitiser.Clean(Steamworks.SteamFriends.GetPersonaName(), PlayerNames.MaxChars, "");
                return n.Length > 0 ? n : null;
            }
            catch (Exception) { return null; }   // Steam not running, or not started by the game yet
        }
    }
}
