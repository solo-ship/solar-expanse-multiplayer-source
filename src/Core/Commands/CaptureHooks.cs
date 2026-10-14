using System;

namespace SolarExpanseMultiplayer
{
    // The capture classes as hook names (HookPlan) and how many game methods each patches. GameHooks fails a class
    // that patches any other number, so a game update that renames a method, or moves a compiler-made lambda, stops
    // the session instead of leaving a click path half captured. A source test counts each class's targets against
    // these numbers.
    public static class CaptureHooks
    {
        public const string Research = "CaptureResearch", Buildings = "CaptureBuildings",
            Production = "CaptureProduction", Drops = "CaptureDrops", Observatory = "CaptureObservatory",
            Greyed = "GreyedButtons", Tripwire = "Tripwire", PopupNoPause = "PopupNoPause",
            Market = "CaptureMarket", Contracts = "CaptureContracts", World = "CaptureWorld",
            Flights = "CaptureFlights", Supply = "CaptureSupply";

        // Every capture hook, in the order the plan builds them: research, buildings, production, the drops, the
        // observatory, then the greyed paths, the tripwire and the popups, then M7's market, contract and world clicks,
        // then M6's flights and supply runs.
        public static readonly string[] All =
        {
            Research, Buildings, Production, Drops, Observatory, Greyed, Tripwire, PopupNoPause, Market, Contracts, World,
            Flights, Supply
        };

        public const int ResearchMethods = 5;      // R1 (2), R3, R4, R5
        public const int BuildingsMethods = 20;    // B1-B4, B7, B8, B10, B11's popups (11), B9's list windows (8)
        public const int ProductionMethods = 4;    // P1, P2, P3 (2)
        public const int DropsMethods = 3;         // B5, P4 and B11's one-module drop
        public const int ObservatoryMethods = 8;   // O1-O3, the stop-first delegate, O4 (2), O5, forget
        public const int GreyedMethods = 4;        // the two rows' space elevator handler and two single handlers
        public const int TripwireMethods = 30;     // the world methods of TripwireTable's hooked entries
        public const int MarketMethods = 7;        // 80-83 (five handlers), 84, B6 and P5
        public const int ContractsMethods = 3;     // the window's accept and decline, the list row's button
        public const int WorldMethods = 2;         // the nuke's button and the leave-stock dialog's OK
        public const int PopupMethods = 1;
        // M6 Task 11: Schedule, the preset save, abort, scrap (2), route pause, delete (2); then a route's Schedule
        // and the two rows' two supply handlers (F6, F7, F10)
        public const int FlightsMethods = 13;
        public const int SupplyMethods = 2;        // F10's plan: SetPMParameterForCodeJobSystem and PlanFlyCode

        // 0 for a name that is not a capture hook.
        public static int Expected(string name)
        {
            switch (name)
            {
                case Research: return ResearchMethods;
                case Buildings: return BuildingsMethods;
                case Production: return ProductionMethods;
                case Drops: return DropsMethods;
                case Observatory: return ObservatoryMethods;
                case Greyed: return GreyedMethods;
                case Tripwire: return TripwireMethods;
                case PopupNoPause: return PopupMethods;
                case Market: return MarketMethods;
                case Contracts: return ContractsMethods;
                case World: return WorldMethods;
                case Flights: return FlightsMethods;
                case Supply: return SupplyMethods;
                default: return 0;
            }
        }

        public static bool Handles(string name) { return Array.IndexOf(All, name) >= 0; }
    }
}
