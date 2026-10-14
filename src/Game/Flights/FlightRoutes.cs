using System;
using System.Collections.Generic;
using Cargo = Game.ObjectInfoDataScripts.Cargo;
using CargoAll = Game.ObjectInfoDataScripts.CargoAll;
using Company = Game.Company;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using CycleMissionsDataData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsDataData;
using ECargoStart = Game.UI.Windows.Elements.PlanMissionElements.ECargoStart;
using EEnds = Game.UI.Windows.Elements.PlanMissionElements.EEnds;
using EResourceTypeType = Game.ObjectInfoDataScripts.EResourceTypeType;
using ETransferType = Game.UI.Windows.Elements.PlanMissionElements.ETransferType;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using LaunchVehicleType = Data.ScriptableObject.LaunchVehicleType;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // A cyclical route (F6, F7: PlanCyclicalMissionWindow.OnClickSchedule) as a request and as the host's check of one
    // (plan kind 62). The route record keeps what the window's toggles hold and the resource types each side carries;
    // the legs themselves are planned by the host afterwards, one by one, as any game-made flight. The wire form is a
    // CyclicalData: an Out and a Back leg that name the same ships, with RouteRules saying which of their fields count.
    internal static class FlightRoutes
    {
        // ---- the click: the window's fields as a request ----

        // Null with `problem` set when the window holds something a route request cannot carry (the route would then
        // differ on the other games): a craft being built, a launch-vehicle-less facility craft or a container, modules
        // in the cargo, a route that ends on a resource count.
        public static ActionRequest Read(PlanCyclicalMissionWindow w, out string problem)
        {
            problem = null;
            PMMissionParameter p = w.PMMissionParameter;
            CycleMissionsDataData d = w.CycleMissionsDataData;
            bool edit = w.CurrentMode == PlanCyclicalMissionWindow.Mode.Edit;
            CycleMissionsData old = edit ? w.ToEdit : null;
            if (p == null || d == null || (edit && old == null)) { problem = "the route window has no plan"; return null; }
            // the edit window keeps its bodies and agency in the data; a new route takes them from the plan
            ObjectInfo a = edit ? d.A : p.Start, b = edit ? d.B : p.Target;
            Company company = edit ? d.Company : p.FlyCompany;
            if (a == null || b == null || company == null || a == b) { problem = "the route has no two bodies"; return null; }
            if (d.Ends == EEnds.ResourceCount) { problem = "a route that ends on a resource count is not shared"; return null; }
            var primary = p.SC as Spacecraft;
            if (primary == null) { problem = "a route of a craft still being built is not shared"; return null; }
            var ships = new List<ISpacecraftInfo>();
            if (p.SCList != null && p.SCList.Count > 0) ships.AddRange(p.SCList); else ships.Add(primary);
            Spacecraft holder = edit ? MonoBehaviourSingleton<CycleMissionManager>.Instance.GetSC(old) : primary;
            if (holder == null || holder.ID < 0) { problem = "the route's ship has no id a request can name"; return null; }
            CargoRow[] outCargo = Types(d.CargoAllStart, out problem);
            if (problem != null) return null;
            CargoRow[] backCargo = Types(d.CargoAllEnd, out problem);
            if (problem != null) return null;
            string name = edit ? d.CustomNameFromPlanMission : p.MissionName;
            name = MissionNames.Clean(name == null || name == "empty" ? "" : name);
            MissionData o = Leg(a, b, FlightMap.ShipRefs(ships), d.LvTypeA, outCargo, name);
            MissionData back = Leg(b, a, FlightMap.ShipRefs(ships), d.LvTypeB, backCargo, name);
            int ends = (int)d.Ends;
            var route = new CyclicalData
            {
                Out = o, Back = back,
                Flags = RouteFlags.Pack(d.CargoStart == ECargoStart.FlyWithWhatIsAvailable,
                    d.CargoEnd == ECargoStart.FlyWithWhatIsAvailable, d.TransferType == ETransferType.Fastest,
                    d.EmptyTank, ends, (int)d.typeAcceleration),
                EndsCount = d.Ends == EEnds.ThisManyTimes ? d.EndsObjectThisManyTimes : 0,
                EndsUntil = d.Ends == EEnds.Until ? d.EndsObjectUntil.Ticks : 0
            };
            return new ActionRequest { Kind = ActionKind.CyclicalSet, Ship = holder.ID, Value = edit ? 1 : 0,
                Route = route };
        }

        // A leg's request form: the dates and figures are placeholders, the host never reads them (RouteRules).
        static MissionData Leg(ObjectInfo from, ObjectInfo to, CraftRef[] ships, LaunchVehicleType lv, CargoRow[] cargo,
            string name)
        {
            return new MissionData
            {
                MissionId = -1, Start = from.id, Target = to.id, DepartTicks = 1, ArriveTicks = 2, Ships = ships,
                Rockets = lv == null ? new CraftRef[0]
                    : new[] { new CraftRef { Kind = CraftKinds.Rocket, Id = RouteRules.RocketTypeId, Type = lv.ID } },
                Cargo = cargo, Name = name, Creator = MissionCreators.Manual, Grid = new GridPoint(),
                Figures = new FlightFigures(), Container = -1
            };
        }

        // The resource types one side of the route carries, each once. A module, a crew or any other row is a problem.
        static CargoRow[] Types(CargoAll cargo, out string problem)
        {
            problem = null;
            var rows = new List<CargoRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (cargo == null) return rows.ToArray();
            foreach (List<Cargo> list in new[] { cargo.listCargo, cargo.listCargoToOrbit })
            {
                if (list == null) continue;
                foreach (Cargo c in list)
                {
                    if (c.resourceTypeType != EResourceTypeType.resorces || c.resourceType == null)
                    {
                        problem = "a route that carries modules is not shared";
                        return null;
                    }
                    if (seen.Add(c.resourceType.ID)) rows.Add(new CargoRow { Kind = CargoKinds.Resource, Item = c.resourceType.ID });
                }
            }
            return rows.ToArray();
        }

        // ---- the host's check ----

        // 0, or why the route cannot be set: the ships are the acting agency's and free (a new route) or already the
        // route's (an edit), the bodies and types exist, and an edit names the route as it is.
        public static int Check(ActionRequest a, Company c)
        {
            CyclicalData r = a.Route;
            ObjectInfo from = GameAgencies.Body(r.Out.Start), to = GameAgencies.Body(r.Out.Target);
            if (from == null || to == null) return RefuseCodes.BadTarget;
            if (from.IsInGameDestroy || to.IsInGameDestroy) return RefuseCodes.TargetGone;
            Spacecraft holder = FlightChecks.Ship(a.Ship);
            if (holder == null) return RefuseCodes.BadTarget;
            if (!ReferenceEquals(holder.GetCompany(), c)) return RefuseCodes.NotYourAgency;
            var members = new List<Spacecraft>();
            foreach (CraftRef cr in r.Out.Ships)
            {
                Spacecraft s = FlightChecks.Ship(cr.Id);
                if (s == null || s.spacecraftType == null || (cr.Type != null && s.spacecraftType.ID != cr.Type))
                    return RefuseCodes.BadTarget;
                if (!ReferenceEquals(s.GetCompany(), c)) return RefuseCodes.NotYourAgency;
                members.Add(s);
            }
            CycleMissionsData old = MonoBehaviourSingleton<CycleMissionManager>.Instance.GetCycleMission(holder);
            if (a.Value == 0)
            {
                if (!ReferenceEquals(members[0], holder)) return RefuseCodes.BadTarget;
                if (old != null) return RefuseCodes.AlreadyDone;
            }
            else
            {
                if (old == null || old.A != from || old.B != to || old.Ends == EEnds.ResourceCount)
                    return RefuseCodes.BadTarget;
                MissionInfo leg = holder.GetMissionInfo();
                // a leg that has launched is not the editor's to cancel
                if (leg != null && leg.DateLaunch.Ticks <= MonoBehaviourSingleton<Manager.TimeController>.Instance
                    .CurrentTime.Ticks) return RefuseCodes.AlreadyLaunched;
            }
            foreach (Spacecraft s in members)
            {
                // a ship the route already flies may be busy with it; any other must be free and routeless
                bool own = a.Value == 1 && old != null && old.ListSC.Contains(s);
                if (own) continue;
                if (s.CurrentPhase != Spacecraft.EPhase.None || s.GetMissionInfo() != null || s.CycleMissionsData != null)
                    return RefuseCodes.CraftBusy;
            }
            if (!TypesExist(r.Out) || !TypesExist(r.Back)) return RefuseCodes.BadTarget;
            return 0;
        }

        static bool TypesExist(MissionData leg)
        {
            foreach (CraftRef rocket in leg.Rockets ?? new CraftRef[0])
                if (LaunchVehicleTypeOf(rocket.Type) == null) return false;
            try { foreach (CargoRow row in leg.Cargo ?? new CargoRow[0]) FlightMap.ResourceOf(row.Item); }
            catch (InvalidOperationException) { return false; }
            return true;
        }

        // The launch vehicle type with this content id, or null.
        public static LaunchVehicleType LaunchVehicleTypeOf(string id)
        {
            LaunchVehicleType type = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllLaunchVehicleType.GetByID(id);
            return type != null && type.ID == id ? type : null;
        }
    }
}
