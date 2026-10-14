using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The parts kit's leak check (DD0, K1): a count of every handler on the game's delegates and UI events, taken
    // before a set of clones is built and again after they are gone. Any count that differs while game time stands
    // still fails, and so does any handler whose target is the mod or a clone (a destroyed clone included). While game
    // time runs the game's own facilities add and drop handlers, so a difference is reported but not judged.
    public sealed class HandlerCensus
    {
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        public readonly List<string> Ours = new List<string>();   // "<where> -> <handler>", target the mod or a clone

        public void Add(string key, int count) { Counts[key] = count; }

        // Every key whose count differs ("key was->now"; "gone" for a key that vanished with handlers on it), sorted.
        public static List<string> Differences(HandlerCensus before, HandlerCensus after)
        {
            var d = new List<string>();
            if (before == null || after == null) { d.Add("no census"); return d; }
            foreach (KeyValuePair<string, int> kv in after.Counts)
            {
                int was;
                before.Counts.TryGetValue(kv.Key, out was);
                if (was != kv.Value) d.Add(kv.Key + " " + was + "->" + kv.Value);
            }
            foreach (KeyValuePair<string, int> kv in before.Counts)
                if (!after.Counts.ContainsKey(kv.Key) && kv.Value != 0) d.Add(kv.Key + " " + kv.Value + "->gone");
            d.Sort(System.StringComparer.Ordinal);
            return d;
        }

        public const string Pass = "PASS", Fail = "FAIL", NotJudged = "NOT JUDGED";

        // timeStill: game time did not run between the two counts (paused, or no game at all).
        public static string Verdict(HandlerCensus before, HandlerCensus after, bool timeStill)
        {
            if (before == null || after == null) return Fail;
            if (after.Ours.Count > 0 || before.Ours.Count > 0) return Fail;
            if (Differences(before, after).Count == 0) return Pass;
            return timeStill ? Fail : NotJudged;
        }
    }
}
