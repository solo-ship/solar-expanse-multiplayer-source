using System;
using System.Collections.Generic;
using HarmonyLib;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;

namespace SolarExpanseMultiplayer
{
    // The game side of BuildStarts (the session's RuleState holds it): the stamps, noted where builds can start, and
    // the first credit of a fresh unit, from CraftCompletionRule's UpdateBuilding prefix. No state of its own.
    public static class BuildStartGlue
    {
        static readonly AccessTools.FieldRef<ProductionItem, ObjectInfoData> SiteOf =
            AccessTools.FieldRefAccess<ProductionItem, ObjectInfoData>("objectInfoData");
        static readonly AccessTools.FieldRef<ObjectInfoData, DateTime?> LastFrame =
            AccessTools.FieldRefAccess<ObjectInfoData, DateTime?>("updateBuildingDateTimeLastUpdate");
        static readonly AccessTools.FieldRef<ProductionItem, float> Progress =
            AccessTools.FieldRefAccess<ProductionItem, float>("buildProgress");

        // An agency's builds may start from `start` on (null: every agency's); noted at this game's clock.
        public static void Note(string agency, long start)
        {
            RuleState st = HumanAgencies.State;
            long now;
            if (st == null || !Clock(out now)) return;
            Manager.TimeController tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
            st.Starts.Note(agency == null ? null : AgencyWire.From(agency), start, now, tc.StartDateTime.Ticks);
        }

        // The game's own clock, now.
        public static void NoteNow(string agency)
        {
            long now;
            if (Clock(out now)) Note(agency, now);
        }

        // The days UpdateBuilding credits this call: the game's own, or the fresh unit's from its stamp.
        public static double Credit(ProductionItem item, List<float> units, int indexBuildingUpdate,
            double daysTimeDelta)
        {
            RuleState st = HumanAgencies.State;
            ObjectInfoData site = SiteOf(item);
            if (st == null || site == null || site.company == null) return daysTimeDelta;
            DateTime? last = LastFrame(site);
            long now;
            if (!last.HasValue || !Clock(out now)) return daysTimeDelta;
            int unit;
            float progress;
            if (units == null || units.Count == 0)
            {
                // the game makes the list on this call, its one unit at the item's progress
                unit = 0;
                progress = Progress(item);
            }
            else
            {
                unit = CraftHold.NextUnit(units, indexBuildingUpdate);
                if (unit < 0) return daysTimeDelta;
                progress = units[unit];
            }
            // most calls: a unit under way and nothing waiting (no agency name is made for them)
            if (progress != 0f && st.Starts.Waiting == 0) return daysTimeDelta;
            string agency = AgencyWire.From(site.company.ID);
            double credit = st.Starts.Credit(item, unit, agency, progress == 0f, now, last.Value.Ticks, daysTimeDelta);
            if (st.Starts.LastUnstamped && st.Starts.Unstamped <= UnstampedLines)
                Log.Info("build: " + agency + " item " + item.ID + " " + (item.ProductionItemType != null
                    ? item.ProductionItemType.ID : "?") + " unit " + unit + " at body "
                    + (site.ObjectInfo != null ? site.ObjectInfo.id : -1) + " starts from this game's frame at "
                    + last.Value.Ticks + " (no stamp; now " + now + "); fresh units stamped " + st.Starts.Stamped
                    + ", from a frame " + st.Starts.Unstamped);
            return credit;
        }

        // a start no stamp covers is a frame-dependent one: the first ones a session meets are logged, both games
        // alike, to be compared (more would only repeat the pattern)
        const long UnstampedLines = 200;

        static bool Clock(out long now)
        {
            now = 0;
            if (GravityEngine.instance == null) return false;
            Manager.TimeController tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
            if (tc == null) return false;
            now = tc.CurrentTime.Ticks;
            return true;
        }
    }
}
