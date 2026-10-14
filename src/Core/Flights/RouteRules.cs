using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The bits of CyclicalData.Flags: what the route window's toggles hold, packed so a route stays inside the request
    // cap (FlightCapsTests). Ends and the acceleration type are two-bit numbers (the game's EEnds and
    // ETypeAcceleration); ResourceCount ends are not carried (the host refuses them with the rest of the unshared
    // route features).
    public static class RouteFlags
    {
        public const int FlyStart = 1, FlyEnd = 2, Fastest = 4, EmptyTank = 8;
        public const int EndsShift = 4, AccelShift = 6;
        public const int Endlessly = 0, ThisManyTimes = 1, Until = 2, ResourceCount = 3;
        public const int MaxAcceleration = 2;

        public static int Pack(bool flyStart, bool flyEnd, bool fastest, bool emptyTank, int ends, int acceleration)
        {
            return (flyStart ? FlyStart : 0) | (flyEnd ? FlyEnd : 0) | (fastest ? Fastest : 0)
                | (emptyTank ? EmptyTank : 0) | ((ends & 3) << EndsShift) | ((acceleration & 3) << AccelShift);
        }

        public static int Ends(int flags) { return (flags >> EndsShift) & 3; }
        public static int Acceleration(int flags) { return (flags >> AccelShift) & 3; }
    }

    // What makes a CyclicalData a route the games can all build from: both legs describe one pair of bodies and one
    // set of ships, each side carries resource types only (the route record keeps types, never amounts), and the
    // fields the trajectories would fill (dates, grid, figures) are placeholders the host never reads.
    public static class RouteRules
    {
        // A route's launch vehicle is named by its type: a rocket reference with this id and the type's content id.
        public const int RocketTypeId = 0;

        public static string Problem(CyclicalData r)
        {
            MissionData o = r.Out, b = r.Back;
            if (b.Start != o.Target || b.Target != o.Start) return "a route's return leg runs from B back to A";
            if (o.Ships.Length != b.Ships.Length) return "a route's legs name different ships";
            for (int i = 0; i < o.Ships.Length; i++)
            {
                if (o.Ships[i].Kind != CraftKinds.Ship) return "a route flies ships only";
                if (o.Ships[i].Key() != b.Ships[i].Key()) return "a route's legs name different ships";
            }
            string e = Side(o, "Out") ?? Side(b, "Back");
            if (e != null) return e;
            if (o.Name != b.Name) return "a route's legs have different names";
            int ends = RouteFlags.Ends(r.Flags);
            if (ends == RouteFlags.ResourceCount) return "a route that ends on a resource count is not shared";
            if (RouteFlags.Acceleration(r.Flags) > RouteFlags.MaxAcceleration) return "Route.Flags acceleration type";
            if (ends == RouteFlags.ThisManyTimes)
            {
                e = CommandCheck.Range(r.EndsCount, "Route.EndsCount", 1, ActionLimits.MaxUnits);
                if (e != null) return e;
            }
            else if (r.EndsCount != 0) return "Route.EndsCount without a count end";
            if (ends == RouteFlags.Until) return JoinCheck.Ticks(r.EndsUntil, "Route.EndsUntil", false);
            return r.EndsUntil != 0 ? "Route.EndsUntil without a date end" : null;
        }

        static string Side(MissionData m, string name)
        {
            CraftRef[] rockets = m.Rockets ?? new CraftRef[0];
            if (rockets.Length > 1) return name + " names more than one launch vehicle type";
            foreach (CraftRef c in rockets)
                if (c.Kind != CraftKinds.Rocket || c.Id != RocketTypeId || c.Type == null)
                    return name + " names its launch vehicle by type only";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CargoRow row in m.Cargo ?? new CargoRow[0])
            {
                if (row.Kind != CargoKinds.Resource || row.Amount != 0 || row.Count != 0)
                    return name + " carries resource types only";
                if (!seen.Add(row.Item)) return name + " lists a resource twice";
            }
            return null;
        }
    }
}
