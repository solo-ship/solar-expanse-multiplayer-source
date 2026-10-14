using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Company = Game.Company;
using BdBehavior = BehaviorDesigner.Runtime.Behavior;
using BdManager = BehaviorDesigner.Runtime.BehaviorManager;

namespace SolarExpanseMultiplayer
{
    // M3 Task 7: the world switches of a drift measurement, and the baseline numbers. Every switch is behind a flag and works only in
    // a test profile (docs/superpowers/plans/2026-10-10-m3-drift.md "Task 7"):
    //   -semp-ai-off        the rivals' AI off (the recipe of docs/research/state-accessors.md "AI"), checked at load and every 30 days
    //   -semp-no-autosave   LoadSaveManager.autoSaveEveryNYears = null
    //   -semp-random-seed   UnityEngine.Random.InitState
    //   -semp-drift-speed   a game without a session: the speed, 3 real s after load, put back when it differs
    //   -semp-drift-days    stop (pause) once the game reaches day N + 1, held
    // and, with ANY of them on (recording or not), the `drift: world day` line every 30 game days: rate, frames, collections, stalls.
    // It also watches for the end of the game (a run that reaches it is invalid), once per game process.
    // Main thread only. Update runs every frame and allocates nothing: plain fields, cached refs, no strings. The only strings are the
    // load-time lines, the 30-day lines, and the one-off event lines (docs/research/gc.md).
    public static class DriftWorld
    {
        const long TicksPerDay = TimeSpan.TicksPerDay;
        const int LogEveryDays = 30;
        const float SpeedDelaySeconds = 3f, StallSeconds = 2f, HoldEverySeconds = 1f, PutBackAfterSeconds = 1f, PutBackLogEverySeconds = 10f;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ what other code may ask

        // "ai-off,no-autosave,seed=12345,speed=25000" for the record header ("" = none). The parts DriftAnalysis.ParseFlags reads.
        public static string Flags { get { return flags; } }

        // Rival trees that are still alive at the last check (load or every 30 days): A = running, B = paused, C = loading threads.
        // The meaning the `ai` and `aicheck` record events carry.
        public static int AiRunning { get { return aiRunning; } }
        public static int AiPaused { get { return aiPaused; } }
        public static int AiLoading { get { return aiLoading; } }

        // Counts the rivals' trees NOW (running + paused + loading threads of the active agencies other than the player and the World
        // Government) and returns the sum; AiRunning/AiPaused/AiLoading hold the three parts. A rival is an agency this class found at
        // the last load. 0 when there are no rivals. -1 when the count could not be taken (the Behavior Designer fields are not usable, or
        // reading them threw): AiCheckFailed is then set, and a caller must treat -1 as a failed check, never as "no trees".
        public static int AiTreesRunning()
        {
            try
            {
                CountTrees(out aiRunning, out aiPaused, out aiLoading);
                return aiRunning + aiPaused + aiLoading;
            }
            catch (Exception e)
            {
                aiCheckFailed = true;
                Log.Error("ai: counting trees failed: " + e.Message);
                return -1;
            }
        }

        // True once any check (load or every 30 days) found a rival tree running, paused or loading.
        public static bool AiCheckFailed { get { return aiCheckFailed; } }

        // True once the game ended (GameManager.EndOfGame or the asteroid sequence): the run is invalid.
        public static bool EndOfGame { get { return endOfGame; } }

        // The paused-for-the-target state (-semp-drift-days): true once the game reached day N + 1.
        public static bool TargetReached { get { return targetReached; } }

        // ------------------------------------------------------------------ state

        static string flags = "";
        static bool active;                                  // a test profile and an M3 flag
        static int loads;
        static DevFlags dev;
        static Manager.TimeController tc;
        static Manager.GameManager gm;
        static Manager.GameManager subscribedTo;             // the instance EndOfGame is hooked on (a new instance, a new hook)
        static StateField<Manager.TimeController, DateTime> nextDayRef;
        static Action<Manager.GameManager.EGameEndCondition> endHandler;   // made in WatchEnd, not by a static initialiser
        static int faults;
        const int MaxFaults = 5;

        // the AI
        static Company[] rivals = new Company[0];
        static string[] rivalIds = new string[0];
        static string skippedInactive = "none";
        static int aiRunning, aiPaused, aiLoading, otherAgencies;
        static bool aiCheckFailed, aiOffOn, treeFieldsOk;
        static FieldInfo pausedField, threadsField;
        static PropertyInfo threadBehavior;

        // the end of game
        static bool endOfGame, asteroidLogged;

        // the baseline window (since the last 30-day line)
        static bool measuring, haveLast, inStall;
        static float lastNow, lastChange, prevScale, runSpeed;   // runSpeed: the last non-zero speed seen
        static float curSpeed, curSpeedSecs, bestSpeed, bestSpeedSecs;   // the longest unbroken run at one speed in the window (what the window ran at)
        static long lastTicks, winGameTicks;
        static double winReal, winStallSecs;
        static int winFrameMax, winOver100, winStalls, winGen0, nextLogDay;
        static bool daysOk;

        // the speed (no session) and the stop
        static float soloSpeed, loadedAt, nextSpeedCheck, diffSince = -1f, lastPutBack = -100f, nextHold;
        static bool speedApplied;
        static int targetDays;
        static bool targetReached, hostSession, stopLogged, wasZeroLastFrame;
        static int resumes, stoppedDay;
        static float lastResumeLog = -100f;

        static bool AnyFlag(DevFlags f)
        {
            return f.Drift || f.AiOff || f.NoAutosave || f.RandomSeed >= 0 || f.DriftSpeed > 0f || f.DriftDays > 0;
        }

        // ------------------------------------------------------------------ at save loaded (every load)

        public static void OnSaveLoaded()
        {
            try
            {
                DevFlags f = Boot.Dev;
                if (Boot.Profile == null || f == null || !AnyFlag(f)) { active = false; return; }   // a normal player: nothing at all
                active = true;
                dev = f;
                bool again = loads++ > 0;
                tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
                gm = MonoBehaviourSingleton<Manager.GameManager>.Instance;
                if (nextDayRef == null) nextDayRef = new StateField<Manager.TimeController, DateTime>("nextDay");
                flags = BuildFlags(f);
                daysOk = Drift.LoadTicks != 0L && nextDayRef.Ok;
                if (!daysOk) Log.Warn("drift: world: no load time or no nextDay field, so no 30-day lines and no stop at a day");

                // The window and the stop start again with each load (the load time moved).
                measuring = false; haveLast = false; inStall = false; nextLogDay = LogEveryDays;
                targetDays = f.DriftDays; targetReached = false; stopLogged = false; resumes = 0; nextHold = 0f; wasZeroLastFrame = false; runSpeed = 0f; curSpeed = 0f; curSpeedSecs = 0f; bestSpeed = 0f; bestSpeedSecs = 0f;
                hostSession = f.Host;
                soloSpeed = !f.SessionWanted ? f.DriftSpeed : 0f;
                speedApplied = false; diffSince = -1f; loadedAt = Time.realtimeSinceStartup;
                Log.Info("drift: world switches " + (flags.Length > 0 ? flags : "(none)") + (targetDays > 0 ? ", stop at day " + targetDays : "") +
                         (soloSpeed > 0f ? ", speed " + soloSpeed.ToString("F0", Inv) + " (no session)" : "") + (again ? " (another save loaded)" : ""));

                faults = 0;
                // Each switch has its own try: one failing must not cost the others (cloud review M-3), and each names itself.
                try { WatchEnd(); }
                catch (Exception e) { Log.Error("drift: could not watch for the end of the game: " + e.Message); }
                try
                {
                    CheckTreeFields();
                    if (f.AiOff) ApplyAiOff();
                    else LogAiLeftOn();
                }
                catch (Exception e)
                {
                    if (f.AiOff) aiCheckFailed = true;
                    Log.Error((f.AiOff ? "ai: could not switch off: " : "ai: could not look at the trees: ") + e);
                }
                try { if (f.NoAutosave) ApplyNoAutosave(again); }
                catch (Exception e) { Log.Error("autosave: could not switch off: " + e); }
                try { ApplySeed(f); }       // last: the Random state at load is then the seed's, whatever the steps above drew
                catch (Exception e) { Log.Error("random: could not seed: " + e); }
            }
            catch (Exception e)
            {
                Log.Error("drift: world switches failed: " + e);
            }
        }

        static string BuildFlags(DevFlags f)
        {
            var sb = new StringBuilder();
            if (f.AiOff) sb.Append("ai-off");
            if (f.NoAutosave) { if (sb.Length > 0) sb.Append(','); sb.Append("no-autosave"); }
            if (f.RandomSeed >= 0) { if (sb.Length > 0) sb.Append(','); sb.Append("seed=").Append(f.RandomSeed.ToString(Inv)); }
            if (f.DriftSpeed > 0f) { if (sb.Length > 0) sb.Append(','); sb.Append("speed=").Append(f.DriftSpeed.ToString("F0", Inv)); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the end of the game

        static void WatchEnd()
        {
            if (gm == null) { Log.Warn("drift: world: no GameManager, the end-of-game watch is off"); return; }
            // A load does not clear GameManager.EndOfGame (CleanStaticEvent only touches ObjectInfo), so one hook per GameManager
            // instance, never one per load. A different instance (the scene was rebuilt) needs its own.
            if (ReferenceEquals(subscribedTo, gm)) return;
            if (endHandler == null) endHandler = OnEnd;
            gm.EndOfGame += endHandler;
            subscribedTo = gm;
            asteroidLogged = false;
        }

        static void OnEnd(Manager.GameManager.EGameEndCondition c)
        {
            endOfGame = true;
            Log.Error("drift: end of game (" + c + ") - run invalid");
        }

        // ------------------------------------------------------------------ the AI

        static void FindRivals()
        {
            var found = new List<Company>(16);
            var sk = new StringBuilder();
            int others = 0;
            List<Company> all = MonoBehaviourSingleton<Manager.GameManager>.Instance.Companies;
            if (all == null) throw new InvalidOperationException("GameManager.Companies is null");
            for (int i = 0; i < all.Count; i++)
            {
                Company c = all[i];
                if (c == null || c.ID == null) continue;
                if (c.IsPlayer) continue;
                if (c.Definition != null && c.Definition.IsWorldGovernment) continue;
                others++;
                // Idle agencies are active GameObjects and get the recipe; only inactive ones (neither enabled nor idle) are skipped and named.
                if (!c.gameObject.activeSelf) { if (sk.Length > 0) sk.Append(", "); sk.Append(c.ID); continue; }
                found.Add(c);
            }
            found.Sort(CompareIds);
            rivals = found.ToArray();
            rivalIds = new string[rivals.Length];
            for (int i = 0; i < rivals.Length; i++) rivalIds[i] = rivals[i].ID;
            otherAgencies = others;
            skippedInactive = sk.Length > 0 ? sk.ToString() : "none";
        }

        static int CompareIds(Company a, Company b) { return string.CompareOrdinal(a.ID, b.ID); }

        static string IdList()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < rivalIds.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(rivalIds[i]); }
            return sb.ToString();
        }

        // At load: are Behavior Designer's private lists where the count looks for them, with the types it expects? The counts of paused
        // trees and loading threads read 0 when a field is missing, so a game update that renames one would otherwise read as "no trees"
        // (cloud review M-2). Not usable means the count throws (-1 for the record) and AiCheckFailed is set. Names checked against
        // BehaviorDesigner.Runtime.BehaviorManager: `private Dictionary<Behavior, BehaviorTree> pausedBehaviorTrees` and
        // `private List<BehaviorThreadLoader> activeThreads`, whose element has `public Behavior Behavior`.
        static bool CheckTreeFields()
        {
            var problems = new StringBuilder();
            pausedField = AccessTools.Field(typeof(BdManager), "pausedBehaviorTrees");
            threadsField = AccessTools.Field(typeof(BdManager), "activeThreads");
            threadBehavior = null;
            string pausedText, threadsText;
            if (pausedField == null) { pausedText = "NOT FOUND"; problems.Append("pausedBehaviorTrees is missing; "); }
            else
            {
                pausedText = "found as " + pausedField.FieldType.Name;
                if (pausedField.FieldType != typeof(Dictionary<BdBehavior, BdManager.BehaviorTree>)) problems.Append("pausedBehaviorTrees is a " + pausedField.FieldType + "; ");
            }
            if (threadsField == null) { threadsText = "NOT FOUND"; problems.Append("activeThreads is missing; "); }
            else
            {
                threadsText = "found as " + threadsField.FieldType.Name;
                Type element = threadsField.FieldType.IsGenericType ? threadsField.FieldType.GetGenericArguments()[0] : null;
                if (!typeof(IList).IsAssignableFrom(threadsField.FieldType) || element == null) problems.Append("activeThreads is a " + threadsField.FieldType + ", not a list; ");
                else
                {
                    threadBehavior = element.GetProperty("Behavior", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (threadBehavior == null || !typeof(BdBehavior).IsAssignableFrom(threadBehavior.PropertyType)) { threadBehavior = null; problems.Append("the activeThreads element " + element.Name + " has no readable Behavior property; "); }
                    else threadsText += " of " + element.Name + ", element Behavior property found";
                }
            }
            treeFieldsOk = problems.Length == 0;
            if (treeFieldsOk) Log.Info("ai: behaviour trees: pausedBehaviorTrees " + pausedText + ", activeThreads " + threadsText);
            else
            {
                aiCheckFailed = true;
                Log.Error("ai: behaviour trees: pausedBehaviorTrees " + pausedText + ", activeThreads " + threadsText + "; " + problems + "the paused and loading counts cannot be taken, so the AI check fails");
            }
            return treeFieldsOk;
        }

        // The recipe is WorldSwitches.AiOff (BlockEnableAI, then each rival's AI and trees off). A load runs it again:
        // each load restores the saved child behaviours, which start themselves.
        static void ApplyAiOff()
        {
            aiOffOn = true;
            FindRivals();
            if (rivals.Length == 0)
            {
                // Nothing to switch off. A save with no other agency at all says so; anything else (every other agency inactive) is a
                // failed check: the switch did nothing and the run must not pass as "AI off".
                if (otherAgencies == 0) Log.Info("ai: off requested; this save has no rivals (no agency besides the player and the World Government), nothing to switch off");
                else { aiCheckFailed = true; Log.Error("ai: off requested, but none of the " + otherAgencies + " other agencies is active (inactive: " + skippedInactive + "): nothing was switched off"); }
                return;
            }
            // The "before" numbers are for the log only: a count that cannot be taken must never cost the recipe itself (cloud review 4 M-1).
            int run0 = -1, pau0 = -1, loa0 = -1;
            bool beforeKnown = true;
            try { CountTrees(out run0, out pau0, out loa0); }
            catch (Exception e) { beforeKnown = false; Log.Warn("ai: the 'before' tree count could not be taken (" + e.Message + "); the recipe runs anyway"); }
            WorldSwitches.AiOff(rivals);   // BlockEnableAI, then the recipe for each rival
            CountTrees(out aiRunning, out aiPaused, out aiLoading);
            Log.Info(beforeKnown ? "ai: before the switch: trees running " + run0 + ", paused " + pau0 + ", loading " + loa0 : "ai: before the switch: unknown");
            string line = "ai: off for " + rivals.Length + " agencies (" + IdList() + "); skipped inactive " + skippedInactive +
                          "; trees running " + aiRunning + ", paused " + aiPaused + ", loading " + aiLoading;
            if (aiRunning + aiPaused + aiLoading > 0) { aiCheckFailed = true; Log.Error(line); }
            else Log.Info(line);
        }

        // The AI-on side of a measurement: nothing is switched, and the load line proves the rivals' trees are there.
        static void LogAiLeftOn()
        {
            aiOffOn = false;
            FindRivals();
            CountTrees(out aiRunning, out aiPaused, out aiLoading);
            Log.Info("ai: left on (no -semp-ai-off) for " + rivals.Length + " agencies (" + IdList() + "); skipped inactive " + skippedInactive +
                     "; trees running " + aiRunning + ", paused " + aiPaused + ", loading " + aiLoading + "; BlockEnableAI " + Company.BlockEnableAI);
        }

        // Every 30 days with -semp-ai-off.
        static void AiCheck(int day)
        {
            CountTrees(out aiRunning, out aiPaused, out aiLoading);
            string line = "ai: check day " + day + ": trees running " + aiRunning + ", paused " + aiPaused + ", loading " + aiLoading;
            if (aiRunning + aiPaused + aiLoading > 0) { aiCheckFailed = true; Log.Error(line); }
            else Log.Info(line);
        }

        // Running trees (BehaviorManager.BehaviorTrees), paused trees (pausedBehaviorTrees) and trees still loading on a thread
        // (activeThreads) that belong to a rival. Allocation-free except the loading list's reflection read (rare). Fails closed: it throws
        // when the fields are not usable, and a loading thread whose Behavior cannot be read counts as a rival's.
        static void CountTrees(out int running, out int paused, out int loading)
        {
            running = 0; paused = 0; loading = 0;
            if (!treeFieldsOk) throw new InvalidOperationException("the Behavior Designer fields were not found or have another type (see the ai: behaviour trees line)");
            if (rivals.Length == 0) return;
            BdManager bm = BdManager.instance;
            if (bm == null) return;
            List<BdManager.BehaviorTree> trees = bm.BehaviorTrees;
            if (trees != null)
                for (int i = 0; i < trees.Count; i++)
                    if (trees[i] != null && IsRival(trees[i].behavior)) running++;
            var pd = pausedField.GetValue(bm) as Dictionary<BdBehavior, BdManager.BehaviorTree>;
            if (pd != null)
                foreach (BdBehavior b in pd.Keys)
                    if (IsRival(b)) paused++;
            var th = threadsField.GetValue(bm) as IList;
            if (th != null)
                for (int i = 0; i < th.Count; i++)
                {
                    object t = th[i];
                    object owner = t != null ? threadBehavior.GetValue(t, null) : null;
                    bool unreadable = t == null || owner == null || !(owner is BdBehavior);
                    if (unreadable || IsRival((BdBehavior)owner)) loading++;
                }
        }

        static bool IsRival(BdBehavior b)
        {
            if (b == null) return false;           // the Unity null: a destroyed tree owner is not a rival's
            Company c = b.GetComponent<Company>();
            if (c == null) return false;
            for (int i = 0; i < rivals.Length; i++) if (ReferenceEquals(rivals[i], c)) return true;
            return false;
        }

        // ------------------------------------------------------------------ autosave and random

        // The switch is WorldSwitches.AutosaveOff (the field is checked at each year event, so it is set again after
        // every load).
        static void ApplyNoAutosave(bool again)
        {
            float? was;
            string problem;
            if (!WorldSwitches.AutosaveOff(out was, out problem))
            {
                Log.Error("autosave: could not switch off: " + problem);
                return;
            }
            Log.Info("autosave: autoSaveEveryNYears was " + (was.HasValue ? was.Value.ToString("R", Inv) : "null") + ", now null" + (again ? " (another save loaded)" : ""));
            Log.Info("autosave: off (test profile)");
        }

        static void ApplySeed(DevFlags f)
        {
            if (f.RandomSeed >= 0)
            {
                UnityEngine.Random.InitState(f.RandomSeed);
                Log.Info("random: UnityEngine.Random seeded with " + f.RandomSeed.ToString(Inv) + " (test profile)");
            }
            string hash;
            try { hash = Fnv64.Text(Fnv64.Offset, JsonUtility.ToJson(UnityEngine.Random.state)).ToString("x16", Inv); }
            catch (Exception e) { hash = "unreadable (" + e.Message + ")"; }
            Log.Info("random: state hash " + hash + " at load");
        }

        // ------------------------------------------------------------------ every frame

        public static void Update()
        {
            if (!active || faults >= MaxFaults) return;     // after MaxFaults failures IN A ROW the per-frame work stops for this load (one ERROR says so)
            try { Tick(); faults = 0; }                     // a Tick that completes ends the run of failures
            catch (Exception e)
            {
                faults++;
                Log.Error("drift: world update failed (" + faults + " of " + MaxFaults + " in a row): " + e);
                if (faults >= MaxFaults) Log.Error("drift: world update failed " + MaxFaults + " times in a row - DriftWorld's per-frame work (the 30-day lines, the speed and the stop) is stopped until the next load");
            }
        }

        static void Tick()
        {
            if (tc == null || GravityEngine.instance == null) return;      // between games, or the menu
            float now = Time.realtimeSinceStartup;
            if (!asteroidLogged && gm != null && gm.EndGameAsteroidImpact)
            {
                asteroidLogged = true; endOfGame = true;
                Log.Error("drift: end of game (asteroid impact) - run invalid");
            }
            float scale = tc.CurrentTimeScale;
            long ticks = tc.CurrentTime.Ticks;
            if (scale > 0f) runSpeed = scale;           // what the window ran at; a pause on the logging frame does not change it

            if (soloSpeed > 0f) SpeedControl(now);
            if (targetDays > 0 && daysOk) TargetControl(now, ticks, scale);

            if (!measuring)
            {
                if (scale <= 0f) return;                                   // paused: the baseline starts when the game first runs
                measuring = true; haveLast = true;
                lastNow = now; lastTicks = ticks; prevScale = scale; lastChange = now;
                winGameTicks = 0; winReal = 0.0; winStallSecs = 0.0; winFrameMax = 0; winOver100 = 0; winStalls = 0;
                winGen0 = GC.CollectionCount(0);
                return;
            }

            double dt = now - lastNow;
            int ms = (int)(dt * 1000.0);
            if (ms > winFrameMax) winFrameMax = ms;
            if (ms > 100) winOver100++;
            if (scale > 0f && prevScale > 0f)
            {
                winReal += dt; winGameTicks += ticks - lastTicks;
                if (scale != curSpeed) { curSpeed = scale; curSpeedSecs = 0f; }
                curSpeedSecs += (float)dt;
                if (curSpeedSecs > bestSpeedSecs) { bestSpeedSecs = curSpeedSecs; bestSpeed = curSpeed; }
            }
            // A stall: game time unchanged for over 2 real seconds while frames run and the speed is above 0 (stalls of about 5 s were seen in testing).
            if (scale <= 0f || ticks != lastTicks) { lastChange = now; inStall = false; }
            else if (inStall) winStallSecs += dt;
            else if (now - lastChange > StallSeconds) { inStall = true; winStalls++; winStallSecs += now - lastChange; }
            lastNow = now; lastTicks = ticks; prevScale = scale;

            if (daysOk)
            {
                int day = (int)((nextDayRef.Get(tc).Ticks - Drift.LoadTicks) / TicksPerDay) - 1;   // nextDay is the next day's key: the day that last ran is one before
                if (day >= nextLogDay) ThirtyDays(day / LogEveryDays * LogEveryDays);
            }
        }

        // The one line every 30 game days (an allowed allocation), recording or not. Each line covers the 30 days before it. The next
        // boundary and the window are advanced BEFORE anything is logged or checked, so a failure below can never repeat the line every
        // frame (cloud review M-1). The speed is the one the window ran at (a pause on this frame does not make the window "at speed 0").
        static void ThirtyDays(int label)
        {
            double gameSeconds = winGameTicks / 1e7;
            double rate = winReal > 0.0 ? gameSeconds / winReal : 0.0;
            double real2 = winReal - winStallSecs;
            double rate2 = real2 > 0.0 ? gameSeconds / real2 : rate;
            int gen0 = GC.CollectionCount(0) - winGen0;
            int frameMax = winFrameMax, over100 = winOver100, stalls = winStalls;
            double stallSecs = winStallSecs;
            float windowSpeed = bestSpeedSecs > 0f ? bestSpeed : runSpeed;     // the speed the window ran at, in solo too (not the configured one)
            nextLogDay = label + LogEveryDays;
            winGameTicks = 0; winReal = 0.0; winStallSecs = 0.0; winFrameMax = 0; winOver100 = 0; winStalls = 0;
            curSpeedSecs = 0f; bestSpeedSecs = 0f;
            Log.Info("drift: world day " + label + ": rate " + rate.ToString("F0", Inv) + " game s per real s at speed " + windowSpeed.ToString("F0", Inv) +
                     " (" + rate2.ToString("F0", Inv) + " without stalls); frame max " + frameMax + " ms; frames over 100 ms " + over100 +
                     "; gen0 " + gen0 + "; stalls " + stalls + " (" + stallSecs.ToString("F1", Inv) + " s)");
            if (aiOffOn)
            {
                try { AiCheck(label); }
                catch (Exception e)
                {
                    aiCheckFailed = true;
                    Log.Error("ai: check day " + label + " failed: " + e.Message);
                }
            }
            winGen0 = GC.CollectionCount(0);       // after the logging: the lines' own garbage is not the window's
        }

        // -semp-drift-speed (no session): 3 real s after load SetTimescale(v, force), then put it back when it has differed for over 1 s
        // (logged at most once per 10 s). After the stop day the stop holds the speed at 0 instead.
        static void SpeedControl(float now)
        {
            if (targetReached) return;
            if (!speedApplied)
            {
                if (now < loadedAt + SpeedDelaySeconds) return;
                tc.SetTimescale(soloSpeed, true);
                speedApplied = true;
                Log.Info("drift: solo speed " + soloSpeed.ToString("F0", Inv) + " (no session)");
                return;
            }
            if (now < nextSpeedCheck) return;
            nextSpeedCheck = now + 0.25f;
            if (Mathf.Approximately(tc.CurrentTimeScale, soloSpeed)) { diffSince = -1f; return; }
            if (diffSince < 0f) { diffSince = now; return; }
            if (now - diffSince > PutBackAfterSeconds)
            {
                float was = tc.CurrentTimeScale;
                tc.SetTimescale(soloSpeed, true);
                diffSince = -1f;
                if (now - lastPutBack > PutBackLogEverySeconds)
                {
                    lastPutBack = now;
                    Log.Info("drift: solo speed put back to " + soloSpeed.ToString("F0", Inv) + " (was " + was.ToString("F0", Inv) + ")");
                }
            }
        }

        // -semp-drift-days N: once the game time reaches LoadTicks + (N + 1) days, pause, and keep it paused. A solo game forces
        // SetTimescale(0) on EVERY frame the speed is not 0: a game-made resume (the first run showed one every second until the game's own
        // sequence ended) is undone before the next fixed step can run, so it adds no more than a frame's game time. A host asks through
        // ClockSync.PlayerSets(0), as a player's pause, AT ONCE on the first frame the speed is seen non-zero after the target, and then
        // at most once a second while the speed stays non-zero (the call was refused). A later game call cannot resume the session past
        // the target. The game's resumes are counted and logged at most once per 10 s.
        static void TargetControl(float now, long ticks, float scale)
        {
            if (ticks < Drift.LoadTicks + (targetDays + 1L) * TicksPerDay) return;
            if (scale != 0f)
            {
                if (hostSession)
                {
                    // At once on the first frame the speed is seen non-zero (a game-made resume would otherwise run the whole session for up
                    // to a second); the spacing is only for repeats while the call is refused (a planner or the countdown blocks it).
                    if (wasZeroLastFrame || now >= nextHold) { nextHold = now + HoldEverySeconds; ClockSync.PlayerSets(0f); if (targetReached) NoteResume(now, scale); }
                }
                else { tc.SetTimescale(0f, true); if (targetReached) NoteResume(now, scale); }
            }
            wasZeroLastFrame = tc.CurrentTimeScale == 0f;     // read AFTER our own call: a successful pause arms the immediate re-request for the next frame
            if (!targetReached)
            {
                targetReached = true;
                Log.Info("drift: target day " + targetDays + " reached - paused");
            }
            else if (!stopLogged && scale == 0f)
            {
                stopLogged = true; stoppedDay = DayNow();
                Log.Info("drift: stopped at day " + stoppedDay + " (game time " + tc.CurrentTime.ToString("yyyy-MM-dd HH:mm:ss", Inv) + ")");
            }
        }

        // At quit, after a stop: where the game ended up, so a run can show that the stop held (day, time and speed against the stop's).
        public static void OnQuit()
        {
            try
            {
                if (!active || !targetReached || tc == null || GravityEngine.instance == null) return;
                Log.Info("drift: at quit: day " + DayNow() + " (game time " + tc.CurrentTime.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "), speed " +
                         tc.CurrentTimeScale.ToString("F0", Inv) + "; the stop was at day " + stoppedDay + ", the game resumed the clock " + resumes + " time(s) that the hold saw");
            }
            catch (Exception) { }
        }

        // The day that last ran (nextDay is the next day's key).
        static int DayNow() { return (int)((nextDayRef.Get(tc).Ticks - Drift.LoadTicks) / TicksPerDay) - 1; }

        // The game resumed the clock after the stop and the hold paused it again.
        static void NoteResume(float now, float wasScale)
        {
            resumes++;
            if (now - lastResumeLog < PutBackLogEverySeconds) return;
            lastResumeLog = now;
            Log.Info("drift: stop held: the game resumed the clock (speed " + wasScale.ToString("F0", Inv) + "); paused again; " + resumes + " resume(s) so far, day " + DayNow());
        }
    }
}
