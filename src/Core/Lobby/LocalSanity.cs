using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What a checked save says about itself, read by the game side (the strict deserialise has passed).
    public sealed class LocalFacts
    {
        public long DateTicks;
        public bool DateReadable = true, StartGameSave, EndGame;
        public string SaveVersion;
        public HashSet<string> AgenciesInSave;     // the agency ids the save holds company data for
        public Func<string, bool> InContent;       // this game has that agency
    }

    // Wall 7's local-load variant (DD24 step 2): there is no claim and no host header, so the save is checked against its
    // own index info. A held copy installed at the menu and a solo load use it; a solo load also names the chosen agency.
    // It does not ask for speed 0: a save may hold any speed.
    public static class LocalSanity
    {
        public const long DayTicks = TimeSpan.TicksPerDay;

        // null = ok, else a fixed reason.
        public static string Check(LocalFacts f, SaveInfoData info, string ownVersion, string chosenAgency, ICollection<string> worldGovernment)
        {
            if (f == null || info == null) return "no save or no info";
            if (!f.DateReadable) return "the save's date is unreadable";
            long diff = f.DateTicks - info.GameDate.Ticks;
            if (diff < 0) diff = -diff;
            if (diff > DayTicks) return "the save's date is not the date in its info";
            if (f.StartGameSave) return "the save is a new-game save, not a running game";
            if (!string.Equals(f.SaveVersion, info.SaveVersion, StringComparison.Ordinal)) return "the save's version is not the one in its info";
            if (!VersionAccepted(f.SaveVersion, ownVersion)) return "the save is from game version " + TextSanitiser.Clean(f.SaveVersion, 32, "?");
            if (f.EndGame) return "the save is a finished game (the impact)";
            StartConfigData c = info.StartGameConfiguration;
            var named = new List<string> { c.Company };
            named.AddRange(c.EnabledRivalCorporations);
            if (chosenAgency != null) named.Add(chosenAgency);
            foreach (string id in named)
            {
                if (f.AgenciesInSave == null || !Has(f.AgenciesInSave, id)) return "an agency of the save is not in the save";
                bool known;
                try { known = f.InContent != null && f.InContent(id); } catch (Exception) { known = false; }
                if (!known) return "an agency of the save is not in this game's content";
            }
            if (chosenAgency != null && worldGovernment != null)
                foreach (string w in worldGovernment)
                    if (string.Equals(w, chosenAgency, StringComparison.OrdinalIgnoreCase)) return "the World Government cannot be played";
            return null;
        }

        // The current game version, or a build this mod version has been verified on (KnownBuilds). An older build whose
        // allowlist was not audited is refused with its version named.
        public static bool VersionAccepted(string saveVersion, string ownVersion)
        {
            string v = ModInfo.ClipGameVersion(saveVersion);
            if (v == ModInfo.ClipGameVersion(ownVersion)) return true;
            foreach (KnownBuild b in KnownBuilds.All)
                if (v == ModInfo.ClipGameVersion(b.GameVersion)) return true;
            return false;
        }

        static bool Has(HashSet<string> set, string id)
        {
            foreach (string s in set) if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
