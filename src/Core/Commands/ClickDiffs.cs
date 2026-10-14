using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The player kinds the dev trigger "click <n>" can drive through a window (docs/research/commands.md, K10), and how
    // a whole-list request is turned back into the one click that makes it.
    public static class ClickKinds
    {
        // Every player kind: the ones a client may send, which are all the Task 1 entries (R1-R5, B1-B11, P1-P5, the
        // observatory's five and the forget button). Host-decided kinds are never clicked. The flight kinds (60 and up)
        // are driven by the "fly" trigger and the planner captures, not by this table.
        public static int[] Player()
        {
            var kinds = new List<int>();
            for (int k = 1; k < ActionKind.FlightSchedule; k++) if (ActionKind.IsKnown(k) && !ActionKind.HostOnly(k)) kinds.Add(k);
            return kinds.ToArray();
        }
    }

    // A whole queue the script names, as the one tree click that turns the game's queue into it.
    public sealed class QueueChange
    {
        public string Id;
        public bool Remove;   // the topic's remove button
        public bool Up;       // else down, when not removed
    }

    public static class QueueDiff
    {
        // Null when the two queues are not one remove or one step apart.
        public static QueueChange Between(IList<string> current, IList<string> wanted)
        {
            if (wanted.Count < current.Count)
            {
                foreach (string id in current)
                    if (!Contains(wanted, id)) return new QueueChange { Id = id, Remove = true };
                return null;
            }
            if (wanted.Count != current.Count) return null;
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i] == wanted[i]) continue;
                bool swap = i + 1 < current.Count && wanted[i] == current[i + 1] && wanted[i + 1] == current[i];
                return swap ? new QueueChange { Id = current[i + 1], Up = true } : null;
            }
            return null;
        }

        static bool Contains(IList<string> list, string id)
        {
            foreach (string s in list) if (s == id) return true;
            return false;
        }
    }

    public enum TargetOp { None, Add, Remove, SetTarget, SetValue }

    // A whole target list the script names, as the one click in the list window that turns the facility's list into it
    // (first difference first).
    public static class TargetDiff
    {
        public static TargetOp Between(IList<TargetRow> current, IList<TargetRow> wanted, bool mirror, out int index)
        {
            index = -1;
            if (wanted.Count > current.Count) return TargetOp.Add;
            if (wanted.Count < current.Count) { index = current.Count - 1; return TargetOp.Remove; }
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i].Body != wanted[i].Body) { index = i; return TargetOp.SetTarget; }
                bool same = mirror ? current[i].Count == wanted[i].Count
                    : Math.Abs(current[i].Ratio - wanted[i].Ratio) < 1e-6;
                if (!same) { index = i; return TargetOp.SetValue; }
            }
            return TargetOp.None;
        }
    }
}
