using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // M7 Task 4: one year event's population growth rows on a client, every part. Rows are taken in any order; a row is
    // taken once (a repeated (agency, body) pair queues its deltas in the order sent).
    public sealed class GrowthBook
    {
        readonly Dictionary<string, Queue<int>> rows = new Dictionary<string, Queue<int>>();
        readonly List<string> order = new List<string>();

        public GrowthBook(IList<GrowthRow[]> parts)
        {
            if (parts == null) return;
            for (int p = 0; p < parts.Count; p++)
            {
                GrowthRow[] part = parts[p];
                if (part == null) continue;
                for (int i = 0; i < part.Length; i++)
                {
                    GrowthRow r = part[i];
                    if (r == null) continue;
                    string key = Key(r.Agency, r.Body);
                    Queue<int> q;
                    if (!rows.TryGetValue(key, out q)) { q = new Queue<int>(); rows[key] = q; order.Add(key); }
                    q.Enqueue(r.Delta);
                }
            }
        }

        static string Key(string agency, int body) { return (agency ?? "") + "/" + body; }

        // false: a body the host did not roll (or one already taken).
        public bool TryTake(string agency, int body, out int delta)
        {
            delta = 0; Queue<int> q;
            if (!rows.TryGetValue(Key(agency, body), out q) || q.Count == 0) return false;
            delta = q.Dequeue();
            return true;
        }

        // null, or the rows never consumed ("agency/body" pairs, the first eight).
        public string Leftovers()
        {
            int total = 0, listed = 0; var sb = new StringBuilder();
            foreach (string key in order)
            {
                int n = rows[key].Count;
                if (n == 0) continue;
                if (listed < 8) { if (sb.Length > 0) sb.Append(", "); sb.Append(key); listed++; }
                total += n;
            }
            return total == 0 ? null : total + " growth row(s) never taken: " + sb + (total > listed ? ", ..." : "");
        }
    }
}
