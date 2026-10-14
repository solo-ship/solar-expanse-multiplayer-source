using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What the host's game tells FlightSanity about a request (Task 9 fills it).
    public sealed class SanityFacts
    {
        public long MinTransferTicks;     // the shortest believable transfer between the two bodies at that departure
        public bool NeedsFuel;            // a craft of the mission burns fuel for this transfer
        public double MaxDeltaV;          // a generous ceiling for the bodies, per leg (F3, SanityPairs)
        public long MaxTransferTicks;     // the longest believable transfer for the pair
        public long WindowStartTicks, WindowEndTicks;   // the departure window the planner offers (the pad, S6)
        public bool CanBePushed;          // AsteroidCanBePush for whole-asteroid cargo (D 125012), true otherwise
        public bool ForbiddenType;        // a cheatSC ship type or the asteroid-impact puller
    }

    // Y1: a client's numbers within coarse bounds; never a re-plan. Capability (thrust, fit, stock) is
    // CheckCanPlanMission's. A modified client can still fly cheaper than the game allows inside these bounds: the
    // user's accepted risk.
    public static class FlightSanity
    {
        const int MaxCrafts = ActionLimits.MaxMissionCrafts, MaxCargoRows = ActionLimits.MaxCargoRows;

        // null, or why the request's numbers are not believable.
        public static string Problem(MissionData request, string agency, SanityFacts facts)
        {
            if (facts == null) throw new ArgumentNullException("facts");
            if (request == null) return "no mission";
            string why = Fields(request);
            if (why != null) return why;

            FlightFigures f = request.Figures;
            // the two legs, each against the ceiling (the largest leg seen anywhere was 19.2 km/s); the sum must be
            // real
            if (!(f.DV11 + f.DV22 > 0)) return "delta-v is not above 0";
            if (f.DV11 > facts.MaxDeltaV || f.DV22 > facts.MaxDeltaV) return "delta-v is above the ceiling";
            if (facts.NeedsFuel && !(f.FuelNeed > 0)) return "no fuel for a craft that needs it";
            // the fuel left cannot be more than the fuel loaded
            if (f.LeftOverFuel > f.FuelNeed) return "leftover fuel is above the loaded fuel";

            if (request.DepartTicks < facts.WindowStartTicks || request.DepartTicks > facts.WindowEndTicks)
                return "the departure is outside the window";
            long transfer = request.ArriveTicks - request.DepartTicks;
            if (transfer < facts.MinTransferTicks) return "the transfer is shorter than believable";
            if (transfer > facts.MaxTransferTicks) return "the transfer is longer than believable";

            if (HasRepeat(request.Ships, request.Rockets)) return "a craft is named twice";
            if (!facts.CanBePushed) return "that asteroid cannot be pushed";
            if (facts.ForbiddenType) return "that ship type is not allowed";
            if (request.Removals != null)
            {
                string removal = RemovalRules.Problem(request.Removals, agency);
                if (removal != null) return removal;
            }
            return null;
        }

        // Every field in range: ids, dates, counts, and each figure finite and not negative.
        static string Fields(MissionData m)
        {
            if (m.Start < 1 || m.Start > ActionLimits.MaxBodyId) return "the start is not a body";
            if (m.Target < 1 || m.Target > ActionLimits.MaxBodyId) return "the target is not a body";
            if (m.Start == m.Target) return "the start is the target";
            if (m.DepartTicks < 0 || m.ArriveTicks > DateTime.MaxValue.Ticks) return "a date is out of range";
            if (m.DepartTicks >= m.ArriveTicks) return "the flight does not arrive after it departs";
            if (m.Ships == null || m.Ships.Length < 1 || m.Ships.Length > MaxCrafts) return "the ships are not 1 to 32";
            if (m.Rockets != null && m.Rockets.Length > MaxCrafts) return "too many launch vehicles";
            foreach (CraftRef c in m.Ships) if (c == null) return "a ship is missing";
            if (m.Rockets != null)
                foreach (CraftRef c in m.Rockets) if (c == null) return "a launch vehicle is missing";
            if (m.Cargo != null)
            {
                if (m.Cargo.Length > MaxCargoRows) return "too many cargo rows";
                foreach (CargoRow c in m.Cargo)
                {
                    if (c == null) return "a cargo row is missing";
                    if (!Plain(c.Amount)) return "a cargo amount is out of range";
                }
            }
            if (!Plain(m.MaxFuelPrice)) return "the accepted fuel price is out of range";
            if (m.Figures == null) return "the figures are missing";
            if (m.Grid == null) return "the grid cell is missing";
            FlightFigures f = m.Figures;
            double[] all = { f.DV11, f.DV22, f.FuelNeed, f.MinFuelCost, f.LeftOverFuel, f.FlightCost, f.FuelToOrbit,
                             f.CostStartLV, f.AllFuelNeed, f.OptimalFuelNeed, f.LifeSupport, f.CargoMassPotencjal };
            foreach (double d in all) if (!Plain(d)) return "a figure is out of range";
            return null;
        }

        // finite, not negative, at most MaxMoney
        static bool Plain(double d) { return d >= 0 && d <= ActionLimits.MaxMoney; }

        static bool HasRepeat(CraftRef[] ships, CraftRef[] rockets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CraftRef c in Join(ships, rockets))
            {
                string key = c.Kind + "|" + c.Id + "|" + c.Index + "|" + c.Type;
                if (!seen.Add(key)) return true;
            }
            return false;
        }

        static IEnumerable<CraftRef> Join(CraftRef[] a, CraftRef[] b)
        {
            if (a != null) foreach (CraftRef c in a) yield return c;
            if (b != null) foreach (CraftRef c in b) yield return c;
        }
    }
}
