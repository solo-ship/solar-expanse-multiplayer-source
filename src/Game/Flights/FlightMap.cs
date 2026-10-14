using System;
using System.Collections.Generic;
using Company = Game.Company;
using Cargo = Game.ObjectInfoDataScripts.Cargo;
using CargoAll = Game.ObjectInfoDataScripts.CargoAll;
using CargoAllData = Game.ObjectInfoDataScripts.CargoAllData;
using CargoData = Game.ObjectInfoDataScripts.CargoData;
using EResourceTypeType = Game.ObjectInfoDataScripts.EResourceTypeType;
using GridClickedDataHelp = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter.GridClickedDataHelp;
using IdForObjectInfo = Game.Info.IdForObjectInfo;
using ILaunchVehicleInfo = CustomUpdate.ILaunchVehicleInfo;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using PdsType = Game.PMMissionParameterDataSave;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using Spacecraft = CustomUpdate.Spacecraft;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using Vector3d = UnityEngine.Vector3d;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    // The one place a flight's protocol rows (MissionData and its parts) meet the game's own objects (plan S1's field
    // map). Both directions: a request from a planned parameter (FlightRequests), the game's saved-mission record
    // from an entry (the load path, FlightApply), and the rows a finished mission prints as (FlightRead). Nothing here
    // changes the world; every lookup is by id on the agency named, and a row that finds nothing throws.
    //
    // What a row cannot carry (the plan's protocol 5 has no room): the to-orbit and gravity-assist cargo lists, the
    // cargo's source module and the cyclical route fields. A request built from a planner that uses them loses them,
    // which FlightRequests reports as a problem rather than sending a plan that would differ.
    internal static class FlightMap
    {
        // EarthMoonCase packs the three moon-case switches the schedule step's checks read (bit 0 earthMoonCaseCache,
        // bit 1 MoonCase, bit 2 the transfer type); the cost vectors are display and porkchop only.
        const int EarthMoonBit = 1, MoonCaseBit = 2, TransferBit = 4;

        // internal to the game's assembly: the construct a plan names, found as the load path finds it
        static readonly System.Reflection.MethodInfo ConstructShip = AccessTools.Method(typeof(Company),
            "GetListSpacecraftAndConstructedPlanned", new[] { typeof(int), typeof(bool) });
        static readonly System.Reflection.MethodInfo ConstructLv = AccessTools.Method(typeof(Company),
            "GetListLVAndConstructedPlanned", new[] { typeof(int) });

        static Manager.AllScriptableObjectManager All
        {
            get { return SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance; }
        }

        // ---- the agency's crafts ----

        public static ISpacecraftInfo FindShip(Company c, CraftRef r, ObjectInfo start)
        {
            switch (r.Kind)
            {
                case CraftKinds.Ship:
                    foreach (Spacecraft s in ShipManager.Instance.ListAllSpaceShip)
                        if (s != null && s.ID == r.Id && ReferenceEquals(s.GetCompany(), c)) return s;
                    return null;
                case CraftKinds.Construct:
                    return (ISpacecraftInfo)ConstructShip.Invoke(c, new object[] { (int)r.Id, false });
                case CraftKinds.Facility:
                    return start.GetObjectInfoData(c).GetSpaceraftFromFacility(r.Type, r.Index);
                case CraftKinds.Container:
                    return ShipManager.Instance.GetLowOrbitContainer(c);
                default: return null;
            }
        }

        public static ILaunchVehicleInfo FindRocket(Company c, CraftRef r, ObjectInfo start)
        {
            if (r.Kind == CraftKinds.Construct) return (ILaunchVehicleInfo)ConstructLv.Invoke(c, new object[] { (int)r.Id });
            if (r.Kind != CraftKinds.Rocket) return null;
            if (start.ListLaunchVehicle != null)
                foreach (LaunchVehicle lv in start.ListLaunchVehicle)
                    if (lv != null && lv.ID == r.Id && ReferenceEquals(lv.company, c)) return lv;
            var rows = start.GetObjectInfoData(c).GetListLaunchFacility();   // a facility's fake launch vehicles
            if (rows != null)
                foreach (var row in rows)
                    if (row.launchVehicle != null && row.launchVehicle.ID == r.Id) return row.launchVehicle;
            return null;
        }

        // The craft a ship ref names, for a plan whose agency is `c`, starting at `start`; throws when it is not there.
        public static ISpacecraftInfo NeedShip(Company c, CraftRef r, ObjectInfo start)
        {
            ISpacecraftInfo s = FindShip(c, r, start);
            if (s == null) throw new InvalidOperationException("no ship " + r.Key());
            return s;
        }

        // ---- the saved-mission record from rows ----

        public static ObjectInfo Body(int id)
        {
            ObjectInfo o = GameAgencies.Body(id);
            if (o == null) throw new InvalidOperationException("no body " + id);
            return o;
        }

        // PMTabSchedule.AddToSave's record (D 157983-158105) built from an entry's mission, for LoadMissionOne.
        public static PdsType ToPds(Company c, MissionData m, int missionId)
        {
            ObjectInfo start = Body(m.Start), target = Body(m.Target);
            var d = new PdsType();
            d.missionID = missionId;
            d.start = new IdForObjectInfo(start);
            d.target = new IdForObjectInfo(target);
            d.departureTimeDate = new DateTime(m.DepartTicks);
            d.arrival = new DateTime(m.ArriveTicks);
            d.cancel = false;
            d.cyclicalMission = (m.Flags & MissionFlags.Cyclical) != 0;
            d.noBurst = (m.Flags & MissionFlags.NoBurst) != 0;
            d.missionCreator = (MissionInfo.EMissionCreator)m.Creator;
            d.costType = MissionInfo.ECostType.Fastest;
            d.missionName = m.Name ?? "empty";
            d.sclistID = new List<int>();
            d.scSpacecraftInfolistID = new List<int>();
            d.lvlistID = new List<int>();
            d.lvLaunchVehicleIndolistID = new List<int>();
            CraftRef first = m.Ships[0];
            foreach (CraftRef r in m.Ships)
            {
                switch (r.Kind)
                {
                    case CraftKinds.Ship: d.sclistID.Add((int)r.Id); d.scSpacecraftInfolistID.Add(-1); break;
                    case CraftKinds.Construct: d.sclistID.Add(-1); d.scSpacecraftInfolistID.Add((int)r.Id); break;
                    case CraftKinds.Container: d.sclistID.Add(-3); d.scSpacecraftInfolistID.Add(-1); break;
                    case CraftKinds.Facility:
                        d.scTypeListIDFacilityQuantityIndex.Add(r.Index);
                        d.sclistID.Add(-1);
                        d.scSpacecraftInfolistID.Add(-1);
                        break;
                    default: throw new InvalidOperationException("a ship of kind " + r.Kind);
                }
            }
            switch (first.Kind)
            {
                case CraftKinds.Ship: d.scID = (int)first.Id; break;
                case CraftKinds.Construct: d.scSpacecraftInfoID = (int)first.Id; break;
                case CraftKinds.Container: d.scID = -3; break;
                case CraftKinds.Facility:
                    d.scIsFromFacility = true;
                    d.scTypeIDFacility = first.Type;
                    d.scTypeFacilityQuantityIndex = first.Index;
                    break;
            }
            if (m.Rockets != null && m.Rockets.Length > 0)
            {
                foreach (CraftRef r in m.Rockets)
                {
                    d.lvlistID.Add(r.Kind == CraftKinds.Rocket ? (int)r.Id : -1);
                    d.lvLaunchVehicleIndolistID.Add(r.Kind == CraftKinds.Construct ? (int)r.Id : -1);
                }
                CraftRef lv = m.Rockets[0];
                if (lv.Kind == CraftKinds.Rocket) d.lvID = (int)lv.Id;
                else if (lv.Kind == CraftKinds.Construct) d.lvLaunchVehicleIndoID = (int)lv.Id;
            }
            ISpacecraftInfo firstShip = NeedShip(c, first, start);
            d.cargoAllData = ToCargoData(m, start, firstShip);
            if (m.Figures != null)
            {
                d.allFuelNeed = m.Figures.AllFuelNeed;
                d.optimalFuelNeed = m.Figures.OptimalFuelNeed;
            }
            if (m.Grid != null) d.gridClickedData = ToGrid(m.Grid);
            if (m.Removals != null) d.transaction = ToTransactions(c, m.Removals);
            return d;
        }

        static CargoAllData ToCargoData(MissionData m, ObjectInfo start, ISpacecraftInfo firstShip)
        {
            var all = new CargoAllData
            {
                listCargoData = new List<CargoData>(), listCargoDataToOrbit = new List<CargoData>(),
                listCargoGravityAssists = new List<CargoData>()
            };
            var fuel = new CargoData { resourceTypeType = EResourceTypeType.resorces };
            fuel.resourceType = firstShip.GetTypeSpaceCraft().GetFuelType();
            fuel.cargoMass = m.Figures != null ? m.Figures.LeftOverFuel : 0;
            fuel.lifeSupportValue = m.Figures != null ? m.Figures.LifeSupport : 0;
            foreach (CargoRow row in m.Cargo ?? new CargoRow[0])
            {
                switch (row.Kind)
                {
                    case CargoKinds.Fuel:
                        if (row.Item != null) fuel.resourceType = ResourceOf(row.Item);
                        fuel.cargoMass = row.Amount;
                        break;
                    case CargoKinds.Ship:
                        all.entireAsteroid = true;
                        break;
                    case CargoKinds.Resource:
                        all.listCargoData.Add(new CargoData { resourceType = ResourceOf(row.Item),
                            resourceTypeType = EResourceTypeType.resorces, cargoMass = row.Amount,
                            idObjectInfoToRevert = start.id });
                        break;
                    case CargoKinds.Module:
                        all.listCargoData.Add(new CargoData { moduleData = Module(row.Item),
                            resourceTypeType = EResourceTypeType.modules, cargoMass = row.Count,
                            idObjectInfoToRevert = start.id });
                        break;
                    case CargoKinds.Crew:
                        all.listCargoData.Add(new CargoData { moduleData = row.Item == null ? null : Module(row.Item),
                            resourceTypeType = EResourceTypeType.modules, crew = true, crewValue = row.Count,
                            cargoMass = row.Amount, idObjectInfoToRevert = start.id });
                        break;
                }
            }
            all.cargoFuel = fuel;
            return all;
        }

        public static ScriptableObjectScripts.ResourceDefinition ResourceOf(string id)
        {
            var rd = All.AllResourceDefinitions.GetByID(id);
            if (rd == null || rd.ID != id) throw new InvalidOperationException("no resource " + id);
            return rd;
        }

        static Data.ScriptableObject.SpaceModuleDescriptor Module(string id)
        {
            var md = All.AllFacility.GetByID(id) as Data.ScriptableObject.SpaceModuleDescriptor;
            if (md == null || md.ID != id) throw new InvalidOperationException("no module " + id);
            return md;
        }

        // A preset's cargo as the game keeps it: rows by content id, an empty fuel row (a preset holds no leftover).
        public static CargoAllData PresetCargo(CargoRow[] rows)
        {
            var all = new CargoAllData
            {
                listCargoData = new List<CargoData>(), listCargoDataToOrbit = new List<CargoData>(),
                listCargoGravityAssists = new List<CargoData>(), cargoFuel = new CargoData()
            };
            foreach (CargoRow row in rows ?? new CargoRow[0])
            {
                switch (row.Kind)
                {
                    case CargoKinds.Resource:
                        all.listCargoData.Add(new CargoData { resourceType = ResourceOf(row.Item),
                            resourceTypeType = EResourceTypeType.resorces, cargoMass = row.Amount });
                        break;
                    case CargoKinds.Module:
                        all.listCargoData.Add(new CargoData { moduleData = Module(row.Item),
                            resourceTypeType = EResourceTypeType.modules, cargoMass = row.Count });
                        break;
                    case CargoKinds.Crew:
                        all.listCargoData.Add(new CargoData { moduleData = row.Item == null ? null : Module(row.Item),
                            resourceTypeType = EResourceTypeType.modules, crew = true, crewValue = row.Count,
                            cargoMass = row.Amount });
                        break;
                    case CargoKinds.Ship: all.entireAsteroid = true; break;
                }
            }
            return all;
        }

        // ---- grid, figures ----

        public static GridClickedDataHelp ToGrid(GridPoint g)
        {
            return new GridClickedDataHelp
            {
                v1 = new Vector3d(g.V1X, g.V1Y, g.V1Z), departPos = new Vector3d(g.DepX, g.DepY, g.DepZ),
                arrivalPos = new Vector3d(g.ArrX, g.ArrY, g.ArrZ)
            };
        }

        public static GridPoint FromGrid(GridClickedDataHelp g)
        {
            if (g == null) return new GridPoint();
            return new GridPoint { V1X = g.v1.x, V1Y = g.v1.y, V1Z = g.v1.z, DepX = g.departPos.x,
                DepY = g.departPos.y, DepZ = g.departPos.z, ArrX = g.arrivalPos.x, ArrY = g.arrivalPos.y,
                ArrZ = g.arrivalPos.z };
        }

        // The values the schedule step reads, set on a parameter the way the planner's own setters do (D 153359,
        // 153383) plus the porkchop-only inputs Task 2 found (the fuel row's capacity and the moon-case switches).
        public static void ApplyFigures(PMMissionParameter p, FlightFigures f)
        {
            p.SetDeltaV(f.DV11, f.DV22);
            p.SetFuelNeed(f.FuelNeed, f.MinFuelCost, f.LeftOverFuel, f.FlightCost, f.FuelToOrbit, f.CostStartLV);
            p.SetOptimalFuelNeed((float)f.OptimalFuelNeed);
            p.SetTransferLambert1(true);
            p.CargoAll.cargoFuel.lifeSupportValue = f.LifeSupport;
            p.CargoAll.cargoFuel.cargoMassPotencjal = f.CargoMassPotencjal;
            p.earthMoonCaseCache = (f.EarthMoonCase & EarthMoonBit) != 0;
            p.SetMoonCase((f.EarthMoonCase & MoonCaseBit) != 0, UnityEngine.Vector2.zero, UnityEngine.Vector2.zero, 0f);
            p.TransferTypeMoonCase = (f.EarthMoonCase & TransferBit) != 0
                ? Game.UI.Windows.Elements.PlanMissionElements.ETransferType.Optimal
                : Game.UI.Windows.Elements.PlanMissionElements.ETransferType.Fastest;
        }

        public static FlightFigures FiguresOf(PMMissionParameter p)
        {
            var f = new FlightFigures
            {
                DV11 = p.DV11 ?? 0.0, DV22 = p.DV22 ?? 0.0, FuelNeed = p.AllFuelNeed, MinFuelCost = p.MINFuelCost,
                LeftOverFuel = p.LeftOverFuel, FlightCost = p.FlightCost, FuelToOrbit = p.FuelNeedToGetFuelToOrbit,
                CostStartLV = p.AllFuelNeedLV, AllFuelNeed = p.AllFuelNeed, OptimalFuelNeed = p.OptimalFuelNeed,
                LifeSupport = p.CargoAll.cargoFuel.lifeSupportValue,
                CargoMassPotencjal = p.CargoAll.cargoFuel.cargoMassPotencjal,
                GravityAssistClick = p.gravityAssistChangeDVOnForUI
            };
            if (p.earthMoonCaseCache) f.EarthMoonCase |= EarthMoonBit;
            if (p.MoonCase) f.EarthMoonCase |= MoonCaseBit;
            if (p.TransferTypeMoonCase == Game.UI.Windows.Elements.PlanMissionElements.ETransferType.Optimal)
                f.EarthMoonCase |= TransferBit;
            return f;
        }

        // ---- removals ----

        // The entry's removals as the game's own transaction list (so a later cancel refunds the same amounts).
        public static ObjectInfoData.TransactionRemoveResource[] ToTransactions(Company c, RemovalRow[] rows)
        {
            var list = new List<ObjectInfoData.TransactionRemoveResource>();
            foreach (RemovalRow r in rows)
            {
                Company who = GameAgencies.Find(r.Agency);
                if (who == null) throw new InvalidOperationException("no agency " + r.Agency);
                var t = new ObjectInfoData.TransactionRemoveResource { transactionOk = true, amount = r.Amount,
                    removeMoney = r.What == RemovalKinds.Money, Company = who };
                if (r.What == RemovalKinds.Resource)
                {
                    t.RD = ResourceOf(r.Resource);
                    t.ObjectInfoData = Body(r.Body).GetObjectInfoData(who);
                }
                list.Add(t);
            }
            return list.ToArray();
        }

        // What a schedule step took, as rows, in order. Null entries (the buy branches leave them) are skipped, a
        // money row never reads its object data, and a row the game did not really make, or a gravity-assist row, is
        // left out because no game removes it; so is a row of 0 (RemovalRules.WithoutEmpty).
        public static RemovalRow[] RemovalsOf(IEnumerable<ObjectInfoData.TransactionRemoveResource> list)
        {
            var rows = new List<RemovalRow>();
            if (list == null) return rows.ToArray();
            foreach (ObjectInfoData.TransactionRemoveResource t in list)
            {
                if (t == null || !t.transactionOk || t.transactionGravityAssists) continue;
                if (t.removeMoney)
                {
                    rows.Add(new RemovalRow { What = RemovalKinds.Money, Agency = GameAgencies.Wire(t.Company),
                        Amount = t.amount });
                    continue;
                }
                rows.Add(new RemovalRow { What = RemovalKinds.Resource, Agency = GameAgencies.Wire(t.Company),
                    Body = t.ObjectInfoData.id, Resource = t.RD.ID, Amount = t.amount });
            }
            return RemovalRules.WithoutEmpty(rows);
        }

        // ---- crafts as refs ----

        public static CraftRef RefOf(ISpacecraftInfo s)
        {
            var sc = s as Spacecraft;
            // ReferenceEquals: a launched rocket or a landed ship is destroyed, and Unity's == then says null
            if (!ReferenceEquals(sc, null))
            {
                if (sc.ID == -3 || sc.ID == -2) return new CraftRef { Kind = CraftKinds.Container };
                if (sc.scFromFacility)
                    return new CraftRef { Kind = CraftKinds.Facility, Index = sc.FacilityQuantityIndex,
                        Type = sc.spacecraftType.ID };
                return new CraftRef { Kind = CraftKinds.Ship, Id = sc.ID, Type = sc.spacecraftType.ID };
            }
            var c = s as SpacecraftConstructData;
            if (c != null) return new CraftRef { Kind = CraftKinds.Construct, Id = c.ID, Type = c.GetTypeSpaceCraft().ID };
            throw new InvalidOperationException("a ship of an unknown kind: " + s.GetType().Name);
        }

        public static CraftRef RefOf(ILaunchVehicleInfo l)
        {
            var lv = l as LaunchVehicle;
            if (!ReferenceEquals(lv, null))
                return new CraftRef { Kind = CraftKinds.Rocket, Id = lv.ID, Type = lv.launchVehicleType.ID };
            var c = l as SpacecraftConstructData;
            if (c != null) return new CraftRef { Kind = CraftKinds.Construct, Id = c.ID, Type = c.GetTypeSpaceCraft().ID };
            throw new InvalidOperationException("a launch vehicle of an unknown kind: " + l.GetType().Name);
        }

        public static CraftRef[] ShipRefs(IList<ISpacecraftInfo> list)
        {
            var refs = new List<CraftRef>();
            foreach (ISpacecraftInfo s in list ?? new List<ISpacecraftInfo>()) if (s != null) refs.Add(RefOf(s));
            return refs.ToArray();
        }

        public static CraftRef[] RocketRefs(IList<ILaunchVehicleInfo> list)
        {
            var refs = new List<CraftRef>();
            foreach (ILaunchVehicleInfo l in list ?? new List<ILaunchVehicleInfo>()) if (l != null) refs.Add(RefOf(l));
            return refs.ToArray();
        }

        // ---- cargo as rows ----

        // The main cargo list and the fuel row (when withFuel, its mass the leftover). The to-orbit and
        // gravity-assist lists have no rows: lost is true when either holds anything.
        public static CargoRow[] RowsOf(CargoAll ca, bool withFuel, ISpacecraftInfo firstShip, out bool lost)
        {
            var rows = new List<CargoRow>();
            lost = false;
            if (ca == null) return rows.ToArray();
            lost = (ca.listCargoToOrbit != null && ca.listCargoToOrbit.Count > 0) || (ca.listCargoGravityAssists != null
                && ca.listCargoGravityAssists.Count > 0);
            foreach (Cargo c in ca.listCargo)
            {
                if (c.resourceTypeType == EResourceTypeType.resorces && c.resourceType != null)
                    rows.Add(new CargoRow { Kind = CargoKinds.Resource, Item = c.resourceType.ID, Amount = c.cargoMass });
                else if (c.crew)
                    rows.Add(new CargoRow { Kind = CargoKinds.Crew, Item = c.moduleData != null ? c.moduleData.ID : null,
                        Count = c.crewValue, Amount = c.cargoMass });
                else if (c.moduleData != null)
                    rows.Add(new CargoRow { Kind = CargoKinds.Module, Item = c.moduleData.ID, Count = (int)c.cargoMass });
                else lost = true;
            }
            if (ca.entireAsteroid && firstShip != null)
                rows.Add(new CargoRow { Kind = CargoKinds.Ship, Item = firstShip.GetTypeSpaceCraft().ID });
            if (withFuel)
                rows.Add(new CargoRow { Kind = CargoKinds.Fuel,
                    Item = ca.cargoFuel.resourceType != null ? ca.cargoFuel.resourceType.ID : null,
                    Amount = ca.cargoFuel.cargoMass });
            return rows.ToArray();
        }

        // ---- a mission as rows ----

        // The finished mission, as the print and the after-check read it.
        public static MissionRow RowOf(MissionInfo m)
        {
            var crafts = new List<string>();
            foreach (ISpacecraftInfo s in m.ListSpacecraftInfo2) if (s != null) crafts.Add(Key(RefOf(s), "S", "C", "F", "K"));
            foreach (ILaunchVehicleInfo l in m.ListLaunchVehicleInfo2) if (l != null) crafts.Add(Key(RefOf(l), "R", "C", "F", "K"));
            return new MissionRow
            {
                MissionId = m.id, Start = m.trajectoryObject.StartObjectInfo.id,
                Target = m.trajectoryObject.EndObjectInfo.id, DepartTicks = m.DateLaunch.Ticks,
                ArriveTicks = m.DateArrive.Ticks, Crafts = crafts.ToArray(), Cancelled = m.cancel,
                LeftOverFuelBits = BitConverter.DoubleToInt64Bits(m.cargoAll != null ? m.cargoAll.cargoFuel.cargoMass : 0),
                CostType = (int)m.costType
            };
        }

        // The key MissionRow.Crafts uses: "S<id>", "R<id>", "C<item id>", "F<type>#<index>", "K".
        static string Key(CraftRef r, string ship, string construct, string facility, string container)
        {
            switch (r.Kind)
            {
                case CraftKinds.Ship: return ship + r.Id;
                case CraftKinds.Rocket: return "R" + r.Id;
                case CraftKinds.Construct: return construct + r.Id;
                case CraftKinds.Facility: return facility + r.Type + "#" + r.Index;
                default: return container;
            }
        }
    }
}
