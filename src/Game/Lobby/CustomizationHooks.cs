using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.UI;
using Game.UI.Menu.GameCustomizationUI;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using ScriptableObjectScripts.StartGameConfiguration;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // The host setup's new game (M8 DD6): the game's own customisation pages, opened by the setup on the star system
    // page (the agency page is skipped), with a reference agency as the user company so the epochs, the rival toggles
    // and the money line do not follow the agency page's default. While the pages are armed (SetupIntents.PagesArmed):
    //  - every rival toggle shows (SetUserCompany's postfix), but in Track U the host's own (the game hides the
    //    player's); there every shown one is ticked and fixed (DD22: every agency but the host's is an AI rival);
    //  - the epoch page offers an epoch only if every playable agency may play it, and its Next is off when none is;
    //  - Prev on the first shown page and the setup's own Esc leave the setup with the panel's own choices put back;
    //  - the last Next's LoadScene("LoadingScreen") is the hand-over: Track U lets the load run with no tutorial and
    //    arms the host intent; the full track keeps a copy, resets the panel's gameplayID and loads nothing.
    // The decisions are SetupFlow's (Core); this class does what they say to the game's objects. Owned by
    // MenuSessions (no static state); main thread only.
    public sealed class HostSetupPages
    {
        static readonly FieldInfo PanelOfMenu = AccessTools.Field(typeof(MenuSceneUI), "gameCustomizationPanel");
        static readonly FieldInfo SettingsOfMenu = AccessTools.Field(typeof(MenuSceneUI), "settingsWindow");
        static readonly MethodInfo NewGameClick = AccessTools.Method(typeof(MenuSceneUI), "OnClickBtnSingle");
        static readonly MethodInfo SettingsClick = AccessTools.Method(typeof(MenuSceneUI), "OnClickBtnSettings");
        static readonly MethodInfo SettingsHide = AccessTools.Method(typeof(Game.UI.Windows.SettingsWindow), "Hide");
        static readonly FieldInfo PagesF = AccessTools.Field(typeof(GameCustomizationUI), "pages");
        static readonly FieldInfo NextF = AccessTools.Field(typeof(GameCustomizationUI), "nextButtons");
        static readonly FieldInfo PrevF = AccessTools.Field(typeof(GameCustomizationUI), "prevButtons");
        static readonly FieldInfo MenuCgF = AccessTools.Field(typeof(GameCustomizationUI), "menuSceneUI");
        static readonly FieldInfo BlurF = AccessTools.Field(typeof(GameCustomizationUI), "blurBackground");
        static readonly FieldInfo OwnConfigF = AccessTools.Field(typeof(GameCustomizationUI), "startGameConfiguration");
        static readonly FieldInfo CorpF = AccessTools.Field(typeof(GameCustomizationUI), "corporationSelectUI");
        static readonly FieldInfo DiffF = AccessTools.Field(typeof(GameCustomizationUI), "difficultySettingsUI");
        static readonly FieldInfo StarsF = AccessTools.Field(typeof(GameCustomizationUI), "starSystemSettingsUI");
        static readonly FieldInfo EpochUiF = AccessTools.Field(typeof(GameCustomizationUI), "startGameEpochSettingsUI");
        static readonly FieldInfo EpochPageF = AccessTools.Field(typeof(GameCustomizationUI), "cgStartEpochSettings");
        static readonly FieldInfo RivalsF = AccessTools.Field(typeof(GameCustomizationUI), "enabledRivalAIs");
        static readonly FieldInfo PageF = AccessTools.Field(typeof(GameCustomizationUI), "currentPage");
        static readonly FieldInfo CorpItemsF = AccessTools.Field(typeof(CorporationSelectUI), "corporationItems");
        static readonly FieldInfo DiffTogglesF = AccessTools.Field(typeof(DifficultySettingsUI), "difficultyToggles");
        static readonly FieldInfo DiffSelectedF = AccessTools.Field(typeof(DifficultySettingsUI), "selectedDifficulty");
        static readonly FieldInfo EpochTogglesF = AccessTools.Field(typeof(StartGameEpochSettingsUI), "toggleList");
        static readonly FieldInfo EpochPanelF =
            AccessTools.Field(typeof(StartGameEpochSettingsUI), "gameCustomizationUI");
        static readonly MethodInfo EpochToggleChange =
            AccessTools.Method(typeof(StartGameEpochSettingsUI), "ToggleChange");
        static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic);

        readonly MenuSessions owner;
        MenuSceneUI menu;                   // this main menu (MenuSessions.MenuShown)
        GameCustomizationUI panel;          // the menu's customisation panel, while the setup uses it
        CompanyDefinition reference;        // the user company while armed: it decides only the money line
        bool fixedRivals;                   // Track U: every rival ticked and not changeable
        bool entered;                       // the pages were switched to the setup's (EnterPages)
        readonly List<KeyValuePair<Toggle, bool>> ticks = new List<KeyValuePair<Toggle, bool>>();
        readonly List<KeyValuePair<Selectable, bool>> live = new List<KeyValuePair<Selectable, bool>>();
        object difficulty;                  // the difficulty page's own selection
        int windowFrame = -10;              // the last frame a game pop-up or the Settings window was up
        bool windowCheckFailed;             // the pop-up check threw (logged once per setup)
        StartGameEpoch controlEpoch;        // the test seams' epoch control: an agency taken out in memory
        CompanyDefinition controlAgency;

        // The full track's kept configuration (a copy, never the panel's own object); null until a kept Start. With it,
        // the rival toggles as they were at that Start (game ids): ticked = AI rival, unticked = not in the game.
        public GameManager.StartGameConfiguration Kept { get; private set; }
        public List<string> KeptAi { get; private set; }
        public List<string> KeptOff { get; private set; }

        // The kept new game for the lobby, once (null when none is waiting).
        public GameManager.StartGameConfiguration TakeKept(out List<string> ai, out List<string> off)
        {
            GameManager.StartGameConfiguration k = Kept;
            ai = KeptAi ?? new List<string>();
            off = KeptOff ?? new List<string>();
            Kept = null; KeptAi = null; KeptOff = null;
            return k;
        }

        // A new game with the game's own defaults and no pages (the test seams' "menu-newgame", the device kit): the
        // menu panel's star system, the first epoch every playable agency may play, Pioneer, and the agencies the
        // game's own rival toggles start ticked as AI rivals. Null (and why) when the menu's panel cannot give them.
        public GameManager.StartGameConfiguration DefaultNewGame(out List<string> ai, out List<string> off,
            out string why)
        {
            ai = new List<string>();
            off = new List<string>();
            why = null;
            if (menu == null) { why = "not at the main menu"; return null; }
            try
            {
                var cg = PanelOfMenu.GetValue(menu) as CanvasGroup;
                GameCustomizationUI p = cg != null ? cg.GetComponent<GameCustomizationUI>() : null;
                if (p == null) { why = "no new-game panel on this menu"; return null; }
                var stars = StarsF.GetValue(p) as StarSystemSettingsUI;
                var epochUi = EpochUiF.GetValue(p) as StartGameEpochSettingsUI;
                PlanetarySystemDescriptor system = stars != null ? stars.userSelectedPlanetarySystemDescriptor : null;
                if (system == null || epochUi == null)
                {
                    why = "the panel names no star system or epochs";
                    return null;
                }
                var all = new List<CompanyDefinition>();
                foreach (CompanyDefinition d in SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                    .AllCompanyDefinitions.ListNotEmpty)
                    if (d != null && !d.IsWorldGovernment) all.Add(d);
                if (all.Count == 0) { why = "no playable agency"; return null; }
                var ids = new List<string>();
                foreach (CompanyDefinition d in all) ids.Add(d.ID);
                StartGameEpoch epoch = null;
                foreach (StartGameEpoch ep in epochUi.StartGameEpochs)
                    if (ep != null && system.MapEpochToToStartData.ContainsKey(ep) && Offer(ep, ids).Offered)
                    {
                        epoch = ep;
                        break;
                    }
                if (epoch == null) { why = "no epoch every agency may play"; return null; }
                var c = new GameManager.StartGameConfiguration
                {
                    Company = all[0], startWithTutorial = false, setupAIs = true,
                    GameDifficulty = GameManager.EGameDifficulty.Pioneer, PlanetarySystemDescriptor = system,
                    StartGameEpoch = epoch, enabledRivalCorporations = new List<CompanyDefinition>(),
                };
                foreach (CompanyDefinition d in all) (d.ONInMenu ? ai : off).Add(d.ID);
                c.SetGameplayIDIfNotSet();
                return c;
            }
            catch (Exception e)
            {
                why = "reading the panel threw " + e.GetType().Name;
                return null;
            }
        }

        // The setup was left without a load (Prev, Esc, a kept or refused Start): the Multiplayer screen comes back.
        public event Action Left;

        public HostSetupPages(MenuSessions owner)
        {
            this.owner = owner;
            owner.MenuShown += m => menu = m;
        }

        SetupFlow Flow { get { return owner.Setup; } }

        // ---- opening ----

        // Opens the pages as the host setup, with agencyWire (the host's pick, the wire id) as the reference agency.
        // keep: the full track's hand-over (nothing loads). Null = open; else a UiText key, and nothing is armed.
        public string Open(string agencyWire, bool keep)
        {
            if (menu == null) return "menu.notAtMenu";
            GameCustomizationUI p = null;
            try
            {
                var cg = PanelOfMenu.GetValue(menu) as CanvasGroup;
                p = cg != null ? cg.GetComponent<GameCustomizationUI>() : null;
            }
            catch (Exception e) { Log.Error("setup: the new-game panel could not be read: " + e.Message); }
            if (p == null) { Log.Error("setup: no new-game panel on this menu"); return SetupFlow.Unavailable; }
            CompanyDefinition agency = Agency(p, agencyWire);
            if (agency == null)
            {
                Log.Warn("setup: '" + agencyWire + "' is not one playable agency of this game");
                return "join.badAgency";
            }
            string why = Flow.OpenPages(keep, SetupHookNames.AllIn(SetupHookNames.ForPages, GameHooks.IsInstalled));
            if (why != null)
            {
                Log.Error("setup: the new-game pages cannot open as the setup (a setup is open, or a hook is missing)");
                return why;
            }
            panel = p;
            reference = agency;
            fixedRivals = !keep;
            windowCheckFailed = false;
            windowFrame = -10;
            Kept = null;
            try
            {
                // A panel shown before has run its Start: the setup's pages now; otherwise Start's postfix does it,
                // before the first frame is drawn.
                if (Rivals(p).Count > 0) EnterPages(p);
                if (!owner.Intents.PagesArmed) return SetupFlow.Unavailable;   // a broken page: left next frame
                NewGameClick.Invoke(menu, null);   // the game's own New game: the cross-fade to the panel
            }
            catch (Exception e)
            {
                Log.Error("setup: opening the new-game pages failed: " + e);
                Leave("a failed opening");
                return SetupFlow.Unavailable;
            }
            Log.Info("setup: the new-game pages open as the host setup (reference agency " + agency.ID + ", " +
                     (keep ? "kept: nothing loads" : "Track U: the load runs") + ")");
            return null;
        }

        CompanyDefinition Agency(GameCustomizationUI p, string wire)
        {
            List<CompanyDefinition> all = Playable(p);
            var ids = new List<string>();
            foreach (CompanyDefinition c in all) ids.Add(c.ID);
            string id = AgencyWire.Resolve(wire, ids);
            foreach (CompanyDefinition c in all) if (id != null && c.ID == id) return c;
            return null;
        }

        // The panel's Start ran (its postfix, armed): the rival toggles exist now.
        internal void PanelStarted(GameCustomizationUI p)
        {
            if (entered || p != panel) return;
            try { EnterPages(p); }
            catch (Exception e)
            {
                Log.Error("setup: switching to the setup's pages failed: " + e);
                LeaveLater("a broken page");
            }
        }

        // A leave asked for from inside a game method that carries on: disarmed now, the panel put back by the next
        // Update (SetupFlow.LeaveLater).
        internal void LeaveLater(string why)
        {
            Log.Info("setup: leaving the new-game pages next frame (" + why + ")");
            Flow.LeaveLater(why);
        }

        void EnterPages(GameCustomizationUI p)
        {
            Snapshot(p);
            entered = true;
            p.SetUserCompany(reference);   // the postfix shows every rival toggle
            if (!owner.Intents.PagesArmed) return;   // the postfix failed: the leave is due next frame
            if (fixedRivals)
                foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(p))
                {
                    r.Value.isOn = true;
                    r.Value.interactable = false;
                }
            ShowOnly(p, SetupFlow.FirstShownPage);
            Button next = EpochNext(p);
            Log.Info("setup: the agency page skipped, the star system page shown; user company " + reference.ID +
                     "; the epoch page's Next " + (next != null ? "is " + PathOf(next.transform) : "was NOT found") +
                     "; " + RivalLine(p));
        }

        // ---- the hooks' work (each called only while the pages are armed) ----

        // SetUserCompany's postfix: every rival toggle shows, except in Track U the host's own agency, which the game
        // hides too (it is the player, never a rival).
        internal void ShowEveryRival(GameCustomizationUI p)
        {
            string host = reference != null ? reference.ID : null;
            foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(p))
                r.Value.gameObject.SetActive(SetupRules.RivalToggleShown(!fixedRivals, r.Key.ID, host));
        }

        // OnPrevButtonClick's prefix: true = the game's own Prev runs.
        internal bool Prev(GameCustomizationUI p)
        {
            int page = (int)PageF.GetValue(p);
            if (!Flow.PrevLeaves(page)) return true;
            Leave("Prev");
            return false;
        }

        // The epoch page's Setup, replaced while armed: the game's own rule over every playable agency.
        internal void SetupEpochs(StartGameEpochSettingsUI ui)
        {
            GameCustomizationUI p = panel ?? EpochPanelF.GetValue(ui) as GameCustomizationUI;
            var toggles = (List<Toggle>)EpochTogglesF.GetValue(ui);
            List<StartGameEpoch> epochs = ui.StartGameEpochs;
            List<string> playable = PlayableIds(p);
            int selected = ui.SelectedStartGameEpochIndex;
            var change = (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), ui, EpochToggleChange);
            int n = Math.Min(toggles.Count, epochs.Count);
            var offered = new List<bool>();
            var parts = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Toggle t = toggles[i];
                StartGameEpoch ep = epochs[i];
                t.onValueChanged.AddListener(change);   // as the game's own Setup does on every call
                EpochOffer o = Offer(ep, playable);
                var tip = t.GetComponent<ShowToolTip>();
                if (tip != null)
                {
                    tip.KeyTooltip = o.TooltipKey;
                    tip.KeyNonInteractableTooltip = o.TooltipKey;
                    tip.enabled = !o.Offered;
                }
                t.interactable = o.Offered;
                offered.Add(o.Offered);
                parts.Add(i + ":" + ep.ID + (o.Offered ? " offered" : " LOCKED (" + o.Why + ")"));
            }
            int pick = SetupRules.PickEpoch(selected, offered);
            for (int i = 0; i < n; i++) if (i != pick) toggles[i].isOn = false;
            if (pick >= 0) toggles[pick].isOn = true;
            Button next = p != null ? EpochNext(p) : null;
            if (next != null) next.interactable = pick >= 0;
            Log.Info("setup: epochs over every playable agency (" + string.Join(",", playable.ToArray()) + "): " +
                     string.Join("; ", parts.ToArray()) + "; selected " + pick + (pick < 0 ? ", Next off" : ""));
            if (pick < 0 && next == null)
            {
                // The page's Next cannot be turned off, so the setup cannot be held here: it is left, after the
                // page fade that called this Setup has finished its own step.
                Log.Error("setup: no epoch is allowed and the epoch page's Next was not found");
                LeaveLater("no epoch allowed");
            }
        }

        static EpochOffer Offer(StartGameEpoch ep, IList<string> playable)
        {
            var possible = new List<string>();
            foreach (CompanyDefinition c in ep.PossiblePlayerCompanies) if (c != null) possible.Add(c.ID);
            string key = ep.LockedTooltipKey;
            return SetupRules.Epoch(ep.IsLocked, PlayerPrefs.GetInt(ep.UnlockedPlayerPrefsKey, 0) != 0,
                StartGameEpoch.OverridePossiblePlayerCompanies, possible, playable, key,
                Language.LEManager.Get(key) != key);
        }

        // The LoadScene prefix, armed: true = the load runs. ran: the new game was handed over and the host intent
        // armed (the finalizer undoes it when LoadScene throws).
        internal bool HandOver(string scene, out bool ran)
        {
            ran = false;
            GameManager.StartGameConfiguration cfg = GameManager.StaticStartGameConfig;
            var own = panel != null ? OwnConfigF.GetValue(panel) as GameManager.StartGameConfiguration : null;
            bool fromPages = cfg != null && ReferenceEquals(cfg, own);
            string broken = fromPages ? Broken(cfg) : null;
            SceneStep step = Flow.OnLoadScene(scene, fromPages, broken == null);
            switch (step)
            {
                case SceneStep.Run:
                    cfg.startWithTutorial = false;   // the tutorial pauses and opens windows on one game only
                    EpochControl(false);
                    Log.Info("setup: the new game starts as the host's (" + Describe(cfg) +
                             "); host once loaded armed");
                    ran = true;
                    return true;
                case SceneStep.Keep:
                    Kept = Copy(cfg);
                    Kept.startWithTutorial = false;
                    KeptAi = new List<string>();
                    KeptOff = new List<string>();
                    SetupRules.FromToggles(Toggles(), KeptAi, KeptOff);   // before Leave puts the panel's ticks back
                    GameManager.StaticStartGameConfig = null;
                    cfg.gameplayID = "";   // SetGameplayIDIfNotSet gave the panel an id the next solo game would reuse
                    Log.Info("setup: the new game is kept, nothing loads (" + Describe(Kept) + "); the toggles chose " +
                             Choices());
                    Leave("the kept Start");
                    return false;
                case SceneStep.Refuse:
                    GameManager.StaticStartGameConfig = null;
                    cfg.gameplayID = "";
                    Log.Error("setup: the new game was refused, nothing loads: " + broken);
                    owner.Dialog.Notice(SetupFlow.Unavailable);
                    Leave("a refused Start");
                    return false;
                default:
                    if (scene == SetupFlow.LoadingScene && !fromPages)
                        Log.Warn("setup: a load that is not the pages' own passed untouched");
                    return true;
            }
        }

        // The hand-over threw: nothing loads, the panel's own configuration is not handed on, the setup is left.
        internal void FailedHandOver()
        {
            try
            {
                var own = panel != null ? OwnConfigF.GetValue(panel) as GameManager.StartGameConfiguration : null;
                if (own != null && ReferenceEquals(GameManager.StaticStartGameConfig, own))
                {
                    GameManager.StaticStartGameConfig = null;
                    own.gameplayID = "";
                }
            }
            catch (Exception e) { Log.Error("setup: clearing the failed hand-over failed: " + e.Message); }
            Leave("a failed hand-over");
        }

        // Why the configuration breaks the setup's rules, or null: the player is the reference agency, the star system
        // is no local file, the epoch is allowed for every playable agency, and in Track U every other agency is an
        // AI rival.
        string Broken(GameManager.StartGameConfiguration cfg)
        {
            if (cfg.Company == null || cfg.Company != reference) return "the player is not the host's agency";
            if (cfg.PlanetarySystemDescriptor == null || cfg.StartGameEpoch == null) return "no star system or epoch";
            var stars = StarsF.GetValue(panel) as StarSystemSettingsUI;
            string local = SetupRules.StarSystem(stars != null,
                stars != null && stars.json != null && cfg.PlanetarySystemDescriptor == stars.json,
                cfg.createdEndGameToStartGameDataFileName);
            if (local != null) return local;
            if (!Offer(cfg.StartGameEpoch, PlayableIds(panel)).Offered)
                return "the epoch is not allowed for every agency";
            if (!fixedRivals) return null;
            var toggles = new List<string>();
            foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(panel)) toggles.Add(r.Key.ID);
            var built = new List<string>();
            if (cfg.enabledRivalCorporations != null)
                foreach (CompanyDefinition c in cfg.enabledRivalCorporations) built.Add(c != null ? c.ID : null);
            return SetupRules.TrackURivals(toggles, cfg.Company.ID, built) ? null : "not every other agency is a rival";
        }

        // A member-wise copy with its own rivals list: nothing in it is shared with the panel's object but the
        // content assets (the agency, the star system, the epoch). Interstellar start data is never used in a session.
        internal static GameManager.StartGameConfiguration Copy(GameManager.StartGameConfiguration c)
        {
            var x = (GameManager.StartGameConfiguration)Clone.Invoke(c, null);
            x.enabledRivalCorporations = c.enabledRivalCorporations == null
                ? new List<CompanyDefinition>() : new List<CompanyDefinition>(c.enabledRivalCorporations);
            x.createdEndGameToStartGameData = null;
            x.createdEndGameToStartGameDataFileName = "";
            return x;
        }

        // ---- leaving ----

        // Leaves the setup without a load: the panel's own choices back, the menu shown (the Multiplayer screen's
        // place until it exists). Never throws.
        public void Leave(string why)
        {
            Flow.PagesLeft();
            EpochControl(false);
            GameCustomizationUI p = panel;
            try { if (p != null) Restore(p); }
            catch (Exception e) { Log.Error("setup: putting the panel back failed: " + e); }
            entered = false;
            Log.Info("setup: left the new-game pages by " + why +
                     (p != null ? "; the panel is as before the setup" : ""));
            Action left = Left;
            if (left == null) return;
            try { left(); }
            catch (Exception e) { Log.Error("setup: a listener of the setup's leaving failed: " + e); }
        }

        void Snapshot(GameCustomizationUI p)
        {
            ticks.Clear();
            live.Clear();
            foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(p))
            {
                ticks.Add(new KeyValuePair<Toggle, bool>(r.Value, r.Value.isOn));
                live.Add(new KeyValuePair<Selectable, bool>(r.Value, r.Value.interactable));
            }
            var diff = (DifficultySettingsUI)DiffF.GetValue(p);
            foreach (Toggle t in DifficultyToggles(diff)) ticks.Add(new KeyValuePair<Toggle, bool>(t, t.isOn));
            foreach (Toggle t in (List<Toggle>)EpochTogglesF.GetValue(EpochUiF.GetValue(p)))
                ticks.Add(new KeyValuePair<Toggle, bool>(t, t.isOn));
            Button next = EpochNext(p);
            if (next != null) live.Add(new KeyValuePair<Selectable, bool>(next, next.interactable));
            difficulty = DiffSelectedF.GetValue(diff);
        }

        void Restore(GameCustomizationUI p)
        {
            if (entered)
            {
                foreach (KeyValuePair<Toggle, bool> t in ticks) if (!t.Value) t.Key.isOn = false;
                foreach (KeyValuePair<Toggle, bool> t in ticks) if (t.Value) t.Key.isOn = true;
                foreach (KeyValuePair<Selectable, bool> s in live) s.Key.interactable = s.Value;
                if (difficulty != null) DiffSelectedF.SetValue(DiffF.GetValue(p), difficulty);
            }
            ticks.Clear();
            live.Clear();
            difficulty = null;
            var pages = (CanvasGroup[])PagesF.GetValue(p);
            foreach (CanvasGroup g in pages) DG.Tweening.DOTween.Kill(g);
            ShowOnly(p, 0);
            p.SetUserCompany(((CorporationSelectUI)CorpF.GetValue(p)).SelectedCompany);   // not armed: vanilla
            ((Image)BlurF.GetValue(p)).enabled = false;
            var panelCg = p.GetComponent<CanvasGroup>();
            var menuCg = (CanvasGroup)MenuCgF.GetValue(p);
            DG.Tweening.DOTween.Kill(panelCg);
            DG.Tweening.DOTween.Kill(menuCg);
            panelCg.gameObject.SetActive(false);
            menuCg.alpha = 1f;
            menuCg.blocksRaycasts = true;
            menuCg.gameObject.SetActive(true);
        }

        static void ShowOnly(GameCustomizationUI p, int page)
        {
            var pages = (CanvasGroup[])PagesF.GetValue(p);
            for (int i = 0; i < pages.Length; i++) pages[i].gameObject.SetActive(i == page);
            pages[page].alpha = 1f;
            pages[page].blocksRaycasts = true;
            PageF.SetValue(p, page);
        }

        // ---- every frame (MenuSessions.Update) and the menu ----

        // The setup's own Esc (the game's pages have none). A game pop-up or the Settings window seen this frame or
        // the last keeps the key theirs, whichever Update closes them first.
        public void Update()
        {
            string due = Flow.TakePendingLeave();
            if (due != null) Leave(due);
            if (!owner.Intents.PagesArmed) return;
            bool up;
            try { up = GameWindowUp(); }
            catch (Exception e)
            {
                // unreadable: Esc stays the game's (Prev still leaves), and the log says why, once per setup
                up = true;
                if (!windowCheckFailed) Log.Error("setup: the pop-up check failed, Esc is left to the game: " + e);
                windowCheckFailed = true;
            }
            if (up) windowFrame = Time.frameCount;
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            bool open = panel != null && panel.gameObject.activeInHierarchy;
            bool yield = SetupFlow.EscIsTheWindows(up, Time.frameCount, windowFrame);
            if (Flow.EscLeaves(open, yield)) Leave("Esc");
            else if (yield) Log.Info("setup: Esc left to the game's window over the pages");
        }

        // At the menu only IsPopUpWindowVisible can be asked (the menu's own pop-ups, and a mod dialog through
        // ModalPopups; the menu's Esc asks the same): IsAnnyPopUpWindowVisible throws in the menu scene, where its
        // popupWindowYesNoBeforeTutorial is unset. The Settings window shows exactly when its object is active in the
        // hierarchy (it opens and hides by SetActive); inactive, or under the faded-out menu, it is not on screen.
        bool GameWindowUp()
        {
            UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
            if (ui != null && ui.IsPopUpWindowVisible) return true;
            return SettingsShown();
        }

        bool SettingsShown()
        {
            var settings = menu != null ? SettingsOfMenu.GetValue(menu) as Component : null;
            return settings != null && settings.gameObject.activeInHierarchy;
        }

        // Every main menu (MenuSessions.OnMenuReady, after the intents were cleared): the old menu's objects are gone.
        public void OnMenuReady()
        {
            EpochControl(false);
            panel = null;
            reference = null;
            entered = false;
            ticks.Clear();
            live.Clear();
            difficulty = null;
        }

        // ---- reading the panel ----

        static Dictionary<CompanyDefinition, Toggle> Rivals(GameCustomizationUI p)
        {
            return (Dictionary<CompanyDefinition, Toggle>)RivalsF.GetValue(p);
        }

        static IEnumerable<Toggle> DifficultyToggles(DifficultySettingsUI d)
        {
            var map = (IDictionary)DiffTogglesF.GetValue(d);
            foreach (DictionaryEntry e in map) yield return (Toggle)e.Value;
        }

        // The agency page's agencies: every playable one (the World Government is not among them).
        static List<CompanyDefinition> Playable(GameCustomizationUI p)
        {
            var list = new List<CompanyDefinition>();
            if (p == null) return list;
            var items = (IEnumerable)CorpItemsF.GetValue(CorpF.GetValue(p));
            foreach (object item in items)
            {
                var c = AccessTools.Field(item.GetType(), "companyDefinition").GetValue(item) as CompanyDefinition;
                if (c != null) list.Add(c);
            }
            return list;
        }

        static List<string> PlayableIds(GameCustomizationUI p)
        {
            var ids = new List<string>();
            foreach (CompanyDefinition c in Playable(p)) ids.Add(c.ID);
            return ids;
        }

        // The epoch page's Next, found by its place under that page, never by an index into nextButtons. Null when
        // not exactly one is there.
        static Button EpochNext(GameCustomizationUI p)
        {
            var page = EpochPageF.GetValue(p) as CanvasGroup;
            var all = NextF.GetValue(p) as Button[];
            if (page == null || all == null) return null;
            Button found = null;
            foreach (Button b in all)
            {
                if (b == null || !b.transform.IsChildOf(page.transform)) continue;
                if (found != null) return null;
                found = b;
            }
            return found;
        }

        List<KeyValuePair<string, bool>> Toggles()
        {
            var toggles = new List<KeyValuePair<string, bool>>();
            if (panel != null)
                foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(panel))
                    toggles.Add(new KeyValuePair<string, bool>(r.Key.ID, r.Value.isOn));
            return toggles;
        }

        string Choices()
        {
            var ai = new List<string>();
            var off = new List<string>();
            SetupRules.FromToggles(Toggles(), ai, off);
            return "AI rival [" + string.Join(",", ai.ToArray()) + "], not in the game [" +
                   string.Join(",", off.ToArray()) + "]";
        }

        static string RivalLine(GameCustomizationUI p)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(p))
                parts.Add(r.Key.ID + (r.Value.gameObject.activeSelf ? "" : " HIDDEN") +
                          (r.Value.isOn ? " on" : " off") + (r.Value.interactable ? "" : " fixed"));
            return "rival toggles [" + string.Join(", ", parts.ToArray()) + "]";
        }

        static string Describe(GameManager.StartGameConfiguration c)
        {
            var rivals = new List<string>();
            if (c.enabledRivalCorporations != null)
                foreach (CompanyDefinition r in c.enabledRivalCorporations) rivals.Add(r != null ? r.ID : "null");
            return "Company " + (c.Company != null ? c.Company.ID : "null") + ", difficulty " + c.GameDifficulty +
                   ", system " + (c.PlanetarySystemDescriptor != null ? c.PlanetarySystemDescriptor.ID : "null") +
                   ", epoch " + (c.StartGameEpoch != null ? c.StartGameEpoch.ID : "null") + ", startWithTutorial " +
                   c.startWithTutorial + ", rivals [" + string.Join(",", rivals.ToArray()) + "], gameplayID '" +
                   c.gameplayID + "'";
        }

        static string PathOf(Transform t)
        {
            string s = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        // ---- test seams (MenuSessions.DevPoll calls TakeDevTriggers, only while TestSeams.Open) ----

        // "setup-pages <agency>" hosts a new game as the Multiplayer screen will (the host's pick, then the pages);
        // "setup-pages <agency> keep" opens them for the full track's hand-over; "setup-epochctl on <agency>|off"
        // takes an agency out of the first epoch in memory (the "no epoch allowed" control); "setup-raise
        // popup|settings" puts a game pop-up or the Settings window over the pages; "setup-where" logs the setup's
        // controls in client pixels for tools\ui-click-at.ps1; "setup-state" logs the panel.
        internal void TakeDevTriggers()
        {
            string pages = DevTriggers.Take("setup-pages");
            if (pages != null) DevPages(pages);
            string ctl = DevTriggers.Take("setup-epochctl");
            if (ctl != null) DevEpochControl(ctl);
            string raise = DevTriggers.Take("setup-raise");
            if (raise != null) DevRaise(raise);
            if (DevTriggers.Take("setup-where") != null) { Where(); owner.Dialog.Where(); }
            if (DevTriggers.Take("setup-state") != null) Log.Info("setup: state " + State());
        }

        void DevPages(string text)
        {
            string[] a = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string agency = a.Length > 0 ? a[0] : "";
            bool keep = a.Length > 1 && a[1] == "keep";
            string why;
            if (keep) why = Open(agency, true);
            else
            {
                why = owner.Actions.PickAgency(agency);
                if (why == null) why = owner.Actions.HostNewGame(null);
            }
            if (why == null) Log.Info("dev setup-pages: ok");
            else Log.Warn("dev setup-pages: refused (" + why + "): " + UiText.Get(why));
        }

        void DevEpochControl(string text)
        {
            string[] a = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length > 1 && a[0] == "on" && panel != null && controlEpoch == null)
            {
                var ui = (StartGameEpochSettingsUI)EpochUiF.GetValue(panel);
                CompanyDefinition c = Agency(panel, a[1]);
                if (c == null || ui.StartGameEpochs.Count == 0)
                {
                    Log.Warn("dev setup-epochctl: no such agency");
                    return;
                }
                controlEpoch = ui.StartGameEpochs[0];
                controlAgency = c;
                bool removed = controlEpoch.PossiblePlayerCompanies.Remove(c);
                Log.Info("dev setup-epochctl: " + c.ID + " taken out of " + controlEpoch.ID + " in memory (" +
                         removed + ")");
                ui.Setup();
            }
            else if (a.Length > 0 && a[0] == "off")
            {
                EpochControl(false);
                if (panel != null && owner.Intents.PagesArmed)
                    ((StartGameEpochSettingsUI)EpochUiF.GetValue(panel)).Setup();
            }
            else Log.Warn("dev setup-epochctl: refused (on <agency> while the pages are open, or off)");
        }

        // Puts the epoch control's agency back (it lives in a game asset, which outlives the menu).
        void EpochControl(bool on)
        {
            if (on || controlEpoch == null) return;
            try
            {
                controlEpoch.PossiblePlayerCompanies.Add(controlAgency);
                Log.Info("dev setup-epochctl: " + controlAgency.ID + " back in " + controlEpoch.ID);
            }
            catch (Exception e) { Log.Error("dev setup-epochctl: putting the agency back failed: " + e.Message); }
            controlEpoch = null;
            controlAgency = null;
        }

        void DevRaise(string what)
        {
            if (menu == null) { Log.Warn("dev setup-raise: not at the menu"); return; }
            if (what == "settings")
            {
                // The game's own Settings button. Its window lives under the menu, which is faded out while the pages
                // show, so it may not be on screen: say so, and close it again (otherwise it would pop up when the
                // menu comes back).
                SettingsClick.Invoke(menu, null);
                var settings = SettingsOfMenu.GetValue(menu) as Component;
                if (SettingsShown())
                {
                    Log.Info("dev setup-raise: settings is up");
                    return;
                }
                Log.Warn("dev setup-raise: settings is NOT on screen: its window " +
                         (settings != null ? PathOf(settings.transform) : "(none)") +
                         " is not active in the hierarchy (the menu it lives under is hidden behind the pages)");
                if (settings != null) SettingsHide.Invoke(settings, null);
                return;
            }
            if (what != "popup") { Log.Warn("dev setup-raise: popup or settings"); return; }
            UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
            ui.ShowPopUP("SEMP test pop-up (dev trigger)", () => { }, () => { }, true, true, true, true, false);
            Log.Info("dev setup-raise: popup " + (ui.IsPopUpWindowVisible ? "is up" : "is NOT on screen"));
        }

        void Where()
        {
            GameCustomizationUI p = panel;
            if (p == null) { Log.Info("setup: where: no setup pages"); return; }
            foreach (Button b in (Button[])NextF.GetValue(p)) WhereOf("setup.next", b);
            foreach (Button b in (Button[])PrevF.GetValue(p)) WhereOf("setup.prev", b);
            foreach (KeyValuePair<CompanyDefinition, Toggle> r in Rivals(p))
                WhereOf("setup.rival." + AgencyWire.From(r.Key.ID), r.Value);
            var map = (IDictionary)DiffTogglesF.GetValue(DiffF.GetValue(p));
            foreach (DictionaryEntry e in map)
                WhereOf("setup.difficulty." + e.Key.ToString().ToLowerInvariant(), (Toggle)e.Value);
            var epochs = (List<Toggle>)EpochTogglesF.GetValue(EpochUiF.GetValue(p));
            for (int i = 0; i < epochs.Count; i++) WhereOf("setup.epoch." + i, epochs[i]);
        }

        // One control's centre in client pixels (top-left origin), only when it is on screen.
        internal static void WhereOf(string id, Component c)
        {
            if (c == null || !c.gameObject.activeInHierarchy) return;
            var rt = c.transform as RectTransform;
            Canvas canvas = c.GetComponentInParent<Canvas>();
            if (rt == null || canvas == null) return;
            Canvas root = canvas.rootCanvas;
            Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            Vector2 s = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
            Log.Info("setup: where " + id + " " + Mathf.RoundToInt(s.x) + "," + Mathf.RoundToInt(Screen.height - s.y) +
                     " (" + PathOf(c.transform) + ")");
        }

        string State()
        {
            GameCustomizationUI p = panel;
            string s = "pages armed " + owner.Intents.PagesArmed + ", dialog armed " + owner.Intents.DialogArmed +
                       ", host intent " + owner.Intents.HostIntent + "; StaticStartGameConfig " +
                       (GameManager.StaticStartGameConfig == null ? "null" : "SET");
            if (p == null) return s + "; no panel";
            var own = (GameManager.StartGameConfiguration)OwnConfigF.GetValue(p);
            var diff = (DifficultySettingsUI)DiffF.GetValue(p);
            var epochUi = (StartGameEpochSettingsUI)EpochUiF.GetValue(p);
            int ei = epochUi.SelectedStartGameEpochIndex;
            return s + "; panel active " + p.gameObject.activeInHierarchy + ", page " + PageF.GetValue(p) +
                   ", user company " + (p.GetUserCompany() != null ? p.GetUserCompany().ID : "null") + "; " +
                   RivalLine(p) + "; difficulty " + DiffSelectedF.GetValue(diff) + "; epoch " +
                   (ei >= 0 && ei < epochUi.StartGameEpochs.Count ? epochUi.StartGameEpochs[ei].ID : "none") +
                   "; panel gameplayID '" + own.gameplayID + "'" + (Kept != null ? "; kept " + Describe(Kept) : "");
        }
    }

    // DD6's hooks on the customisation panel, each acting only while the pages are armed. A failure inside one leaves
    // the setup (fail closed), never the game's own step half done.
    [HarmonyPatch(typeof(GameCustomizationUI))]
    public static class SetupPages
    {
        static HostSetupPages Armed()
        {
            MenuSessions e = Session.Early;
            return e != null && e.Intents.PagesArmed ? e.Pages : null;
        }

        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        static void StartPostfix(GameCustomizationUI __instance)
        {
            HostSetupPages p = Armed();
            if (p != null) p.PanelStarted(__instance);
        }

        [HarmonyPatch(nameof(GameCustomizationUI.SetUserCompany))]
        [HarmonyPostfix]
        static void SetUserCompanyPostfix(GameCustomizationUI __instance)
        {
            HostSetupPages p = Armed();
            if (p == null) return;
            try { p.ShowEveryRival(__instance); }
            catch (Exception e)
            {
                Log.Error("setup: showing every rival toggle failed: " + e);
                p.LeaveLater("a broken page");   // SetUserCompany may be inside the setup's own page switch
            }
        }

        [HarmonyPatch("OnPrevButtonClick")]
        [HarmonyPrefix]
        static bool PrevPrefix(GameCustomizationUI __instance)
        {
            HostSetupPages p = Armed();
            if (p == null) return true;
            try { return p.Prev(__instance); }
            catch (Exception e) { Log.Error("setup: Prev failed: " + e); p.Leave("a broken page"); return false; }
        }
    }

    [HarmonyPatch(typeof(StartGameEpochSettingsUI))]
    public static class SetupEpochs
    {
        [HarmonyPatch(nameof(StartGameEpochSettingsUI.Setup))]
        [HarmonyPrefix]
        static bool Prefix(StartGameEpochSettingsUI __instance)
        {
            MenuSessions e = Session.Early;
            if (e == null || !e.Intents.PagesArmed) return true;
            try { e.Pages.SetupEpochs(__instance); }
            catch (Exception ex)
            {
                Log.Error("setup: the epoch page failed: " + ex);
                e.Pages.LeaveLater("a broken page");   // Setup runs inside the page's own OnEnable and fade
            }
            return false;
        }
    }

    // The hand-over: the last Next builds the configuration inline and calls LoadScene("LoadingScreen"); there is no
    // "build only" method. A save's Load calls LoadScene too, so this acts on the pages' intent only.
    [HarmonyPatch(typeof(SceneManager))]
    public static class SetupHandOver
    {
        [HarmonyPatch(nameof(SceneManager.LoadScene), new[] { typeof(string) })]
        [HarmonyPrefix]
        static bool Prefix(object[] __args, out bool __state)
        {
            __state = false;
            MenuSessions e = Session.Early;
            if (e == null || !e.Intents.PagesArmed) return true;
            string scene = __args != null && __args.Length > 0 ? __args[0] as string : null;
            try { return e.Pages.HandOver(scene, out __state); }
            catch (Exception ex)
            {
                if (scene != SetupFlow.LoadingScene) return true;
                Log.Error("setup: the hand-over failed, nothing loads: " + ex);
                e.Pages.FailedHandOver();
                return false;
            }
        }

        // A LoadScene that threw after the hand-over armed the host intent loaded nothing: the intent goes with the
        // setup, or the next solo load at this menu would host.
        [HarmonyPatch(nameof(SceneManager.LoadScene), new[] { typeof(string) })]
        [HarmonyFinalizer]
        static void Finalizer(Exception __exception, bool __state)
        {
            if (!__state || __exception == null) return;
            MenuSessions e = Session.Early;
            if (e == null) return;
            Log.Error("setup: the new game's load threw, nothing is hosted: " + __exception.Message);
            e.Pages.FailedHandOver();
        }
    }
}
