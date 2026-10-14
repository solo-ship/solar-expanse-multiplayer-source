using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityToConstructNotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfo = Game.Info.ObjectInfo;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using UIRowFacility = Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;
using CargoData = Game.ObjectInfoDataScripts.CargoData;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using EResourceTypeType = Game.ObjectInfoDataScripts.EResourceTypeType;
using MissionInfo = Game.Info.MissionInfo;
using MissionPresetController = Game.UI.Windows.Elements.PlanMissionElements.MissionPresetController;
using MissionPresetData = Game.UI.Windows.Elements.PlanMissionElements.MissionPresetData;
using MissionRowCyclical = Game.UI.Windows.Elements.MissionsElements.MissionRowCyclical;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;
using RowRocketData = Game.UI.Windows.Elements.ObjectInfoElements.RowRocketData;
using Spacecraft = CustomUpdate.Spacecraft;
using SpaceCraftInfoWindow = Game.UI.Windows.Windows.SpaceCraftInfoWindow;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using SpacecraftInfo = Game.Info.SpacecraftInfo;
using StackedRowRocketData = Game.UI.Windows.Elements.ObjectInfoElements.StackedRowRocketData;

namespace SolarExpanseMultiplayer
{
    // M6 Task 11, the flight clicks (plan DD-F1; docs/research/flights.md S5):
    //   F1-F3 the planner's Schedule button (60), F4 the preset save (67), F5 the abort popup's OK (61), F8 pause or
    //   resume a route (63), F9 delete a route (64) in both lists, and the scrap popup's two OKs (66).
    //   F6, F7 a route's Schedule and edit (62), F10 the production rows' "send supplies to orbit" (65, Left Shift on
    //   a queued construct): the row's click only notes which row it was on (CaptureSupply sends the plan it starts).
    // Greyed still: the rest of GreyedButtons.
    // The planner captures act only on the visible planner (the hidden ones are the load path's, the AI's, the
    // routes' and an accepted flight's copy: ForCode). The class needs a [HarmonyPatch] of its own: HarmonyX
    // (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureFlights
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "PMTabSchedule.OnClickScheduleButton", Schedule },
            { "MissionPresetController.OnSaveClicked", SavePreset },
            { "SpaceCraftInfoWindow/<>c__DisplayClass66_0.<OnAbortMissionButtonClick>b__0", Abort },
            { "SpaceCraftInfoWindow/<>c__DisplayClass69_0.<OnScrapButtonClick>b__1", ScrapOne },
            { "SpaceCraftInfoWindow/<>c__DisplayClass69_0.<OnScrapButtonClick>b__3", ScrapMany },
            { "MissionRowCyclicalNew.OnButtonPausePlay", PauseRoute },
            { "MissionRowCyclicalNew.OnButtonDelete", DeleteRoute },
            { "MissionRowCyclical.OnButtonDelete", DeleteRoute },
            { "PlanCyclicalMissionWindow.OnClickSchedule", ScheduleRoute },
            { "UIRowFacility.CreatedFlyToOrbit", FacilityUp },
            { "UIRowFacility.CreatedFlyToPlanet", FacilityDown },
            { "UIRowRocket.CreatedFlyToOrbit", RocketUp },
            { "UIRowRocket.CreatedFlyToPlanet", RocketDown }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.NoArgs(typeof(PMTabSchedule), "OnClickScheduleButton");
            yield return HookTargets.Method(typeof(MissionPresetController), "OnSaveClicked");
            yield return HookTargets.Lambda(typeof(SpaceCraftInfoWindow), "<>c__DisplayClass66_0",
                "<OnAbortMissionButtonClick>b__0");
            yield return HookTargets.Lambda(typeof(SpaceCraftInfoWindow), "<>c__DisplayClass69_0",
                "<OnScrapButtonClick>b__1");
            yield return HookTargets.Lambda(typeof(SpaceCraftInfoWindow), "<>c__DisplayClass69_0",
                "<OnScrapButtonClick>b__3");
            yield return HookTargets.Method(typeof(MissionRowCyclicalNew), "OnButtonPausePlay");
            yield return HookTargets.Method(typeof(MissionRowCyclicalNew), "OnButtonDelete");
            yield return HookTargets.Method(typeof(MissionRowCyclical), "OnButtonDelete");
            yield return HookTargets.Method(typeof(PlanCyclicalMissionWindow), "OnClickSchedule");
            yield return HookTargets.Method(typeof(UIRowFacility), "CreatedFlyToOrbit", typeof(ObjectInfo),
                typeof(ResourcePrice));
            yield return HookTargets.Method(typeof(UIRowFacility), "CreatedFlyToPlanet", typeof(ObjectInfo),
                typeof(ResourcePrice));
            yield return HookTargets.Method(typeof(UIRowRocket), "CreatedFlyToOrbit", typeof(ObjectInfo),
                typeof(ResourcePrice));
            yield return HookTargets.Method(typeof(UIRowRocket), "CreatedFlyToPlanet", typeof(ObjectInfo),
                typeof(ResourcePrice));
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        // ---- F1-F3: the Schedule button of the visible planner ----

        static bool Schedule(ICaptureSeam seam, object self, object[] args)
        {
            var window = HookTargets.Field<PlanMissionWindow>(self, "planMissionWindow");
            // a hidden planner is the load path's, the AI's, a route leg's or an accepted flight's copy
            if (window == null || window.ForCode) return true;
            PMMissionParameter p = window.PMMissionParameter;
            // the game's own popup for a hypothetical plan; a plan loaded from a save is not a click
            if (p == null || p.Hypothetical || p.LoadingFromSave) return true;
            if (PlannerWait.IsWaiting(window)) { seam.Note(CommandText.Waiting()); return false; }
            string problem;
            ActionRequest a = FlightRequests.Schedule(p, out problem);
            if (a == null)
            {
                Log.Info("flight: the plan was not sent: " + TextSanitiser.OneLine(problem));
                WindowCapture.CannotSend(seam);
                return false;
            }
            if (!WindowCapture.Send(seam, a, p.FlyCompany)) return false;
            PlannerWait.Begin(window);
            return false;
        }

        // ---- F4: save a preset ----

        static readonly MethodInfo CanSave = AccessTools.Method(typeof(MissionPresetController), "CanSaveCurrentPreset");

        static bool SavePreset(ICaptureSeam seam, object self, object[] args)
        {
            var window = HookTargets.Field<PlanMissionWindow>(self, "planMissionWindow");
            if (window == null || window.ForCode) return true;
            // the game logs "cannot save" and does nothing
            if (!(bool)CanSave.Invoke(self, null)) return true;
            PMMissionParameter p = window.PMMissionParameter;
            Company player = WindowCapture.Player();
            if (p == null || player == null) { WindowCapture.CannotSend(seam); return false; }
            PresetData preset = PresetOf(p, player.MissionPresets.Count);
            if (preset == null) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.PresetSave, Agency = WindowCapture.Local(), Preset = preset };
            WindowCapture.Send(seam, a, p.FlyCompany);
            return false;
        }

        // The preset the game would make (its own constructor reads the planner), in the request's plain values.
        // Null when the planner holds cargo a preset row cannot carry.
        internal static PresetData PresetOf(PMMissionParameter p, int held)
        {
            var d = new MissionPresetData(p, FlightClickRequests.PresetName(p.MissionName, held));
            var rows = new List<CargoRow>();
            if (d.cargoAllData != null)
            {
                bool lost = (d.cargoAllData.listCargoDataToOrbit != null && d.cargoAllData.listCargoDataToOrbit.Count > 0)
                    || (d.cargoAllData.listCargoGravityAssists != null
                        && d.cargoAllData.listCargoGravityAssists.Count > 0);
                if (d.cargoAllData.listCargoData != null)
                    foreach (CargoData c in d.cargoAllData.listCargoData)
                    {
                        if (c.resourceTypeType == EResourceTypeType.resorces && c.resourceType != null)
                            rows.Add(new CargoRow { Kind = CargoKinds.Resource, Item = c.resourceType.ID,
                                Amount = c.cargoMass });
                        else if (c.crew)
                            rows.Add(new CargoRow { Kind = CargoKinds.Crew,
                                Item = c.moduleData != null ? c.moduleData.ID : null, Count = c.crewValue,
                                Amount = c.cargoMass });
                        else if (c.moduleData != null)
                            rows.Add(new CargoRow { Kind = CargoKinds.Module, Item = c.moduleData.ID,
                                Count = (int)c.cargoMass });
                        else lost = true;
                    }
                if (d.cargoAllData.entireAsteroid)
                {
                    string ship = !string.IsNullOrEmpty(d.scTypeID) ? d.scTypeID : d.scTypeIDFacility;
                    if (string.IsNullOrEmpty(ship)) lost = true;
                    else rows.Add(new CargoRow { Kind = CargoKinds.Ship, Item = ship });
                }
                if (lost) return null;
            }
            string lv = string.IsNullOrEmpty(d.lvTypeID) ? null : d.lvTypeID;
            return new PresetData
            {
                Name = d.presetName, Target = p.Target.id, ScType = string.IsNullOrEmpty(d.scTypeID) ? null : d.scTypeID,
                ScTypeFacility = string.IsNullOrEmpty(d.scTypeIDFacility) ? null : d.scTypeIDFacility, LvType = lv,
                ScCount = d.scCount, LvCount = lv == null ? 0 : d.lvCount,
                ScIsLowOrbitContainer = d.scIsLowOrbitContainer, ScIsFromFacility = d.scIsFromFacility,
                ForCyclical = d.forCyclical, Cargo = rows.ToArray(), CostType = (int)d.costType
            };
        }

        // ---- F5: the abort popup's OK ----

        static bool Abort(ICaptureSeam seam, object self, object[] args)
        {
            var window = HookTargets.Field<SpaceCraftInfoWindow>(self, "<>4__this");
            var row = HookTargets.Field<RowRocketData>(window, "rowRocketData");
            var shown = HookTargets.Field<MissionInfo>(window, "missionInfo");
            var cmd = HookTargets.Field<CycleMissionsData>(self, "cmd");
            MissionInfo mi = null;
            int ship = -1, branch = 0;
            if (row != null)
            {
                if (row.spacecraft != null)
                {
                    mi = row.spacecraft.GetMissionInfo();
                    ship = row.spacecraft.ID;
                    branch = FlightClickRequests.AbortShip;
                }
                else if (row.launchVehicle != null && row.launchVehicle.spacecraft != null)
                {
                    mi = row.launchVehicle.spacecraft.GetMissionInfo();
                    ship = row.launchVehicle.spacecraft.ID;
                    branch = FlightClickRequests.AbortLaunchVehicle;
                }
                else if (row.rConstruct != null)
                {
                    mi = MissionOfConstruct(row.rConstruct);
                    branch = FlightClickRequests.AbortConstruct;
                }
            }
            else if (shown != null)
            {
                mi = shown;
                var sc = shown.spacecraftInfo2 as Spacecraft;
                var info = shown.spacecraftInfo2 as SpacecraftInfo;
                if (sc != null)
                {
                    ship = sc.ID;
                    branch = cmd != null ? FlightClickRequests.AbortCyclical : FlightClickRequests.AbortShip;
                }
                else if (info != null && info.spacecraft != null)
                {
                    ship = info.spacecraft.ID;
                    branch = FlightClickRequests.AbortShipInfo;
                }
                else if (shown.spacecraftInfo2 is SpacecraftConstructData) branch = FlightClickRequests.AbortConstruct;
            }
            else return true;   // nothing shown: the game does nothing either
            if (mi == null || branch == 0) { WindowCapture.CannotSend(seam); return false; }
            WindowCapture.Send(seam, FlightClickRequests.Cancel(mi.id, ship, branch), mi.company);
            return false;
        }

        // The live mission a construct under way carries (the game's CancelMissions cancels all of them).
        static MissionInfo MissionOfConstruct(SpacecraftConstructData construct)
        {
            Manager.MissionInfoManager manager = MonoBehaviourSingleton<Manager.MissionInfoManager>.Instance;
            if (manager == null || manager.ListMissionInfo == null) return null;
            foreach (MissionInfo mi in manager.ListMissionInfo)
                if (mi != null && !mi.cancel && ReferenceEquals(mi.spacecraftInfo2, construct)) return mi;
            return null;
        }

        // ---- the scrap popup's OKs (one ship, or a count of the stacked rows) ----

        static bool ScrapOne(ICaptureSeam seam, object self, object[] args)
        {
            var window = HookTargets.Field<SpaceCraftInfoWindow>(self, "<>4__this");
            var row = HookTargets.Field<RowRocketData>(window, "rowRocketData");
            return SendScrap(seam, new List<RowRocketData> { row });
        }

        static bool ScrapMany(ICaptureSeam seam, object self, object[] args)
        {
            var stacked = HookTargets.Field<StackedRowRocketData>(self, "similarStackedRows");
            long wanted;
            if (!long.TryParse(args[0] as string, NumberStyles.None, CultureInfo.InvariantCulture, out wanted))
                return true;   // the game ignores a number it cannot read
            var rows = new List<RowRocketData>();
            if (stacked != null)
                foreach (RowRocketData r in stacked)
                    if (rows.Count < wanted) rows.Add(r);
            return SendScrap(seam, rows);
        }

        static bool SendScrap(ICaptureSeam seam, List<RowRocketData> rows)
        {
            var list = new List<ActionRequest>();
            Company owner = null;
            foreach (RowRocketData r in rows)
            {
                if (r == null) continue;
                Company who;
                if (r.spacecraft != null && r.spacecraft.CurrentlyOnThisObject != null)
                {
                    who = r.spacecraft.GetCompany();
                    list.Add(FlightClickRequests.ScrapShip(r.spacecraft.CurrentlyOnThisObject.id, r.spacecraft.ID));
                }
                else if (r.launchVehicle != null && r.launchVehicle.objectInfo != null)
                {
                    who = r.launchVehicle.company;
                    list.Add(FlightClickRequests.ScrapRocket(r.launchVehicle.objectInfo.id, r.launchVehicle.ID));
                }
                else continue;   // the game logs "nothing to scrap" for such a row
                if (owner == null) owner = who;
            }
            if (list.Count == 0) { WindowCapture.CannotSend(seam); return false; }
            WindowCapture.SendAll(seam, list, owner);
            return false;
        }

        // ---- F8, F9: a route's pause and delete ----

        static bool PauseRoute(ICaptureSeam seam, object self, object[] args)
        {
            var cmd = HookTargets.Field<CycleMissionsData>(self, "cmd");
            Spacecraft ship = RouteShip(cmd);
            if (ship == null) { WindowCapture.CannotSend(seam); return false; }
            // a set kind: the click flips the state still on its way, not the stale one on screen
            ActionRequest a = FlightClickRequests.Pause(ship.ID, true);
            a.Agency = WindowCapture.Local();
            bool paused = ClickGate.PendingOr(seam, a, cmd.Pause ? 1 : 0) == 1;
            a.Value = paused ? 0 : 1;
            WindowCapture.Send(seam, a, cmd.Company);
            return false;
        }

        // both lists' delete buttons keep the route in a field named cmd
        static bool DeleteRoute(ICaptureSeam seam, object self, object[] args)
        {
            var cmd = HookTargets.Field<CycleMissionsData>(self, "cmd");
            Spacecraft ship = RouteShip(cmd);
            if (ship == null) { WindowCapture.CannotSend(seam); return false; }
            WindowCapture.Send(seam, FlightClickRequests.Delete(ship.ID), cmd.Company);
            return false;
        }

        // ---- F6, F7: a route's Schedule (a new route, or the editor's) ----

        static bool ScheduleRoute(ICaptureSeam seam, object self, object[] args)
        {
            var window = (PlanCyclicalMissionWindow)self;
            if (window.ForCode) return true;
            PMMissionParameter p = window.PMMissionParameter;
            // the game's own popup for a hypothetical plan
            if (p != null && p.Hypothetical) return true;
            if (PlannerWait.IsWaiting(window)) { seam.Note(CommandText.Waiting()); return false; }
            string problem;
            ActionRequest a = FlightRoutes.Read(window, out problem);
            if (a == null)
            {
                Log.Info("cyclical: the route was not sent: " + TextSanitiser.OneLine(problem));
                WindowCapture.CannotSend(seam);
                return false;
            }
            if (!WindowCapture.Send(seam, a, p.FlyCompany)) return false;
            PlannerWait.Begin(window);
            return false;
        }

        // ---- F10: "send supplies to orbit" on a queued construct's row (Left Shift) ----

        static bool FacilityUp(ICaptureSeam seam, object self, object[] args) { return SupplyRow(seam, self, true, false); }
        static bool FacilityDown(ICaptureSeam seam, object self, object[] args) { return SupplyRow(seam, self, true, true); }
        static bool RocketUp(ICaptureSeam seam, object self, object[] args) { return SupplyRow(seam, self, false, false); }
        static bool RocketDown(ICaptureSeam seam, object self, object[] args) { return SupplyRow(seam, self, false, true); }

        // The game's own handler goes on to plan the flight in the hidden planner; this notes which row asked, so the
        // plan can be sent as that row's supply flight when it reaches PlanFlyCode (CaptureSupply). Another agency's
        // row, or one the host would not find by its queue place, is refused here.
        static bool SupplyRow(ICaptureSeam seam, object self, bool facilityRow, bool down)
        {
            object row;
            Company owner;
            ObjectInfoData site;
            if (facilityRow)
            {
                var f = HookTargets.Field<Facility>(self, "facility") as FacilityToConstructNotYetPay;
                if (f == null) return true;   // the game logs and does nothing
                row = f; owner = f.Company; site = f.ObjectInfoData;
            }
            else
            {
                var r = HookTargets.Property<RowRocketData>(self, "CurrentRowRocketData").rConstruct;
                if (r == null || !(r is Data.SpacecraftToConstructNotYetPay || r is Data.LaunchVehicleToConstructNotYetPay))
                    return true;
                row = r; owner = r.Company; site = r.ObjectInfoData;
            }
            int index = FlightSupply.IndexOf(site, row);
            if (index < 0 || site.ObjectInfo == null) { WindowCapture.CannotSend(seam); return false; }
            if (!ReferenceEquals(owner, WindowCapture.Player()))
            {
                ClickGate.Refuse(seam, RefuseCodes.NotYourAgency, "a supply row of another agency");
                return false;
            }
            seam.Memory.Supply.Note(new SupplyClick { Site = site.ObjectInfo.id, Down = down, Index = index,
                Item = site.ListProductionQuery[index].MyIDScriptableObjectProductionItem.ID },
                UnityEngine.Time.frameCount);
            return true;
        }

        // The ship a route belongs to (a route of a craft still being built has none: the host has no request for it).
        static Spacecraft RouteShip(CycleMissionsData cmd)
        {
            if (cmd == null) return null;
            CycleMissionManager manager = MonoBehaviourSingleton<CycleMissionManager>.Instance;
            return manager != null ? manager.GetSC(cmd) : null;
        }
    }
}
