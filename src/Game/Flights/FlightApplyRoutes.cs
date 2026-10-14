using System;
using System.Collections.Generic;
using HarmonyLib;
using CargoAll = Game.ObjectInfoDataScripts.CargoAll;
using Company = Game.Company;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using CycleMissionsDataData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsDataData;
using ECargoStart = Game.UI.Windows.Elements.PlanMissionElements.ECargoStart;
using EEnds = Game.UI.Windows.Elements.PlanMissionElements.EEnds;
using EndsResourceCountData = Game.UI.Windows.Elements.PlanMissionElements.EndsResourceCountData;
using ETransferType = Game.UI.Windows.Elements.PlanMissionElements.ETransferType;
using InfoCargoCyclicalMission = Game.UI.Windows.Elements.PlanMissionElements.InfoCargoCyclicalMission;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using LaunchVehicleType = Data.ScriptableObject.LaunchVehicleType;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // Kind 62 on every game (plan F6, F7): the route window's OnClickSchedule for the agency the entry names, run from the
    // request instead of the window. A new route is added to the ship and the controller tries a first leg (only the
    // host's controller does; the legs a client sees are the host's FlightPlanned entries). An edit does what the
    // editor does when it opens and when it is scheduled: cancel the planned leg, replace the route's ships, cargo
    // types and launch vehicle types in place, then add the route to its (possibly new) ship. Everything the call
    // needs is looked up before the first change, so a request that cannot be followed changes nothing.
    public sealed partial class FlightApply
    {
        static readonly AccessTools.FieldRef<InfoCargoCyclicalMission, ResourceDefinition[]> CargoTypes =
            AccessTools.FieldRefAccess<InfoCargoCyclicalMission, ResourceDefinition[]>("tab");

        ApplyOutcome RouteSet(ActionRequest a, bool host)
        {
            Company c = GameAgencies.Find(a.Agency);
            CyclicalData r = a.Route;
            if (c == null || r == null) return Bad(host);
            ObjectInfo from = GameAgencies.Body(r.Out.Start), to = GameAgencies.Body(r.Out.Target);
            Spacecraft holder = FlightChecks.Ship(a.Ship);
            if (from == null || to == null || holder == null) return Bad(host);
            var list = new List<ISpacecraftInfo>();
            foreach (CraftRef cr in r.Out.Ships)
            {
                Spacecraft s = FlightChecks.Ship(cr.Id);
                if (s == null || !ReferenceEquals(s.GetCompany(), c)) return Bad(host);
                list.Add(s);
            }
            LaunchVehicleType lvA = TypeOf(r.Out), lvB = TypeOf(r.Back);
            ResourceDefinition[] tabA = Types(r.Out), tabB = Types(r.Back);
            if ((r.Out.Rockets.Length > 0 && lvA == null) || (r.Back.Rockets.Length > 0 && lvB == null)
                || tabA == null || tabB == null) return Bad(host);
            CycleMissionManager routes = MonoBehaviourSingleton<CycleMissionManager>.Instance;
            CycleMissionsData old = routes.GetCycleMission(holder);
            if (a.Value == 1 && (old == null || old.A != from || old.B != to)) return Bad(host);
            if (a.Value == 0 && old != null) return Bad(host);

            int flags = r.Flags;
            var d = new CycleMissionsDataData
            {
                A = from, B = to, Company = c, LvTypeA = lvA, LvTypeB = lvB, CustomNameFromPlanMission = r.Out.Name,
                CargoStart = (flags & RouteFlags.FlyStart) != 0 ? ECargoStart.FlyWithWhatIsAvailable
                    : ECargoStart.WaitForCargoToFill,
                CargoEnd = (flags & RouteFlags.FlyEnd) != 0 ? ECargoStart.FlyWithWhatIsAvailable
                    : ECargoStart.WaitForCargoToFill,
                TransferType = (flags & RouteFlags.Fastest) != 0 ? ETransferType.Fastest : ETransferType.Optimal,
                typeAcceleration = (ETypeAcceleration)RouteFlags.Acceleration(flags),
                EmptyTank = (flags & RouteFlags.EmptyTank) != 0, Ends = (EEnds)RouteFlags.Ends(flags),
                EndsObjectThisManyTimes = r.EndsCount, EndsObjectUntil = new DateTime(Math.Max(0, r.EndsUntil)),
                EndsResourceCountDataA = new EndsResourceCountData(), EndsResourceCountMaxA = new EndsResourceCountData(),
                EndsResourceCountDataB = new EndsResourceCountData(), EndsResourceCountMaxB = new EndsResourceCountData(),
                ListSC = list
            };
            if (a.Value == 0) AddRoute(c, d, list, tabA, tabB, from, to);
            else EditRoute(c, old, d, list, tabA, tabB, from, to);

            session.FlightWatches.Dirty();
            Log.Info("cyclical: " + a.Agency + " ship " + a.Ship + (a.Value == 0 ? " route set " : " route edited ")
                + from.id + "<->" + to.id + " with " + list.Count + " ship(s)");
            PlannerWait.Applied(a);
            return Done(a, Missions(a.Agency));
        }

        // OnClickSchedule, Normal mode.
        static void AddRoute(Company c, CycleMissionsDataData d, List<ISpacecraftInfo> list, ResourceDefinition[] tabA,
            ResourceDefinition[] tabB, ObjectInfo from, ObjectInfo to)
        {
            var ship = (Spacecraft)list[0];
            ship = ContainerForCapsule(c, list, ship);
            var route = new CycleMissionsData(d);
            route.customNameFromPlanMission = d.CustomNameFromPlanMission;
            route.cargoAllStart = Types(from, c, tabA);
            route.cargoAllEnd = Types(to, c, tabB);
            MonoBehaviourSingleton<CycleMissionManager>.Instance.AddCycleMission(ship, route, list);
            ship.TryPlanCycleMission();
        }

        // SetMode(Edit) and OnClickSchedule, Edit mode. The editor's window stays on the clicking game until the entry
        // comes back, so what its opening cancelled (the planned leg) is cancelled here, on every game at once.
        static void EditRoute(Company c, CycleMissionsData route, CycleMissionsDataData d, List<ISpacecraftInfo> list,
            ResourceDefinition[] tabA, ResourceDefinition[] tabB, ObjectInfo from, ObjectInfo to)
        {
            CycleMissionManager routes = MonoBehaviourSingleton<CycleMissionManager>.Instance;
            Spacecraft holder = routes.GetSC(route);
            MissionInfo leg = holder.GetMissionInfo();
            if (leg != null && leg.fromCyclicalMission)
            {
                holder.CancelMission(leg);
                holder.ClearCycleMissionsData();
            }
            var before = new List<ISpacecraftInfo>(route.ListSC);
            route.ListSC = list;
            if (route.cargoAllStart != null)
            {
                route.cargoAllStart.ReleaseSpaceModule(from.GetObjectInfoData(c));
                route.cargoAllEnd.ReleaseSpaceModule(from.GetObjectInfoData(c));
            }
            route.cargoAllStart = Types(from, c, tabA);
            route.cargoAllEnd = Types(to, c, tabB);
            route.cargoStart = d.CargoStart;
            route.cargoEnd = d.CargoEnd;
            route.customNameFromPlanMission = d.CustomNameFromPlanMission;
            route.lvTypeA = d.LvTypeA;
            route.lvTypeB = d.LvTypeB;
            var ship = (Spacecraft)list[0];
            if (ship.GetMissionInfo() != null) ship.CancelMission(ship.GetMissionInfo());
            ship.SetFakeCurrentlyOnThisObjectToEditCyclicalMission(null);
            ship.ClearCycleMissionsData(true);
            ship.CraftCyclicalMissionController.CycleMissionPlanFlyWas = false;
            Spacecraft was = routes.GetSC(route);
            if (was != ship)
            {
                routes.RemoveCycleMission(was);
                ship = ContainerForCapsule(c, list, ship);
                routes.AddCycleMission(ship, route, list);
            }
            else routes.RefreshAfterEdit(ship, route, list);
            ship.TryPlanCycleMission();
            foreach (ISpacecraftInfo gone in before)
            {
                var s = gone as Spacecraft;
                if (s != null && !list.Contains(gone)) s.SetMultiMissionParentNull();
            }
        }

        // A capsule flies a route as an orbital container made for the route, in its place in the ship list.
        static Spacecraft ContainerForCapsule(Company c, List<ISpacecraftInfo> list, Spacecraft ship)
        {
            Data.ScriptableObject.SpacecraftType capsule = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>
                .Instance.AllSpacecraftType.GetByID("spacecraft_capsule");
            if (ship.spacecraftType == null || ship.spacecraftType != capsule) return ship;
            Spacecraft container = ShipManager.Instance.AddOrbitalContainerForCyclicalMission(c);
            list[list.IndexOf(ship)] = container;
            return container;
        }

        static LaunchVehicleType TypeOf(MissionData leg)
        {
            return leg.Rockets.Length > 0 ? FlightRoutes.LaunchVehicleTypeOf(leg.Rockets[0].Type) : null;
        }

        // The leg's resource types, or null when one is not in this game.
        static ResourceDefinition[] Types(MissionData leg)
        {
            var found = new List<ResourceDefinition>();
            try { foreach (CargoRow row in leg.Cargo) found.Add(FlightMap.ResourceOf(row.Item)); }
            catch (InvalidOperationException) { return null; }
            return found.ToArray();
        }

        // The route's record of what one side carries: the game's own constructor over no cargo, then the types.
        static InfoCargoCyclicalMission Types(ObjectInfo place, Company c, ResourceDefinition[] tab)
        {
            var info = new InfoCargoCyclicalMission(place.GetObjectInfoData(c), (CargoAll)null);
            CargoTypes(info) = tab;
            return info;
        }
    }
}
