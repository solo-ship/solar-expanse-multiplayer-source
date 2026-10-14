using System;
using System.Collections.Generic;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // One unfinished build of a site, as the print and the copy read it.
    public struct BuildFact
    {
        public long ItemId; public string Item; public bool Craft; public int CountToBuild; public int CountBuild;
    }

    // One facility of a site.
    public struct FacilityFact
    {
        public long ItemId; public string Item; public long Quantity; public long Enabled; public bool Built;
    }

    // The rows of the day-boundary prints (DD10), apart from the game, so the host's print and every client's are made
    // by the same code. Discrete only: ids, counts, order and flags, never progress, money or stock, which drift by a
    // frame between games (drift.md section 1). Ship names are never printed (N7).
    public static class ObjectRows
    {
        // An agency's research: the running topic, the queue in order and the started-not-finished topics in the
        // game's order (StartResearch moves topics in and out of it the same way on every game).
        public static List<PrintRow> Research(string running, IList<string> queue, IList<string> paused)
        {
            return Research(running, queue, paused, null);
        }

        // The same with the topics the agency has finished, as one row: their count and an order-independent hash.
        // A topic finishes on whichever frame crosses its end, on each game alone, and a restore cannot take a finished
        // topic back or give one, so a finished set that differs must show in the print (it is then out of step).
        // null leaves the row out.
        public static List<PrintRow> Research(string running, IList<string> queue, IList<string> paused,
            IList<string> completed)
        {
            var rows = new List<PrintRow>();
            rows.Add(Row("run", running ?? "", 0, 0, 0));
            for (int i = 0; queue != null && i < queue.Count; i++) rows.Add(Row("queue", queue[i] ?? "", i, 0, 0));
            for (int i = 0; paused != null && i < paused.Count; i++) rows.Add(Row("paused", paused[i] ?? "", i, 0, 0));
            if (completed != null)
                rows.Add(Row("done", SetHash(completed).ToString("x16", CultureInfo.InvariantCulture), completed.Count,
                    0, 0));
            return rows;
        }

        // A set's hash that does not depend on the order of its ids: the sum of each id's FNV-1a 64 hash of its UTF-8
        // bytes (each id once, so the sum is the set's).
        public static ulong SetHash(IList<string> ids)
        {
            ulong sum = 0;
            for (int i = 0; ids != null && i < ids.Count; i++)
            {
                ulong h = 14695981039346656037UL;
                foreach (byte b in System.Text.Encoding.UTF8.GetBytes(ids[i] ?? ""))
                {
                    h ^= b;
                    h *= 1099511628211UL;
                }
                sum = unchecked(sum + h);
            }
            return sum;
        }

        // An agency's builds at a site: its item counter, the unfinished builds in the game's order and the queue.
        public static List<PrintRow> Builds(long productionCount, IList<BuildFact> builds, IList<QueueRow> queue)
        {
            var rows = new List<PrintRow>();
            rows.Add(Row("count", "production", productionCount, 0, 0));
            for (int i = 0; builds != null && i < builds.Count; i++)
            {
                BuildFact b = builds[i];
                rows.Add(Row(b.Craft ? "craft" : "build", b.Item ?? "", b.ItemId, b.CountToBuild, b.CountBuild));
            }
            for (int i = 0; queue != null && i < queue.Count; i++)
                rows.Add(Row("queue", queue[i] != null ? queue[i].Item ?? "" : "", i,
                    queue[i] != null ? queue[i].Count : 0, 0));
            return rows;
        }

        // An agency's built facilities at a site (in the game's order), the priority of each of their types (sorted:
        // the game keeps them in a dictionary), and the deposits selected for mining.
        public static List<PrintRow> Facilities(IList<FacilityFact> facilities, IDictionary<string, int> priorities,
            IList<string> mining)
        {
            var rows = new List<PrintRow>();
            for (int i = 0; facilities != null && i < facilities.Count; i++)
            {
                FacilityFact f = facilities[i];
                if (!f.Built) continue;
                rows.Add(Row("fac", f.Item ?? "", f.ItemId, f.Quantity, f.Enabled));
            }
            if (priorities != null)
            {
                var keys = new List<string>(priorities.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys) rows.Add(Row("prio", k, priorities[k], 0, 0));
            }
            for (int i = 0; mining != null && i < mining.Count; i++) rows.Add(Row("mine", mining[i] ?? "", i, 0, 0));
            return rows;
        }

        // A selected deposit, named by its resource and its position among the agency's rows at the site (a deposit
        // row has no id of its own, docs/research/commands.md "B10").
        public static string MiningKey(string resource, int position)
        {
            return (resource ?? "") + "@" + position.ToString(CultureInfo.InvariantCulture);
        }

        // The number a craft's name was made from: the game names it "<type name> SN:<n>" (Company.GetUniqueRocketName,
        // D 102137), so the number is the digits after the last " SN:". 0 when the name has none.
        public static long NameNumber(string name)
        {
            const string mark = " SN:";
            if (string.IsNullOrEmpty(name)) return 0;
            int at = name.LastIndexOf(mark, StringComparison.Ordinal);
            if (at < 0) return 0;
            string digits = name.Substring(at + mark.Length);
            if (digits.Length == 0 || digits.Length > 9) return 0;
            foreach (char c in digits) if (c < '0' || c > '9') return 0;
            return long.Parse(digits, CultureInfo.InvariantCulture);
        }

        static PrintRow Row(string a, string b, long c, long d, long e)
        {
            return new PrintRow { A = a, B = b, C = c, D = d, E = e };
        }
    }
}
