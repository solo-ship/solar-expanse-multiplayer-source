using System;
using System.Collections.Generic;
using Company = Game.Company;
using ILaunchVehicleInfo = CustomUpdate.ILaunchVehicleInfo;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;

namespace SolarExpanseMultiplayer
{
    // A request read from a planned PMMissionParameter before its schedule step (plan DD-F2): what the acting game's
    // planner computed (the trajectory, the crafts, the cargo, the name, the chosen cell and the figures), with
    // nothing removed and nothing recorded. The window captures, F10 and the `fly` trigger send what this builds. It
    // changes nothing in the world except what the game's own fuel check reserves, which it clears again.
    public static class FlightRequests
    {
        // The mission part of a request; null with `problem` set when the planner holds something a request cannot
        // carry (the plan would then differ on the other games). `supply`: F10's plan (creator 4).
        public static MissionData Read(PMMissionParameter p, bool supply, out string problem)
        {
            return Read(p, supply, false, out problem);
        }

        // A plan the host's own game made and scheduled (a route's leg, a rival's flight, the end-game flight): read
        // after its CreateFly, with its own creator and flags, for the FlightPlanned entry. The removals, id and name
        // are the caller's (FlightPlanReader).
        public static MissionData ReadPlanned(PMMissionParameter p, out string problem)
        {
            return Read(p, false, true, out problem);
        }

        static MissionData Read(PMMissionParameter p, bool supply, bool gameMade, out string problem)
        {
            problem = null;
            if (p == null || p.Start == null || p.Target == null || p.SC == null) { problem = "the plan is incomplete"; return null; }
            if (p.Hypothetical) { problem = "a hypothetical plan is not scheduled"; return null; }
            if (p.ForCyclicalMission && !gameMade) { problem = "a cyclical plan is not a flight"; return null; }
            var ships = new List<ISpacecraftInfo>();
            if (p.SCList != null && p.SCList.Count > 0) ships.AddRange(p.SCList); else ships.Add(p.SC);
            var rockets = new List<ILaunchVehicleInfo>();
            if (p.LVList != null && p.LVList.Count > 0) rockets.AddRange(p.LVList); else if (p.LV != null) rockets.Add(p.LV);
            bool lost;
            CargoRow[] cargo = FlightMap.RowsOf(p.CargoAll, false, p.SC, out lost);
            if (lost)
            {
                problem = "the cargo uses a list a request cannot carry";
                return null;
            }
            string name = p.MissionName == null || p.MissionName == "empty" ? "" : p.MissionName;
            var m = new MissionData
            {
                MissionId = -1, Start = p.Start.id, Target = p.Target.id, DepartTicks = p.DepartureTimeDate.Ticks,
                ArriveTicks = p.Arrival.Ticks, Ships = FlightMap.ShipRefs(ships), Rockets = FlightMap.RocketRefs(rockets),
                Cargo = cargo, Name = MissionNames.Clean(name), Creator = Creator(p, supply), FuelSource = 0,
                Flags = p.Burst ? 0 : MissionFlags.NoBurst, Grid = FlightMap.FromGrid(p.GridClickedData),
                Figures = FlightMap.FiguresOf(p), Container = -1
            };
            if (gameMade)
            {
                // the game's own creator and flags: the plan is already scheduled, so nothing is reserved again
                m.Creator = (int)p.MissionCreator;
                if (p.ForCyclicalMission) m.Flags |= MissionFlags.Cyclical;
                if (p.Fast) m.Flags |= MissionFlags.Fast;
                return m;
            }
            // a plan that buys fuel carries the price the player accepted
            var reserved = new List<ObjectInfoData>();
            var take = p.CheckFuelTakeFrom(false, reserved);
            foreach (ObjectInfoData d in reserved) if (d != null) d.ClearReservedResources();
            bool buyShip = take.Item1 == PMMissionParameter.EFuelTakeFrom.Buy;
            bool buyLv = take.Item2 == PMMissionParameter.EFuelTakeFrom.Buy;
            if (buyShip || buyLv)
            {
                m.Flags |= MissionFlags.BuyFuel;
                m.MaxFuelPrice = (buyShip ? p.FuelCostAllInDollar : 0) + (buyLv ? p.FuelCostAllInDollarLV : 0);
            }
            return m;
        }

        // A request can carry the planner's creator only for the two a player can make; F10's is 4.
        static int Creator(PMMissionParameter p, bool supply)
        {
            if (supply) return MissionCreators.ResourcesToOrbitToBuildSC;
            int c = (int)p.MissionCreator;
            return c == MissionCreators.DragAndDrop ? c : MissionCreators.Manual;
        }

        // Kind 60 from a visible planner's parameter (or a code plan's, for the `fly` trigger).
        public static ActionRequest Schedule(PMMissionParameter p, out string problem)
        {
            MissionData m = Read(p, false, out problem);
            if (m == null) return null;
            return new ActionRequest { Kind = ActionKind.FlightSchedule, Agency = GameAgencies.Wire(p.FlyCompany),
                Body = m.Start, Mission = m };
        }

        // Kind 65 (F10): the production row's construct, the direction and the plan the hidden planner made.
        public static ActionRequest Supply(PMMissionParameter p, int site, int toBody, string contentId, int itemId,
            bool down, out string problem)
        {
            MissionData m = Read(p, true, out problem);
            if (m == null) return null;
            return new ActionRequest { Kind = ActionKind.SupplyFlight, Agency = GameAgencies.Wire(p.FlyCompany),
                Body = site, ToBody = toBody, Item = contentId, ItemId = itemId, Value = down ? 1 : 0, Mission = m };
        }
    }
}
