using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public sealed class SpeedCluster { public float Scale; public List<int> Players = new List<int>(); }

    // What the speed strip under the game's speed buttons shows (DD10), worked out from the host's Clock.
    public sealed class SpeedMarks
    {
        public float InEffect;               // the session's real speed (Clock.Scale), 0 = paused
        public int SetBy;                    // the player holding the slowest choice; -1 when none
        public float Highest;
        public bool ShowStrip;               // the choices differ or the game is paused (hover shows it regardless)
        public List<SpeedCluster> Marks = new List<SpeedCluster>();   // one per chosen button, slowest first

        // Null outside a game (no Clock). Only a loaded player's choice is marked: a vote whose roster row is not in
        // game (joining, dropped, away, rejoining) is left out, and a vote with no roster row at all (the early
        // session has none) counts. Writes each row's Vote and HasVote to match, so the PLAYERS screen agrees with the
        // strip. The slowest choice sets the speed; of equal ones the lowest player id is named.
        public static SpeedMarks From(Clock clock, IList<PlayerRow> roster)
        {
            if (clock == null) return null;
            var rows = new Dictionary<int, PlayerRow>();
            if (roster != null)
                foreach (PlayerRow r in roster)
                    if (r != null) { r.HasVote = false; r.Vote = 0f; rows[r.Id] = r; }

            var chosen = new SortedDictionary<int, float>();
            if (clock.Votes != null)
            {
                foreach (Vote v in clock.Votes)
                {
                    if (v == null || float.IsNaN(v.Scale) || float.IsInfinity(v.Scale) || v.Scale < 0f) continue;
                    PlayerRow row;
                    if (rows.TryGetValue(v.Player, out row))
                    {
                        if (row.State != PlayerRow.InGame) continue;
                        row.Vote = v.Scale; row.HasVote = true;
                    }
                    chosen[v.Player] = v.Scale;
                }
            }

            var m = new SpeedMarks { InEffect = clock.Scale, SetBy = -1 };
            var byScale = new SortedDictionary<float, SpeedCluster>();
            bool first = true; float slowest = 0f;
            foreach (KeyValuePair<int, float> c in chosen)
            {
                SpeedCluster cl;
                if (!byScale.TryGetValue(c.Value, out cl))
                {
                    cl = new SpeedCluster { Scale = c.Value }; byScale[c.Value] = cl;
                }
                cl.Players.Add(c.Key);
                if (first || c.Value < slowest) { slowest = c.Value; m.SetBy = c.Key; }
                if (first || c.Value > m.Highest) m.Highest = c.Value;
                first = false;
            }
            foreach (SpeedCluster cl in byScale.Values) m.Marks.Add(cl);
            m.ShowStrip = m.Marks.Count > 1 || clock.Scale <= 0f;
            return m;
        }
    }
}
