using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public static class CraftIdUndoKinds { public const int RemoveFake = 1, RestoreId = 2, RestoreCount = 3; }

    // One step of the undo. RemoveFake: Key names the fake craft to remove (Value unused). RestoreId: give Key's craft
    // Value as its id again. RestoreCount: set the counter named by Key ("SC" Spacecraft.count, "LV"
    // LaunchVehicle.count) to Value.
    public sealed class CraftIdUndo { public int What; public string Key; public long Value; }

    // The host's acceptance moves no craft id (revision 15, DD-F2). Planning a facility craft makes a fake ship once
    // per (facility, index) and numbers it with highest id + 1; left behind, the host's own copy would number one
    // higher than every client's (the spike's S88 against S87). FlightHost.Accept feeds this inside a try/finally and
    // runs the undo End returns in the finally, in order.
    public sealed class CraftIdScope
    {
        sealed class Seen { public string Key; public long IdBefore; public bool CachedBefore; }

        readonly List<Seen> seen = new List<Seen>();
        readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        bool open;
        long scBefore, lvBefore;

        public void Begin(long spacecraftCount, long launchVehicleCount)
        {
            // a scope still open would be an acceptance that never ended: its undo must not be lost quietly
            if (open) throw new InvalidOperationException("the previous scope was never ended");
            seen.Clear(); keys.Clear();
            scBefore = spacecraftCount; lvBefore = launchVehicleCount;
            open = true;
        }

        // A fake facility craft the step touched, by key "F<type>#<index>" (or "L<type>#<index>" for a fake launch
        // vehicle): its id before the step and whether the site's cache held it then. Only the first sighting counts,
        // because a second one already sees what the step did.
        public void Touched(string key, long idBefore, bool cachedBefore)
        {
            if (!open) throw new InvalidOperationException("Touched before Begin");
            if (key == null || key.Length < 3 || (key[0] != 'F' && key[0] != 'L') || key.IndexOf('#') < 0)
                throw new ArgumentException("not a fake craft key (F<type>#<index> or L<type>#<index>)", "key");
            if (!keys.Add(key)) return;
            seen.Add(new Seen { Key = key, IdBefore = idBefore, CachedBefore = cachedBefore });
        }

        // What to undo: every fake the step made removed, every cached fake given its old id back, then both counters
        // restored to Begin's values. Empty when the step touched nothing and moved no counter.
        public IList<CraftIdUndo> End(long spacecraftCountNow, long launchVehicleCountNow)
        {
            if (!open) throw new InvalidOperationException("End before Begin");
            open = false;
            var undo = new List<CraftIdUndo>();
            foreach (Seen s in seen)
                if (!s.CachedBefore)
                    undo.Add(new CraftIdUndo { What = CraftIdUndoKinds.RemoveFake, Key = s.Key });
            foreach (Seen s in seen)
                if (s.CachedBefore)
                    undo.Add(new CraftIdUndo { What = CraftIdUndoKinds.RestoreId, Key = s.Key, Value = s.IdBefore });
            if (spacecraftCountNow != scBefore)
                undo.Add(new CraftIdUndo { What = CraftIdUndoKinds.RestoreCount, Key = "SC", Value = scBefore });
            if (launchVehicleCountNow != lvBefore)
                undo.Add(new CraftIdUndo { What = CraftIdUndoKinds.RestoreCount, Key = "LV", Value = lvBefore });
            seen.Clear(); keys.Clear();
            return undo;
        }
    }
}
