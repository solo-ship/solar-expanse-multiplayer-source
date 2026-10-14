using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What the construction window knows about one ship or launch-vehicle type at one body, for the ACTING agency
    // (docs/research/commands.md "K10", P1 and P2). The game glue reads these from the agency's own data at the body,
    // never through the window (it reads the local player) and never through GetObjectInfoData (its fallback hands back
    // another object's data).
    public sealed class CraftFacts
    {
        public bool Known;              // a spacecraft or launch-vehicle type with exactly this id
        public bool LaunchVehicle;
        public bool Unlocked;           // the type is in the agency's unlocked list
        public bool HasSite;            // the agency has data at the window's body
        public double Assembly;         // VehicleAssemblyCountEnable there
        // a ship that is built in orbit: the agency's data at its assembly orbit (the body's low orbit, or the body)
        public bool OrbitOnly;
        public bool HasOrbitSite;
        public double OrbitAssembly;
        // a launch vehicle: the game's own per-type rules at the body
        public bool LvWithoutAssembly, LvOnThisBody, LvParameter;
    }

    // One row of a site's build list as the player sees it and as the day-boundary print orders it: the unfinished
    // builds in the game's order, then the queued (unpaid) entries in theirs. Position in a request is an index in this
    // list (ActionLimits.MaxPosition covers both parts).
    public sealed class SiteRow
    {
        public long ItemId = -1;        // -1 for a queued entry
        public string Item;             // the content id
        public bool Craft;              // a ship or launch vehicle, not a facility
        public bool Queued;
        public int Remaining;           // units still to build (a queued entry: its count)
    }

    // P1-P4 (DD3): the construction window's and the build list's checks, repeated for the acting agency on the host.
    // Pure, so each refusal is a test rather than a game run. P5 (buy what is missing) is refused in ActionChecks.
    public static class ProductionCommand
    {
        // the counts the window makes: one, or ten with Left Shift (D 139200-139204)
        public const int OneClick = 1, ShiftClick = 10;

        // NameCounter rows: AgencyState holds at most MaxStateCounters, two of them game-wide, one item counter per
        // agency and up to MaxRivalCounters rivals' item counters, so each agency's name counters are capped to fit
        // every seat; a site's copy carries the item counter and at most MaxCounters - 1 name counters
        public const int NameRowsPerAgency =
            (ActionLimits.MaxStateCounters - 2 - ActionLimits.MaxRivalCounters) / JoinLimits.MaxPlayers - 1;
        public const int NameRowsPerSite = ActionLimits.MaxCounters - 1;

        public static bool Handles(int kind)
        {
            return kind == ActionKind.CraftBuild || kind == ActionKind.CraftQueue || kind == ActionKind.CraftCancel
                || kind == ActionKind.CraftReorder;
        }

        // P1 and P2: is this type's row on in the window, for this agency at this body? The window's list-wide
        // switches are overridden by each row's own check (D 139694-139716), so the row check is the whole rule.
        public static int CheckCraft(CraftFacts f, int count)
        {
            if (f == null || !f.Known) return RefuseCodes.BadTarget;
            if (count != OneClick && count != ShiftClick) return RefuseCodes.BadTarget;
            if (!f.Unlocked) return RefuseCodes.Locked;
            if (!f.HasSite) return RefuseCodes.BadTarget;
            if (f.LaunchVehicle)
            {
                bool assembly = f.Assembly > 0 || f.LvWithoutAssembly;
                return assembly && f.LvOnThisBody && f.LvParameter ? 0 : RefuseCodes.NotAllowedHere;
            }
            // a ship needs a vehicle assembly where it is built: an orbit-only ship at its orbit, any other at the body
            if (!f.OrbitOnly) return f.Assembly > 0 ? 0 : RefuseCodes.NotAllowedHere;
            if (!f.HasOrbitSite) return RefuseCodes.NotAllowedHere;
            return f.OrbitAssembly > 0 ? 0 : RefuseCodes.NotAllowedHere;
        }

        // P1: the window's count (D 139241-139252): the largest number of units, from count down, whose resources the
        // site holds; then the money for that many, which the window does not count down for: short of it, the payment
        // fails and the window offers the queue (P2) instead. 0 = that queue case, refused CannotAfford.
        public static int Affordable(int count, Func<int, bool> haveResources, Func<int, bool> haveMoney)
        {
            if (haveResources == null || haveMoney == null) return 0;
            for (int n = count; n >= 1; n--)
                if (haveResources(n)) return haveMoney(n) ? n : 0;
            return 0;
        }

        // Where P1 and P2 put the build (D 139223-139230, 139606): an orbit-only ship ordered at a body that is not
        // an orbit, not a pushed asteroid, and has a low orbit that is an orbit, is built on that low orbit.
        public static bool BuildsInLowOrbit(bool orbitOnly, bool bodyIsOrbit, bool pushedAsteroid, bool hasLow,
            bool lowIsOrbit)
        {
            return orbitOnly && !bodyIsOrbit && !pushedAsteroid && hasLow && lowIsOrbit;
        }

        // A client's P1 made the item id the host's entry names: an entry with no id, or another one, is out of step.
        public static bool NewIdMatches(long[] given, long made)
        {
            return given != null && given.Length == 1 && given[0] == made;
        }

        // P3: the row a cancel names. A build is named by its item id at its position; a queued entry (item id -1) by
        // its position and type. Anything else is a list that changed under the player.
        public static int CancelTarget(IList<SiteRow> rows, ActionRequest a, out int index)
        {
            index = -1;
            if (rows == null || a == null) return RefuseCodes.BadTarget;
            SiteRow r = At(rows, a.Position);
            if (r == null || r.Item != a.Item || !r.Craft) return RefuseCodes.TargetChanged;
            if (a.ItemId < 0)
            {
                if (!r.Queued) return RefuseCodes.TargetChanged;
            }
            else if (r.Queued || r.ItemId != a.ItemId) return RefuseCodes.TargetChanged;
            // the cancel button is shown only for an unfinished build (D 166687-166693)
            if (r.Remaining < 1) return RefuseCodes.AlreadyDone;
            index = a.Position;
            return 0;
        }

        // P4: the dragged build and the build it is dropped on. ToPosition 0 is the front of the list (the game's
        // null target); else the row there must be another unfinished ship or launch-vehicle build. Queued entries are
        // never dragged (UIRowRocket.CanDrag, D 167749-167760) and dropping on one moves nothing in the game.
        public static int ReorderTarget(IList<SiteRow> rows, ActionRequest a, out int index, out int before)
        {
            index = -1;
            before = -1;
            if (rows == null || a == null) return RefuseCodes.BadTarget;
            SiteRow r = At(rows, a.Position);
            if (r == null || r.Queued || !r.Craft || r.ItemId != a.ItemId || r.Item != a.Item)
                return RefuseCodes.TargetChanged;
            if (r.Remaining < 1) return RefuseCodes.AlreadyDone;
            if (a.ToPosition == a.Position) return RefuseCodes.AlreadyDone;
            index = a.Position;
            if (a.ToPosition == 0) return 0;
            SiteRow to = At(rows, a.ToPosition);
            if (to == null) return RefuseCodes.TargetChanged;
            if (to.Queued || !to.Craft) return RefuseCodes.NotAllowedHere;
            before = a.ToPosition;
            return 0;
        }

        static SiteRow At(IList<SiteRow> rows, int i) { return i >= 0 && i < rows.Count ? rows[i] : null; }

        // The name counters a state message carries: keys with a count above 0, in the order given (the content's own
        // order, the same on every game), each once and at most `max`. A key that is not a content id is left out:
        // names are display only (N7), and a row the message would refuse must never stop the stream. dropped: how
        // many counted keys the cap left out.
        public static List<KeyValuePair<string, long>> NameRows(IEnumerable<KeyValuePair<string, long>> counts,
            int max, out int dropped)
        {
            var picked = new List<KeyValuePair<string, long>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            dropped = 0;
            if (counts == null) return picked;
            foreach (KeyValuePair<string, long> kv in counts)
            {
                if (kv.Value < 1 || kv.Value > int.MaxValue || !ActionShapes.IsContentId(kv.Key)) continue;
                if (!seen.Add(kv.Key)) continue;
                if (picked.Count < max) picked.Add(kv);
                else dropped++;
            }
            return picked;
        }
    }
}
