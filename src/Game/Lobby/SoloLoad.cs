using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Data.ScriptableObject;
using Game;
using Game.ObjectInfoDataScripts;
using ScriptableObjectScripts.StartGameConfiguration;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using SaveGameData = Manager.SaveGameData;
using LoadSaveManager = Manager.LoadSaveManager;
using GameManager = Manager.GameManager;
using Company = Game.Company;
using LoadingScreen = Game.UI.Screens.LoadingScreen;
using SearchObjectWindow = Game.UI.Windows.Windows.SearchObjectWindow;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // A Multiplayer save read from the folder and put through the walls (DD24 step 2), ready for a solo load. Hosting a
    // saved session from the folder starts from the same result (the lobby's claims take the place of step 3).
    public sealed class CheckedMpSave
    {
        public MpSaveEntry Entry;
        public SaveGameData World;     // the strict deserialise, scanned and sane; nothing has touched the game yet
    }

    // Playing a Multiplayer save alone (DD24), from the main menu: the walls, the start configuration of a solo game (the
    // chosen agency the player, every other agency in the game a rival run by the game's own AI), the former seats' trees
    // emptied, offer locks cleared, finite money for the chosen agency, the load statics, then route A (the game's
    // LoadingScreen and ExtractAllFromSaveData with the checked object, never LoadSaveManager.Load and never a file read by
    // the game). The solo game saves to My saves because no session is on, and lastSaveName is empty so "save" never points
    // back at the multiplayer save. One load at a time; owned by MenuSessions; main thread only.
    public sealed class SoloLoad
    {
        static readonly FieldInfo LastSaveName = AccessTools.Field(typeof(LoadSaveManager), "lastSaveName");
        static readonly FieldInfo EvaluatedBy = AccessTools.Field(typeof(Offer), "isBeingEvaluatedByCompanyAI");

        readonly SaveGlue glue;
        readonly SnapshotLoad deserialiser = new SnapshotLoad(false);
        bool loading;

        public SoloLoad(SaveGlue glue) { this.glue = glue; }

        public bool Loading { get { return loading; } }

        // null = the load has started; else a UiText key.
        public string Start(string mpSaveName, string agencyId)
        {
            if (loading) return "menu.solo.busy";
            if (!Session.AtMenu || Session.Live) return "menu.notAtMenu";
            if (!deserialiser.AtMenu) return "menu.notAtMenu";
            try
            {
                string why;
                CheckedMpSave save = Read(mpSaveName, agencyId, out why);
                if (save == null) return Refused(why);
                IList<AgencyRow> rows = SoloAgencies.For(save.Entry, IdleOf(save.Entry), id => SaveInfoMap.Company(id) != null);
                SaveInfoData solo = SoloStart.For(save.Entry.Info, agencyId, rows, WorldGovernment(), out why);
                if (solo == null) return Refused(why);
                GameManager.StartGameConfiguration cfg = SaveInfoMap.ToGame(solo.StartGameConfiguration, out why);
                if (cfg == null) return Refused(why);

                List<string> seats = SoloAgencies.FormerSeats(rows);
                bool wasHuman = seats.Exists(s => string.Equals(s, agencyId, StringComparison.OrdinalIgnoreCase));
                if (!wasHuman) seats.Add(agencyId);   // an AI rival's saved trees go too (DD24 step 4)
                int emptied = EmptyTrees(save.World, seats);
                int unlocked = ClearOfferLocks(save.World, seats);
                SetFiniteMoney(save.World, agencyId, cfg);
                var lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
                if (lsm == null) return Refused("no LoadSaveManager");
                why = SaveInfoMap.SetLoadStatics(lsm, save.Entry.Info);
                if (why != null) return Refused(why);
                if (LastSaveName != null) LastSaveName.SetValue(lsm, "");
                Company.BlockEnableAI = false;
                Log.Info("solo: " + save.Entry.Name + " as " + agencyId + ": " + seats.Count + " seat(s) reset (" + emptied +
                         " trees emptied), " + unlocked + " offer lock(s) cleared, " + (cfg.enabledRivalCorporations.Count) +
                         " rival(s)");
                loading = true;
                GameManager.StaticStartGameConfig = cfg;
                lsm.IsScheduledToLoadAfterMainSceneReload = true;
                UnityEngine.Events.UnityAction<Scene, LoadSceneMode> handler = null;
                handler = (scene, mode) =>
                {
                    if (mode != LoadSceneMode.Additive || scene.name != LoadingScreen.MySceneGame) return;
                    SceneManager.sceneLoaded -= handler;
                    SempRoot root = SempRoot.Instance;
                    if (root == null) { Failed("no mod object to run the load on"); return; }
                    root.StartCoroutine(GuardedSteps.Run(RouteA(save.World), e => Failed("the load threw " + e.GetType().Name)));
                };
                SceneManager.sceneLoaded += handler;
                SceneManager.LoadScene("LoadingScreen");
                return null;
            }
            catch (Exception e)
            {
                loading = false;
                Log.Error("solo: the load could not start: " + e);
                return "menu.solo.failed";
            }
        }

        // Steps 2 of DD24: the file against the index, walls 2 to 4, then the strict deserialise and the local sanity (walls 5 to
        // 7). null + the reason when a wall refuses. Nothing is changed in the game.
        public CheckedMpSave Read(string mpSaveName, string agencyId, out string why)
        {
            why = null;
            MpSaveIndex index = glue.Folder.ReadIndex();
            MpSaveEntry e = SaveNames.IsLegal(mpSaveName) ? index.ForName(mpSaveName) : null;
            if (e == null) { why = "that save is not in Multiplayer saves any more"; return null; }
            string path = Path.Combine(glue.Folder.Dir, e.Name + ".json.gz");
            if (!PathRules.IsInside(glue.Folder.Dir, path) || !File.Exists(path)) { why = "the save's file is missing"; return null; }
            if (new FileInfo(path).Length > JoinLimits.MaxCompressed) { why = "the save's size is outside the cap"; return null; }
            byte[] json;
            why = PackedSave.Unpack(File.ReadAllBytes(path), e, out json);
            if (why != null) return null;
            object world;
            why = SaveWalls.Check(deserialiser, json, e.Info, agencyId, out world);
            if (why != null) return null;
            return new CheckedMpSave { Entry = e, World = (SaveGameData)world };
        }

        string Refused(string why)
        {
            Log.Warn("solo: refused: " + TextSanitiser.Clean(why, 200, "?"));
            return "menu.solo.refused";
        }

        void Failed(string why)
        {
            loading = false;
            Log.Error("solo: the load failed: " + why);
            try { SceneManager.LoadScene("Menu"); } catch (Exception e) { Log.Error("solo: could not go back to the menu: " + e.Message); }
        }

        // The game's own LoadInternal waits, then the extract of the checked object (the same recipe as the joiner's route A,
        // without its session gates: the AI runs, nothing is skipped).
        IEnumerator RouteA(SaveGameData obj)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            while (MonoBehaviourSingleton<GameManager>.InstanceIsNull) yield return new WaitForEndOfFrame();
            if (!SerializedMonoBehaviourSingleton<UIManager>.InstanceIsNull)
            {
                var sw = SerializedMonoBehaviourSingleton<UIManager>.Instance.GetWindow<SearchObjectWindow>();
                if (sw != null)
                    for (int i = 0; i < 20; i++)
                        if (!sw.SpawnRowsCorutineFinish) yield return new WaitForEndOfFrame();
            }
            LoadSaveManager lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            lsm.IsScheduledToLoadAfterMainSceneReload = false;
            GameManager.SetInitFromStartGameConfigurationOrFromSaveFile(false);
            bool extract = false;
            string threw = null;
            try { extract = lsm.ExtractAllFromSaveData(obj); }
            catch (Exception e) { threw = e.GetType().Name; }
            loading = false;
            if (!extract || threw != null) { Failed("the extract " + (threw != null ? "threw " + threw : "returned false")); yield break; }
            Log.Info("solo: loaded");
        }

        // ---- the world, before the extract ----

        static bool In(List<string> ids, string id)
        {
            return id != null && ids.Exists(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase));
        }

        // The saved behaviour trees of these agencies emptied (their aiStateData stays), so EnableAI starts each fresh.
        static int EmptyTrees(SaveGameData obj, List<string> ids)
        {
            int was = 0;
            if (obj.companyAISave == null) return 0;
            foreach (SaveGameData.CompanyAISave s in obj.companyAISave)
            {
                if (s == null || s.IDCompany == null || !In(ids, s.IDCompany.ID)) continue;
                was += s.behaviorSaves != null ? s.behaviorSaves.Count : 0;
                s.behaviorSaves = new List<AiCompanyBehaviour.AIBehaviorSave>();
            }
            return was;
        }

        // The market offers an emptied agency was weighing: their lock goes, or no other AI could ever weigh them.
        static int ClearOfferLocks(SaveGameData obj, List<string> ids)
        {
            int n = 0;
            if (obj.marketOfferData == null || obj.marketOfferData.offerts == null || EvaluatedBy == null) return 0;
            foreach (Offer o in obj.marketOfferData.offerts)
            {
                var by = o != null ? EvaluatedBy.GetValue(o) as IdForCompany : null;
                if (by == null || !In(ids, by.ID)) continue;
                EvaluatedBy.SetValue(o, null);
                n++;
            }
            return n;
        }

        // An AI rival's saved money is +Infinity: the player's agency starts with the epoch's opening money for the
        // difficulty instead, in the object, so no frame runs with infinite money. A finite value stays as it is.
        static void SetFiniteMoney(SaveGameData obj, string agency, GameManager.StartGameConfiguration cfg)
        {
            if (obj.companyDataSave == null) return;
            foreach (var cd in obj.companyDataSave)
            {
                if (cd == null || cd.companyID == null || !string.Equals(cd.companyID.id, agency, StringComparison.OrdinalIgnoreCase)) continue;
                if (!double.IsInfinity(cd.money) && !double.IsNaN(cd.money)) return;
                double opening = OpeningMoney(cfg, cd.companyID.id);
                Log.Info("solo: the chosen agency was an AI rival: its money is set to " + opening.ToString("F0") + " in the save");
                cd.money = opening;
                return;
            }
        }

        static double OpeningMoney(GameManager.StartGameConfiguration cfg, string agency)
        {
            double money = 100000000.0;   // CompanyDataSave's own default, if the epoch has no figure
            try
            {
                SaveGameData start = cfg.StartGameData != null ? cfg.StartGameData.SaveGameData : null;
                var row = start != null && start.companyDataSave != null
                    ? start.companyDataSave.FirstOrDefault(c => c.companyID != null && c.companyID.id == agency) : null;
                if (row != null && !double.IsInfinity(row.money)) money = row.money;
                var dc = Resources.FindObjectsOfTypeAll<DifficultyConfig>().FirstOrDefault();
                if (dc != null) money *= dc.DifficultyToBaseMultipliersMap[cfg.GameDifficulty].StartMoneyMultiplier;
            }
            catch (Exception e) { Log.Warn("solo: the opening money could not be read, the default is used: " + e.Message); }
            return money;
        }

        // The star system's idle companies, from this game's content.
        static List<string> IdleOf(MpSaveEntry e)
        {
            var idle = new List<string>();
            try
            {
                string system = e.Info.StartGameConfiguration.PlanetarySystemDescriptor;
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                foreach (var o in all.AllMyIDScriptableObjects)
                {
                    var d = o as PlanetarySystemDescriptor;
                    if (d == null || d.ID != system || d.idleCompanies == null) continue;
                    foreach (var cd in d.idleCompanies) if (cd != null && cd.ID != null) idle.Add(cd.ID);
                }
            }
            catch (Exception) { }
            return idle;
        }

        static List<string> WorldGovernment()
        {
            var ids = new List<string>();
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            if (all == null) return ids;
            foreach (var o in all.AllMyIDScriptableObjects)
            {
                var d = o as ScriptableObjectScripts.CompanyDefinition;
                if (d != null && d.IsWorldGovernment && d.ID != null) ids.Add(d.ID);
            }
            return ids;
        }
    }
}
