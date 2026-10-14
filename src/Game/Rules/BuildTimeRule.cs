using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using SpacecraftConstructData = Data.SpacecraftConstructData;

namespace SolarExpanseMultiplayer
{
    // S5 (the 2026-10-11 decision: each agency its own build speed). A build's days are the type's days times the
    // owner's bonus for that item times the local player's global BuildSpeed bonus, on every agency (rivals too), so
    // two players with different research see different build dates. In a session the last factor is the owning
    // agency's own, in every method that has it: ProductionItem and SpacecraftConstructData.TimeToBuildInDays and the
    // four LaunchVehicle methods that time a reusable vehicle's turnaround. The windows that show a build time for the
    // local player keep the local player's (display only). The game's Player call is replaced by an owner lookup.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class BuildTimeRule
    {
        static readonly AccessTools.FieldRef<ProductionItem, ObjectInfoData> SiteOf =
            AccessTools.FieldRefAccess<ProductionItem, ObjectInfoData>("objectInfoData");

        [HarmonyPatch(typeof(ProductionItem), nameof(ProductionItem.TimeToBuildInDays))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileItem(IEnumerable<CodeInstruction> code)
        {
            return Owner(code, "ProductionItem.TimeToBuildInDays", 2, "ItemOwner");
        }

        [HarmonyPatch(typeof(SpacecraftConstructData), nameof(SpacecraftConstructData.TimeToBuildInDays))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileConstruct(IEnumerable<CodeInstruction> code)
        {
            return Owner(code, "SpacecraftConstructData.TimeToBuildInDays", 1, "ItemOwner");
        }

        [HarmonyPatch(typeof(LaunchVehicle), nameof(LaunchVehicle.GetDataReadyToLaunch))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileReady(IEnumerable<CodeInstruction> code)
        {
            return Owner(code, "LaunchVehicle.GetDataReadyToLaunch", 1, "VehicleOwner");
        }

        [HarmonyPatch(typeof(LaunchVehicle), nameof(LaunchVehicle.ProgressReusable))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileProgress(IEnumerable<CodeInstruction> code)
        {
            return Owner(code, "LaunchVehicle.ProgressReusable", 1, "VehicleOwner");
        }

        [HarmonyPatch(typeof(LaunchVehicle), nameof(LaunchVehicle.ProgressReusable2))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileProgress2(IEnumerable<CodeInstruction> code)
        {
            return Owner(code, "LaunchVehicle.ProgressReusable2", 1, "VehicleOwner");
        }

        [HarmonyPatch(typeof(LaunchVehicle), nameof(LaunchVehicle.IsReadyToLaunchReusable))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileReusable(IEnumerable<CodeInstruction> code)
        {
            return Owner(code, "LaunchVehicle.IsReadyToLaunchReusable", 1, "VehicleOwner");
        }

        // Every "GameManager.Player" in the method (its only uses are the build speed) becomes `owner(manager, this)`.
        static IEnumerable<CodeInstruction> Owner(IEnumerable<CodeInstruction> code, string where, int expected,
            string helper)
        {
            return Il.PlayerToOwner(code, where, expected, (c, i) => true, OpCodes.Ldarg_0,
                AccessTools.Method(typeof(BuildTimeRule), helper));
        }

        // The company whose global BuildSpeed bonus counts for this item: the one that owns the site it builds at. The
        // game reads the field and falls back to the last site; a construct's own getter is the last resort.
        public static Company ItemOwner(GameManager manager, ProductionItem item)
        {
            if (!HumanAgencies.Active) return manager.Player;
            ObjectInfoData site = SiteOf(item) ?? item.objectInfoDataLast ?? item.ObjectInfoData;
            return HumanRules.OwnBonus(true) && site != null && site.company != null ? site.company : manager.Player;
        }

        public static Company VehicleOwner(GameManager manager, LaunchVehicle vehicle)
        {
            if (!HumanAgencies.Active) return manager.Player;
            return HumanRules.OwnBonus(true) && vehicle.company != null ? vehicle.company : manager.Player;
        }
    }
}
