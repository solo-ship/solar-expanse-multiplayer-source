using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // What the game reads of one mission for a print (Task 9's FlightRead fills it). It deliberately has no phase, fuel
    // or stock: those differ by a fixed step between two games without the mission being wrong.
    public sealed class MissionRow
    {
        public long MissionId; public int Start, Target; public long DepartTicks, ArriveTicks;
        public string[] Crafts;  // CraftRef keys: "S<id>", "R<id>", "C<item id>", "F<type>#<index>", "K" (container)
        public bool Cancelled;
        public long LeftOverFuelBits;   // copied exactly at load (revision 14, ruling 8)
        // logged, never printed: the game only labels a mission with it (its analytics), and a copy's path sets it
        // its own way (the fastest button, 2) where the host's planner set another
        public int CostType;
    }

    // The Missions print (M5's DD10 for flights): one agency's missions on each day it created, cancelled, launched or
    // landed one. Rows come from FlightRead's own index keyed by mission id, never from list order.
    public static class MissionPrint
    {
        // ids, bodies, dates, crafts, the cancel flag and the leftover fuel; no phase, no cost type. The texts carry
        // what does not fit a number; ObjectPrint length-prefixes them, so a moved field cannot hash the same.
        public static PrintRow RowOf(MissionRow m)
        {
            if (m == null) throw new ArgumentNullException("m");
            var crafts = new StringBuilder();
            if (m.Crafts != null)
                for (int i = 0; i < m.Crafts.Length; i++)
                {
                    if (i > 0) crafts.Append(',');
                    crafts.Append(m.Crafts[i]);
                }
            string rest = string.Format(CultureInfo.InvariantCulture, "{0}>{1}|{2}|{3}",
                m.Start, m.Target, m.Cancelled ? 1 : 0, m.LeftOverFuelBits);
            return new PrintRow
            {
                A = crafts.ToString(), B = rest, C = m.MissionId, D = m.DepartTicks, E = m.ArriveTicks
            };
        }

        // What the after-check expects of a created mission: the applied entry's own values. An entry carries no cost
        // type, so it is 0 here; the fuel row's amount is the leftover fuel (revision 14, ruling 3).
        public static MissionRow FromEntry(MissionData applied)
        {
            if (applied == null) throw new ArgumentNullException("applied");
            var crafts = new List<string>();
            AddCrafts(crafts, applied.Ships);
            AddCrafts(crafts, applied.Rockets);
            long leftover = 0;
            if (applied.Cargo != null)
                foreach (CargoRow c in applied.Cargo)
                {
                    if (c == null || c.Kind != CargoKinds.Fuel) continue;
                    leftover = BitConverter.DoubleToInt64Bits(c.Amount);
                    break;
                }
            return new MissionRow
            {
                MissionId = applied.MissionId, Start = applied.Start, Target = applied.Target,
                DepartTicks = applied.DepartTicks, ArriveTicks = applied.ArriveTicks,
                Crafts = crafts.ToArray(), Cancelled = false, LeftOverFuelBits = leftover, CostType = 0
            };
        }

        // null when a mission made from an applied entry is what the entry says (its printed fields), else what
        // differs. The cost type is not compared: an entry carries none, and the copy path sets its own.
        public static string AfterCheck(MissionRow made, MissionData applied)
        {
            if (made == null) throw new ArgumentNullException("made");
            MissionRow expected = FromEntry(applied);
            PrintRow got = RowOf(made), want = RowOf(expected);
            if (got.A == want.A && got.B == want.B && got.C == want.C && got.D == want.D && got.E == want.E) return null;
            return "mission " + applied.MissionId + " came out as " + got.A + " " + got.B + " " + got.C + " " + got.D
                + " " + got.E + ", the entry says " + want.A + " " + want.B + " " + want.C + " " + want.D + " " + want.E;
        }

        static void AddCrafts(List<string> into, CraftRef[] refs)
        {
            if (refs == null) return;
            foreach (CraftRef c in refs) into.Add(KeyOf(c));
        }

        // The craft's key as MissionRow.Crafts spells it.
        public static string KeyOf(CraftRef c)
        {
            if (c == null) return "";
            switch (c.Kind)
            {
                case CraftKinds.Ship: return "S" + c.Id.ToString(CultureInfo.InvariantCulture);
                case CraftKinds.Rocket: return "R" + c.Id.ToString(CultureInfo.InvariantCulture);
                case CraftKinds.Construct: return "C" + c.Id.ToString(CultureInfo.InvariantCulture);
                case CraftKinds.Facility: return "F" + c.Type + "#" + c.Index.ToString(CultureInfo.InvariantCulture);
                case CraftKinds.Container: return "K";
                default: return "?" + c.Kind.ToString(CultureInfo.InvariantCulture);
            }
        }

        // The day's rows, by mission id: the live missions, plus those landed on `day` (by arrival) or cancelled by an
        // entry of `day`. dayOfTicks gives the day an arrival counts for. Older landings and older cancels are dropped:
        // they were printed on their own day and a joiner's list no longer has them.
        public static IList<MissionRow> ForDay(IEnumerable<MissionRow> all, long day, Func<long, long> dayOfTicks,
                                               ICollection<long> cancelledToday)
        {
            if (all == null) throw new ArgumentNullException("all");
            if (dayOfTicks == null) throw new ArgumentNullException("dayOfTicks");
            var keep = new List<MissionRow>();
            foreach (MissionRow m in all)
            {
                if (m == null) continue;
                bool today = cancelledToday != null && cancelledToday.Contains(m.MissionId);
                bool take = m.Cancelled ? today : dayOfTicks(m.ArriveTicks) >= day;
                if (take) keep.Add(m);
            }
            keep.Sort(ById);
            return keep;
        }

        static int ById(MissionRow a, MissionRow b) { return a.MissionId.CompareTo(b.MissionId); }

        // The rows as readable text for the log (a Missions print is only a hash): "<id> <start>><target> <depart>
        // <arrive> <crafts> c<cancelled> t<cost type> f<fuel bits>", by id, at most `max` rows.
        public static string Text(IList<MissionRow> rows, int max)
        {
            var sorted = new List<MissionRow>();
            if (rows != null) foreach (MissionRow m in rows) if (m != null) sorted.Add(m);
            sorted.Sort(ById);
            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count && i < max; i++)
            {
                MissionRow m = sorted[i];
                PrintRow p = RowOf(m);
                if (i > 0) sb.Append("; ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0} {1}>{2} {3} {4} {5} c{6} t{7} f{8:x}",
                    m.MissionId, m.Start, m.Target, m.DepartTicks, m.ArriveTicks, p.A, m.Cancelled ? 1 : 0,
                    m.CostType, m.LeftOverFuelBits));
            }
            if (sorted.Count > max) sb.Append("; (+" + (sorted.Count - max) + " more)");
            return sb.ToString();
        }

        // ObjectPrint.Of(Missions, agency, 0, rows by id): the same text on every game whatever order its list is in.
        public static string Of(string agency, IList<MissionRow> rows)
        {
            var sorted = new List<MissionRow>();
            if (rows != null) sorted.AddRange(rows);
            sorted.Sort(ById);
            var printed = new List<PrintRow>(sorted.Count);
            foreach (MissionRow m in sorted) printed.Add(RowOf(m));
            return ObjectPrint.Of(ObjectKind.Missions, agency, 0, printed);
        }
    }
}
