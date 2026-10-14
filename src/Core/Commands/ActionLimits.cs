namespace SolarExpanseMultiplayer
{
    // Every cap, rate, budget and deadline of M5's commands, in one place so the host, the client, the tests and the
    // fake client all build to the same numbers (plan "Limits"). Caps on the messages are derived from the field caps
    // and proved by CommandCapsTests; the rates are proved by HostLinkBudgetTests.
    public static class ActionLimits
    {
        // ---- field caps ----
        // A content id: valid UTF-8, at most 96 bytes, no control, format or line-separator character, no '"' or '\'.
        // Newtonsoft writes such text as raw UTF-8, so a message's JSON size is its bytes plus the two quotes.
        public const int MaxIdBytes = 96;
        // units in one build, queue, cancel or scrap (the host checks the game's own cap)
        public const int MaxCount = 999;
        // units on one market offer the host's game made: the World Government's maker quotes tens of thousands, and
        // vanilla's own count is an int (OfferData). A player's own Add stays inside MaxCount.
        public const int MaxOfferCount = int.MaxValue;
        public const int MaxQueue = 128;              // research topics in one set or expanded add
        // ids one apply may create; a larger craft click is split by the capture
        public const int MaxNewIds = 8;
        public const int MaxMoveNewIds = 64;          // B11 only: non-stacking modules make one id each
        public const int MaxTargets = 32;             // rows in one EnergyTargets or MirrorTargets list
        public const int MaxCounters = 6;             // counter rows in one entry
        // counter rows in AgencyState: the game-wide two, each human agency's item and name counters, and the item
        // counters of at most MaxRivalCounters other agencies (a rival's next item id must be the host's too)
        public const int MaxStateCounters = 144;
        public const int MaxRivalCounters = 16;
        // crew-death timers in AgencyState (an unsaved per-site field a fresh copy lacks): sites whose crew has run
        // out of supplies, every human agency together
        public const int MaxLifeSupportRows = 128;
        // prints in one DayClosed; more objects touched in a day are printed the next
        public const int MaxPrintsPerDay = 128;
        public const int MaxPaused = 128, MaxMiningKeys = 64;
        public const int MaxBuildRows = 1024, MaxQueueRows = 1024, MaxBuiltRows = 1024;   // per site
        public const int MaxRowsPerPiece = 160;       // rows of one ObjectData piece (builds and queue together)
        public const int MaxPieces = 16;              // pieces of one object
        // observatory draws in one entry (the dice of a step, a found asteroid, a new system)
        public const int MaxDraws = 256;
        // the re-plans (57) that follow an impact's ObjectRemoved in the stream, which the impact's Count announces
        // M6 (V1, V6): the observatories that target the asteroid (M5's 256), plus the reason-5 mission cancels (71)
        // that follow its 57s (MaxFlightsPerTarget)
        public const int MaxObservatoryGroup = 256;
        public const int MaxImpactGroup = MaxObservatoryGroup + MaxFlightsPerTarget;
        // asteroids one removal may spawn: SpawnAsteroidIfNeed runs twice (the removed object and its lowOrbitCustom
        // object) for each sub-type of asteroidSubTypeNeedIfNotSpawn, which a session start refuses above the cap
        public const int MaxSpawnSubTypes = 16;
        public const int MaxSpawns = 2 * MaxSpawnSubTypes;
        public const int MaxSystemNameBytes = 64;     // a generated star system's name (a place name, not a ship's)
        public const int MaxDrawSite = 4;             // the five call sites of an observatory draw (0-4)
        public const int MaxSeed = 999999999;         // a new star system's seed, inside ActionRequest.Value's range
        // The orbit numbers of a spawned asteroid: the game's own ranges are the host's; this only keeps them a sane
        // size (a message's width is known) and finite.
        public const double MaxOrbitValue = 1e12;

        // Ranges of the numbers a request carries. The game's own limits (HowManyCanBuild and the rest) are the host's
        // check; these only keep every number a sane size, so the worst case of a message is known.
        public const int MaxBodyId = 1000000;         // ObjectInfo.id
        public const int MaxPosition = 2048;          // a row in a list the player saw (builds and queue together)
        public const int MaxUnits = 1000000000;       // units on, units in a stack
        public const double MaxMoney = 1e15;          // an amount of stock or money
        public const double MaxProgress = 1e12;       // research or build progress
        // an energy-transfer share (a fraction or a percentage: the game's range is the host's check)
        public const double MaxRatio = 100;
        // the stored priority (the window's minMaxPriority 0..10, Q2)
        public const int MinPriority = -5, MaxPriority = 5;
        public const long MaxDay = 1000000;           // DayKey of a game that lasts 2,700 years

        // ---- flights (M6, plan "Limits") ----
        public const int MaxCargoRows = 32;           // rows of one mission's cargo
        public const int MaxMissionCrafts = 32;       // ships, and launch vehicles, of one mission
        public const int MaxRemovals = 64;            // transactions one schedule records
        public const int MaxMissionNameChars = 64;    // a mission name: the player-name rule (Hello.Name)
        public const int MaxDeferred = 8;             // accepted player flights whose host copy is still being created
        public const int MaxDeferredPerPlayer = 1;    // per player: over it, Busy
        public const int PlanHoldMaxMs = 15000;       // the host's plan hold ends here
        public const int PlanPadSeconds = 8;          // a code plan's pad covers this many real seconds at the speed
        public const int FlightApplyMs = 15000;       // a client's apply, from leaving the queue: out of step after it
        public const int FlightsPerMinute = 12, FlightBurst = 4;   // kinds 60, 62, 65, 67, the same on both sides
        public const int MaxPresetsPerAgency = 64;    // a save beyond it is PresetsFull
        // flights to or from one asteroid that a pending impact of it may have to cancel (SCOPED TO IMPACTS, W1)
        public const int MaxFlightsPerTarget = 64;
        public const int MaxCyclicalFlags = 255;      // CyclicalData.Flags: the route record's flag bits (S1)

        // ---- the shared world (M7, plan "Limits"; WorldCapsTests proves the caps) ----
        public const int MaxMarketOps = 128;          // ops in one entry (the maker: 20 resources x (2 cancels + 2 adds) + 20 PriceDiff = 100; room for mods)
        public const int MaxAiSteps = 32;             // steps in one AiAction (one task call)
        public const int MaxGrowthRows = 480;         // rows in one PopulationGrowth part (a year may have several parts)
        public const int MaxChainRows = 64;           // objectives and contracts in one 92's chain
        public const int MaxBuyFills = 96;            // fills in a 15/34 entry (the widest fill is ~460 bytes: 96 fit the 56 KiB entry)
        public const int MaxOffersPerAgency = 32;     // a human agency's live offers (OffersFull past it)
        public const int MaxAreaPrints = 32;          // DayClosed.World: one per area (14 today; DriftAreas is append-only)
        public const int MaxAreaValues = 300;         // DayClosed.Values on a value day (block field sums)
        // HealData's group prints for one area and day, over all its pieces: as many as the pieces carry. An area's
        // prints cover every agency (asteroidsinorbit's stock went past 512 on its first value day).
        public const int MaxGroupPrints = MaxGroupsPerPiece * MaxHealPieces;
        public const int MaxGroupsPerPiece = 80;      // group prints in one HealData piece (WorldCapsTests: a value day's widest)
        public const int MaxGroupSums = 16;           // value sums in one group print
        public const int MaxHealRows = 96;            // rows in one HealData piece (WorldCapsTests proves the 56 KiB piece)
        public const int MaxHealValues = 16;          // values in one heal row
        public const int MaxHealKeyBytes = 128;       // an ItemCapture key
        public const int MaxGroupBytes = 64;          // an item key's first segment
        public const int MaxHealPieces = 16;          // a group over 16 x MaxHealRows rows: TooBig (out of step)
        public const int KeptPrintDays = 32;          // = ReplayDays = KeptCopyDays
        public const int MaxKeptPrintBytes = 4 * 1024 * 1024;
        public const int MaxHealsPerClose = 8;        // groups read for one client at one close; the rest wait
        public const int HealRepeatDays = 30;         // game days: a Discrete heal of one group twice within it: out of step
        public const int ValueHealsPerAgency = 4;     // value heals per agency per 30 game days; past it: out of step
        public const int ValueDayEvery = 30;          // value days; also the day after a year event or a heal
        public const int ReplayDays = 32, ReplayBytes = 8 * 1024 * 1024;   // DD-W12's buffer
        public const int ResumeMaxDays = 30;
        public const int ResumeDeadlineMs = 120000;   // real time from Resume to release; past it: a full rejoin
        public const int CatchUpSpeed = 200000;       // the client's own speed while it catches up
        public const int CatchUpDaysPerSecond = 10;
        public const int ResumeAheadEntries = 1024;   // replay entries sent beyond the client's LastApplied
        public const int MaxFlushRounds = 64;         // save flush rounds (DD-W7); past it: host fault
        public const int MaxObjective = 1023;         // ContractRef.Objective: an objective's index in its contract
        public const double MaxGrowth = 1e6;          // GrowthRow.Delta

        // ---- rates (D7), the same on both sides ----
        public const int SetPerSecond = 4;            // set actions: latest wins, per client in all
        public const int DoPerSecond = 5, DoBurst = 20;
        public const int ClientBytesPerSecond = 16 * 1024, ClientBytesBurst = 64 * 1024;    // a client's command bytes
        public const int ObjectRequestsPerMinute = 6;     // per client; over it the host answers TooMany (out of step)
        public const int ObjectBytesPerMinute = 1024 * 1024;   // pieces to one client a minute, paced by OutputBudget
        public const int RepeatClickMs = 300;         // an identical do request while the first is pending: dropped

        // ---- queues ----
        // requests sent, not yet answered: over it, refused locally (TooMany)
        public const int MaxPendingPerClient = 32;
        // coalesced set keys waiting on a client: over it, refused locally
        public const int MaxSetKeysPerClient = 16;
        // requests the host keeps for one player in a batch: over it, Busy
        public const int MaxHeldPerPlayer = 64;
        public const int MaxHeldSetKeys = 16;         // distinct set keys held for one player: over it, Busy
        public const int MaxQueuedEntries = 4096;     // entries a client keeps for a later day: over it, out of step
        // accepted requests the host has not applied yet (all players): over it, Busy
        public const int MaxWaiting = 256;
        public const int KeptCopyDays = 32, MaxKeptCopyBytes = 8 * 1024 * 1024;   // the kept copies of printed objects

        // ---- the host's output to ONE client (every kind counted), and the client's inbound limiter on its host link
        // ----
        // sized above it; HostLinkBudgetTests holds the arithmetic.
        public const int HostOutMsgsPerSecond = 100, HostOutMsgBurst = 200;
        public const int HostOutBytesPerSecond = 256 * 1024, HostOutByteBurst = 512 * 1024;
        public const int HostLinkMsgBurst = 300, HostLinkMsgsPerSecond = 150;
        public const int HostLinkByteBurst = 1024 * 1024, HostLinkBytesPerSecond = 512 * 1024;

        // ---- times ----
        // an ObjectRequest unanswered this long: out of step (provisional)
        public const int FetchDeadlineMs = 10000;
        public const int PieceDeadlineMs = 2000;      // plus this per piece after the first
        public const int PendingNoticeMs = 1000, NoticeShowMs = 6000;

        // ---- the -semp-actions script (a test tool's input) ----
        public const int MaxScriptBytes = 16 * 1024, MaxScriptLines = 256, MaxScriptLineChars = 512;
    }
}
