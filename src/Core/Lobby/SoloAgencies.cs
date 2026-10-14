using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The agencies of a Multiplayer save as the saves list and the solo picker see them (DD24 step 1), from what the index
    // keeps: the host's own agency and the last holders are the former human seats, the saved rivals that nobody held are AI
    // rivals, and the star system's idle companies are idle. An agency not in the game is simply not listed.
    public static class SoloAgencies
    {
        public static List<AgencyRow> For(MpSaveEntry e, IEnumerable<string> idle, Func<string, bool> playable)
        {
            var rows = new List<AgencyRow>();
            if (e == null || e.Info == null || e.Info.StartGameConfiguration == null) return rows;
            StartConfigData c = e.Info.StartGameConfiguration;
            Add(rows, c.Company, AgencyStanding.Human, e, playable);
            foreach (KeyValuePair<string, string> kv in e.Holders) Add(rows, kv.Key, AgencyStanding.Human, e, playable);
            if (c.EnabledRivalCorporations != null)
                foreach (string r in c.EnabledRivalCorporations) Add(rows, r, AgencyStanding.AiRival, e, playable);
            if (idle != null) foreach (string i in idle) Add(rows, i, AgencyStanding.Idle, e, playable);
            return rows;
        }

        static void Add(List<AgencyRow> rows, string id, AgencyStanding state, MpSaveEntry e, Func<string, bool> playable)
        {
            if (!LobbyIds.IsContentId(id)) return;
            foreach (AgencyRow r in rows) if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return;
            string holder;
            e.Holders.TryGetValue(id, out holder);
            bool ok;
            try { ok = playable == null || playable(id); } catch (Exception) { ok = false; }
            rows.Add(new AgencyRow { Id = id, State = state, Playable = ok, EpochAllows = true, LastHolder = holder });
        }

        // The agencies the chosen one leaves running as AI rivals in the solo game, and the former human seats among them
        // (their saved behaviour trees are emptied before the load). Both come from the same rows SoloStart takes.
        public static List<string> FormerSeats(IList<AgencyRow> rows)
        {
            var seats = new List<string>();
            if (rows != null) foreach (AgencyRow r in rows) if (r != null && r.State == AgencyStanding.Human) seats.Add(r.Id);
            return seats;
        }
    }
}
