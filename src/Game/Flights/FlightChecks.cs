using System;
using System.Collections.Generic;
using Company = Game.Company;
using ILaunchVehicleInfo = CustomUpdate.ILaunchVehicleInfo;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The windows' and the planner's checks for a flight request, repeated on the host for the ACTING agency (plan
    // "Game seams", S2/S3): whose crafts, where they are, whether they are free, whether the bodies still exist and
    // whether the launch is far enough ahead. The schedule step's own stock and money checks are the acceptance's
    // (FlightHost); everything here is a read. 0 is allowed, anything else is a RefuseCodes value.
    public static class FlightChecks
    {
        // The kinds FlightChecks answers for.
        public static bool Handles(int kind)
        {
            return (kind >= ActionKind.FlightSchedule && kind <= ActionKind.PresetSave);
        }

        // The agency that owns the request's target: the crafts' (schedule, scrap, cyclical), the mission's (cancel),
        // the construct's (supply flight). null: there is no such target.
        public static string OwnerOf(ActionRequest a)
        {
            switch (a.Kind)
            {
                case ActionKind.FlightSchedule:
                    return a.Mission == null || a.Mission.Ships == null || a.Mission.Ships.Length == 0
                        ? null : OwnerOfCraft(a.Mission.Ships[0], a.Mission.Start, a.Agency);
                case ActionKind.SupplyFlight:
                    return a.Mission == null ? null : OwnerOfConstruct(a);
                case ActionKind.FlightCancel:
                {
                    MissionInfo m = Mission(a.ItemId);
                    return m == null || m.company == null ? null : GameAgencies.Wire(m.company);
                }
                case ActionKind.CyclicalSet: case ActionKind.CyclicalPause: case ActionKind.CyclicalDelete:
                case ActionKind.CraftScrap:
                {
                    if (a.Ship < 0) return a.ItemId >= 0 ? OwnerOfRocket(a) : null;
                    Spacecraft s = Ship(a.Ship);
                    return s == null ? null : GameAgencies.Wire(s.GetCompany());
                }
                case ActionKind.PresetSave: return a.Agency;
                default: return null;
            }
        }

        static string OwnerOfCraft(CraftRef r, int start, string agency)
        {
            // a facility craft, a container and a construct belong to the site they are made at: the agency's own
            // data at the start, which GameAgencies.Site finds by company (never through the fallback)
            if (r.Kind == CraftKinds.Facility || r.Kind == CraftKinds.Container || r.Kind == CraftKinds.Construct)
            {
                Company c = GameAgencies.Find(agency);
                return GameAgencies.Site(GameAgencies.Body(start), c) != null ? agency : null;
            }
            Spacecraft s = Ship(r.Id);
            return s == null ? null : GameAgencies.Wire(s.GetCompany());
        }

        static string OwnerOfConstruct(ActionRequest a)
        {
            Company c = GameAgencies.Find(a.Agency);
            return GameAgencies.Site(GameAgencies.Body(a.Body), c) != null ? a.Agency : null;
        }

        static string OwnerOfRocket(ActionRequest a)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            if (body == null || body.ListLaunchVehicle == null) return null;
            foreach (LaunchVehicle lv in body.ListLaunchVehicle)
                if (lv != null && lv.ID == a.ItemId) return GameAgencies.Wire(lv.company);
            return null;
        }

        public static Spacecraft Ship(long id)
        {
            ShipManager ships = ShipManager.Instance;
            if (ships == null) return null;
            foreach (Spacecraft s in ships.ListAllSpaceShip)
                if (s != null && s.ID == id && !s.scFromFacility) return s;
            return null;
        }

        public static MissionInfo Mission(long id)
        {
            Manager.MissionInfoManager m = MonoBehaviourSingleton<Manager.MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null) return null;
            foreach (MissionInfo mi in m.ListMissionInfo)
                if (mi != null && mi.id == id) return mi;
            return null;
        }

        // 0, or why the request cannot be accepted now. host: the checks of the acceptance; the completion step runs
        // the same again (Recheck) just before it creates anything.
        public static int Check(ActionRequest a, long nextDayTicks)
        {
            Company c = GameAgencies.Find(a.Agency);
            if (c == null) return RefuseCodes.BadTarget;
            switch (a.Kind)
            {
                case ActionKind.FlightSchedule: return CheckSchedule(a, c, nextDayTicks);
                case ActionKind.SupplyFlight:
                {
                    int supply = CheckSchedule(a, c, nextDayTicks);
                    return supply != 0 ? supply : FlightSupply.Check(a, c);
                }
                case ActionKind.CyclicalSet: return FlightRoutes.Check(a, c);
                case ActionKind.FlightCancel: return CheckCancel(a, nextDayTicks);
                case ActionKind.PresetSave:
                    return c.MissionPresets.Count >= ActionLimits.MaxPresetsPerAgency ? RefuseCodes.PresetsFull : 0;
                case ActionKind.CraftScrap: return CheckScrap(a, c);
                case ActionKind.CyclicalPause: case ActionKind.CyclicalDelete:
                {
                    Spacecraft s = Ship(a.Ship);
                    return s != null && s.CycleMissionsData != null ? 0 : RefuseCodes.BadTarget;
                }
                default: return RefuseCodes.NotAllowedHere;
            }
        }

        static int CheckSchedule(ActionRequest a, Company c, long nextDay)
        {
            MissionData m = a.Mission;
            if (m == null) return RefuseCodes.BadTarget;
            ObjectInfo start = GameAgencies.Body(m.Start), target = GameAgencies.Body(m.Target);
            if (start == null || target == null) return RefuseCodes.BadTarget;
            if (start.IsInGameDestroy || target.IsInGameDestroy) return RefuseCodes.TargetGone;
            if (GameAgencies.Site(start, c) == null) return RefuseCodes.BadTarget;
            if (LaunchTiming.TooClose(m.DepartTicks, nextDay)) return RefuseCodes.TooCloseToLaunch;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CraftRef r in m.Ships)
            {
                if (!seen.Add(r.Key())) return RefuseCodes.BadTarget;   // a craft named twice
                int code = CheckShip(c, r, start);
                if (code != 0) return code;
            }
            if (m.Rockets != null)
                foreach (CraftRef r in m.Rockets)
                {
                    if (!seen.Add(r.Key())) return RefuseCodes.BadTarget;
                    int code = CheckRocket(c, r, start);
                    if (code != 0) return code;
                }
            return 0;
        }

        static int CheckShip(Company c, CraftRef r, ObjectInfo start)
        {
            ISpacecraftInfo s = FlightMap.FindShip(c, r, start);
            if (s == null) return RefuseCodes.BadTarget;
            if (r.Type != null && s.GetTypeSpaceCraft() != null && s.GetTypeSpaceCraft().ID != r.Type)
                return RefuseCodes.BadTarget;
            var sc = s as Spacecraft;
            if (sc == null) return 0;
            if (!ReferenceEquals(sc.GetCompany(), c)) return RefuseCodes.NotYourAgency;
            if (r.Kind == CraftKinds.Ship)
            {
                if (sc.CurrentPhase != Spacecraft.EPhase.None || sc.GetMissionInfo() != null) return RefuseCodes.CraftBusy;
                if (sc.CurrentlyOnThisObject != null && sc.CurrentlyOnThisObject.id != start.id)
                    return RefuseCodes.BadTarget;
            }
            return 0;
        }

        static int CheckRocket(Company c, CraftRef r, ObjectInfo start)
        {
            ILaunchVehicleInfo l = FlightMap.FindRocket(c, r, start);
            if (l == null) return RefuseCodes.BadTarget;
            var lv = l as LaunchVehicle;
            if (lv == null) return 0;
            if (!ReferenceEquals(lv.company, c)) return RefuseCodes.NotYourAgency;
            if (r.Type != null && lv.launchVehicleType != null && lv.launchVehicleType.ID != r.Type)
                return RefuseCodes.BadTarget;
            return LvBusy(lv) ? RefuseCodes.CraftBusy : 0;
        }

        // A launch vehicle belongs to a mission that is neither cancelled nor finished.
        static bool LvBusy(LaunchVehicle lv)
        {
            Manager.MissionInfoManager m = MonoBehaviourSingleton<Manager.MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null) return false;
            foreach (MissionInfo mi in m.ListMissionInfo)
            {
                if (mi == null || mi.cancel || mi.complete) continue;
                if (ReferenceEquals(mi.launchVehicleInfo2, lv)) return true;
                if (mi.ListLaunchVehicleInfo2 != null && mi.ListLaunchVehicleInfo2.Contains(lv)) return true;
            }
            return false;
        }

        static int CheckCancel(ActionRequest a, long nextDay)
        {
            MissionInfo m = Mission(a.ItemId);
            if (m == null || m.cancel || m.complete) return RefuseCodes.BadTarget;
            // launched: the abort button is gone (a flight in the air cannot be cancelled by a player)
            if (m.DateLaunch.Ticks <= MonoBehaviourSingleton<Manager.TimeController>.Instance.CurrentTime.Ticks) return RefuseCodes.AlreadyLaunched;
            // a launch inside the entry's own day could already be past on a client (D4)
            if (LaunchTiming.TooClose(m.DateLaunch.Ticks, nextDay)) return RefuseCodes.TooCloseToLaunch;
            if (m.spacecraftInfo2 is Spacecraft s && s.spacecraftType != null
                && s.spacecraftType.ID == "spacecraft_asteroid_puller_ForAsteroidImpact")
                return RefuseCodes.NotAllowedHere;   // vanilla blocks cancelling the impact puller (D 140004)
            return 0;
        }

        static int CheckScrap(ActionRequest a, Company c)
        {
            if (a.Ship >= 0)
            {
                Spacecraft s = Ship(a.Ship);
                if (s == null) return RefuseCodes.BadTarget;
                return s.CurrentPhase != Spacecraft.EPhase.None || s.GetMissionInfo() != null ? RefuseCodes.CraftBusy : 0;
            }
            ObjectInfo body = GameAgencies.Body(a.Body);
            if (body == null || body.ListLaunchVehicle == null) return RefuseCodes.BadTarget;
            foreach (LaunchVehicle lv in body.ListLaunchVehicle)
                if (lv != null && lv.ID == a.ItemId && ReferenceEquals(lv.company, c))
                    return LvBusy(lv) ? RefuseCodes.CraftBusy : 0;
            return RefuseCodes.BadTarget;
        }

        // What the sanity check needs of the game for this request (Y1): the body pair's bounds, whether a craft
        // burns fuel, the window and the asteroid and ship type rules.
        public static SanityFacts Facts(ActionRequest a, Company c, long nowTicks, long nextDayTicks)
        {
            MissionData m = a.Mission;
            ObjectInfo start = GameAgencies.Body(m.Start), target = GameAgencies.Body(m.Target);
            long min, max;
            double dv;
            SanityPairs.Bounds(start.ObjectName, target.ObjectName, out min, out max, out dv);
            var facts = new SanityFacts { MinTransferTicks = min, MaxTransferTicks = max, MaxDeltaV = dv,
                NeedsFuel = true, WindowStartTicks = nextDayTicks,
                WindowEndTicks = nowTicks + 60L * 365L * TimeSpan.TicksPerDay, CanBePushed = true };
            ISpacecraftInfo first = FlightMap.FindShip(c, m.Ships[0], start);
            Data.ScriptableObject.SpacecraftType type = first != null ? first.GetTypeSpaceCraft() : null;
            if (type != null)
            {
                facts.ForbiddenType = type.cheatSC || type.ID == "spacecraft_asteroid_puller_ForAsteroidImpact";
                // the container and the cheat ship burn no fuel the check can ask for
                facts.NeedsFuel = !type.LowOrbitContainer && !type.cheatSC;
            }
            if (HasEntireAsteroid(m) && type != null && !target.AsteroidCanBePush(c, type, m.Ships.Length))
                facts.CanBePushed = false;
            return facts;
        }

        static bool HasEntireAsteroid(MissionData m)
        {
            foreach (CargoRow r in m.Cargo ?? new CargoRow[0]) if (r.Kind == CargoKinds.Ship) return true;
            return false;
        }
    }
}
