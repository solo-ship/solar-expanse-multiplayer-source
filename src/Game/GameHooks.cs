using System;
using System.Collections.Generic;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    public static class GameHooks
    {
        static Harmony harmony;
        public static int Installed, Failed;
        public static bool IsolationOk;   // fails closed: true only once every required patch is in
        public static bool Ready;         // Install ran to the end
        // I8: why a session may not start (a rule hook did not patch), or null. Set by EnsureRuleHooks.
        public static string SessionHookRefusal;
        static readonly List<string> ruleHooks = new List<string>();
        static string plantedRule;
        static SessionHookReport ruleReport;
        static readonly HashSet<string> done = new HashSet<string>();
        // the plan's Deferred names not patched yet; static like the rest here, as Harmony's patches are process-wide
        static readonly HashSet<string> deferred = new HashSet<string>();

        // HookPlan names -> patch classes. A name in the plan with no class here counts as a failed patch.
        static readonly Dictionary<string, Type> Classes = new Dictionary<string, Type>
        {
            { HookPlan.SaveDirectory, typeof(DataIsolation.SaveDirectory) },
            { HookPlan.EndGameDirectory, typeof(DataIsolation.EndGameDirectory) },
            { HookPlan.CustomMapsDirectory, typeof(DataIsolation.CustomMapsDirectory) },
            { HookPlan.CustomMapsTranslationDirectory, typeof(DataIsolation.CustomMapsTranslationDirectory) },
            { HookPlan.NoClearSaveData, typeof(DataIsolation.NoClearSaveData) },
            { HookPlan.NoClearData, typeof(DataIsolation.NoClearData) },
            { HookPlan.MenuMarker, typeof(MenuMarker) },
            { HookPlan.RoadmapSkip, typeof(RoadmapSkip) },
            { HookPlan.ClockSync, typeof(ClockSync) },
            { HookPlan.TutorialCalls, typeof(TutorialCalls) },
            { HookPlan.GamePauses, typeof(GamePauses) },
            { HookPlan.DriftProbe, typeof(DriftProbe) },
            { HookPlan.HabitabilityPrewarm, typeof(HabitabilityPrewarm) },
            { HookPlan.MenuEntry, typeof(MenuEntry) },
            { HookPlan.PauseScreenEarly, typeof(PauseScreenEarly) },
            { HookPlan.ModalPopups, typeof(ModalPopups) },
            { HookPlan.SpeedPanelHooks, typeof(SpeedPanelHooks) },
            { HookPlan.SetupPages, typeof(SetupPages) },
            { HookPlan.SetupEpochs, typeof(SetupEpochs) },
            { HookPlan.SetupHandOver, typeof(SetupHandOver) },
            { HookPlan.SetupLoad, typeof(SetupLoad) },
            { HookPlan.SetupDialogHide, typeof(SetupDialogHide) },
            { HookPlan.SaveNameRewrite, typeof(SaveNameRewrite) },
            { HookPlan.PauseExitHooks, typeof(PauseExitHooks) },
            { HookPlan.HumanMoney, typeof(MoneyRules) },
            { HookPlan.HumanLifeSupport, typeof(LifeSupportRule) },
            { HookPlan.HumanResearch, typeof(ResearchRules) },
            { HookPlan.HumanQueuePayment, typeof(QueuePaymentRule) },
            { HookPlan.CraftCompletion, typeof(CraftCompletionRule) },
            { HookPlan.HumanBuildTime, typeof(BuildTimeRule) },
            { HookPlan.HumanBankruptcy, typeof(BankruptcyRule) },
            { HookPlan.LinkClick, typeof(LinkClickRule) },
            { HookPlan.Observatory, typeof(ObservatoryRule) },
            { HookPlan.AsteroidSpawn, typeof(AsteroidSpawnRule) },
            { HookPlan.FlightLoad, typeof(FlightLoadRule) },
            { HookPlan.FlightRules, typeof(FlightRules) },
            { HookPlan.FlightSources, typeof(FlightSources) },
            { HookPlan.FlightLaunch, typeof(FlightLaunch) },
            { HookPlan.StationCap, typeof(StationCapRule) },
            { HookPlan.DayCloser, typeof(DayCloser) },
            { HookPlan.DayGateHook, typeof(DayGateHook) },
            { HookPlan.FlightHost, typeof(FlightHostHooks) },
            { HookPlan.FlightJournal, typeof(FlightJournalHooks) },
            { CaptureHooks.Research, typeof(CaptureResearch) },
            { CaptureHooks.Buildings, typeof(CaptureBuildings) },
            { CaptureHooks.Production, typeof(CaptureProduction) },
            { CaptureHooks.Drops, typeof(CaptureDrops) },
            { CaptureHooks.Observatory, typeof(CaptureObservatory) },
            { CaptureHooks.Greyed, typeof(GreyedButtons) },
            { CaptureHooks.Tripwire, typeof(Tripwire) },
            { CaptureHooks.PopupNoPause, typeof(PopupNoPause) },
            { CaptureHooks.Market, typeof(CaptureMarket) },
            { CaptureHooks.Contracts, typeof(CaptureContracts) },
            { CaptureHooks.World, typeof(CaptureWorld) },
            { WorldHooks.Market, typeof(MarketRule) },
            { WorldHooks.Growth, typeof(GrowthRule) },
            { WorldHooks.AiSeam, typeof(AiSeam) },
            { WorldHooks.AiSteps, typeof(AiSteps) },
            { WorldHooks.Contracts, typeof(ContractRule) },
            { CaptureHooks.Flights, typeof(CaptureFlights) },
            { CaptureHooks.Supply, typeof(CaptureSupply) },
            { HookPlan.FlightLaunchStep, typeof(FlightLaunchHook) },
        };

        public static bool IsInstalled(string name) { return done.Contains(name); }

        public static void Install()
        {
            harmony = new Harmony("com.solo-ship.solarexpansemultiplayer");
            HashSet<string> before = NoFileAssemblies();
            // Which Harmony runs the patches: our own 2.4 (Doorstop) or BepInEx's HarmonyX, which differ in places.
            System.Reflection.AssemblyName h = typeof(Harmony).Assembly.GetName();
            string hFile = typeof(Harmony).Assembly.Location;
            Log.Info("hooks: Harmony " + h.Version + " from "
                + (string.IsNullOrEmpty(hFile) ? "(no file)" : LogPrivacy.Path(hFile, Boot.GameRootDir)));
            HookPlan plan = HookPlan.For(Boot.ProfileFlagGiven, Boot.Profile, Boot.GameRootDir != null, Boot.Dev != null && Boot.Dev.SessionWanted,
                                      Boot.Dev != null && Boot.Dev.DriftWanted,
                                      Boot.Dev != null && Boot.Dev.SessionWanted && !Boot.Dev.Host,
                                      MenuSessions.PlayerPath);
            if (Boot.Dev != null && Boot.Dev.NoHumanRules) plan.DropRuleHooks();
            bool ok = !plan.ProfileBroken;   // asked for a profile but it can't be set up
            if (plan.Required.Contains(HookPlan.SaveDirectory)) DataIsolation.Init();
            foreach (string name in plan.Required) ok &= Patch(name);
            foreach (string name in plan.Optional) Patch(name);   // a failure here must not quit the game
            // I8: the rule hooks are not patched here: under BepInEx some load a MonoMod helper assembly the menu's
            // content scan would count as a difference, so they wait for EnsureRuleHooks (a host's session start, a
            // joiner's snapshot load), both after the scan.
            ruleHooks.Clear();
            ruleHooks.AddRange(plan.SessionRequired);
            plantedRule = Boot.Dev != null && Boot.Dev.CommandFault == "hook" && ruleHooks.Count > 0
                ? ruleHooks[0] : null;
            ruleReport = null;
            SessionHookRefusal = null;
            foreach (string name in plan.Deferred) deferred.Add(name);
            LogMemoryAssemblies("start-up", before);
            IsolationOk = ok;
            Ready = true;
            string later = deferred.Count > 0
                ? "; deferred to the first load: " + string.Join(", ", new List<string>(deferred).ToArray()) : "";
            Log.Info("hooks: " + Installed + " installed, " + Failed + " failed" + later);
        }

        // A deferred hook (HookPlan.Deferred), patched on first use. True once it is in; false when the plan has no
        // such hook or the patch failed (the caller then stops what needed it). plantFailure: a test profile's planted
        // fault takes the failure path instead of patching (the caller asks JoinTestFaults; this class never does).
        public static bool EnsureDeferred(string name, bool plantFailure)
        {
            if (done.Contains(name)) return true;
            if (harmony == null || !deferred.Contains(name)) return false;
            deferred.Remove(name);   // tried once: a failed patch is not retried on every load
            bool ok;
            if (plantFailure)
            {
                Failed++;
                Log.Error("patch " + name + " failed: planted test fault");
                ok = false;
            }
            else ok = Patch(name);
            Log.Info("hooks: deferred " + name + (ok ? " installed" : " FAILED") + "; " + Installed + " installed, " +
                     Failed + " failed");
            return ok;
        }

        // The rule hooks (HookPlan.SessionRequired), patched once, after the menu's content scan: a host calls it in
        // Session.Start before it builds its host, a joiner in SnapshotLoad before the extract (so the S7 prefix is in
        // place). False = a hook did not patch (SessionHookRefusal says which): no session, no load (I8). True with
        // nothing to patch (no session profile, or -semp-no-human-rules). The test profile's planted fault fails the
        // first hook without patching it, to prove the refusal in the real game.
        public static bool EnsureRuleHooks()
        {
            if (ruleReport != null) return ruleReport.Ok;
            if (harmony == null || ruleHooks.Count == 0) return true;
            HashSet<string> before = NoFileAssemblies();
            if (plantedRule != null)
            {
                Failed++;
                Log.Error("patch " + plantedRule + " failed: planted test fault");
            }
            var report = new SessionHookReport();
            report.PatchAll(ruleHooks, Patch, plantedRule);
            ruleReport = report;
            SessionHookRefusal = report.Refusal;
            Log.Info("hooks: rule hooks " + (report.Ok ? "installed" : "FAILED") + "; " + Installed + " installed, "
                     + Failed + " failed");
            // the game's consoles are off in a session, so the speed cheat is the dev flag's alone
            if (report.Ok && Boot.Dev != null && Boot.Dev.ObservatorySpeed && done.Contains(HookPlan.Observatory))
                ObservatoryRule.SetSpeedCheat();
            LogMemoryAssemblies("rule hooks", before);
            return report.Ok;
        }

        // Assemblies with no file (made in memory, such as MonoMod's helper under HarmonyX): the content scan counts
        // them, and each launch makes a new one, so one that appears while patching at start-up is a join refusal
        // waiting to happen. Named by identity and module id.
        static HashSet<string> NoFileAssemblies()
        {
            var set = new HashSet<string>();
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string file = null;
                try { if (!a.IsDynamic) file = a.Location; } catch (Exception) { }
                if (!string.IsNullOrEmpty(file)) continue;
                // Harmony 2.4.2 makes its shared state at its first patch (the content scan knows it by name)
                if (a.GetName().Name == SolarExpanseMultiplayer.MemoryAssemblies.HarmonyName) continue;
                string id;
                try { id = a.GetName().Name + " " + a.ManifestModule.ModuleVersionId; }
                catch (Exception) { id = a.FullName; }
                set.Add(id);
            }
            return set;
        }

        static void LogMemoryAssemblies(string phase, HashSet<string> before)
        {
            foreach (string id in NoFileAssemblies())
                if (!before.Contains(id))
                    Log.Warn("hooks: a memory assembly appeared while patching the " + phase + " hooks: " + id);
        }

        // One class at a time, so one broken patch doesn't take the others down.
        static bool Patch(string name)
        {
            Type t;
            if (!Classes.TryGetValue(name, out t))
            {
                Failed++;
                Log.Error("patch " + name + " failed: no patch class for it");
                return false;
            }
            try
            {
                // HarmonyX (BepInEx) silently patches nothing for a class without a [HarmonyPatch] of its own, where our
                // Harmony patches it anyway: such a class fails here on both loaders, and so does one that patched a
                // different number of methods than it declares (HookPlan.ExpectedMethods: ClockSync 5, GamePauses 8,
                // SetupPages 3, TutorialCalls, HabitabilityPrewarm, ModalPopups and PauseExitHooks 2, the rule hooks
                // 1 to 13, the capture and world hooks their own, the others 1), so a half-patched class (a renamed
                // game method) cannot look healthy.
                if (!t.IsDefined(typeof(HarmonyPatch), false))
                {
                    Failed++;
                    Log.Error("patch " + t.Name + " failed: the class has no [HarmonyPatch] of its own (HarmonyX would skip it)");
                    return false;
                }
                List<System.Reflection.MethodInfo> patched = harmony.CreateClassProcessor(t).Patch();
                int expected = HookPlan.ExpectedMethods(name);
                if (patched == null || patched.Count != expected)
                {
                    Failed++;
                    Log.Error("patch " + t.Name + " failed: Harmony patched " + (patched == null ? 0 : patched.Count) + " method(s), expected " + expected);
                    return false;
                }
                Installed++;
                done.Add(name);
                Log.Info("patched " + t.Name + " (" + patched.Count + (patched.Count == 1 ? " method)" : " methods)"));
                return true;
            }
            catch (Exception e)
            {
                Failed++;
                Log.Error("patch " + t.Name + " failed: " + e.Message);
                return false;
            }
        }
    }
}
