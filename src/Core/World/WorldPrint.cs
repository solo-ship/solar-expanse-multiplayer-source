using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One item as M3's ItemCapture holds it: its block's agency (null in a world area), its key, its Discrete hash, its values.
    public struct ItemRow { public string Agency; public string Key; public long DiscreteHash; public double[] Values; }

    // M7 Task 5 (DD-W9, DD-W10): the world print from a DaySnapshot. Pure.
    public static class WorldPrint
    {
        // The separator between an item key's segments. M3 keeps segments apart in ItemCapture; the text form is Task 3/11's
        // to settle (TEST-DEBT), '/' is the stand-in.
        public const char KeySeparator = '/';
        public const int MaxSums = 16;

        static string Hex(ulong v) { return v.ToString("x16"); }

        // One print per DriftAreas index, in order: the Discrete hash over that area's blocks (agency, count, hash), in block order.
        public static AreaPrint[] Areas(DaySnapshot s)
        {
            if (s == null || s.Blocks == null) return null;
            var h = new ulong[DriftAreas.All.Length];
            for (int a = 0; a < h.Length; a++) h[a] = Fnv64.Offset;
            int n = Math.Min(s.BlockCount, s.Blocks.Length);
            for (int i = 0; i < n; i++)
            {
                BlockSum b = s.Blocks[i];
                if (b.Area < 0 || b.Area >= h.Length) continue;
                ulong x = Fnv64.Long(h[b.Area], b.Agency);
                x = Fnv64.Long(x, b.Count);
                h[b.Area] = Fnv64.Long(x, (long)b.Discrete);
            }
            var r = new AreaPrint[h.Length];
            for (int a = 0; a < r.Length; a++) r[a] = new AreaPrint { Area = a, Discrete = Hex(h[a]) };
            return r;
        }

        // The day's whole Discrete hash, in M4's DayDiscrete form (FingerprintBuild's).
        public static string Day(DaySnapshot s)
        {
            if (s == null || s.Blocks == null) return null;
            ulong h = Fnv64.Offset;
            int n = Math.Min(s.BlockCount, s.Blocks.Length);
            for (int i = 0; i < n; i++) h = Fnv64.Long(h, (long)s.Blocks[i].Discrete);
            return Hex(h);
        }

        // Every block's field sums. Agency is the snapshot's agency index as text, or DriftAreas.World for a world block.
        public static AreaValue[] Values(DaySnapshot s)
        {
            if (s == null || s.Fields == null) return null;
            int n = Math.Min(s.FieldCount, s.Fields.Length);
            var r = new AreaValue[n];
            for (int i = 0; i < n; i++)
            {
                FieldSum f = s.Fields[i];
                r[i] = new AreaValue
                {
                    Area = f.Area, Field = f.Field, Sum = f.Sum,
                    Agency = f.Agency == DriftAreas.WorldAgency ? DriftAreas.World : f.Agency.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            }
            return r;
        }

        // The sums as the host sends them (CommandServiceWorld.NamedValues): a per-agency block named by its agency,
        // a world block with no agency. A position with no name is left out. Unchanged without names.
        public static AreaValue[] Named(AreaValue[] values, string[] names)
        {
            if (values == null || names == null) return values;
            var r = new List<AreaValue>(values.Length);
            foreach (AreaValue v in values)
            {
                if (v == null) continue;
                string agency = null;
                if (DriftAreas.PerAgency(v.Area))
                {
                    int at;
                    if (!int.TryParse(v.Agency, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out at) || at < 0 || at >= names.Length)
                        continue;
                    agency = names[at];
                }
                r.Add(new AreaValue { Area = v.Area, Agency = agency, Field = v.Field, Sum = v.Sum });
            }
            return r.ToArray();
        }

        // Area indexes that differ (an area missing on either side differs); [] when equal. Fails closed: a null side differs everywhere.
        public static int[] Differs(AreaPrint[] host, AreaPrint[] mine)
        {
            var d = new List<int>();
            if (host == null && mine == null) return d.ToArray();
            var a = Map(host); var b = Map(mine);
            var all = new SortedDictionary<int, bool>();
            foreach (int k in a.Keys) all[k] = true;
            foreach (int k in b.Keys) all[k] = true;
            foreach (int k in all.Keys)
            {
                string x, y;
                if (!a.TryGetValue(k, out x) || !b.TryGetValue(k, out y) || !string.Equals(x, y, StringComparison.Ordinal)) d.Add(k);
            }
            return d.ToArray();
        }

        static Dictionary<int, string> Map(AreaPrint[] p)
        {
            var m = new Dictionary<int, string>();
            if (p != null) foreach (AreaPrint x in p) if (x != null) m[x.Area] = x.Discrete;
            return m;
        }

        // The groups of one area: the key's first segment, with money and research one fixed group each. Hashed at Discrete
        // (a wrapping sum of the items' hashes, so item order does not matter); Sums (value days) are per-value sums of |v|.
        public static GroupPrint[] Groups(IList<ItemRow> items, int area, bool valueDay)
        {
            var list = new List<GroupPrint>();
            if (items == null) return list.ToArray();
            var sums = new Dictionary<string, ulong>();
            var index = new Dictionary<string, int>();
            for (int i = 0; i < items.Count; i++)
            {
                ItemRow it = items[i];
                string g = GroupOf(area, it.Key);
                string key = (it.Agency ?? "") + "\n" + g;
                int at;
                if (!index.TryGetValue(key, out at))
                {
                    at = list.Count; index[key] = at; sums[key] = 0;
                    list.Add(new GroupPrint { Area = area, Agency = it.Agency, Group = g, Sums = valueDay ? new double[0] : null });
                }
                GroupPrint gp = list[at];
                gp.Count++;
                unchecked { sums[key] += Fnv64.Long(Fnv64.Offset, it.DiscreteHash); }
                if (valueDay && it.Values != null)
                {
                    int m = Math.Min(it.Values.Length, MaxSums);
                    if (gp.Sums.Length < m) Array.Resize(ref gp.Sums, m);
                    for (int v = 0; v < m; v++) { double x = Math.Abs(it.Values[v]); if (!double.IsNaN(x) && !double.IsInfinity(x)) gp.Sums[v] += x; }
                }
            }
            foreach (KeyValuePair<string, int> kv in index) list[kv.Value].Discrete = Hex(Fnv64.Long(sums[kv.Key], list[kv.Value].Count));
            return list.ToArray();
        }

        // The group an item key belongs to: its first segment, with money and research one fixed group each.
        public static string GroupOf(int area, string key)
        {
            if (area == 0) return DriftAreas.Money;
            if (area == 1) return DriftAreas.Research;
            if (key == null) return "";
            int p = key.IndexOf(KeySeparator);
            return p < 0 ? key : key.Substring(0, p);
        }
    }
}
