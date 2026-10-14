using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Sirenix.Serialization;
using UnityEngine;
using UnityEngine.SceneManagement;
using Data.ScriptableObject;
using ScriptableObjectScripts;
using ScriptableObjectScripts.StartGameConfiguration;
using Company = Game.Company;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using AiChildBehavior = AI.CompanyChildBehavior;
using BdBehavior = BehaviorDesigner.Runtime.Behavior;
using BdManager = BehaviorDesigner.Runtime.BehaviorManager;
using SaveGameData = Manager.SaveGameData;
using LoadSaveManager = Manager.LoadSaveManager;
using GameManager = Manager.GameManager;
using ObjectInfoManager = Manager.ObjectInfoManager;
using TimeController = Manager.TimeController;
using LoadingScreen = Game.UI.Screens.LoadingScreen;
using SearchObjectWindow = Game.UI.Windows.Windows.SearchObjectWindow;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // The joiner's game side of the snapshot: the strict deserialise at the menu, the facts for the sanity checks, the
    // load with the claimed agency as the player (route A: the game's own LoadingScreen and ExtractAllFromSaveData with
    // an object we checked, never Load() and its file), the four load-failure signals, the apply step and the way back
    // to the menu. The rules are Core's (JoinerLoad.cs); this class gathers the game's facts and acts. Owned by one
    // JoinClient; one load at a time, held in an Attempt object that StartLoad makes fresh and every ending drops, so
    // nothing outlives its load. Never on this path: LoadSaveManager.Load, BaseSaveGameData<>.Deserialize (the default
    // binder), the game's own unbounded Decompress, ChangePlayer and EnableAI. Main thread only, except the Unity log
    // handler (its counter has its own lock).
    public sealed class SnapshotLoad
    {
        readonly bool devFeed;
        Prepared prepared;
        Attempt attempt;
        int attempts;
        // A load abandoned before its extract leaves the game's "a save will be loaded" flag set on purpose: the game
        // scene can still finish loading after the abort, and with the flag cleared GameManager.Start would start a NEW
        // game from the joiner's configuration. The flag is cleared at the next "main menu ready" instead.
        bool clearScheduleAtMenu;

        public SnapshotLoad(bool devFeed) { this.devFeed = devFeed; }

        // wired by JoinClient's owner (ContentScan.AfterLoad); null = not scanned
        public Func<CategoryHash[]> PostLoadScan;

        // wired by Session: reads the game's speed list once the game is loaded; null = fine, else why not
        public Func<string> SpeedListCheck;

        // What Sanity checked: exactly this object and header may be loaded, with this configuration.
        sealed class Prepared
        {
            public SaveGameData Obj;
            public JoinHeader Header;
            public GameManager.StartGameConfiguration Config;
        }

        // One load, from StartLoad to Loaded, a failure or a cancel.
        sealed class Attempt
        {
            public int Id;
            public SaveGameData Pending;               // dropped at the extract
            public JoinHeader Header;
            public Action<long> Loaded;
            public Action<int, string> Failed;
            public bool Cancelled, Extracting, ExtractCalled, SuccessSeen, Success, Settling;
            public int AiOff = -1, RivalTreesLeft = -1, OwnTreesEmptied;
            public TreeTally RivalTrees;
            public bool AutosaveOff, BoundaryCopied;
            public bool KeepPrewarm;                    // -semp-keep-prewarm, the negative control
            public readonly PrewarmSkip Prewarm = new PrewarmSkip();
            public readonly LoadErrorCounter Errors = new LoadErrorCounter();
            public Application.LogCallback LogHandler;
            public UnityEngine.Events.UnityAction<Scene, LoadSceneMode> SceneHandler;
        }

        public bool Loading { get { return attempt != null; } }

        // The current load's habitability prewarm gate (HabitabilityPrewarm's prefixes read it); null with no load.
        public PrewarmSkip PrewarmSkip
        {
            get
            {
                Attempt a = attempt;
                return a != null ? a.Prewarm : null;
            }
        }

        // ------------------------------------------------------------------ the menu

        // At the main menu with nothing of ours loading (MenuRules decides).
        public bool AtMenu
        {
            get
            {
                try
                {
                    bool gameManager = !MonoBehaviourSingleton<GameManager>.InstanceIsNull;
                    bool loadSaveManager = !SerializedMonoBehaviourSingleton<LoadSaveManager>.InstanceIsNull;
                    string scene = SceneManager.GetActiveScene().name;
                    return MenuRules.AtMenu(scene, gameManager, loadSaveManager, attempt != null);
                }
                catch (Exception) { return false; }
            }
        }

        // The premise the cast audit rests on (docs/research/save-allowlist.md 10.1; MenuRules.Premise decides): looked
        // for anywhere, active or not, right before the deserialise, and always logged. Null = it holds.
        public static string MenuPremise()
        {
            bool gm, oim, lsm;
            try
            {
                gm = UnityEngine.Object.FindObjectOfType<GameManager>(true) != null;
                oim = UnityEngine.Object.FindObjectOfType<ObjectInfoManager>(true) != null;
                lsm = UnityEngine.Object.FindObjectOfType<LoadSaveManager>(true) != null;
            }
            catch (Exception e) { return "the menu check could not run (" + e.GetType().Name + ")"; }
            Log.Info(MenuRules.PremiseLine(gm, oim, lsm));
            return MenuRules.Premise(gm, oim);
        }

        // Every "main menu ready": a cancelled load's schedule flag is cleared now that no game scene is loading.
        public void AtMenuNow()
        {
            if (!clearScheduleAtMenu) return;
            clearScheduleAtMenu = false;
            try
            {
                var lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
                if (lsm == null) { Log.Warn("join: no LoadSaveManager at the menu: the schedule flag stays"); return; }
                lsm.IsScheduledToLoadAfterMainSceneReload = false;
                // Read back, so the log shows what the game now holds, not what was meant.
                bool still = lsm.IsScheduledToLoadAfterMainSceneReload;
                if (still) Log.Warn("join: the cancelled load's schedule flag is STILL set at the menu");
                else Log.Info("join: the cancelled load's schedule flag cleared at the menu (now " + still + ")");
            }
            catch (Exception e) { Log.Warn("join: the schedule flag could not be cleared: " + e.Message); }
        }

        // ------------------------------------------------------------------ wall 6: the strict deserialise

        // Odin's warnings and errors all throw under ThrowOnWarningsAndErrors; anything logged anyway is counted here.
        sealed class CountingLogger : Sirenix.Serialization.ILogger
        {
            public int Count;
            public string First;
            public void LogWarning(string message) { Note("warning: " + message); }
            public void LogError(string message) { Note("error: " + message); }
            public void LogException(Exception e) { Note("exception: " + (e != null ? e.GetType().Name : "null")); }
            void Note(string s) { Count++; if (First == null) First = TextSanitiser.Clean(s, 200, ""); }
        }

        // 0, TypeRefused (the binder refused a name: that alone decides, whatever Odin did with the exception) or
        // StrictDeserialise (not at the menu, the premise failed, an exception, anything logged, or no object).
        public int Deserialise(byte[] json, out object saveData, out string error)
        {
            saveData = null;
            error = null;
            if (json == null) { error = "no bytes"; return JoinCodes.StrictDeserialise; }
            string why = MenuPremise();   // looked at and logged whatever the scene, so a refusal says what was alive
            if (!AtMenu)
            {
                error = "not at the main menu: the snapshot is deserialised only there";
                return JoinCodes.StrictDeserialise;
            }
            if (why != null) { error = why; return JoinCodes.StrictDeserialise; }
            LoadSaveManager lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            if (lsm == null) { error = "no LoadSaveManager"; return JoinCodes.StrictDeserialise; }

            var binder = new RefusingBinder();
            var logger = new CountingLogger();
            // The getter builds a NEW context each time (with the game's string resolver), so nothing shared is
            // changed; it is reset afterwards all the same, so no later holder of this object could inherit the binder
            // or the policy.
            DeserializationContext ctx = LoadSaveManager.DeserializationContext;
            ctx.Binder = binder;
            ctx.Config.DebugContext.ErrorHandlingPolicy = ErrorHandlingPolicy.ThrowOnWarningsAndErrors;
            ctx.Config.DebugContext.LoggingPolicy = LoggingPolicy.LogWarningsAndErrors;
            ctx.Config.DebugContext.Logger = logger;
            SaveGameData obj = null;
            string failure = null;
            // Raised as the game's own deserialise does, so IDForObject's id lookups stay off while Odin builds
            // objects; always lowered again, or every later save of the game would think it is still deserialising.
            lsm.InSerializationContext++;
            try { obj = SerializationUtility.DeserializeValue<SaveGameData>(json, DataFormat.JSON, ctx); }
            catch (Exception e) { failure = e.GetType().Name + ": " + TextSanitiser.Clean(e.Message, 200, ""); }
            finally
            {
                lsm.InSerializationContext--;
                ctx.Reset();
            }
            if (binder.Refused)
            {
                error = "a type was refused: " + binder.FirstRefused + " (" + binder.Why + ")";
                return JoinCodes.TypeRefused;
            }
            if (failure != null)
            {
                error = "the strict deserialise stopped: " + failure;
                return JoinCodes.StrictDeserialise;
            }
            if (logger.Count > 0)
            {
                error = "Odin logged " + logger.Count + " message(s): " + logger.First;
                return JoinCodes.StrictDeserialise;
            }
            if (obj == null) { error = "the deserialise gave nothing"; return JoinCodes.StrictDeserialise; }
            Log.Info("join: strict deserialise ok (" + binder.Calls + " type names through the refusing binder)");
            saveData = obj;
            return 0;
        }

        // ------------------------------------------------------------------ wall 7: sanity, and the configuration

        // 0 or a JoinCodes value with the reason. On 0 the start configuration (load mode A: Company = the claimed
        // agency) is built from the header by id against this game's content and kept for StartLoad, for exactly this
        // object and header.
        public int Sanity(object saveData, JoinHeader h, string ownAgency, out string error)
        {
            prepared = null;
            var obj = saveData as SaveGameData;
            if (obj == null) { error = "not a save"; return JoinCodes.SanityFailed; }
            int code = SnapshotSanity.Check(Facts(obj), h, ownAgency, Application.version, out error);
            if (code != 0) return code;
            GameManager.StartGameConfiguration cfg = StartConfigMap.ToGame(h.Config, out error);
            if (cfg == null) return JoinCodes.SanityFailed;
            prepared = new Prepared { Obj = obj, Header = h, Config = cfg };
            return 0;
        }

        static SnapshotFacts Facts(SaveGameData obj)
        {
            var f = new SnapshotFacts
            {
                Scale = obj.currentTimeScale, StartGameSave = obj.startGameSave, SaveVersion = obj.saveVersion,
            };
            try { f.DateTicks = ((DateTime)obj.currentTime).Ticks; }
            catch (Exception) { f.DateReadable = false; }
            if (obj.ObjectInfoSaves != null)
                foreach (SaveGameData.ObjectInfoSave o in obj.ObjectInfoSaves)
                    if (o != null && o.IDObjectInfo != null && o.IDObjectInfo.ID == 533 && o.isInGameDestroy &&
                        o.idImpactFrom == 66)
                        f.EndGame = true;
            var agencies = new HashSet<string>(StringComparer.Ordinal);
            if (obj.companyDataSave != null)
                foreach (var cd in obj.companyDataSave)
                    if (cd != null && cd.companyID != null && cd.companyID.id != null)
                        agencies.Add(AgencyWire.From(cd.companyID.id));
            f.AgenciesInSave = agencies;
            f.InContent = id => StartConfigMap.Company(id) != null;
            return f;
        }

        // ------------------------------------------------------------------ route A

        // From the menu only, with the object Sanity checked. Every load runs the same way, in this order: the claimed
        // agency's saved behaviour trees emptied (the host's snapshot still holds the trees its AI ran for that agency,
        // and the load would start them whatever the AI flag says), Company.BlockEnableAI set (so nothing in the load
        // can switch any rival's AI on; a client never runs AI), then the game's start configuration and its
        // LoadingScreen. Returns at once; the result comes through loaded or failed. Throws when it cannot start (the
        // flow then cancels and goes back with LoadThrew).
        public void StartLoad(object saveData, JoinHeader h, Action<long> loaded, Action<int, string> failed)
        {
            Prepared p = prepared;
            prepared = null;
            if (p == null || !ReferenceEquals(p.Obj, saveData) || !ReferenceEquals(p.Header, h))
                throw new InvalidOperationException("the snapshot did not pass the sanity checks");
            if (!AtMenu) throw new InvalidOperationException("not at the main menu");
            LoadSaveManager lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            if (lsm == null) throw new InvalidOperationException("no LoadSaveManager");
            // Patched only now, after the content scan at the menu (HookPlan says why); never loaded without it.
            if (!GameHooks.EnsureDeferred(HookPlan.HabitabilityPrewarm, JoinTestFaults.FailDeferredPatch()))
                throw new LoadRefused(JoinCodes.LoadsDifferently,
                                      "the habitability prewarm patch could not be installed");
            // The rule hooks too, before the extract: its AfterLoadState must not give a human agency +Infinity.
            if (!GameHooks.EnsureRuleHooks())
                throw new LoadRefused(JoinCodes.LoadsDifferently, GameHooks.SessionHookRefusal);

            int emptied = EmptyOwnTrees(p.Obj, h.Config.Company);
            Company.BlockEnableAI = true;
            Log.Info("join: route A as " + h.Config.Company + ": its " + emptied +
                     " saved behaviour trees emptied, BlockEnableAI set");

            var a = new Attempt
            {
                Id = ++attempts, Pending = p.Obj, Header = h, Loaded = loaded, Failed = failed,
                OwnTreesEmptied = emptied, KeepPrewarm = Boot.Dev != null && Boot.Dev.KeepPrewarm,
            };
            a.LogHandler = (condition, stack, type) =>
                a.Errors.Note(LogKinds.FromUnity((int)type), condition, Thread.CurrentThread.ManagedThreadId);
            a.SceneHandler = (scene, mode) => OnSceneLoaded(a, scene, mode);
            attempt = a;
            clearScheduleAtMenu = false;
            JoinTestFaults.Arm();
            a.Errors.Begin(Thread.CurrentThread.ManagedThreadId);
            // The threaded event carries every thread's messages, the main thread's included: one subscription, no
            // double count.
            Application.logMessageReceivedThreaded += a.LogHandler;
            SceneManager.sceneLoaded += a.SceneHandler;
            GameManager.StaticStartGameConfig = p.Config;
            lsm.IsScheduledToLoadAfterMainSceneReload = true;
            SceneManager.LoadScene("LoadingScreen");
        }

        // Only the claimed agency's behaviorSaves are emptied (OwnTrees picks them); its aiStateData and every other
        // agency's entry are kept.
        static int EmptyOwnTrees(SaveGameData obj, string claimed)
        {
            int was = 0;
            List<SaveGameData.CompanyAISave> saves = obj.companyAISave;
            if (saves == null) return 0;
            var ids = new List<string>(saves.Count);
            foreach (SaveGameData.CompanyAISave s in saves)
                ids.Add(s != null && s.IDCompany != null ? s.IDCompany.ID : null);
            foreach (int i in OwnTrees.ToEmpty(ids, claimed))
            {
                was += saves[i].behaviorSaves != null ? saves[i].behaviorSaves.Count : 0;
                saves[i].behaviorSaves = new List<AiCompanyBehaviour.AIBehaviorSave>();
            }
            return was;
        }

        // MySceneGame loaded additively: our coroutine repeats the waits of the game's own LoadInternal and extracts.
        // Not for an abandoned attempt: the scene still finishes loading after an abort.
        void OnSceneLoaded(Attempt a, Scene s, LoadSceneMode m)
        {
            if (m != LoadSceneMode.Additive || s.name != LoadingScreen.MySceneGame) return;
            if (a != attempt || a.Pending == null) return;
            if (a.Cancelled)
            {
                Log.Info("join: the game scene finished loading after the abort: not extracting");
                return;
            }
            SempRoot root = SempRoot.Instance;
            if (root == null) { Fail(a, JoinCodes.LoadThrew, "no mod object to run the load on"); return; }
            // A step that throws fails the load at once instead of leaving it "loading" until the flow's deadline.
            root.StartCoroutine(GuardedSteps.Run(RouteA(a), e => Fail(a, JoinCodes.LoadThrew, Threw(e))));
        }

        static string Threw(Exception e) { return "the load threw " + e.GetType().Name; }

        // Every rival's saved behaviour trees go through CompanyBehaviour.ExtractBehaviorSave, which reads
        // BehaviorManager.instance unguarded. The game makes that manager when a tree is enabled; on a joiner rival AI
        // is blocked, so only a saved child tree (enabled by its own Setup) ever made it. A fresh game's rivals have only
        // their root trees, so without this the extract threw and returned false. The manager runs nothing: no tree is
        // enabled on a joiner. Null when it was already there.
        static string EnsureBehaviorManager()
        {
            if (BdManager.instance != null) return null;
            BdBehavior.CreateBehaviorManager();
            if (BdManager.instance == null)
                return "join: BehaviorDesigner's manager could not be made before the extract";
            return "join: BehaviorDesigner's manager made before the extract (no saved tree had made it)";
        }

        IEnumerator RouteA(Attempt a)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            while (MonoBehaviourSingleton<GameManager>.InstanceIsNull)
            {
                if (a.Cancelled || a != attempt) yield break;
                yield return new WaitForEndOfFrame();
            }
            if (!SerializedMonoBehaviourSingleton<UIManager>.InstanceIsNull)
            {
                var sw = SerializedMonoBehaviourSingleton<UIManager>.Instance.GetWindow<SearchObjectWindow>();
                if (sw != null)
                    for (int i = 0; i < 20; i++)
                        if (!sw.SpawnRowsCorutineFinish) yield return new WaitForEndOfFrame();
            }
            if (a.Cancelled || a != attempt || a.Pending == null)
            {
                Log.Info("join: route A cancelled before ExtractAllFromSaveData: not extracting");
                yield break;
            }
            LoadSaveManager lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            lsm.IsScheduledToLoadAfterMainSceneReload = false;
            GameManager.SetInitFromStartGameConfigurationOrFromSaveFile(false);
            SaveGameData obj = a.Pending;
            a.Pending = null;
            a.ExtractCalled = true;
            a.Extracting = true;
            bool extract = false;
            string threw = null;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // The game's habitability warm-up runs inside the extract (ObjectInfo.AfterLoadState), so the skip gate is
            // open for exactly this call and shut in its finally: no ending of the load, and no later monthly step,
            // can find it open.
            if (!a.KeepPrewarm && !JoinTestFaults.HoldPrewarmGate()) a.Prewarm.Open();
            string prewarm = null;
            string manager = EnsureBehaviorManager();
            if (manager != null) Log.Info(manager);
            try { extract = lsm.ExtractAllFromSaveData(obj); }
            catch (Exception e) { threw = e.GetType().Name; }
            finally { prewarm = a.Prewarm.Close(); }
            a.Extracting = false;
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 /
                      System.Diagnostics.Stopwatch.Frequency;
            obj = null;
            LoadErrors errors = a.Errors.End();
            Application.logMessageReceivedThreaded -= a.LogHandler;
            Log.Info(a.KeepPrewarm ? "join: habitability prewarm KEPT (-semp-keep-prewarm, the negative control)"
                                   : prewarm ?? "join: habitability prewarm gate was never opened");
            if (a != attempt) yield break;

            string detail;
            string signal = LoadSignals.Decide(a.Cancelled, extract, threw, a.SuccessSeen, a.Success, errors,
                                               out detail);
            if (signal != null)
            {
                Log.Info("join: load FAILED (" + signal + "): " + detail);
                if (signal != LoadSignals.Cancelled) Fail(a, JoinCodes.LoadThrew, signal + ": " + detail);
                yield break;
            }
            Log.Info("join: load ok (extract true, success true, unity errors 0 beyond the benign list; benign " +
                     errors.Benign + "; extract " + ms + " ms)");

            // The skip must have seen the whole warm-up, or this game build loads differently and the joiner would
            // part from the host for good (PrewarmSkip.Verdict). The negative control keeps the warm-up on purpose.
            string moved = a.KeepPrewarm ? null : a.Prewarm.Verdict(PrewarmRounds());
            if (moved != null)
            {
                Log.Info("join: load FAILED (prewarm): " + moved);
                Fail(a, JoinCodes.LoadsDifferently, "prewarm: " + moved);
                yield break;
            }

            // The apply step (boundaries, AI off, autosave off) must have held, or the joiner would part from the host.
            bool noCopy = Boot.Dev != null && Boot.Dev.NoBoundaryCopy;
            string applied = ApplyVerdict.Decide(a.AiOff, a.RivalTreesLeft, a.AutosaveOff, a.BoundaryCopied, noCopy);
            if (applied != null)
            {
                Log.Info("join: load FAILED (apply): " + applied + "; rival trees: " +
                         (a.RivalTrees != null ? a.RivalTrees.Line : "not counted"));
                Fail(a, JoinCodes.LoadThrew, "apply: " + applied);
                yield break;
            }

            // Loaded only once the load has settled, so the joiner's state is what the game's own reload of the
            // snapshot gives.
            a.Settling = true;
            var watch = new SettleWatch(Time.realtimeSinceStartup);
            int queuedAtStart = PlanQueue(), startFrame = Time.frameCount;
            float start = Time.realtimeSinceStartup;
            while (true)
            {
                if (a.Cancelled || a != attempt) yield break;
                yield return null;
                if (a.Cancelled || a != attempt) yield break;
                if (watch.Step(PlanQueue(), PorkchopsDone(), Time.realtimeSinceStartup)) break;
            }
            Log.Info("join: settled after " + (Time.realtimeSinceStartup - start).ToString("F2", Inv) + " s and " +
                     (Time.frameCount - startFrame) + " frames (plan queue " + queuedAtStart + " at the extract, now " +
                     PlanQueue() +
                     ", porkchops " + (PorkchopsDone() ? "done" : "NOT done") + ")" +
                     (watch.CeilingReached ? "; the " + SettleWatch.CeilingSeconds + " s ceiling was reached" : ""));

            string content = ContentAfterLoad(a.Header);
            if (content == "differs")
            {
                Fail(a, JoinCodes.PostLoadContent, "the content after the load differs from the host's");
                yield break;
            }
            if (content == "unavailable")
            {
                Fail(a, JoinCodes.PostLoadContent, "the content after the load could not be checked");
                yield break;
            }

            // the speed list is read from the loaded game, after the content matched and before any clock runs
            string speeds = SpeedListCheck != null ? SpeedListCheck() : null;
            if (speeds != null)
            {
                Fail(a, JoinCodes.PostLoadContent, "the speed list after the load: " + speeds);
                yield break;
            }

            string player = "none";
            int run = -1, pau = -1, loa = -1;
            try
            {
                Company me = MonoBehaviourSingleton<GameManager>.Instance.Player;
                if (me != null) { player = me.ID; Trees.Count(me, out run, out pau, out loa); }
            }
            catch (Exception e) { Log.Warn("join: the own agency's trees could not be counted: " + e.Message); }
            Log.Info("join: loaded as " + a.Header.Config.Company + "; AI off for " +
                     (a.AiOff >= 0 ? a.AiOff.ToString() : "?") +
                     " agencies; autosave " + (a.AutosaveOff ? "off" : "NOT off") + "; post-load content " + content);
            Log.Info("join: player " + player + "; own trees running " + run + ", paused " + pau + ", loading " + loa +
                     "; boundaries " + (a.BoundaryCopied ? "copied" : "NOT copied"));
            Log.Info("join: market " + MarketText());
            long now = MonoBehaviourSingleton<TimeController>.Instance.CurrentTime.Ticks;
            Action<long> cb = a.Loaded;
            End(a);
            if (cb != null) cb(now);
        }

        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // The game's warm-up rounds per body (ObjectInfo.AfterLoadState's loop count); -1 when it cannot be read.
        static int PrewarmRounds()
        {
            try
            {
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                return all.TerraformationConfig.Habitability.preWarmInitCalculationsPerObject;
            }
            catch (Exception) { return -1; }
        }

        // The save-loaded event (from Session, BEFORE Drift). True when it was this attempt's extract. Steps 2 and 3 of
        // the apply run here, inside ExtractAllFromSaveData and so before any fixed step: the boundary copy, then the
        // AI and the yearly autosave off. A failed flag or a Unity error so far skips them; the coroutine then fails
        // the load.
        public bool OnSaveLoaded(bool success)
        {
            Attempt a = attempt;
            if (a == null || !a.Extracting) return false;
            a.SuccessSeen = true;
            a.Success = JoinTestFaults.FilterSuccess(success);
            if (!a.Success) { Log.Info("join: save loaded with success false: nothing applied"); return true; }
            if (a.Errors.ErrorsSoFar > 0)
            {
                Log.Info("join: a Unity error during the load: nothing applied");
                return true;
            }
            // Each step catches its own failure so the others still run; ApplyVerdict then fails the load.
            try
            {
                if (JoinTestFaults.FailApplyStep()) throw new InvalidOperationException("planted test fault (apply)");
                a.BoundaryCopied = CopyBoundaries(a.Header);
            }
            catch (Exception e) { Log.Error("join: the boundary copy failed: " + e); }
            try
            {
                a.AiOff = JoinerAi.Off();
                a.RivalTrees = JoinerAi.TreesLeft();
                a.RivalTreesLeft = a.RivalTrees.Total;
            }
            catch (Exception e) { Log.Error("join: AI off failed: " + e); }
            try { a.AutosaveOff = AutosaveOff(); }
            catch (Exception e) { Log.Error("join: autosave off failed: " + e); }
            try { Drift.JoinOrigin(a.Header.OriginTicks, a.Header.SaveId); }
            catch (Exception e) { Log.Error("join: the drift origin failed: " + e.Message); }
            return true;
        }

        // ------------------------------------------------------------------ the boundary copy (step 2)

        // Without it the joiner's day, month and year boundaries would be its load moment, up to a day off the host's.
        StateField<TimeController, DateTime> lastDay, nextDay, lastMonth, nextMonth, lastYear, nextYear;

        string BoundaryLine(TimeController tc, JoinHeader h)
        {
            long now = tc.CurrentTime.Ticks;
            string fraction = BoundaryRules.FractionText(BoundaryRules.DayFraction(now, h.OriginTicks));
            return "date " + now + " origin " + h.OriginTicks + " fraction " + fraction +
                   " lastDay " + lastDay.Get(tc).Ticks + " nextDay " + nextDay.Get(tc).Ticks +
                   " lastMonth " + lastMonth.Get(tc).Ticks + " nextMonth " + nextMonth.Get(tc).Ticks +
                   " lastYear " + lastYear.Get(tc).Ticks + " nextYear " + nextYear.Get(tc).Ticks;
        }

        // True when copied. The negative control (-semp-no-boundary-copy) skips only the copy and still logs the line
        // with this game's own values, which is what the comparison tool reads.
        bool CopyBoundaries(JoinHeader h)
        {
            if (lastDay == null)
            {
                lastDay = new StateField<TimeController, DateTime>("lastDay");
                nextDay = new StateField<TimeController, DateTime>("nextDay");
                lastMonth = new StateField<TimeController, DateTime>("lastMonth");
                nextMonth = new StateField<TimeController, DateTime>("nextMonth");
                lastYear = new StateField<TimeController, DateTime>("lastYear");
                nextYear = new StateField<TimeController, DateTime>("nextYear");
            }
            if (!lastDay.Ok || !nextDay.Ok || !lastMonth.Ok || !nextMonth.Ok || !lastYear.Ok || !nextYear.Ok)
                throw new InvalidOperationException("a TimeController boundary field is missing");
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            Log.Info("join: boundaries before copy: " + BoundaryLine(tc, h));
            DayBoundaries b = BoundaryRules.Of(h);
            string bad = BoundaryRules.Check(h.DateTicks, h.OriginTicks, b);
            if (bad != null) throw new InvalidOperationException("the header's boundaries: " + bad);
            if (Boot.Dev != null && Boot.Dev.NoBoundaryCopy)
            {
                Log.Info("join: boundary copy SKIPPED (-semp-no-boundary-copy, the negative control)");
                Log.Info("join: boundaries " + BoundaryLine(tc, h));
                return false;
            }
            lastDay.Set(tc, new DateTime(b.LastDay));
            nextDay.Set(tc, new DateTime(b.NextDay));
            lastMonth.Set(tc, new DateTime(b.LastMonth));
            nextMonth.Set(tc, new DateTime(b.NextMonth));
            lastYear.Set(tc, new DateTime(b.LastYear));
            nextYear.Set(tc, new DateTime(b.NextYear));
            Log.Info("join: boundaries " + BoundaryLine(tc, h));
            return true;
        }

        // ------------------------------------------------------------------ the yearly autosave off (step 3)

        // A client never writes a save of the shared game; the yearly autosave reads this field at each year event.
        StateField<LoadSaveManager, float?> autoSaveRef;
        float? autoSaveWas;
        bool autoSaveSwitched;

        // The join is over for good and the player is at the menu: a solo game started there must be the game's own,
        // so what a join switched is put back. Rival AI was blocked for the whole process (Company.BlockEnableAI), and
        // the yearly autosave switched off lives on the LoadSaveManager, which may outlast the scene.
        public void SessionOver()
        {
            Company.BlockEnableAI = false;
            if (!autoSaveSwitched) return;
            autoSaveSwitched = false;
            var lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            if (lsm != null && autoSaveRef != null && autoSaveRef.Ok && !autoSaveRef.Get(lsm).HasValue)
                autoSaveRef.Set(lsm, autoSaveWas);
            Log.Info("join: session over; BlockEnableAI cleared, the autosave setting put back");
        }

        bool AutosaveOff()
        {
            var lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            if (autoSaveRef == null) autoSaveRef = new StateField<LoadSaveManager, float?>("autoSaveEveryNYears");
            if (lsm == null || !autoSaveRef.Ok)
            {
                Log.Error("join: the autosave could not be switched off");
                return false;
            }
            if (!autoSaveSwitched) { autoSaveWas = autoSaveRef.Get(lsm); autoSaveSwitched = true; }
            autoSaveRef.Set(lsm, null);
            return !autoSaveRef.Get(lsm).HasValue;
        }

        // ------------------------------------------------------------------ the settle's facts

        FieldInfo planQueueField, pmwCodeField, pmwAiField;

        int PlanQueue()
        {
            try
            {
                if (planQueueField == null) planQueueField = AccessTools.Field(typeof(GameManager), "list");
                GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
                var l = planQueueField != null && gm != null ? planQueueField.GetValue(gm) as ICollection : null;
                return l != null ? l.Count : -1;
            }
            catch (Exception) { return -1; }
        }

        bool PorkchopsDone()
        {
            try
            {
                if (pmwCodeField == null)
                {
                    pmwCodeField = AccessTools.Field(typeof(GameManager), "pmwForCode");
                    pmwAiField = AccessTools.Field(typeof(GameManager), "pmwForAI");
                }
                GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
                if (gm == null || pmwCodeField == null || pmwAiField == null) return false;
                var a = pmwCodeField.GetValue(gm) as PlanMissionWindow;
                var b = pmwAiField.GetValue(gm) as PlanMissionWindow;
                return a != null && b != null && a.PmTabSchedule.ComputePorkchopJobDone() &&
                       b.PmTabSchedule.ComputePorkchopJobDone();
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ the content after the load (step 4)

        // "same", "differs", "unavailable" (no content scan: a network join fails), or "not checked" (the dev feed
        // only). A content mod can build definitions at load, so the content is compared again after it.
        string ContentAfterLoad(JoinHeader h)
        {
            Func<CategoryHash[]> scan = PostLoadScan;
            if (scan == null) return devFeed ? "not checked (dev feed; no content scan in this build)" : "unavailable";
            CategoryHash[] mine;
            try { mine = scan(); }
            catch (Exception e)
            {
                Log.Error("join: the content scan after the load threw: " + e.Message);
                return "unavailable";
            }
            return PostLoadContent.Same(h.PostLoad, mine) ? "same" : "differs";
        }

        internal static string MarketText()
        {
            try
            {
                Company wg = MonoBehaviourSingleton<GameManager>.Instance.WorldGovernmentCompany;
                AiCompanyBehaviour cb = wg != null ? wg.GetComponent<AiCompanyBehaviour>() : null;
                FieldInfo st = AccessTools.Field(typeof(AiCompanyBehaviour), "aiStateData");
                var data = cb != null && st != null ? st.GetValue(cb) as AiCompanyBehaviour.CompanyAIStateData : null;
                DateTime? last = data != null ? data.lastOfferPricesUpdateTime : null;
                var mm = Manager.MarketOfferManager.Instance;
                double cycle = mm != null && mm.marketConfig != null ? mm.marketConfig.PriceChangeCycleLength
                                                                     : double.NaN;
                string next = last.HasValue && !double.IsNaN(cycle) ? last.Value.AddDays(cycle).Ticks.ToString(Inv)
                                                                     : "none";
                return "lastOfferPricesUpdateTime " + (last.HasValue ? last.Value.Ticks.ToString(Inv) : "none") +
                       " next price change " + next;
            }
            catch (Exception e) { return "unreadable (" + e.GetType().Name + ")"; }
        }

        // ------------------------------------------------------------------ endings

        // Before the extract the coroutine stops and never extracts; after it the load is abandoned. The flow's
        // BackToMenu follows.
        public void CancelLoad()
        {
            Attempt a = attempt;
            if (a == null) return;
            a.Cancelled = true;
            Log.Info("join: load cancelled (" + (a.ExtractCalled ? "after" : "before") + " ExtractAllFromSaveData)");
            if (!a.ExtractCalled) clearScheduleAtMenu = true;   // not now: see clearScheduleAtMenu
            End(a);
        }

        void Fail(Attempt a, int code, string why)
        {
            Action<int, string> cb = a.Failed;
            End(a);
            if (cb != null) cb(code, why);
        }

        // Every ending: the attempt's handlers off, the attempt dropped.
        void End(Attempt a)
        {
            a.Prewarm.Close();
            a.Errors.End();
            Application.logMessageReceivedThreaded -= a.LogHandler;
            SceneManager.sceneLoaded -= a.SceneHandler;
            a.Pending = null;
            a.Loaded = null;
            a.Failed = null;
            if (attempt == a) attempt = null;
        }

        // Any failure goes back with SceneManager.LoadScene("Menu"), as the game's own settings reset does (never the
        // pause screen's exit, which autosaves). Already at the menu: nothing to load. "main menu ready" follows.
        // menuLoadPending: a LoadScene("Menu") is already under way (GoToMenu's), so the scene is not loaded again.
        public void BackToMenu(int code, string reason, bool menuLoadPending = false)
        {
            Log.Info("join: back at the main menu (" + code + "): " + TextSanitiser.Clean(reason, 300, ""));
            if (attempt != null) CancelLoad();
            prepared = null;
            bool atMenu = false;
            try { atMenu = SceneManager.GetActiveScene().name == "Menu" && SceneManager.sceneCount == 1; }
            catch (Exception) { }
            if (atMenu || menuLoadPending) return;
            try { SceneManager.LoadScene("Menu"); }
            catch (Exception e) { Log.Error("join: could not go back to the menu: " + e.Message); }
        }
    }

    // StartConfig <-> the game's StartGameConfiguration, by id against this game's content (equal on both sides once
    // the content check has passed). The wire forms (AgencyWire): agencies in lower case, the epoch as an index.
    // Stateless.
    public static class StartConfigMap
    {
        static List<MyIDScriptableObject> All()
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            return all != null ? all.AllMyIDScriptableObjects : null;
        }

        static List<string> Ids<T>() where T : MyIDScriptableObject
        {
            var ids = new List<string>();
            List<MyIDScriptableObject> all = All();
            if (all != null)
                foreach (MyIDScriptableObject o in all)
                    if (o is T && o.ID != null) ids.Add(o.ID);
            return ids;
        }

        static T ById<T>(string id) where T : MyIDScriptableObject
        {
            List<MyIDScriptableObject> all = All();
            if (id == null || all == null) return null;
            foreach (MyIDScriptableObject o in all)
            {
                var t = o as T;
                if (t != null && t.ID == id) return t;
            }
            return null;
        }

        public static CompanyDefinition Company(string wire)
        {
            return ById<CompanyDefinition>(AgencyWire.Resolve(wire, Ids<CompanyDefinition>()));
        }

        public static int EpochIndex(string epochId) { return AgencyWire.EpochIndex(Ids<StartGameEpoch>(), epochId); }

        // The joiner's configuration (load mode A): null and the reason when an id does not resolve or a value is out
        // of range.
        public static GameManager.StartGameConfiguration ToGame(StartConfig c, out string error)
        {
            error = null;
            CompanyDefinition company = Company(c.Company);
            if (company == null) { error = "Config.Company is not in this game's content"; return null; }
            var rivals = new List<CompanyDefinition>();
            if (c.Rivals != null)
                foreach (string r in c.Rivals)
                {
                    CompanyDefinition d = Company(r);
                    if (d == null) { error = "a rival is not in this game's content"; return null; }
                    if (d == company) { error = "the claimed agency is also a rival"; return null; }
                    rivals.Add(d);
                }
            if (!Enum.IsDefined(typeof(GameManager.EGameDifficulty), c.Difficulty))
            {
                error = "Config.Difficulty is not one of this game's";
                return null;
            }
            PlanetarySystemDescriptor system = ById<PlanetarySystemDescriptor>(c.PlanetarySystem);
            if (system == null) { error = "Config.PlanetarySystem is not in this game's content"; return null; }
            StartGameEpoch epoch = ById<StartGameEpoch>(AgencyWire.EpochAt(Ids<StartGameEpoch>(), c.Epoch));
            if (epoch == null) { error = "Config.Epoch is not one of this game's epochs"; return null; }
            if (system.MapEpochToToStartData == null || !system.MapEpochToToStartData.ContainsKey(epoch))
            {
                error = "Config.Epoch has no start data in that planetary system";
                return null;
            }
            // startWithTutorial matters only to a new game (GameManager.InitFromStartGameConfiguration), never to a
            // load.
            return new GameManager.StartGameConfiguration
            {
                gameplayID = c.GameplayId ?? "", startWithTutorial = false, setupAIs = c.SetupAis,
                enabledRivalCorporations = rivals,
                Company = company, GameDifficulty = (GameManager.EGameDifficulty)c.Difficulty,
                PlanetarySystemDescriptor = system,
                StartGameEpoch = epoch,
            };
        }
    }

    // The joiner's AI-off step: M3's recipe (WorldSwitches.AiOff) for every active agency other than the player and the
    // World Government: the client simulates the other agencies as non-players and must never decide for them.
    // BlockEnableAI was set before the load (the recipe sets it again, to the same value).
    internal static class JoinerAi
    {
        public static int Off()
        {
            return WorldSwitches.AiOff(MonoBehaviourSingleton<GameManager>.Instance.Player);
        }

        // The trees still alive for the agencies Off switches off, by kind (Failed when they cannot be counted).
        public static TreeTally TreesLeft()
        {
            var tally = new TreeTally();
            try
            {
                foreach (Company c in new List<Company>(MonoBehaviourSingleton<GameManager>.Instance.Companies))
                {
                    if (c == null || c.ID == null || c.IsPlayer) continue;
                    if (c.Definition != null && c.Definition.IsWorldGovernment) continue;
                    if (!c.gameObject.activeSelf) continue;
                    int r, p, l, u;
                    Trees.Count(c, out r, out p, out l, out u);
                    tally.Add(r, p, l, u);
                }
            }
            catch (Exception) { tally.Failed = true; }
            return tally;
        }
    }

    // Behaviour trees of one agency: running, paused, and loading on a thread (Behavior Designer's private lists).
    // Throws when the fields are not there, so a count that cannot be taken is never read as "none".
    internal static class Trees
    {
        public static void Count(Company c, out int running, out int paused, out int loading)
        {
            int unowned;
            Count(c, out running, out paused, out loading, out unowned);
        }

        // unowned: the loading threads among them whose owner could not be read (counted for every agency).
        public static void Count(Company c, out int running, out int paused, out int loading, out int unowned)
        {
            running = 0; paused = 0; loading = 0; unowned = 0;
            FieldInfo pausedField = AccessTools.Field(typeof(BdManager), "pausedBehaviorTrees");
            FieldInfo threadsField = AccessTools.Field(typeof(BdManager), "activeThreads");
            Type el = threadsField != null && threadsField.FieldType.IsGenericType
                ? threadsField.FieldType.GetGenericArguments()[0] : null;
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo threadBehavior = el != null ? el.GetProperty("Behavior", Any) : null;
            if (pausedField == null || threadsField == null || threadBehavior == null)
                throw new InvalidOperationException("Behavior Designer's tree lists were not found");
            BdManager bm = BdManager.instance;
            if (bm == null) return;
            List<BdManager.BehaviorTree> trees = bm.BehaviorTrees;
            if (trees != null)
                for (int i = 0; i < trees.Count; i++)
                {
                    BdBehavior owner = trees[i] != null ? trees[i].behavior : null;
                    if (owner != null && owner.GetComponent<Company>() == c) running++;
                }
            var pd = pausedField.GetValue(bm) as Dictionary<BdBehavior, BdManager.BehaviorTree>;
            if (pd != null)
                foreach (BdBehavior b in pd.Keys)
                    if (b != null && b.GetComponent<Company>() == c) paused++;
            var th = threadsField.GetValue(bm) as IList;
            if (th != null)
                for (int i = 0; i < th.Count; i++)
                {
                    var ob = (th[i] != null ? threadBehavior.GetValue(th[i], null) : null) as BdBehavior;
                    if (ob == null) { loading++; unowned++; }
                    else if (ob.GetComponent<Company>() == c) loading++;
                }
        }
    }
}
