namespace SolarExpanseMultiplayer
{
    // The production row a "send supplies to orbit" click (F10) was on: the site, the queue entry's content id and its
    // place in the site's production queue (the construct itself has no id of its own until it is paid for, so the
    // queue place is what every game can find it by), and the direction. Kept from the click to the plan it starts.
    public sealed class SupplyClick
    {
        public int Site;
        public string Item;
        public int Index;
        public bool Down;     // the construct is on a planet and the supplies come down from its orbit
    }

    // Where a click's row waits for the plan it starts. The game plans a supply flight a few calls after the click
    // (the row's handler queues the plan, the planner computes it, then PlanFlyCode is asked to schedule it), so the
    // click is noted for the frame it happened in, bound to the first supply plan queued in that frame, and taken
    // when that plan reaches PlanFlyCode. A note nothing queued in its frame goes stale and binds nothing.
    public sealed class SupplyMemory
    {
        SupplyClick pending;
        int pendingFrame = -1;
        readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SupplyClick> bound =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, SupplyClick>();

        public void Note(SupplyClick click, int frame)
        {
            pending = click;
            pendingFrame = frame;
        }

        // Attaches the click noted in this frame to the plan it queued; false when there is none.
        public bool Attach(object plan, int frame)
        {
            if (plan == null || pending == null || pendingFrame != frame) return false;
            bound.Remove(plan);
            bound.Add(plan, pending);
            pending = null;
            return true;
        }

        // The click a plan was bound to, once (null for a plan nobody clicked for).
        public SupplyClick Take(object plan)
        {
            SupplyClick click;
            if (plan == null || !bound.TryGetValue(plan, out click)) return null;
            bound.Remove(plan);
            return click;
        }

        public bool HasPending(int frame) { return pending != null && pendingFrame == frame; }

        public void Clear()
        {
            pending = null;
            pendingFrame = -1;
        }
    }
}
