using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // DD24 step 3: the start configuration of a solo load of a Multiplayer save, as plain ids. The agencies come as AgencyRow
    // (Task 8's Core type: Id and State), so this builds before Task 6's lobby types exist.
    public static class SoloStart
    {
        // Company = chosen; the rivals = every OTHER agency in the game that is a former human seat or an AI rival; idle agencies stay
        // idle and not-in-game agencies stay out (neither is a rival); the save's difficulty, star system, epoch and gameplay id;
        // no tutorial; setupAIs on. null + why when the chosen agency is not in the game, is unknown, or is the World Government.
        // worldGovernmentIds: the agency ids that are the World Government (the glue passes them from the definitions).
        public static SaveInfoData For(SaveInfoData saved, string chosenAgency, IList<AgencyRow> agencies, out string why)
        {
            return For(saved, chosenAgency, agencies, null, out why);
        }

        public static SaveInfoData For(SaveInfoData saved, string chosenAgency, IList<AgencyRow> agencies, ICollection<string> worldGovernmentIds, out string why)
        {
            why = null;
            if (saved == null || saved.StartGameConfiguration == null) { why = "the save has no start configuration"; return null; }
            string bad = saved.Validate();
            if (bad != null) { why = bad; return null; }
            if (!LobbyIds.IsContentId(chosenAgency)) { why = "the chosen agency is not valid"; return null; }
            if (agencies == null) { why = "the save's agencies are not known"; return null; }
            AgencyRow chosen = null;
            foreach (AgencyRow a in agencies) if (a != null && Same(a.Id, chosenAgency)) { chosen = a; break; }
            if (chosen == null) { why = "the chosen agency is not in this save"; return null; }
            if (IsWorldGovernment(chosen.Id, worldGovernmentIds)) { why = "the World Government cannot be played"; return null; }
            if (chosen.State == AgencyStanding.NotInGame) { why = "the chosen agency is not in the game"; return null; }

            var rivals = new List<string>();
            foreach (AgencyRow a in agencies)
            {
                if (a == null || Same(a.Id, chosenAgency) || IsWorldGovernment(a.Id, worldGovernmentIds)) continue;
                if (a.State == AgencyStanding.Human || a.State == AgencyStanding.AiRival) rivals.Add(a.Id);
            }
            SaveInfoData r = saved.Clone();
            StartConfigData c = r.StartGameConfiguration;
            c.Company = chosen.Id;
            c.EnabledRivalCorporations = rivals;
            c.SetupAIs = true;
            c.StartWithTutorial = false;
            bad = r.Validate();
            if (bad != null) { why = bad; return null; }
            return r;
        }

        // The seats whose saved behaviour trees are emptied before the load: the save's own company (the host's seat) and, when the
        // chosen agency was an AI rival at the start, the chosen agency too. Only what the info itself knows; the other former
        // human seats come with the agency list (the overload below).
        public static List<string> SeatsToReset(SaveInfoData saved, string chosenAgency)
        {
            var seats = new List<string>();
            if (saved == null || saved.StartGameConfiguration == null) return seats;
            StartConfigData c = saved.StartGameConfiguration;
            if (c.Company != null) seats.Add(c.Company);
            if (chosenAgency != null && c.EnabledRivalCorporations != null)
                foreach (string r in c.EnabledRivalCorporations)
                    if (Same(r, chosenAgency) && !seats.Exists(s => Same(s, r))) seats.Add(r);
            return seats;
        }

        // The same plus every other agency that was a human seat in the session save.
        public static List<string> SeatsToReset(SaveInfoData saved, string chosenAgency, IList<AgencyRow> agencies)
        {
            List<string> seats = SeatsToReset(saved, chosenAgency);
            if (agencies != null)
                foreach (AgencyRow a in agencies)
                    if (a != null && a.State == AgencyStanding.Human && !seats.Exists(s => Same(s, a.Id))) seats.Add(a.Id);
            return seats;
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        static bool IsWorldGovernment(string id, ICollection<string> wg)
        {
            if (wg == null) return false;
            foreach (string w in wg) if (Same(w, id)) return true;
            return false;
        }
    }
}
