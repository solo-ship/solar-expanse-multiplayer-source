using System;
using System.Collections.Generic;
using HarmonyLib;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBonus = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.FacilityBonus;
using InterstellarShipFacilityFinish =
    Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.InterstellarShipFacilityFinish;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using SpaceModuleDescriptor = Data.ScriptableObject.SpaceModuleDescriptor;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // DD11: in a session the host decides every craft id taken by a finishing build, for every agency. A finished ship
    // or launch vehicle takes the next game-wide Spacecraft.count or LaunchVehicle.count on whichever frame its build
    // completes, so two games can number crafts differently and M6's missions name ships by id.
    //   * The host: ProductionItem.Build runs as the game has it; when the item is one whose completion takes a craft
    //     id and no entry is being applied, the host raises CraftFinished with the item, the counters before and after
    //     (a jump is copied, not recomputed). It is the same test the clients hold by, so a hold always has an entry.
    //   * A client: ProductionItem.UpdateBuilding is held for such an item. The unit that would reach 1 this frame is
    //     kept at 0.9999, so Build() is not called and no id is taken (Task 2's Q4: the same craft id on both games,
    //     released by unit = 1 and Build()). While one unit is held the slot keeps working on it, so the next unit
    //     starts late on that client, which costs only its progress bar, as that unit's own completion is the host's
    //     entry too. Release() is what the entry's apply calls, after the counters were set.
    // The items are the ones K13 lists as finishing on a frame: ship and launch-vehicle constructs, the asteroid
    // pulling facility and module, the interstellar ship facility and the launch facility's launch vehicle. Flights,
    // contract rewards and the load take ids on other paths (M6, M7 and the snapshot).
    // A facility finishes on a frame too (its progress is added once per render frame, ObjectInfoData.MyUpdate), so
    // two games finish it on different frames, sometimes either side of midnight: that changes the built stack, the
    // energy and the day's print, and a built count cannot be restored (DD10). So the same hold and the same entry
    // cover every facility of every agency: Item is its descriptor, ItemId its item id, and the entry carries the
    // agency's item counter too (a stacking-blocked facility takes a new id). The host raises it only when the
    // progress update finished it: a facility added prebuilt, or a probe's own, is inside an applied entry or a load.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch(typeof(ProductionItem))]
    public static class CraftCompletionRule
    {

        static readonly AccessTools.FieldRef<ProductionItem, bool> StartBuild =
            AccessTools.FieldRefAccess<ProductionItem, bool>("startBuild");
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            AccessTools.FieldRefAccess<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<ProductionItem, List<float>> Units =
            AccessTools.FieldRefAccess<ProductionItem, List<float>>("listBuildProgress");
        static readonly AccessTools.FieldRef<ObjectInfoData, List<ProductionItem>> Items =
            AccessTools.FieldRefAccess<ObjectInfoData, List<ProductionItem>>("productionItems");

        // ---- the host notes that a progress update is running ----

        [HarmonyPatch(nameof(ProductionItem.UpdateBuilding))]
        [HarmonyPrefix]
        static void NoteUpdate(ref bool __state)
        {
            if (!HumanAgencies.Active) return;
            if (HumanAgencies.Host == null) return;
            HumanAgencies.EnterBuildUpdate();
            __state = true;
        }

        // A finalizer, so a throwing update still leaves.
        [HarmonyPatch(nameof(ProductionItem.UpdateBuilding))]
        [HarmonyFinalizer]
        static void LeaveUpdate(bool __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state) HumanAgencies.LeaveBuildUpdate();
        }

        // ---- where a fresh unit starts (BuildStarts), on every game, before the hold reads the step ----

        [HarmonyPatch(nameof(ProductionItem.UpdateBuilding))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static void StartCredit(ProductionItem __instance, ref double daysTimeDelta, int indexBuildingUpdate)
        {
            if (!HumanAgencies.Active) return;
            try
            {
                if (!StartBuild(__instance) || Built(__instance)) return;
                daysTimeDelta = BuildStartGlue.Credit(__instance, Units(__instance), indexBuildingUpdate,
                    daysTimeDelta);
            }
            catch (Exception e) { Log.Error("craft: the start credit failed: " + e.GetType().Name); }
        }

        // ---- the client's hold ----

        [HarmonyPatch(nameof(ProductionItem.UpdateBuilding))]
        [HarmonyPrefix]
        static bool HoldPrefix(ProductionItem __instance, float ConstructionPower, double daysTimeDelta,
            int indexBuildingUpdate)
        {
            if (!HumanAgencies.Active) return true;
            if (HumanAgencies.Host != null || HumanAgencies.Applying) return true;
            try
            {
                if (!CompletionRules.IsHeld(TakesCraftId(__instance), IsFacility(__instance))
                    || !StartBuild(__instance) || Built(__instance)) return true;
                string agency, item;
                int body;
                if (!Resolve(__instance, out agency, out body, out item))
                {
                    HumanAgencies.ClientOutOfStep("a held build could not be told apart from the host's");
                    return false;
                }
                List<float> units = Units(__instance);
                int unit = CraftHold.HoldUnit(units, indexBuildingUpdate, daysTimeDelta,
                    __instance.TimeToBuildInDays(), ConstructionPower);
                if (unit < 0) return true;
                bool first = units[unit] != CraftHold.HeldProgress;
                units[unit] = CraftHold.HeldProgress;
                if (first) HumanAgencies.State.HeldSince[HoldKey(agency, body, __instance.ID)] = GameTicks();
                if (first)
                    Log.Info("hold: " + (IsFacility(__instance) ? "facility " : "craft ") + item + " of "
                        + AgencyWire.From(agency)
                        + " at body " + body + " item " + __instance.ID + " waits for the host's entry");
                return false;
            }
            catch (Exception e)
            {
                Log.Error("craft: the hold failed (out of step): " + e.GetType().Name);
                HumanAgencies.ClientOutOfStep("the build hold threw " + e.GetType().Name);
                return false;
            }
        }

        // ---- the host's entry ----

        [HarmonyPatch(nameof(ProductionItem.Build))]
        [HarmonyPrefix]
        static void BuildPrefix(ProductionItem __instance, ref CraftTaken __state)
        {
            if (!HumanAgencies.Active) return;
            if (HumanAgencies.Host == null || HumanAgencies.Applying) return;
            try
            {
                if (Manager.LoadSaveManager.OnExtractAllFromSaveData) return;
                bool craft = TakesCraftId(__instance), facility = IsFacility(__instance);
                if (!CompletionRules.Raises(craft, facility, HumanAgencies.InBuildUpdate)) return;
                var taken = new CraftTaken();
                if (!Resolve(__instance, out taken.Agency, out taken.Body, out taken.Item)) return;
                taken.Facility = !craft;
                taken.Outer = HumanAgencies.EnterCraftBuild() == 1;
                taken.ItemId = __instance.ID;
                taken.SpacecraftBefore = Spacecraft.count;
                taken.LaunchVehicleBefore = LaunchVehicle.count;
                taken.ProductionBefore = __instance.ObjectInfoData.company.productionCountID;
                // a probe observatory the craft makes draws inside this Build; the entry carries its dice
                if (taken.Outer) taken.Recorded = HumanAgencies.Dice.BeginRecord();
                __state = taken;
            }
            catch (Exception e)
            {
                Log.Error("craft: could not note a completion (clients will wait for it): " + e.GetType().Name);
            }
        }

        // A finalizer, not a postfix: a Build() that throws part way still tells the clients, who hold for this entry.
        [HarmonyPatch(nameof(ProductionItem.Build))]
        [HarmonyFinalizer]
        static void BuildFinalizer(ProductionItem __instance, CraftTaken __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state == null) return;
            HumanAgencies.LeaveCraftBuild();
            if (!__state.Outer) return;
            if (__state.Recorded) __state.Draws = HumanAgencies.Dice.EndRecord();
            IHostDecisions host = HumanAgencies.Host;
            if (host == null) return;
            __state.SpacecraftAfter = Spacecraft.count;
            __state.LaunchVehicleAfter = LaunchVehicle.count;
            __state.ProductionAfter = __instance.ObjectInfoData.company.productionCountID;
            try { host.CraftFinished(__state); }
            catch (Exception e) { Log.Error("craft: could not raise a completion: " + e.GetType().Name); }
        }

        // ---- shared ----

        // Does this item's completion take a craft id (call ConstructShipOnPlanet or AddRocket)? One answer for the
        // host's entry and the clients' hold. A module construct (a space module made like a ship) takes none.
        public static bool TakesCraftId(ProductionItem item)
        {
            var construct = item as SpacecraftConstructData;
            if (construct != null) return !(construct.ProductionItemType is SpaceModuleDescriptor);
            if (item is AsteroidPullingFacility || item is AsteroidPullingModule
                || item is InterstellarShipFacilityFinish) return true;
            var launch = item as FacilityBonus;
            return launch != null && launch.BonusData != null && !string.IsNullOrEmpty(launch.BonusData.fakeLVId);
        }

        // The game's clock reads the gravity engine, which is absent early in a load.
        static long GameTicks()
        {
            try
            {
                if (GravityEngine.instance == null) return 0;
                Manager.TimeController time = MonoBehaviourSingleton<Manager.TimeController>.Instance;
                return time != null ? time.CurrentTime.Ticks : 0;
            }
            catch (Exception) { return 0; }
        }

        // The hold is noted with the game's id and released with the stream's (its lower-case wire form): one key.
        static string HoldKey(string agency, int body, long itemId)
        {
            return AgencyWire.From(agency) + "/" + body + "/" + itemId;
        }

        // A hold that ends without a release (the item is gone or already built) leaves nothing behind.
        static void EndHold(string agency, int body, long itemId)
        {
            RuleState st = HumanAgencies.State;
            if (st != null) st.HeldSince.Remove(HoldKey(agency, body, itemId));
        }

        // The hold ends with the host's entry: the log says how long it lasted in game time (Task 13 reads these).
        static void NoteReleased(ProductionItem item, string agency, int body)
        {
            RuleState st = HumanAgencies.State;
            long since;
            string key = HoldKey(agency, body, item.ID);
            if (st == null || !st.HeldSince.TryGetValue(key, out since)) return;
            st.HeldSince.Remove(key);
            double hours = (GameTicks() - since) / (double)TimeSpan.TicksPerHour;
            Log.Info("hold: " + (IsFacility(item) ? "facility " : "craft ") + item.ProductionItemType.ID + " of "
                + AgencyWire.From(agency) + " at body " + body + " item " + item.ID + " released after "
                + hours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " game hours");
        }

        // A facility of any kind: the hold and the host's entry cover it (R19-1).
        static bool IsFacility(ProductionItem item) { return item is Facility; }

        // The agency, the body and the content id the entry names; false when the item has no owner or site yet.
        static bool Resolve(ProductionItem item, out string agency, out int body, out string content)
        {
            agency = null; content = null; body = 0;
            ObjectInfoData site = item.ObjectInfoData;
            if (site == null || site.company == null || site.ObjectInfo == null || item.ProductionItemType == null)
                return false;
            agency = site.company.ID;
            body = site.ObjectInfo.id;
            content = item.ProductionItemType.ID;
            return !string.IsNullOrEmpty(agency) && !string.IsNullOrEmpty(content) && body > 0;
        }

        public enum ReleaseResult { Released, AlreadyBuilt, NotFound }

        // The client's side of CraftFinished: the entry's apply sets the counters to the host's first, then calls
        // this to finish the item the hold kept (or that this game had not reached yet): the unit being built is
        // set to done and Build() runs, taking the id the counters now hold. The item is found by site, content
        // and item id, never through a window.
        public static ReleaseResult Release(string agency, int body, string content, long itemId)
        {
            ProductionItem item = Find(agency, body, content, itemId);
            if (item == null) { EndHold(agency, body, itemId); return ReleaseResult.NotFound; }
            if (Built(item)) { EndHold(agency, body, itemId); return ReleaseResult.AlreadyBuilt; }
            NoteReleased(item, agency, body);
            List<float> units = Units(item);
            for (int i = 0; units != null && i < units.Count; i++)
                if (units[i] < 1f) { units[i] = 1f; break; }
            item.Build();
            return ReleaseResult.Released;
        }

        // agency: the entry's wire id (or the game's own id, which has the same wire form); the site is the one whose
        // company is that agency, never GetObjectInfoData's fallback. Ids repeat at a site, so CraftHold.Pick
        // chooses by content and id, an unfinished item first.
        static ProductionItem Find(string agency, int body, string content, long itemId)
        {
            Manager.ObjectInfoManager objects = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance;
            Game.Company company = GameAgencies.Find(AgencyWire.From(agency));
            if (objects == null || company == null) return null;
            var found = new List<ProductionItem>();
            var seen = new List<SiteItem>();
            foreach (ObjectInfo o in objects.allObjectInfos)
            {
                if (o == null || o.id != body) continue;
                foreach (ObjectInfoData d in o.ObjectsInfoData)
                {
                    if (d == null || !ReferenceEquals(d.company, company)) continue;
                    List<ProductionItem> items = Items(d);
                    for (int i = 0; items != null && i < items.Count; i++)
                    {
                        ProductionItem p = items[i];
                        if (p == null || p.ID != itemId) continue;
                        found.Add(p);
                        string id = p.ProductionItemType != null ? p.ProductionItemType.ID : null;
                        seen.Add(new SiteItem(p.ID, id, Built(p)));
                    }
                }
            }
            int pick = CraftHold.Pick(seen, itemId, content);
            return pick >= 0 ? found[pick] : null;
        }
    }
}
