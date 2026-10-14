using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Cargo = Game.ObjectInfoDataScripts.Cargo;
using CargoAll = Game.ObjectInfoDataScripts.CargoAll;
using Company = Game.Company;
using FacilityIntTuple = Game.ObjectInfoDataScripts.ObjectInfoData.FacilityIntTuple;
using MissionInfo = Game.Info.MissionInfo;
using MoneyController = Game.CompanyScripts.MoneyController;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMTabCargo = Game.UI.Windows.Elements.PlanMissionElements.PMTabCargo;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;
using ResourcePriceOne = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePriceOne;
using Spacecraft = CustomUpdate.Spacecraft;
using SpacecraftType = Data.ScriptableObject.SpacecraftType;

namespace SolarExpanseMultiplayer
{
    // The host's flight hooks (M6 Task 9), all no-ops outside a session:
    //   * PMTabSchedule.CreateFly (prefix): the acceptance's step stops here; a mission this apply queued takes its
    //     cargo, inside the journal, after the host's re-checks (FlightApply.BeforeCreateFly);
    //   * ObjectInfoData.GetFakeSCFromFacility (prefix): which fake facility ships a plan touches, for CraftIdScope;
    //   * MissionInfo.Cancel (postfix): the Missions print and the watches follow a cancel made inside an entry;
    //   * PMTabCargo.SetCargoFromPreset (prefix): a loaded preset's rows with no site take the planner's start.
    // The class needs a [HarmonyPatch] of its own: HarmonyX silently skips a patch class without one.
    [HarmonyPatch]
    public static class FlightHostHooks
    {
        [HarmonyPatch(typeof(PMTabSchedule), "CreateFly")]
        [HarmonyPrefix]
        static bool CreateFlyPrefix(PMTabSchedule __instance)
        {
            CommandSession s = Session.Commands;
            if (s == null || s.FlightApply == null) return true;
            var p = FlightApply.ParameterOf(__instance);
            if (s.Flights != null && s.Flights.StopsAcceptance(p)) return false;
            return s.FlightApply.BeforeCreateFly(__instance);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "GetFakeSCFromFacility")]
        [HarmonyPrefix]
        static void FakeShipPrefix(ObjectInfoData __instance, FacilityIntTuple facility, SpacecraftType scType)
        {
            CommandSession s = Session.Commands;
            if (s == null || s.Flights == null || !s.Flights.Accepting) return;
            Spacecraft cached;
            FlightApply.FakeShips(__instance).TryGetValue(facility, out cached);
            s.Flights.NoteFake(__instance, facility, scType.ID, cached);
        }

        [HarmonyPatch(typeof(MissionInfo), "Cancel")]
        [HarmonyPostfix]
        static void CancelPostfix(MissionInfo __instance)
        {
            CommandSession s = Session.Commands;
            if (s == null || s.FlightRead == null || !__instance.cancel) return;
            s.FlightRead.Cancelled(__instance.id);
            if (s.FlightWatches != null) s.FlightWatches.Dirty();
            if (s.Steps != null) s.Steps.Watch.Dirty = true;
        }

        // A preset loaded into the planner (MissionPresetController.LoadPreset hands its cargo to the cargo tab): a
        // row with no site takes the planner's start (PresetRows). Not only in a session: the presets one applied
        // stay in the save.
        [HarmonyPatch(typeof(PMTabCargo), "SetCargoFromPreset")]
        [HarmonyPrefix]
        static void PresetCargoPrefix(PMTabCargo __instance, CargoAll _cargoAll)
        {
            PlanMissionWindow w = __instance.PlanMissionWindow;
            if (_cargoAll == null || w == null || w.PMMissionParameter == null) return;
            IObjectInfo start = w.PMMissionParameter.GetIObjectInfo;
            if (start == null) return;
            int filled = Fill(_cargoAll.listCargo, start) + Fill(_cargoAll.listCargoToOrbit, start)
                + Fill(_cargoAll.listCargoGravityAssists, start);
            if (filled > 0) Log.Info("flight: " + filled + " cargo rows of a preset took the planner's start");
        }

        static int Fill(List<Cargo> rows, IObjectInfo start)
        {
            int n = 0;
            if (rows == null) return 0;
            foreach (Cargo c in rows)
                if (c != null && PresetRows.NeedsSite(c.objectInfo != null, c.fromAtoBtoC))
                {
                    c.objectInfo = start;
                    n++;
                }
            return n;
        }
    }

    // The removal journal's hooks (Z-1): every ObjectInfoData.RemoveResource(ResourcePriceOne) and
    // MoneyController.RemoveMoney(double, out ...) an acceptance or a copy's cargo step makes is recorded, made or
    // not, because the step's chained removals stay made when a later one fails and MoneyController deducts even
    // when it then returns false. Both are overloaded, so the class names its targets (HarmonyTargetMethods) and
    // one postfix reads the game's own answer.
    [HarmonyPatch]
    public static class FlightJournalHooks
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(ObjectInfoData), "RemoveResource", new[] { typeof(ResourcePriceOne) });
            yield return AccessTools.Method(typeof(MoneyController), "RemoveMoney",
                new[] { typeof(double), typeof(ObjectInfoData.TransactionRemoveResource).MakeByRefType() });
        }

        [HarmonyPostfix]
        static void Postfix(MethodBase __originalMethod, object __instance, object[] __args, bool __result)
        {
            CommandSession s = Session.Commands;
            RemovalJournal j = s != null && s.Flights != null ? s.Flights.Recording : null;
            if (j == null) return;
            var site = __instance as ObjectInfoData;
            if (site != null)
            {
                var price = __args[0] as ResourcePriceOne;
                if (price == null || price.ResourceDefinition == null) return;
                j.Record(RemovalKinds.Resource, GameAgencies.Wire(site.company), site.id, price.ResourceDefinition.ID,
                    Math.Max(0.0, price.Price), __result);
                return;
            }
            var purse = __instance as MoneyController;
            if (purse == null) return;
            // the purse is debited whatever the answer, so the amount is made
            j.Record(RemovalKinds.Money, GameAgencies.Wire(purse.GetComponent<Company>()), 0, null,
                Math.Max(0.0, (double)__args[0]), true);
        }
    }
}
