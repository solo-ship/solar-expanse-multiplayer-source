using System;
using System.Collections.Generic;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using MissionInfo = Game.Info.MissionInfo;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;

namespace SolarExpanseMultiplayer
{
    // The host's reader of a plan its own game made (plan DD-F4: a route's leg, a rival's flight with the AI on, the
    // end-game flight), read just after its CreateFly as the FlightPlanned (70) entry every client copies through the
    // load path: the trajectory and figures from the plan, the host's mission id and name, the removals its schedule
    // step made, one fuel row with the leftover fuel, and the mission counter. FlightSources raises it; null means it
    // cannot be sent (the host faults, never skips it). Registered on every HumanAgencies.Start (CommandSession).
    public sealed class FlightPlanReader : IFlightPlanReader
    {
        public ActionRequest Planned(PMMissionParameter plan, MissionInfo created, out ApplyNumbers numbers)
        {
            numbers = null;
            if (plan == null || created == null) return null;
            string problem;
            MissionData m = FlightRequests.ReadPlanned(plan, out problem);
            if (m == null)
            {
                Log.Warn("flight: a game-made flight of " + GameAgencies.Wire(plan.FlyCompany) + " cannot be read: "
                    + problem);
                return null;
            }
            m.MissionId = created.id;
            // the name the game gave the mission (a rival's keeps its AI suffix, which MissionNames.ForGame restores)
            m.Name = MissionNames.Clean(created.missionName);
            if (m.Name.Length == 0) m.Name = MissionNames.Clean("Mission " + created.id);
            m.Removals = plan.Transaction != null ? FlightMap.RemovalsOf(plan.Transaction) : new RemovalRow[0];
            var ships = plan.SCList != null && plan.SCList.Count > 0 ? plan.SCList
                : new List<ISpacecraftInfo> { plan.SC };
            m.Ships = FlightMap.ShipRefs(ships);
            bool container = false;
            foreach (CraftRef r in m.Ships) if (r.Kind == CraftKinds.Container) container = true;
            m.Container = container ? 1 : 0;
            if (container) m.Flags |= MissionFlags.OrbitalContainer;
            m.Cargo = FlightHost.WithFuelRow(m.Cargo, plan, m.Figures.LeftOverFuel);
            string invalid = m.Validate(true);
            if (invalid != null)
            {
                Log.Warn("flight: a game-made flight of " + GameAgencies.Wire(plan.FlyCompany) + " is not sendable: "
                    + invalid);
                return null;
            }
            // the mission counter only: the game's own planner made any facility craft long before this CreateFly,
            // so the ship counters are not this flight's to state (the next entry that names them sets them)
            numbers = new ApplyNumbers
            {
                Counters = new[]
                {
                    new CounterRow { Which = CounterKind.MissionId, Before = created.id - 1, After = created.id }
                }
            };
            Log.Info("flight: FlightPlanned " + GameAgencies.Wire(plan.FlyCompany) + " mission " + created.id + " "
                + m.Start + "->" + m.Target + " depart " + m.DepartTicks + " arrive " + m.ArriveTicks);
            return new ActionRequest { Kind = ActionKind.FlightPlanned, Agency = GameAgencies.Wire(plan.FlyCompany),
                Body = m.Start, Mission = m };
        }
    }
}
