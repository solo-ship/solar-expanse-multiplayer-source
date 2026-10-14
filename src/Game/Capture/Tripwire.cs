using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;
using Spacecraft = CustomUpdate.Spacecraft;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using MarketOfferManager = Manager.MarketOfferManager;
using Offer = Game.ObjectInfoDataScripts.Offer;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using ResearchManager = Manager.ResearchManager;

namespace SolarExpanseMultiplayer
{
    // The tripwire (DD1): a postfix on each world method an action reaches, which logs a WARN line when a human
    // agency changed outside an applied entry and outside the method's automatic contexts (TripwireTable, K13's lists).
    // It finds a click path no capture caught; it never blocks, and a run must show none. The enabled-units setter is
    // not watched: the day event writes it for every facility, and B7 is caught at the slider.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class Tripwire
    {
        static readonly AccessTools.FieldRef<ResearchManager, Company> ResearchOwner =
            AccessTools.FieldRefAccess<ResearchManager, Company>("company");

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(ResearchManager), "StartResearch");
            yield return HookTargets.Method(typeof(ResearchManager), "AddToQueue");
            yield return HookTargets.Method(typeof(ResearchManager), "StopResearch");
            yield return HookTargets.Method(typeof(ObjectInfoData), "AddFacility");
            yield return HookTargets.Method(typeof(ObjectInfoData), "AddProductItemToQuery");
            yield return HookTargets.Method(typeof(ObjectInfoData), "AddRocketToConstruct");
            yield return HookTargets.Method(typeof(ObjectInfoData), "SetProductItemBefore");
            yield return HookTargets.Method(typeof(Facility), "Scrap");
            yield return HookTargets.Method(typeof(Facility), "CancelBuild", typeof(int));
            yield return HookTargets.NoArgs(typeof(SpacecraftConstructData), "CancelBuild");
            yield return HookTargets.Method(typeof(ObjectInfoData), "SetPriority");
            yield return HookTargets.Method(typeof(Facility), "StopOperation");
            yield return HookTargets.Method(typeof(ObjectInfo), "VirtualDestroyFromNuclearBomb");
            yield return HookTargets.Method(typeof(ObservatoryFacility), "StopOperation");
            yield return HookTargets.Method(typeof(ObservatoryFacility), "OrderExploration");
            yield return HookTargets.Method(typeof(ObservatoryFacility), "OrderDiscovery");
            yield return HookTargets.Method(typeof(ObservatoryFacility), "AssignToExoplanetSurvey");
            yield return HookTargets.Method(typeof(MarketOfferManager), "AddOffer");
            yield return HookTargets.Method(typeof(MarketOfferManager), "CancelOffer");
            yield return HookTargets.Method(typeof(Offer), "FullFill");
            yield return HookTargets.Method(typeof(ContractManager), "ActivateContractForCompany", typeof(Company),
                typeof(Contract));
            yield return HookTargets.Method(typeof(ContractManager), "CancelContractForCompany");
            yield return HookTargets.Method(typeof(Contract), "FinishContractForCompany");
            // M6: the flight world (CreateFly, the cancel, a route added or removed, a ship or a rocket scrapped)
            yield return HookTargets.Method(typeof(PMTabSchedule), "CreateFly");
            yield return HookTargets.NoArgs(typeof(MissionInfo), "Cancel");
            yield return HookTargets.Method(typeof(CycleMissionManager), "AddCycleMission", typeof(Spacecraft),
                typeof(CycleMissionsData), typeof(List<ISpacecraftInfo>));
            yield return HookTargets.Method(typeof(CycleMissionManager), "RemoveCycleMission",
                typeof(CycleMissionsData));
            yield return HookTargets.Method(typeof(CycleMissionManager), "RefreshAfterEdit", typeof(Spacecraft),
                typeof(CycleMissionsData), typeof(List<ISpacecraftInfo>));
            yield return HookTargets.Method(typeof(Spacecraft), "Scrap", typeof(bool));
            yield return HookTargets.Method(typeof(LaunchVehicle), "Scrap", typeof(bool));
        }

        [HarmonyPostfix]
        static void Postfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null || !HumanAgencies.Active) return;
            string name = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            try
            {
                Company who = Changed(__instance, __args);
                if (who == null || !HumanAgencies.IsHuman(who)) return;
                // the rows' previews call SetProductItemBefore with returnIndexWillMove: nothing changes
                if (__originalMethod.Name == "SetProductItemBefore" && (bool)__args[2]) return;
                if (TripwireTable.IsAutomatic(name, FrameNames())) return;
                // a developer-console command is a known source, not a click path nothing captured
                string console;
                if (ConsoleGuard.InConsole(out console))
                {
                    if (seam.Memory.MayReport(name))
                        Log.Info(TripwireTable.ConsoleLine(console, name, GameAgencies.Wire(who)));
                    return;
                }
                Report(seam, name, GameAgencies.Wire(who));
            }
            catch (Exception e)
            {
                // the tripwire must never turn a world change into an exception
                Log.Warn("command: the tripwire could not judge " + name + ": " + e.GetType().Name);
            }
        }

        // The agency whose state the call changed.
        static Company Changed(object instance, object[] args)
        {
            var research = instance as ResearchManager;
            if (research != null) return ResearchOwner(research);
            var data = instance as ObjectInfoData;
            if (data != null) return data.company;
            var facility = instance as Facility;
            if (facility != null) return facility.Company;
            var item = instance as ProductionItem;   // a ship or launch-vehicle build
            if (item != null) return item.ObjectInfoData != null ? item.ObjectInfoData.company : null;
            if (instance is MarketOfferManager && args != null && args.Length > 0 && args[0] != null)
                return HookTargets.Property<Company>(args[0], "Company");
            if (instance is ObjectInfo && args != null && args.Length > 0) return args[0] as Company;
            // a contract call names its agency first; a fill changes the taker and the maker, a human one is reported
            if ((instance is ContractManager || instance is Contract) && args != null && args.Length > 0)
                return args[0] as Company;
            var offer = instance as Offer;
            if (offer != null)
            {
                var taker = args != null && args.Length > 0 ? args[0] as Company : null;
                return taker != null && HumanAgencies.IsHuman(taker) ? taker : offer.Company;
            }
            var schedule = instance as PMTabSchedule;
            if (schedule != null)
            {
                var window = HookTargets.Field<PlanMissionWindow>(schedule, "planMissionWindow");
                return window != null && window.PMMissionParameter != null ? window.PMMissionParameter.FlyCompany : null;
            }
            var mission = instance as MissionInfo;
            if (mission != null) return mission.company;
            if (instance is CycleMissionManager && args != null && args.Length > 0)
            {
                // AddCycleMission(ship, route, ...) and RemoveCycleMission(route)
                var route = (args.Length > 1 ? args[1] : args[0]) as CycleMissionsData;
                return route != null ? route.Company : null;
            }
            var ship = instance as Spacecraft;
            if (ship != null) return ship.GetCompany();
            var rocket = instance as LaunchVehicle;
            if (rocket != null) return rocket.company;
            return null;
        }

        // The stack above the postfix, each frame as the table names it.
        static List<string> FrameNames()
        {
            var names = new List<string>();
            foreach (StackFrame f in new StackTrace(2, false).GetFrames() ?? new StackFrame[0])
            {
                MethodBase m = f.GetMethod();
                if (m == null) continue;
                Type outer = m.DeclaringType;
                while (outer != null && outer.DeclaringType != null) outer = outer.DeclaringType;
                // a method a patch rebuilt (a patched caller) has the original's name in its own
                string frame = TripwireTable.FrameOf(outer != null ? outer.Name : null, m.Name);
                if (frame != null) names.Add(frame);
                if (names.Count >= 60) break;
            }
            return names;
        }

        static void Report(ICaptureSeam seam, string name, string agency)
        {
            if (!seam.Memory.MayReport(name)) return;
            Log.Warn(TripwireTable.Line(name, agency));
        }

        // The dev trigger "trip <name>": the line for any table entry (a world method or a greyed path), exactly as
        // the real path writes it, so each is proved without the window that would reach it. False for an unknown name.
        public static bool Trigger(ICaptureSeam seam, string name)
        {
            TripwireEntry entry = TripwireTable.Resolve(name);
            if (seam == null || entry == null) return false;
            Report(seam, entry.Name, WindowCapture.Local());
            return true;
        }
    }
}
