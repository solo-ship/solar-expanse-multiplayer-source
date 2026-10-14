using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // DD-F10: the status line while players hold planner pauses. "Paused: Player 2 is planning (1:05)", every game,
    // each pause timed from the first Clock that lists it (no protocol change). A pause that is gone from the list
    // starts again from zero next time. One instance per clock side; the real clock is the caller's.
    public sealed class PlanningStatus
    {
        readonly Dictionary<int, long> since = new Dictionary<int, long>();

        // "" when nobody holds a pause; otherwise what PauseWhy.Describe says, with each planner timed.
        public string Describe(Pause[] pauses, long nowMs)
        {
            var planners = new List<int>();
            var others = new List<Pause>();
            if (pauses != null)
                foreach (Pause p in pauses)
                {
                    if (p == null) continue;
                    if (p.Reason == PauseWhy.Planning) planners.Add(p.Player); else others.Add(p);
                }
            var gone = new List<int>();
            foreach (int player in since.Keys) if (!planners.Contains(player)) gone.Add(player);
            foreach (int player in gone) since.Remove(player);
            if (planners.Count == 0) return PauseWhy.Describe(others.ToArray());
            var parts = new List<string>();
            foreach (int player in planners)
            {
                long from;
                if (!since.TryGetValue(player, out from)) since[player] = from = nowMs;
                long ms = nowMs - from;
                parts.Add(CommandText.Planning(PauseWhy.Who(player),
                    (int)(ms < 0 ? 0 : ms / 1000)));
            }
            foreach (Pause p in others) parts.Add(PauseWhy.Part(p));
            return PauseWhy.Paused(parts);
        }
    }
}
