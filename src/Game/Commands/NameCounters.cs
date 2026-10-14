using System;
using System.Collections.Generic;
using Company = Game.Company;
using LaunchVehicleType = Data.ScriptableObject.LaunchVehicleType;
using SpaceCraftConstructData = Game.UI.Windows.Elements.SpaceCraftConstructElements.SpaceCraftConstructData;
using SpacecraftType = Data.ScriptableObject.SpacecraftType;

namespace SolarExpanseMultiplayer
{
    // The ship-name counters (N7). The game counts names in Company.CountSN under the name's own text (a localised
    // type name, or a hull's name), so two games in two languages count under different keys. A NameCounter row is
    // keyed by the craft type's content id instead, and each game maps that id to the text its own language makes for
    // the type, the text P1 names a ship with (SpaceCraftConstructData.SpaceCraftName). Names are display only: nothing
    // prints, checks or compares them. Stateless; the client glue sets a counter through Set.
    public static class NameCounters
    {
        // The text this game names the type's ships with, or null for an id that is no ship or launch-vehicle type.
        public static string TextOf(string key)
        {
            SpaceCraftConstructData d = ConstructOf(key);
            return d != null ? d.SpaceCraftName : null;
        }

        static SpaceCraftConstructData ConstructOf(string key)
        {
            if (key == null) return null;
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            foreach (SpacecraftType t in all.AllSpacecraftType.ListNotEmpty)
                if (t != null && t.ID == key) return t.spaceCraftConstructDefault;
            foreach (LaunchVehicleType t in all.AllLaunchVehicleType.ListNotEmpty)
                if (t != null && t.ID == key) return t.spaceCraftConstructDefault;
            return null;
        }

        // The count under a name text (0 when the game has none yet).
        public static long Count(Company c, string text)
        {
            int n;
            return c != null && text != null && c.CountSN.TryGetValue(text, out n) ? n : 0;
        }

        // A client sets a counter to the host's value before an apply (CounterSync). False for a key this game cannot
        // map, which the caller treats as out of step.
        public static bool Set(Company c, string key, long value)
        {
            string text = TextOf(key);
            if (c == null || text == null || value < 0 || value > int.MaxValue) return false;
            c.CountSN[text] = (int)value;
            return true;
        }

        public static CounterRow Row(string agency, string key, long before, long after)
        {
            return new CounterRow { Which = CounterKind.NameCounter, Agency = agency, Key = key, Before = before,
                After = after };
        }

        // AgencyState's name counters for one agency: every type it has named a ship of, in the content's order.
        public static List<CounterRow> StateRows(Company c, string agency)
        {
            return Rows(c, agency, AllKeys(), ProductionCommand.NameRowsPerAgency);
        }

        // A site's copy carries the name counters of the crafts it is building (their rows name the same keys).
        public static CounterRow[] WithSite(CounterRow[] counters, Company c, string agency, BuildRow[] building)
        {
            var keys = new List<string>();
            if (building != null)
                foreach (BuildRow r in building)
                    if (r != null && r.Craft && r.NameKey != null) keys.Add(r.NameKey);
            List<CounterRow> rows = Rows(c, agency, keys, ProductionCommand.NameRowsPerSite);
            if (rows.Count == 0) return counters;
            var all = new List<CounterRow>(counters ?? new CounterRow[0]);
            all.AddRange(rows);
            return all.ToArray();
        }

        // Once a session (the flag is the rules' state): names are display only, so a capped list loses nothing that
        // is checked, but the log says so.
        static void NoteDropped(string agency, int dropped, int max)
        {
            RuleState st = HumanAgencies.State;
            if (st != null && st.NameKeysDropLogged) return;
            if (st != null) st.NameKeysDropLogged = true;
            Log.Warn("command: " + agency + " has " + dropped + " more ship-name counters than the " + max
                + " a message carries; their names may differ on a joiner (logged once)");
        }

        static List<string> AllKeys()
        {
            var keys = new List<string>();
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            foreach (SpacecraftType t in all.AllSpacecraftType.ListNotEmpty) if (t != null) keys.Add(t.ID);
            foreach (LaunchVehicleType t in all.AllLaunchVehicleType.ListNotEmpty) if (t != null) keys.Add(t.ID);
            return keys;
        }

        static List<CounterRow> Rows(Company c, string agency, List<string> keys, int max)
        {
            if (c == null) throw new InvalidOperationException("no agency " + agency);
            var counts = new List<KeyValuePair<string, long>>();
            foreach (string key in keys) counts.Add(new KeyValuePair<string, long>(key, Count(c, TextOf(key))));
            var rows = new List<CounterRow>();
            int dropped;
            foreach (KeyValuePair<string, long> kv in ProductionCommand.NameRows(counts, max, out dropped))
                rows.Add(Row(agency, kv.Key, kv.Value, kv.Value));
            if (dropped > 0) NoteDropped(agency, dropped, max);
            return rows;
        }
    }
}
