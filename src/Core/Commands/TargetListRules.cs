using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The energy-transfer and mirror-targeting windows' edits, judged as the whole list the player ends with
    // (EnergyTargets, MirrorTargets). Each window lets a player add a row, pick a row's target through a search filter,
    // edit its share and trash it; the game's own rows (an orbit's link to its body) are fixed. So a new list is one
    // the window could have made when it keeps every fixed row, names each body at most once, picks only bodies the
    // filter offers and stays inside the shares the window clamps to.
    public static class TargetListRules
    {
        // A float's worth of slack on the sum of shares: the window sums floats it clamped one by one.
        public const double ShareSlack = 1e-4;

        // A row with no target yet (Body 0): the windows' add-row makes one (D 133672, 136193) and the game skips it.
        public const int NoTarget = 0;

        // The energy window's own rows, which it will not let the player trash or retarget (D 133713-133733): an
        // orbit's link to its body, and a body's link to its orbit.
        public static bool EnergyRowFixed(bool onOrbit, int parent, int target, bool targetIsOrbit, int targetParent,
            int body)
        {
            if (target == NoTarget) return false;
            return (onOrbit && target == parent) || (targetIsOrbit && targetParent == body);
        }

        // EnergyTransferWindow (D 133626-133785). fixedTargets: the current list's rows the window will not let the
        // player trash or retarget; body, parent: the facility's body and its parent (0 when none); receiver(b): the
        // acting agency has an energy receiver at body b (the search filter, D 133772-133779).
        public static int EnergyProblem(TargetRow[] rows, IList<int> fixedTargets, int body, int parent,
            Func<int, bool> receiver)
        {
            if (rows == null) return RefuseCodes.BadTarget;
            var seen = new HashSet<int>();
            double sum = 0;
            foreach (TargetRow r in rows)
            {
                if (r == null) return RefuseCodes.BadTarget;
                if (!(r.Ratio >= 0) || r.Ratio > 1) return RefuseCodes.NotAllowedHere;
                sum += r.Ratio;
                if (r.Body == NoTarget) continue;
                if (!seen.Add(r.Body)) return RefuseCodes.BadTarget;
                bool isFixed = fixedTargets != null && fixedTargets.Contains(r.Body);
                if (isFixed) continue;
                if (r.Body == body || r.Body == parent || !receiver(r.Body)) return RefuseCodes.BadTarget;
            }
            if (fixedTargets != null)
                foreach (int f in fixedTargets) if (!seen.Contains(f)) return RefuseCodes.NotAllowedHere;
            return sum > 1 + ShareSlack ? RefuseCodes.TooManyUnits : 0;
        }

        // MirrorTargetingWindow (D 136147-136305). The request carries the whole list: the default rows first, as the
        // facility made them (their targets fixed, their counts editable: the allocation puts every spare mirror on
        // the first row, so a count can only move off it by lowering it), then the rows the player added.
        // defaultTargets: the current default rows' targets, in order; target(b): the search filter's own test (a
        // planet, moon, comet, dwarf planet, protoplanet or asteroid the acting agency knows of). Every mirror counted
        // in the rows is one of the facility's units.
        public static int MirrorProblem(TargetRow[] rows, IList<int> defaultTargets, long quantity, int body,
            int parent, Func<int, bool> target)
        {
            if (rows == null) return RefuseCodes.BadTarget;
            int k = defaultTargets != null ? defaultTargets.Count : 0;
            if (rows.Length < k) return RefuseCodes.NotAllowedHere;
            var seen = new HashSet<int>();
            long sum = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                TargetRow r = rows[i];
                if (r == null) return RefuseCodes.BadTarget;
                if (r.Count < 0) return RefuseCodes.TooManyUnits;
                sum += r.Count;
                if (i < k)
                {
                    if (r.Body != defaultTargets[i]) return RefuseCodes.NotAllowedHere;
                    if (r.Body != NoTarget) seen.Add(r.Body);
                    continue;
                }
                if (r.Body == NoTarget) continue;
                if (!seen.Add(r.Body)) return RefuseCodes.BadTarget;
                if (r.Body == body || r.Body == parent || !target(r.Body)) return RefuseCodes.BadTarget;
            }
            return sum > quantity ? RefuseCodes.TooManyUnits : 0;
        }
    }
}
