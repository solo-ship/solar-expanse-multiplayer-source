namespace SolarExpanseMultiplayer
{
    // One kind per player action, close to the game call that applies it (docs/research/commands.md section 1), and the
    // host-decided kinds the stream also carries. The numbers are on the wire: never reuse one.
    public static class ActionKind
    {
        public const int ResearchStart = 1, ResearchAdd = 2, ResearchAddWithPrereqs = 3, ResearchSetQueue = 4;
        // 18 (a facility's own action) is retired: only the energy-transfer and mirror windows' list edits are sent
        public const int Build = 10, BuildQueue = 11, BuildCancel = 12, Scrap = 13, BuildReorder = 14, BuyAndBuild = 15,
            FacilityEnabled = 16, FacilityPriority = 17, SelectDeposit = 19, MoveToOrbit = 20,
            EnergyTargets = 21, MirrorTargets = 22;
        public const int CraftBuild = 30, CraftQueue = 31, CraftCancel = 32, CraftReorder = 33, CraftBuyAndBuild = 34;
        // the observatory's five player actions (examine, discover, survey, stop, notifications: a set kind) and the
        // asteroid "forget" button
        public const int ObservatoryExamine = 40, ObservatoryDiscover = 41, ObservatorySurvey = 42,
            ObservatoryStop = 43,
            ObservatoryNotify = 44, ForgetObject = 45;
        // host-decided: never from a client
        public const int AgencyBankrupt = 50, SeatClaimed = 51, SeatReleased = 52, CraftFinished = 53;
        // the observatory's own state changes (a step done, an asteroid found, a star system found, a re-plan or stop
        // outside a player's action) and the asteroid renewal loop (a spawn the game made, an object removed);
        // for the last two, Body is the object concerned and ItemId the impacting mission's id (-1: none)
        public const int ObservatoryStep = 54, ObservatoryFound = 55, SystemFound = 56, ObservatoryReplan = 57,
            AsteroidSpawned = 58, ObjectRemoved = 59;

        // M6 flights (DD-F1). Player kinds 60-67; host-decided 70-74. Never reuse a number.
        public const int FlightSchedule = 60, FlightCancel = 61, CyclicalSet = 62, CyclicalPause = 63,
            CyclicalDelete = 64, SupplyFlight = 65, CraftScrap = 66, PresetSave = 67;
        public const int FlightPlanned = 70, MissionCancelled = 71, AsteroidPulled = 72, LandingCraft = 73,
            MissionLaunched = 74;

        // M7 shared world (protocol 6, DD-W2..W7). Player kinds 80-89; host-decided 90-96 (HostOnly adds 90-99). Never reuse a
        // number.
        public const int OfferCreate = 80, OfferCancel = 81, OfferEdit = 82, OfferTake = 83, MarketInstant = 84,
            NukeAsteroid = 85, ContractAccept = 86, ContractCancel = 87, ObjectiveUi = 88, LeaveStock = 89;
        public const int MarketMaker = 90, PopulationGrowth = 91, ObjectiveDone = 92, ContractDone = 93, AiAction = 94,
            ContractActivated = 95, EndGameShip = 96;

        // The kinds DD-F9's flight bucket counts.
        public static bool IsFlight(int k)
        {
            return k == FlightSchedule || k == CyclicalSet || k == SupplyFlight || k == PresetSave;
        }

        public static bool IsKnown(int k)
        {
            return (k >= ResearchStart && k <= ResearchSetQueue) || (k >= Build && k <= FacilityPriority)
                || (k >= SelectDeposit && k <= MirrorTargets) || (k >= CraftBuild && k <= CraftBuyAndBuild)
                || (k >= ObservatoryExamine && k <= ForgetObject) || (k >= AgencyBankrupt && k <= ObjectRemoved)
                || (k >= FlightSchedule && k <= PresetSave) || (k >= FlightPlanned && k <= MissionLaunched)
                || (k >= OfferCreate && k <= LeaveStock) || (k >= MarketMaker && k <= EndGameShip);
        }

        // A "set" action says what a value should be, so only the latest of a burst matters; every other kind is a
        // "do".
        public static bool IsSet(int k)
        {
            return k == ResearchSetQueue || k == FacilityEnabled || k == FacilityPriority || k == SelectDeposit
                || k == EnergyTargets || k == MirrorTargets || k == ObservatoryNotify || k == CyclicalPause;
        }

        public static bool HostOnly(int k)
        {
            return (k >= AgencyBankrupt && k <= ObjectRemoved) || (k >= FlightPlanned && k <= MissionLaunched)
                || (k >= MarketMaker && k <= 99);
        }

        // Which entries may carry the observatory and asteroid numbers (ActionResult.Validate holds them to it, so the
        // worst case of an entry is the worst case of its kind). An observatory's dice come with its own steps, with
        // the two orders that pick a target (examine and discover) and with a finished craft (a probe observatory); its
        // state after, with its actions and steps; spawns, with a removal.
        public static bool CarriesDraws(int k)
        {
            return k == ObservatoryExamine || k == ObservatoryDiscover
                || (k >= ObservatoryStep && k <= ObservatoryReplan) || k == CraftFinished;
        }

        public static bool CarriesAfter(int k)
        {
            return (k >= ObservatoryExamine && k <= ObservatoryNotify)
                || (k >= ObservatoryStep && k <= ObservatoryReplan);
        }

        public static bool CarriesSpawns(int k)
        {
            return k == ForgetObject || k == AsteroidSpawned || k == ObjectRemoved;
        }

        // The kinds that carry market ops in their request (and in their entry's Ops).
        public static bool IsMarket(int k) { return k >= OfferCreate && k <= MarketInstant; }

        // The kinds a rival's AI may be the origin of: its observatory order and its tree-task records. A rival's flights
        // and crafts are Host-origin entries (70, 53).
        public static bool AiAllowed(int k) { return k == ObservatoryExamine || k == AiAction; }

        // For logs.
        public static string Name(int k)
        {
            switch (k)
            {
                case ResearchStart: return "ResearchStart";
                case ResearchAdd: return "ResearchAdd";
                case ResearchAddWithPrereqs: return "ResearchAddWithPrereqs";
                case ResearchSetQueue: return "ResearchSetQueue";
                case Build: return "Build";
                case BuildQueue: return "BuildQueue";
                case BuildCancel: return "BuildCancel";
                case Scrap: return "Scrap";
                case BuildReorder: return "BuildReorder";
                case BuyAndBuild: return "BuyAndBuild";
                case FacilityEnabled: return "FacilityEnabled";
                case FacilityPriority: return "FacilityPriority";
                case SelectDeposit: return "SelectDeposit";
                case MoveToOrbit: return "MoveToOrbit";
                case EnergyTargets: return "EnergyTargets";
                case MirrorTargets: return "MirrorTargets";
                case CraftBuild: return "CraftBuild";
                case CraftQueue: return "CraftQueue";
                case CraftCancel: return "CraftCancel";
                case CraftReorder: return "CraftReorder";
                case CraftBuyAndBuild: return "CraftBuyAndBuild";
                case AgencyBankrupt: return "AgencyBankrupt";
                case SeatClaimed: return "SeatClaimed";
                case SeatReleased: return "SeatReleased";
                case CraftFinished: return "CraftFinished";
                case ObservatoryExamine: return "ObservatoryExamine";
                case ObservatoryDiscover: return "ObservatoryDiscover";
                case ObservatorySurvey: return "ObservatorySurvey";
                case ObservatoryStop: return "ObservatoryStop";
                case ObservatoryNotify: return "ObservatoryNotify";
                case ForgetObject: return "ForgetObject";
                case ObservatoryStep: return "ObservatoryStep";
                case ObservatoryFound: return "ObservatoryFound";
                case SystemFound: return "SystemFound";
                case ObservatoryReplan: return "ObservatoryReplan";
                case AsteroidSpawned: return "AsteroidSpawned";
                case ObjectRemoved: return "ObjectRemoved";
                case FlightSchedule: return "FlightSchedule";
                case FlightCancel: return "FlightCancel";
                case CyclicalSet: return "CyclicalSet";
                case CyclicalPause: return "CyclicalPause";
                case CyclicalDelete: return "CyclicalDelete";
                case SupplyFlight: return "SupplyFlight";
                case CraftScrap: return "CraftScrap";
                case PresetSave: return "PresetSave";
                case FlightPlanned: return "FlightPlanned";
                case MissionCancelled: return "MissionCancelled";
                case AsteroidPulled: return "AsteroidPulled";
                case LandingCraft: return "LandingCraft";
                case MissionLaunched: return "MissionLaunched";
                case OfferCreate: return "OfferCreate";
                case OfferCancel: return "OfferCancel";
                case OfferEdit: return "OfferEdit";
                case OfferTake: return "OfferTake";
                case MarketInstant: return "MarketInstant";
                case NukeAsteroid: return "NukeAsteroid";
                case ContractAccept: return "ContractAccept";
                case ContractCancel: return "ContractCancel";
                case ObjectiveUi: return "ObjectiveUi";
                case LeaveStock: return "LeaveStock";
                case MarketMaker: return "MarketMaker";
                case PopulationGrowth: return "PopulationGrowth";
                case ObjectiveDone: return "ObjectiveDone";
                case ContractDone: return "ContractDone";
                case AiAction: return "AiAction";
                case ContractActivated: return "ContractActivated";
                case EndGameShip: return "EndGameShip";
                default: return "Kind" + k;
            }
        }

        // The kind a script or a log names, or 0.
        public static int Parse(string name)
        {
            if (name == null) return 0;
            for (int k = 1; k <= EndGameShip; k++)
                if (IsKnown(k) && Name(k) == name) return k;
            return 0;
        }
    }

    // What an ObjectRequest or ObjectData names: the things a day-boundary print covers.
    public static class ObjectKind
    {
        public const int Research = 1, SiteBuilds = 2, SiteFacilities = 3;
        // M6: one agency's missions (Body 0). Printed in a DayClosed, never fetched: a difference is out of step.
        public const int Missions = 4;

        // Kinds an ObjectRequest or ObjectData may name.
        public static bool Fetchable(int kind) { return kind >= Research && kind <= SiteFacilities; }
    }

    // ObjectData's answer: Ok, or why the host cannot give the object (each puts the asking client out of step).
    public static class ObjectCodes { public const int Ok = 0, TooBig = 1, TooMany = 2, NoSuchObject = 3; }

    // StreamReset: why the host starts a new stream (every client rejoins fully).
    public static class StreamCodes { public const int HostLoaded = 1, HostFault = 2; }
}
