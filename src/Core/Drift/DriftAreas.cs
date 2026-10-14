using System;

namespace SolarExpanseMultiplayer
{
    // The 14 state areas M3 checksums: 10 per agency (All[0..9]), then 4 for the world. The order is the record file's
    // area table and is append-only. Caps are per (area, agency) block. Pure: no UnityEngine, no Harmony.
    public static class DriftAreas
    {
        public const string Money = "money", Research = "research", Facilities = "facilities", Production = "production",
            Stock = "stock", Population = "population", Exploration = "exploration", Fleet = "fleet", Missions = "missions",
            Contracts = "contracts", Market = "market", Bodies = "bodies", Deposits = "deposits", Habitability = "habitability";

        public static readonly string[] All =
        {
            Money, Research, Facilities, Production, Stock, Population, Exploration, Fleet, Missions, Contracts,
            Market, Bodies, Deposits, Habitability
        };

        public const int PerAgencyCount = 10;       // All[0..9] are per agency, the rest are world areas
        public const string World = "-";            // the agency column of a world area (text form)
        public const int WorldAgency = -1;          // the agency index of a world area (binary form)

        // Per (area, agency) block: max(4 x the spike's largest block, 64). The spike's blocks (task-2-report.md 3.1):
        // money 4, research 39, facilities 23, production 23, stock 3450, population 1, exploration 647, fleet 2,
        // missions 2, contracts 13, market 59, bodies 150, deposits 497, habitability 150.
        public static readonly int[] MaxItems = { 64, 156, 92, 92, 13800, 64, 2588, 64, 64, 64, 1024, 600, 1988, 600 };

        // [add] The most values (keys included) one item of the area can carry. Sizes ItemCapture.Values.
        public static readonly int[] MaxValuesPerItem = { 6, 4, 12, 8, 5, 3, 6, 15, 11, 4, 10, 7, 5, 7 };

        // [add] The most continuous (double) value names a block of the area can carry: one FieldSum each. Sizes DaySnapshot.Fields.
        public static readonly int[] MaxFieldSumsPerBlock = { 3, 2, 2, 1, 2, 0, 1, 1, 2, 1, 4, 0, 2, 6 };

        // -1 when unknown (offline and set-up use only: it compares strings).
        public static int IndexOf(string area)
        {
            for (int i = 0; i < All.Length; i++) if (string.Equals(All[i], area, StringComparison.Ordinal)) return i;
            return -1;
        }

        public static bool PerAgency(int index) { return index >= 0 && index < PerAgencyCount; }

        // [add] Sizes for the preallocated arrays, from the number of agencies (6 in multiplayertest).
        public static int Blocks(int agencies) { return PerAgencyCount * agencies + (All.Length - PerAgencyCount); }

        public static int FieldSums(int agencies)
        {
            int perAgency = 0, world = 0;
            for (int i = 0; i < All.Length; i++) { if (PerAgency(i)) perAgency += MaxFieldSumsPerBlock[i]; else world += MaxFieldSumsPerBlock[i]; }
            return perAgency * agencies + world;
        }

        // The ItemCapture size: the sum of every block's cap, and of every cap times its values per item.
        public static int CaptureItems(int agencies)
        {
            long perAgency = 0, world = 0;
            for (int i = 0; i < All.Length; i++) { if (PerAgency(i)) perAgency += MaxItems[i]; else world += MaxItems[i]; }
            return Clamp(perAgency * agencies + world);
        }

        public static int CaptureValues(int agencies)
        {
            long perAgency = 0, world = 0;
            for (int i = 0; i < All.Length; i++)
            {
                long v = (long)MaxItems[i] * MaxValuesPerItem[i];
                if (PerAgency(i)) perAgency += v; else world += v;
            }
            return Clamp(perAgency * agencies + world);
        }

        static int Clamp(long n) { return n > int.MaxValue ? int.MaxValue : (int)n; }
    }
}
