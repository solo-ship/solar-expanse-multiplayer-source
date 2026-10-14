using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Which Harmony patch classes GameHooks applies. Pure, so the normal-player path (no dev flags) is pinned by tests:
    // a normal player gets the menu marker and the early track below (the player build), no dev seam. The clock
    // hooks (ClockSync, TutorialCalls, GamePauses) come with a session or the early track; the drift probe (M3) is for a test profile with -semp-drift, session or
    // not. The habitability prewarm skip is for a joiner only (a session that is not the host), and deferred: patched
    // at the joiner's first snapshot load, after the content scan at the menu. Under BepInEx, HarmonyX patching a
    // method that returns a value type loads an in-memory MonoMod helper assembly, which the scan would count as a
    // content difference from the host; patched later it is never scanned. A deferred patch that fails stops the load.
    //
    // M8's early track (Track U): a session may also start from the menus, so a valid test profile (the player-path
    // profile included) gets the menu entry (the Multiplayer button), the pause screen's PLAYERS button, every hook
    // a dev-flag session gets (the clock hooks, the rules, the command, flight and world hooks) and (deferred, and not
    // on a dev host) the prewarm skip. Each is inert outside a session. A normal install gets them too since the real
    // path opened (RealPath.Open, the beta's player build), without a test profile's isolation; the player-path
    // profile is its stand-in in the test copies. No dev seam reaches either (TestSeams).
    public sealed class HookPlan
    {
        public const string SaveDirectory = "SaveDirectory", EndGameDirectory = "EndGameDirectory",
            CustomMapsDirectory = "CustomMapsDirectory", CustomMapsTranslationDirectory = "CustomMapsTranslationDirectory",
            NoClearSaveData = "NoClearSaveData", NoClearData = "NoClearData", MenuMarker = "MenuMarker",
            RoadmapSkip = "RoadmapSkip", ClockSync = "ClockSync", TutorialCalls = "TutorialCalls",
            GamePauses = "GamePauses", DriftProbe = "DriftProbe", HabitabilityPrewarm = "HabitabilityPrewarm",
            MenuEntry = "MenuEntry", PauseScreenEarly = "PauseScreenEarly", ModalPopups = "ModalPopups",
            SpeedPanelHooks = "SpeedPanelHooks", SetupPages = SetupHookNames.Pages,
            SetupEpochs = SetupHookNames.Epochs, SetupHandOver = SetupHookNames.HandOver, SetupLoad = SetupHookNames.Load,
            SetupDialogHide = SetupHookNames.DialogHide, SaveNameRewrite = "SaveNameRewrite",
            PauseExitHooks = "PauseExitHooks";

        // M5 Task 8's rule hooks (docs/superpowers/plans/2026-10-12-m5-commands.md, "Game seams"): each makes the game
        // treat every human agency as it treats the local player, and is a no-op outside a session. They are listed in
        // SessionRequired, never in Required or Optional: one that fails stops the session, not the game.
        public const string HumanMoney = "HumanMoney", HumanLifeSupport = "HumanLifeSupport",
            HumanResearch = "HumanResearch", HumanQueuePayment = "HumanQueuePayment",
            CraftCompletion = "CraftCompletion", HumanBuildTime = "HumanBuildTime",
            HumanBankruptcy = "HumanBankruptcy", LinkClick = "LinkClick";

        // M5 Task 8b's hooks (same rules): the observatory dice, holds and wrappers (DD14), the asteroid spawn and the
        // impact guard (DD15), and the saved flights' leftover fuel (FS2).
        public const string Observatory = "Observatory", AsteroidSpawn = "AsteroidSpawn", FlightLoad = "FlightLoad";

        // M5 Task 10a: a station's facility cap is its owner's on every game (StationCapRule).
        public const string StationCap = "StationCap";

        // M6 Tasks 7 and 8: the flight rules (every human agency as the local player, the acting agency, the departure
        // pad) and the flight sources (who plans, launches, cancels and lands a flight).
        // FlightLaunch is the launch-moment verdict, a class of its own because its target is a private nested type.
        public const string FlightRules = "FlightRules", FlightSources = "FlightSources",
            FlightLaunch = "FlightLaunch";

        // M5 Task 10a: the host's day close (DD4), in the fixed step before the day event. Required for a session like
        // the rules, but not a rule: -semp-no-human-rules keeps it, as the stream needs its days whatever the rules.
        public const string DayCloser = "DayCloser";

        // M5 Task 10b: the day gate and the impact holds in the fixed step, the client's look before its day event and
        // its impact landing (DD4, DD15). Required for a session like the day close, rules or not.
        public const string DayGateHook = "DayGateHook";

        // M6 Task 9: the host's flight hooks (the acceptance's stop at CreateFly, the fake facility ships, the cancel
        // postfix) and the removal journal's (two overloaded game methods). Required for a session like the day gate.
        public const string FlightHost = "FlightHost", FlightJournal = "FlightJournal";

        // M6 Task 10: the client's launch step, a prefix on the game's private OnLaunch (RocketLauncher's nested
        // LaunchSpaceCraft). Required for a session like the day gate; a no-op on the host. FlightLaunch (a rule hook)
        // patches the same method for the verdict itself.
        public const string FlightLaunchStep = "FlightLaunchStep";

        // Every hook name above (a test checks the two agree).
        public static readonly string[] All =
        {
            SaveDirectory, EndGameDirectory, CustomMapsDirectory, CustomMapsTranslationDirectory, NoClearSaveData, NoClearData,
            MenuMarker, RoadmapSkip, ClockSync, TutorialCalls, GamePauses, DriftProbe, HabitabilityPrewarm, MenuEntry,
            PauseScreenEarly, ModalPopups, SpeedPanelHooks, SetupPages, SetupEpochs, SetupHandOver, SetupLoad,
            SetupDialogHide, SaveNameRewrite, PauseExitHooks,
            HumanMoney, HumanLifeSupport, HumanResearch, HumanQueuePayment, CraftCompletion, HumanBuildTime,
            StationCap,
            HumanBankruptcy, LinkClick, Observatory, AsteroidSpawn, FlightLoad, FlightRules, FlightSources,
            FlightLaunch, DayCloser, DayGateHook, FlightHost, FlightJournal,
            CaptureHooks.Research, CaptureHooks.Buildings, CaptureHooks.Production, CaptureHooks.Drops,
            CaptureHooks.Observatory, CaptureHooks.Greyed, CaptureHooks.Tripwire, CaptureHooks.PopupNoPause,
            CaptureHooks.Market, CaptureHooks.Contracts, CaptureHooks.World, WorldHooks.Market, WorldHooks.Growth,
            WorldHooks.AiSeam, WorldHooks.AiSteps, WorldHooks.Contracts, CaptureHooks.Flights, CaptureHooks.Supply,
            FlightLaunchStep
        };

        // The rule hooks in the order M4 Task 14's ruling fixes (earliest and largest divergence first): money (S7, S8,
        // EnableAI), life support (S1), research (S3, S4), the queue payment (S2), craft completion (DD11), then the
        // build time (S5), bankruptcy (S6) and the link click (S18); then the observatory, the asteroid spawn and the
        // saved flights.
        static readonly string[] RuleHooks =
        {
            HumanMoney, HumanLifeSupport, HumanResearch, HumanQueuePayment, CraftCompletion, HumanBuildTime,
            StationCap,
            HumanBankruptcy, LinkClick, Observatory, AsteroidSpawn, FlightLoad, FlightRules, FlightSources,
            FlightLaunch
        };

        // How many game methods a hook's patch class must patch (the count Harmony reports). GameHooks fails a class that
        // patches any other number, so a game update that renames one method can't leave a half-patched class looking
        // healthy. ClockSync: SetTimescale, the two mission-planner methods and the space bar's PauseGame2 /
        // UnPauseGame2; TutorialCalls: the two tutorial camera centrings; GamePauses: the eight methods that pause
        // for a menu, tutorial step or warning;
        // DriftProbe: DoThePeriodicEvents only (a prefix, a postfix and a finalizer on the one method);
        // HabitabilityPrewarm: HabitabilityParametersNew.Update and UpdateDepositStates; MenuEntry: MenuSceneUI.Start;
        // PauseScreenEarly: BaseScreen.Visible's setter; ModalPopups: UIManager's two pop-up getters; SpeedPanelHooks:
        // the speed panel's refresh; SetupPages: the customisation panel's Start, SetUserCompany and
        // OnPrevButtonClick (SetupHookNames.Methods; the other setup hooks one each).
        // The rule hooks: HumanMoney: CompanyBehaviour.AfterLoadState and EnableAI, Company.OnPlayerChange;
        // HumanResearch: AutoPickResearch and CalculateMaintenanceCostsAndIncomePerDay; CraftCompletion:
        // ProductionItem.UpdateBuilding (the client's hold) and Build (the host's entry); HumanBuildTime:
        // ProductionItem and SpacecraftConstructData TimeToBuildInDays and four LaunchVehicle readiness methods;
        // Observatory: the five draw sites (RandomResourceToExplore, ScheduleNextExplorationStep, FindNextObject-
        // ForExplorationFromObjectInfoGroups, RandomizeNextObjectToDiscover, FindNextObjectForDiscovery), the four
        // held calls (OnScheduledTimeOfResourceExploration, OnObjectDiscovered, AnyResourceExploredByOtherProbeOr-
        // Observatory, ObjectInfoOnvirtualDestroy), MyUpdate, Company.DecreaseExoplanetSurveyCost and
        // ObjectInfoManager.GetSystemToDiscover and NewSystemDiscover (13); AsteroidSpawn: CreateDynamicAsteroid, the
        // landing handler and the pulled asteroid's display draws (3); FlightLoad: PlanFlyCode's lambda (1);
        // FlightRules: the landing, the load's mission, the container pool, the container swap, the schedule step, the
        // buy-fuel popup, the fake facility vehicle, the departure pad and the two price-change methods (10);
        // FlightSources: PlanFlyCode, the route controller, the end-game flight, the stalled build, the edit window
        // (opening and closing), the planner queue, the schedule step, CreateFly, MissionInfo.Cancel, the landing and
        // the ship it builds (12); FlightLaunch: OnLaunch (1); the other four one method each. DayCloser:
        // DoThePeriodicEvents (a prefix and a finalizer, beside DriftProbe's). DayGateHook: TimeController.FixedUpdate,
        // DoThePeriodicEvents, the 3D view's private landing handler, the ship's landing step and the facilities its
        // landing builds (5). FlightHost:
        // PMTabSchedule.CreateFly, ObjectInfoData.GetFakeSCFromFacility, MissionInfo.Cancel and
        // PMTabCargo.SetCargoFromPreset (4). FlightJournal: ObjectInfoData.RemoveResource (ResourcePriceOne) and
        // MoneyController.RemoveMoney (2).
        // 0 for a name that is not a hook.
        public static int ExpectedMethods(string name)
        {
            if (name == SetupPages) return SetupHookNames.Methods(name);
            if (name == HumanMoney) return 3;
            if (name == HumanBuildTime) return 6;
            if (name == Observatory) return 13;
            if (name == AsteroidSpawn) return 3;
            if (name == FlightRules) return 10;
            if (name == FlightSources) return 12;
            if (name == DayGateHook) return 5;
            if (name == FlightHost) return 4;
            if (name == FlightJournal) return 2;
            if (name == ClockSync) return 5;
            if (name == GamePauses) return 8;
            if (name == PauseExitHooks) return 2;   // PauseScreen.OnExitToWindowsClick and OnExitToMenuClick
            if (name == TutorialCalls || name == HabitabilityPrewarm || name == ModalPopups) return 2;
            if (name == HumanResearch || name == CraftCompletion) return 2;
            // the capture hooks' counts are CaptureHooks' own, pinned by a source test against their target lists
            int captured = CaptureHooks.Expected(name);
            if (captured > 0) return captured;
            int world = WorldHooks.Expected(name);   // M7 Tasks 8-10, pinned the same way
            if (world > 0) return world;
            return System.Array.IndexOf(All, name) >= 0 ? 1 : 0;
        }

        public readonly List<string> Required = new List<string>();   // every one must apply, or a test profile quits
        public readonly List<string> Optional = new List<string>();   // a failure is logged, nothing more
        public readonly List<string> Deferred = new List<string>();   // patched on first use (GameHooks.EnsureDeferred)
        // I8: every one must apply, or the session does not start (the game stays single player); the test profile and
        // the game itself carry on. Empty without a session.
        public readonly List<string> SessionRequired = new List<string>();
        public bool ProfileBroken;                                     // -semp-profile given but unusable: quit

        // Track U's gate for its own hooks: a game root, and the real path open (a normal install, the player build)
        // or a valid profile. A rejected profile name (the launch arguments give null for it) gets nothing in For:
        // the profile is broken and the game quits.
        public static bool EarlyTrack(string profile, bool gameRootFound)
        {
            return gameRootFound && (RealPath.Open || TestSeams.Valid(profile));
        }

        // The command hooks every session needs, rules or not.
        static readonly string[] CommandHooks =
        {
            DayCloser, DayGateHook, FlightHost, FlightJournal, CaptureHooks.Research, CaptureHooks.Buildings,
            CaptureHooks.Production, CaptureHooks.Drops, CaptureHooks.Observatory, CaptureHooks.Greyed, CaptureHooks.Tripwire,
            CaptureHooks.PopupNoPause, CaptureHooks.Market, CaptureHooks.Contracts, CaptureHooks.World,
            CaptureHooks.Flights, CaptureHooks.Supply, FlightLaunchStep
        };

        // -semp-no-human-rules (the negative control): no rule hook is patched or required, the rest is unchanged.
        public void DropRuleHooks()
        {
            foreach (string name in RuleHooks) SessionRequired.Remove(name);
            foreach (string name in WorldHooks.All) SessionRequired.Remove(name);   // the world rules are human rules too
        }

        // joiner: a session flag that is not -semp-host (the snapshot is loaded here, not saved). playerPath: the
        // player-path profile, where the dev flags never act (TestSeams), so they add no hook either.
        public static HookPlan For(bool profileFlagGiven, string profile, bool gameRootFound, bool sessionWanted,
                                   bool driftWanted, bool joiner = false, bool playerPath = false)
        {
            var p = new HookPlan();
            bool seams = TestSeams.Open(profile, playerPath);
            sessionWanted &= seams;
            driftWanted &= seams;
            bool early = EarlyTrack(profile, gameRootFound) && !(profileFlagGiven && profile == null);
            if (profile != null && gameRootFound)
                p.Required.AddRange(new[] { SaveDirectory, EndGameDirectory, CustomMapsDirectory, CustomMapsTranslationDirectory,
                                            NoClearSaveData, NoClearData });
            else if (profileFlagGiven) p.ProfileBroken = true;
            // Without it a joiner would load a world that parts from the host's for good, so its load needs it. A
            // session from the menus may join, so every early-track profile but a dev host gets it (patched only by
            // a snapshot load).
            bool devHost = sessionWanted && !joiner;
            if ((profile != null && gameRootFound && sessionWanted && joiner) || (early && !devHost))
                p.Deferred.Add(HabitabilityPrewarm);
            p.Required.Add(MenuMarker);
            if (seams) p.Optional.Add(RoadmapSkip);
            // A session from the menus needs every hook a dev-flag session gets, or its games would run out of step
            // with each other: the clock hooks, the rules, the command, flight and world hooks (the session lists are
            // patched only when a session starts, after the menu's content scan; GameHooks.EnsureRuleHooks).
            bool sessionHooks = (profile != null && gameRootFound && sessionWanted) || early;
            if (sessionHooks)
            {
                p.Optional.Add(ClockSync);
                p.Optional.Add(TutorialCalls);
                p.Optional.Add(GamePauses);
                p.SessionRequired.AddRange(RuleHooks);
                p.SessionRequired.AddRange(CommandHooks);
                p.SessionRequired.AddRange(WorldHooks.All);
            }
            if (profile != null && gameRootFound && driftWanted) p.Optional.Add(DriftProbe);   // M3: the day probe, for -semp-drift in a test profile
            // A failed entry means no Multiplayer button (and a log line), never a quit. ModalPopups goes with them:
            // the mod's dialogs live where the Multiplayer button does, and without it Esc behind one opens the
            // pause menu (a nuisance, not a quit). It patches two bool getters, for which HarmonyX makes a helper
            // assembly the content scan would count, so it waits for the first menu's scan (Session.OnMenuReady).
            if (early) { p.Optional.Add(MenuEntry); p.Optional.Add(PauseScreenEarly); p.Deferred.Add(ModalPopups); }
            // The speed panel's real state (DD10): inert outside a session; a failure leaves the game's own panel,
            // never a quit. No automatic pauses in a session (DD28) is GamePauses' (with the clock hooks above) and,
            // for the click pop-ups, PopupNoPause's (a command hook).
            if (early) p.Optional.Add(SpeedPanelHooks);
            // The host setup's hooks (DD6, DD7): inert until the setup arms them. Load's is patched only when the
            // setup opens the load dialog, after the menu's content scan (HarmonyX's helper assembly, as above).
            if (early)
            {
                p.Optional.AddRange(SetupHookNames.AtStartUp);
                p.Deferred.AddRange(SetupHookNames.Deferred);
            }
            // DD8 and DD21: the pause screen's exits ask in a session; the save name rewrite goes in at the session's first
            // world (SaveToFile returns a bool, so HarmonyX must not patch it before the menu's content scan).
            if (early) { p.Optional.Add(PauseExitHooks); p.Deferred.Add(SaveNameRewrite); }
            return p;
        }
    }

    // The result of patching a plan's SessionRequired list (I8). Pure, so the refusal is a test: GameHooks feeds it
    // the real patch function. Every hook is tried even after one failed, so the log shows all the failures.
    public sealed class SessionHookReport
    {
        public readonly List<string> Failed = new List<string>();
        public int Patched;

        public bool Ok { get { return Failed.Count == 0; } }

        // The log line and the status text of a refused session; null when every hook patched.
        public string Refusal
        {
            get { return Failed.Count == 0 ? null : "session: refused, required hook " + Failed[0] + " failed"; }
        }

        // `planted`: a test profile's planted fault (-semp-command-fault hook): that hook counts as failed and is not
        // patched. A throw out of `patch` is a failure, not an exception.
        public void PatchAll(IList<string> names, System.Func<string, bool> patch, string planted)
        {
            for (int i = 0; i < names.Count; i++)
            {
                bool ok;
                if (planted != null && names[i] == planted) ok = false;
                else
                {
                    try { ok = patch(names[i]); }
                    catch (System.Exception) { ok = false; }
                }
                if (ok) Patched++;
                else Failed.Add(names[i]);
            }
        }
    }
}
