using System;
using System.Collections.Generic;
using System.Linq;

namespace SolarExpanseMultiplayer
{
    // The research queue as the tree's buttons edit it (ResearchTreeElement.AddRemoveQueuePart1, DeleteFromQueue),
    // worked out on a copy: the capture sends the whole list the player now means, and the game's own queue changes
    // only when the host's entry arrives.
    public static class QueueEdits
    {
        // Up (add: true) or down one place, as AddRemoveQueuePart1 does it, edge clamps included. A topic that is not
        // queued goes in at the front, as the game's own code would put it.
        public static string[] Move(IList<string> queue, string id, bool up)
        {
            var list = new List<string>(queue);
            int from = list.IndexOf(id);
            list.Remove(id);
            int to = from + (up ? -1 : 1);
            if (to > list.Count) to = list.Count;
            if (to < 0) to = 0;
            list.Insert(to, id);
            return list.ToArray();
        }

        // DeleteFromQueue: the topic goes, then every queued topic that needs something that is neither finished nor
        // still queued. `needs(topic)` is GetAllRDNeed: the topics the game says it takes to get there, itself last.
        // The game's safety stop of 1000 rounds is kept.
        public static string[] Remove(IList<string> queue, string id, Func<string, IList<string>> needs)
        {
            var list = new List<string>(queue);
            list.Remove(id);
            string drop = null;
            int rounds = 0;
            while (true)
            {
                if (drop != null) list.Remove(drop);
                rounds++;
                string next = null;
                foreach (string item in list)
                {
                    foreach (string need in needs(item))
                        if (need != item && !list.Contains(need)) { next = item; break; }
                    if (next != null) break;
                }
                if (next == null || rounds >= 1000) break;
                drop = next;
            }
            return list.ToArray();
        }
    }

    // The two target-list windows' edits, worked out on a copy of the facility's list. Energy rows carry the game's
    // fraction (TransferTargetInfo.ratio), never the percentage the field shows; a row with no target yet is Body 0.
    // Mirror rows carry the unit count, the default rows first (the list's own order).
    public static class TargetEdits
    {
        public static TargetRow[] Copy(IList<TargetRow> rows)
        {
            var copy = new TargetRow[rows.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = new TargetRow { Body = rows[i].Body, Ratio = rows[i].Ratio, Count = rows[i].Count };
            return copy;
        }

        // The game sums a float field through LINQ, which adds in double and returns a float.
        static float Total(IList<TargetRow> rows)
        {
            double sum = 0;
            for (int i = 0; i < rows.Count; i++) sum += (float)rows[i].Ratio;
            return (float)sum;
        }

        static long TotalCount(IList<TargetRow> rows)
        {
            long sum = 0;
            for (int i = 0; i < rows.Count; i++) sum += rows[i].Count;
            return sum;
        }

        static TargetRow[] Appended(IList<TargetRow> rows, TargetRow row)
        {
            TargetRow[] copy = Copy(rows);
            Array.Resize(ref copy, copy.Length + 1);
            copy[copy.Length - 1] = row;
            return copy;
        }

        static TargetRow[] Without(IList<TargetRow> rows, int index)
        {
            var list = new List<TargetRow>(Copy(rows));
            list.RemoveAt(index);
            return list.ToArray();
        }

        // ---- energy transfer ----

        // The add-row button: a row with no target and what share is left.
        public static TargetRow[] AddEnergyRow(IList<TargetRow> rows)
        {
            float left = 1f - Total(rows);
            return Appended(rows, new TargetRow { Body = 0, Ratio = left });
        }

        public static TargetRow[] SetEnergyTarget(IList<TargetRow> rows, int index, int body)
        {
            if (index < 0 || index >= rows.Count) return null;
            TargetRow[] copy = Copy(rows);
            copy[index].Body = body;
            return copy;
        }

        // RatioInputFieldEndEdit: the field's percentage, or the row's own share when the text is no number, held to
        // what the other rows leave.
        public static TargetRow[] SetEnergyShare(IList<TargetRow> rows, int index, string percentText)
        {
            if (index < 0 || index >= rows.Count) return null;
            float own = (float)rows[index].Ratio;
            float b;
            b = float.TryParse(percentText, out b) ? b / 100f : own;
            float others = Total(rows) - own;
            b = Math.Min(1f - others, b);
            b = b < 0f ? 0f : b > 1f ? 1f : b;
            TargetRow[] copy = Copy(rows);
            copy[index].Ratio = b;
            return copy;
        }

        // The row of `baseRows` that is the one the player clicked in a window still showing `localRows`: the same
        // content, nearest to where the window shows it. A window not yet redrawn after an earlier edit shows rows
        // the base no longer has; -1 then, and nothing is sent for a click on a row that is gone.
        public static int Locate(IList<TargetRow> baseRows, IList<TargetRow> localRows, int localIndex)
        {
            if (localIndex < 0 || localIndex >= localRows.Count) return -1;
            TargetRow want = localRows[localIndex];
            int best = -1;
            for (int i = 0; i < baseRows.Count; i++)
            {
                TargetRow r = baseRows[i];
                if (r.Body != want.Body || r.Count != want.Count || Math.Abs(r.Ratio - want.Ratio) > 1e-6) continue;
                if (best < 0 || Math.Abs(i - localIndex) < Math.Abs(best - localIndex)) best = i;
            }
            return best;
        }

        public static TargetRow[] RemoveRow(IList<TargetRow> rows, int index)
        {
            return index < 0 || index >= rows.Count ? null : Without(rows, index);
        }

        // ---- mirror targeting ----

        // The add-row button: a row with no target and the units not yet given out.
        public static TargetRow[] AddMirrorRow(IList<TargetRow> rows, long quantity)
        {
            long left = Math.Max(0L, quantity - TotalCount(rows));
            return Appended(rows, new TargetRow { Body = 0, Count = Clamp(left) });
        }

        public static TargetRow[] SetMirrorTarget(IList<TargetRow> rows, int index, int body)
        {
            return SetEnergyTarget(rows, index, body);
        }

        // MirrorsInputFieldEndEdit: the typed count, or the row's own when the text is no number, held between 0 and
        // what the other rows leave of the facility's units.
        public static TargetRow[] SetMirrorCount(IList<TargetRow> rows, int index, string text, long quantity)
        {
            if (index < 0 || index >= rows.Count) return null;
            long result;
            if (!long.TryParse(text, out result)) result = rows[index].Count;
            long others = TotalCount(rows) - rows[index].Count;
            result = Math.Min(quantity - others, result);
            TargetRow[] copy = Copy(rows);
            copy[index].Count = Clamp(result);   // never below nothing, never over the wire cap
            return copy;
        }

        static int Clamp(long n)
        {
            return n < 0 ? 0 : n > ActionLimits.MaxUnits ? ActionLimits.MaxUnits : (int)n;
        }
    }

    // P1's decision as the construction window makes it (OnClickAcceptPart1): the largest count whose resources are
    // there, then the payment for that count, money included; short of money the window offers the queue.
    public static class CraftBuyRules
    {
        public static bool BuildsNow(int count, Func<int, bool> haveResources, Func<int, bool> haveMoney)
        {
            int n = 0;
            for (int k = count; k >= 1; k--) if (haveResources(k)) { n = k; break; }
            return n > 0 && haveMoney(n);
        }
    }

    // B11: a module drop is held to MaxMoveNewIds new ids a request (a type that never stacks makes one id per unit),
    // so a bigger one goes as several requests, each leaving the source standing until the last.
    public static class MoveSplit
    {
        public static int[] Chunks(int count, bool blockStacking)
        {
            if (count < 1) return new int[0];
            if (!blockStacking || count <= ActionLimits.MaxMoveNewIds) return new[] { count };
            // each part is smaller than the one before: CommandClient drops a request identical to one still
            // pending as a double click, and equal parts would be dropped (up to 2080 units fit; more is refused)
            var parts = new List<int>();
            int cap = ActionLimits.MaxMoveNewIds;
            for (int left = count; left > 0;)
            {
                if (cap < 1) return new int[0];
                int part = Math.Min(left, cap);
                parts.Add(part);
                left -= part;
                cap = part - 1;
            }
            return parts.ToArray();
        }
    }
}
