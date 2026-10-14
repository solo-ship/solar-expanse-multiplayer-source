using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One row of a site's build list as a building request names it: the site's unfinished items in the game's order
    // (productionItems: facilities and crafts together), then its queued, unpaid entries (listProductionQuery). This
    // is the order the day's SiteBuilds print uses, so a Position means the same row on every game.
    public sealed class BuildListRow
    {
        public long ItemId;      // the item's id; -1 for a queue entry (its placeholder has no id of its own)
        public string Item;      // the content id of what is built
        public bool Queue;       // a queued, unpaid entry
        public bool Facility;    // a facility (not a ship or launch vehicle construct)
    }

    // The building windows' decisions (B1-B5, B7, B8, B10; Task 1's list in docs/research/commands.md "K10"), apart
    // from the game types, so they are tested here. The game side (BuildingChecks) feeds them the ACTING agency's
    // facts, never the local player's. Each returns 0 or a RefuseCodes value; a normalised count comes back through
    // `out`.
    public static class BuildingRules
    {
        // B1: how many to build now, as ObjectInfoWindow.FacilityListOnOnClickCreateFacilityPart1 decides it (D
        // 136777-136841): the click's count capped by HowManyCanBuild, the largest affordable count up to it (resources
        // only: HaveResources ignores money), then the station cap. Then the payment (resources and money) must go
        // through for that count, or the window offers the queue (B2) instead of building: refused CannotAfford.
        //   canBuild: HowManyCanBuild for the descriptor; affordable(n): the site has the resources for n;
        //   pays(n): the payment for n would go through (resources and money); station: the station cap applies;
        //   stationMax, stationHave: the station's room and what is built or building on it (every descriptor).
        public static int BuildNow(int requested, int canBuild, Func<int, bool> affordable, Func<int, bool> pays,
            bool station, int stationMax, int stationHave, out int count)
        {
            count = 0;
            if (requested < 1) return RefuseCodes.BadTarget;
            if (canBuild <= 0) return RefuseCodes.TooManyUnits;
            int capped = Math.Min(requested, canBuild);
            int n = 0;
            for (int k = capped; k >= 1; k--)
                if (affordable(k)) { n = k; break; }
            if (station && (long)stationHave + n >= stationMax)
            {
                n = stationMax - stationHave;
                if (n <= 0) return RefuseCodes.TooManyUnits;
            }
            if (n < 1 || !pays(n)) return RefuseCodes.CannotAfford;
            count = n;
            return 0;
        }

        // B2: the count the popup queues (D 136842-136874): the click's count capped by HowManyCanBuild; the station
        // cap only refuses a full station (the window shows its limit instead of the popup), never lowers the count.
        // Being able to pay is not refused: the queue pays when it can, which is what the queue is for.
        public static int QueueCount(int requested, int canBuild, bool station, int stationMax, int stationHave,
            out int count)
        {
            count = 0;
            if (requested < 1) return RefuseCodes.BadTarget;
            if (canBuild <= 0) return RefuseCodes.TooManyUnits;
            if (station && stationHave >= stationMax) return RefuseCodes.TooManyUnits;
            count = Math.Min(requested, canBuild);
            return 0;
        }

        // B3: the cancel's count, in 1..what is left to build (the window clamps its field to that range).
        public static int CancelProblem(int requested, int countToBuild, int countBuilt)
        {
            long left = (long)countToBuild - countBuilt;
            if (left < 1) return RefuseCodes.AlreadyDone;
            if (requested < 1 || requested > left) return RefuseCodes.TooManyUnits;
            return 0;
        }

        // B4: FacilityInfoWindow.OnScrapButtonClick (D 134654-134724): not while the units in use are at the minimum,
        // not an asteroid engine with a flight under way; one unit through the confirm, else a count that leaves the
        // minimum standing.
        public static int ScrapProblem(long requested, long quantity, long enabled, long minEnabled, bool valid,
            bool engineInFlight)
        {
            if (minEnabled > 0 && enabled <= minEnabled && valid) return RefuseCodes.InUse;
            if (engineInFlight) return RefuseCodes.InUse;
            if (quantity < 1) return RefuseCodes.AlreadyDone;
            if (requested < 1 || requested > quantity) return RefuseCodes.TooManyUnits;
            if (quantity == 1) return 0;   // the one-unit confirm scraps it without the minimum check
            if (quantity - requested < minEnabled) return RefuseCodes.InUse;
            return 0;
        }

        // B7: the units switched on. The slider runs 0..quantity; below the minimum the window sets the minimum (D
        // 134607-134616), so the value applied is normalised to it. A value that changes nothing is AlreadyDone.
        public static int EnabledValue(long requested, long quantity, long minEnabled, long current, out long value)
        {
            value = current;
            if (requested < 0 || requested > quantity) return RefuseCodes.TooManyUnits;
            long target = requested < minEnabled ? minEnabled : requested;
            if (target == current) return RefuseCodes.AlreadyDone;
            value = target;
            return 0;
        }

        // B8: the stored priority, in the window's range (minMaxPriority (0, 10) shown, so -5..5 stored, Q2).
        public static int PriorityProblem(int requested, int current)
        {
            if (requested < ActionLimits.MinPriority || requested > ActionLimits.MaxPriority)
                return RefuseCodes.NotAllowedHere;
            return requested == current ? RefuseCodes.AlreadyDone : 0;
        }

        // B10: a click toggles the row; the request says which way, so a stale click changes nothing.
        public static int DepositProblem(bool selected, bool wanted)
        {
            return selected == wanted ? RefuseCodes.AlreadyDone : 0;
        }

        // ---- naming rows of the build list (DD3) ----

        // A reorder's ToPosition that means "to the front" (SetProductItemBefore with no target).
        public const int FrontPosition = 0;

        // B3 of a started build: the row with this item id, which must be a facility of this type at this position.
        public static int NamedItem(IList<BuildListRow> rows, long itemId, string item, int position, out int index)
        {
            index = -1;
            if (rows == null) return RefuseCodes.BadTarget;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null && !rows[i].Queue && rows[i].ItemId == itemId) { index = i; break; }
            if (index < 0) return RefuseCodes.BadTarget;
            BuildListRow r = rows[index];
            if (!r.Facility || r.Item != item || index != position) return RefuseCodes.TargetChanged;
            return 0;
        }

        // B3 of a queue entry: the queue row at this position, of this type; queueIndex is its index in the queue.
        public static int NamedQueueEntry(IList<BuildListRow> rows, string item, int position, out int queueIndex)
        {
            queueIndex = -1;
            if (rows == null || position < 0 || position >= rows.Count || rows[position] == null)
                return RefuseCodes.TargetChanged;
            BuildListRow r = rows[position];
            if (!r.Queue || !r.Facility || r.Item != item) return RefuseCodes.TargetChanged;
            int q = 0;
            for (int i = 0; i < position; i++) if (rows[i] != null && rows[i].Queue) q++;
            queueIndex = q;
            return 0;
        }

        // B5: the dragged facility (named by item id, type and position) moves before the facility at toPosition, or
        // to the front for 0 (the game's null target, the meaning every reorder kind gives 0). Queue entries cannot be
        // dragged (UIRowFacility.CanDrag) and are no target; a craft row is no target of a facility drag.
        public static int ReorderProblem(IList<BuildListRow> rows, long itemId, string item, int position,
            int toPosition)
        {
            int index;
            int code = NamedItem(rows, itemId, item, position, out index);
            if (code != 0) return code;
            if (toPosition == FrontPosition) return index == 0 ? RefuseCodes.AlreadyDone : 0;
            if (toPosition < 0 || toPosition >= rows.Count || rows[toPosition] == null)
                return RefuseCodes.TargetChanged;
            BuildListRow to = rows[toPosition];
            if (to.Queue || !to.Facility) return RefuseCodes.TargetChanged;
            if (toPosition == index) return RefuseCodes.AlreadyDone;
            return 0;
        }

        // ---- what an apply reports ----

        // The item ids an apply made: the agency's item counter only ever counts up, one id per new item, so they are
        // the ids from its value before to its value after. A counter that went down or jumped past the cap is not
        // something an apply does: null, and the caller treats the apply as a fault.
        public static long[] NewItemIds(long before, long after, int cap)
        {
            if (after < before || after - before > cap) return null;
            var ids = new long[after - before];
            for (long i = 0; i < ids.Length; i++) ids[i] = before + i;
            return ids;
        }
    }
}
