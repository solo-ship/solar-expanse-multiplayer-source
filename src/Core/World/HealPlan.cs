using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // M7 Task 5 (DD-W10): the client's heal bookkeeping. Every limit is in game days.
    public sealed class HealPlan
    {
        readonly int repeatDays, valueHeals;
        readonly HashSet<string> outstanding = new HashSet<string>();
        readonly Dictionary<string, long> lastDiscrete = new Dictionary<string, long>();
        readonly Dictionary<string, List<long>> valueByAgency = new Dictionary<string, List<long>>();

        public HealPlan(int repeatDays, int valueHealsPerAgency) { this.repeatDays = repeatDays; valueHeals = valueHealsPerAgency; }

        // money, research, stock, population, exploration, contracts, market, deposits (DD-W10).
        public bool Restorable(int area)
        {
            switch (area) { case 0: case 1: case 4: case 5: case 6: case 9: case 10: case 12: return true; default: return false; }
        }

        static string Key(int area, string agency, string group) { return area + "\n" + (agency ?? "") + "\n" + (group ?? ""); }

        // One outstanding per group, and only in a restorable area.
        public bool MayAsk(int area, string agency, string group)
        {
            return Restorable(area) && !outstanding.Contains(Key(area, agency, group));
        }

        public void Asked(int area, string agency, string group, long day) { outstanding.Add(Key(area, agency, group)); }

        // null, or why it is out of step: a Discrete heal of one group twice within repeatDays; the agency's value budget spent.
        public string Healed(int area, string agency, string group, long day, bool discrete)
        {
            string key = Key(area, agency, group);
            outstanding.Remove(key);
            string what = DriftAreas.All[System.Math.Max(0, System.Math.Min(area, DriftAreas.All.Length - 1))] + " " + (agency ?? "-") + " " + group;
            if (discrete)
            {
                long last;
                bool repeat = lastDiscrete.TryGetValue(key, out last) && day - last < repeatDays;
                lastDiscrete[key] = day;
                return repeat ? "heal: " + what + " healed twice within " + repeatDays + " days" : null;
            }
            string a = agency ?? "";
            List<long> days;
            if (!valueByAgency.TryGetValue(a, out days)) { days = new List<long>(); valueByAgency[a] = days; }
            days.RemoveAll(d => day - d >= repeatDays);
            if (days.Count >= valueHeals) return "heal: value budget spent (" + what + ")";
            days.Add(day);
            return null;
        }

        // A link that may resume (M7): the requests in flight will not be answered. The history stays.
        public void DropOutstanding() { outstanding.Clear(); }

        // One request will not be answered (the host no longer keeps the day, or its rows came too late).
        public void Forget(int area, string agency, string group) { outstanding.Remove(Key(area, agency, group)); }

        // Skip comparing that group until it is healed.
        public bool Pending(int area, string agency, string group) { return outstanding.Contains(Key(area, agency, group)); }
    }
}
