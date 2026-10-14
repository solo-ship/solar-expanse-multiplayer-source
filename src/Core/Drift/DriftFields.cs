namespace SolarExpanseMultiplayer
{
    // The fixed field ids for IItemSink.Val / Date: one per value name in the area table (state-accessors.md names the
    // game fields). APPEND ONLY: a record file stores the ids, and `Names` is written once in its header. A constant is
    // the name with its first letter in capitals, and a test pins every constant to its position in `Names`.
    // Magnitudes (FieldSum) are kept for the continuous (double) names only.
    public static class DriftFields
    {
        public const int Value = 0, Balance = 1, MiningFactor = 2,
            CurrentMoney = 3, TotalProfit = 4, ExoplanetCurrentSurveyCost = 5, DiscoveredSystemsCount = 6, ProductionCountID = 7, Count = 8,
            Id = 9, Progress = 10, Complete = 11, BonusFromObservatory = 12,
            Descriptor = 13, Quantity = 14, Enabled = 15, BuildProgress = 16, CountBuild = 17, CountToBuild = 18, StartBuild = 19,
            Mode = 20, DaysToFinish = 21, Target = 22,
            Type = 23, Item = 24, Name = 25,
            Crew = 26, CrewInWork = 27,
            Discovered = 28, BeingDiscovered = 29, Preliminary = 30, Full = 31, Selected = 32,
            Phase = 33, OnObject = 34, TrajStart = 35, TrajEnd = 36, TrajStartDate = 37, TrajEndDate = 38, LaunchTime = 39,
            LandingTime = 40, CargoCount = 41, CargoMass = 42, Body = 43,
            Launch = 44, Arrive = 45, Start = 46, Cancel = 47, Landed = 48, CostFuel = 49, DeltaV = 50,
            State = 51, PerCompanyState = 52, HowMuchCurrent = 53,
            Company = 54, Resource = 55, BuySell = 56, Price = 57, Amount = 58, Completed = 59, Where = 60, Done = 61,
            Subtype = 62, TrueName = 63, Destroyed = 64, NbodyDeleted = 65, PushedAsteroid = 66,
            Temperature = 67, Pressure = 68, Composition = 69, Gravity = 70, Radiation = 71, Result = 72;

        public static readonly string[] Names =
        {
            "Value", "Balance", "MiningFactor",
            "CurrentMoney", "TotalProfit", "exoplanetCurrentSurveyCost", "DiscoveredSystemsCount", "productionCountID", "count",
            "id", "Progress", "complete", "bonusFromObservatory",
            "descriptor", "Quantity", "Enabled", "BuildProgress", "countBuild", "countToBuild", "StartBuild",
            "mode", "daysToFinish", "target",
            "type", "item", "name",
            "crew", "crewInWork",
            "discovered", "beingDiscovered", "preliminary", "full", "selected",
            "phase", "onObject", "trajStart", "trajEnd", "trajStartDate", "trajEndDate", "launchTime",
            "landingTime", "cargoCount", "cargoMass", "body",
            "launch", "arrive", "start", "cancel", "landed", "costFuel", "deltaV",
            "state", "perCompanyState", "howMuchCurrent",
            "company", "resource", "buySell", "price", "amount", "completed", "where", "done",
            "subtype", "trueName", "destroyed", "nbodyDeleted", "pushedAsteroid",
            "temperature", "pressure", "composition", "gravity", "radiation", "result"
        };

        // [add] ScalarValue.Name is an index into this table. The first PerAgencyScalars are per agency, the rest are world scalars.
        public const int ScalarMoney = 0, ScalarProfit = 1, ScalarResearch = 2, ScalarCrew = 3, ScalarStock = 4,
            ScalarOffers = 5, ScalarBodies = 6, ScalarHabitabilityUncomputed = 7;
        public const int PerAgencyScalars = 5;

        public static readonly string[] ScalarNames =
        {
            "money", "profit", "research", "crew", "stock", "offers", "bodies", "habitability-uncomputed"
        };

        // [add] The ScalarValue count of one day: 33 for 6 agencies, as measured in the spike.
        public static int MaxScalars(int agencies) { return PerAgencyScalars * agencies + (ScalarNames.Length - PerAgencyScalars); }
    }
}
