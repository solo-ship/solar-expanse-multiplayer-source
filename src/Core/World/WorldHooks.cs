using System;

namespace SolarExpanseMultiplayer
{
    // M7 Tasks 8-10's patch classes as hook names (HookPlan) and how many game methods each patches (the same pinning
    // as CaptureHooks): a game update that renames one stops the session instead of leaving a rule half applied.
    public static class WorldHooks
    {
        public const string Market = "MarketRule", Growth = "GrowthRule", AiSeam = "AiSeam", AiSteps = "AiSteps",
            Contracts = "ContractRule";

        // In the order the plan builds them: the maker, the growth, the AI seam and its recorders, the contracts.
        public static readonly string[] All = { Market, Growth, AiSeam, AiSteps, Contracts };

        public const int MarketMethods = 8;     // the maker's five entry points, OnNewOffer, AddOffer, CancelOffer
        public const int GrowthMethods = 2;     // DoThePeriodicEvents (the year transpiler), OnEachYearPopulationGrowth
        public const int AiSeamMethods = 14;    // the 13 pinned seam methods and ExtractAllFromSaveData
        public const int AiStepsMethods = 13;   // the recorders of DD-W6
        public const int ContractMethods = 13;  // see ContractRule (the end game's ChangeTrajectoryAsteroidToEarth too)

        public static int Expected(string name)
        {
            switch (name)
            {
                case Market: return MarketMethods;
                case Growth: return GrowthMethods;
                case AiSeam: return AiSeamMethods;
                case AiSteps: return AiStepsMethods;
                case Contracts: return ContractMethods;
                default: return 0;
            }
        }

        public static bool Handles(string name) { return Array.IndexOf(All, name) >= 0; }
    }
}
