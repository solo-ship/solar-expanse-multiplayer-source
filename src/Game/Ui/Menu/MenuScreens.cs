using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The main menu's multiplayer screens (M8 Task 18): the Multiplayer screen the menu's Multiplayer button opens,
    // host setup, join by code and, behind their capabilities, the Multiplayer saves list with its solo agency picker
    // and the Load Game dialog's "Multiplayer saves" button. One instance, owned by MenuSessions (no static state);
    // each main menu builds its screens again, on a canvas made like the game's, from the parts kit (DD0). The screens
    // read only the session's capabilities, SessionView and UiText, and act only through ISessionActions (and the
    // host setup's game windows, through MenuSessions). Main thread only.
    public sealed class MenuScreens
    {
        static readonly FieldInfo BtnMult = AccessTools.Field(typeof(MenuSceneUI), "btnMult");
        static readonly FieldInfo DialogOfMenu = AccessTools.Field(typeof(MenuSceneUI), "loadSaveDialogWindow");
        static readonly MethodInfo LoadClick = AccessTools.Method(typeof(MenuSceneUI), "OnClickBtnLoad");
        static readonly FieldInfo PanelOfMenu = AccessTools.Field(typeof(MenuSceneUI), "gameCustomizationPanel");
        const float FadeSeconds = 0.5f;   // the game's own, opening its new-game pages (OnClickBtnSingle)
        const string NameFmt = "Game.UI.CustomizationScreen.CorporationInfo.{0}.Name";

        readonly MenuSessions owner;
        readonly NameStore names;
        MenuSceneUI menu;
        MenuDriver driver;
        GameParts parts;
        Canvas canvas;
        MenuKit kit;
        IMenuScreen open;
        MenuScreen openKind;
        bool savesFromLoadDialog;
        bool failedClosed;
        MenuTestAdapter test;                  // test seams only: the screens over a fake session, in a named state
        readonly Dictionary<string, Transform> tracked = new Dictionary<string, Transform>();
        List<KeyValuePair<string, string>> agencies;   // this menu's playable agencies (wire id, the game's name)
        MenuParts extra;
        readonly MenuFade fade = new MenuFade();
        bool mapDirty;
        float nextMap;

        // The host setup's game windows (Task 14a, HostSetupPages and HostSetupDialog): opening the game's own load
        // dialog as the setup (null: not in this build, and "My saves" answers menu.notYet). The new-game pages are
        // opened by ISessionActions.HostNewGame itself.
        public Func<string> OpenLoadDialog;

        // A join code was accepted and the session is connecting (the host's lobby opens by itself).
        public event Action JoinAccepted;

        // The fixture's "entry update" state: the Multiplayer button as it was, put back on "screen-menu off".
        sealed class ButtonLook
        {
            public bool Interactable;
            public readonly List<KeyValuePair<TMP_Text, string>> Texts = new List<KeyValuePair<TMP_Text, string>>();
            public readonly List<FontStyles> Styles = new List<FontStyles>();
            public readonly List<bool> AutoSize = new List<bool>();
            public readonly List<float> Sizes = new List<float>();
        }
        ButtonLook buttonBefore;

        public MenuScreens(MenuSessions owner)
        {
            this.owner = owner;
            names = new NameStore(Boot.DataDir);
            PlayerNames.Default = names.Get;
            owner.MenuShown += OnMenuShown;
            owner.MultiplayerClicked += () => Open(MenuScreen.Multiplayer);
        }

        public ISessionActions Actions { get { return test != null ? (ISessionActions)test : owner.Actions; } }
        public MenuControls Controls { get { return MenuControls.For(Actions.Capabilities); } }
        public NameStore Names { get { return names; } }

        // The name typed on a screen, kept for next time; over the test adapter nothing real is written.
        public void KeepName(string typed)
        {
            if (test != null) { Log.Info("test adapter: the name would be kept"); return; }
            names.Set(typed);
        }
        public MenuKit Kit { get { return kit; } }
        public MenuParts Extra { get { return extra; } }
        public Canvas Canvas { get { return canvas; } }
        internal MenuTestAdapter Test { get { return test; } }

        // The Multiplayer saves the session offers (the full track; the test adapter), else none.
        public IList<MpSaveRow> MpSaves
        {
            get
            {
                var s = Actions as IMpSaves;
                IList<MpSaveRow> l = null;
                try { l = s != null ? s.MpSaves : null; }
                catch (Exception e) { Log.Warn("menu: saves list: " + e.Message); }
                return l ?? new List<MpSaveRow>();
            }
        }

        // ---- the menu ----

        void OnMenuShown(MenuSceneUI m)
        {
            menu = m;
            parts = null;
            agencies = null;
            canvas = null;
            kit = null;
            open = null;
            openKind = MenuScreen.None;
            failedClosed = false;
            test = null;
            pendingRefusal = null;
            reopenOwnTab = false;
            savesFromLoadDialog = false;
            buttonBefore = null;
            // the last menu's widgets are gone with it
            foreach (string id in tracked.Keys) owner.Map.Remove(id);
            if (tracked.Count > 0) owner.MapChanged();
            tracked.Clear();
            extra = null;
            fade.Forget();
            var go = new GameObject(Strip.ObjectPrefix + "menu screens");
            driver = go.AddComponent<MenuDriver>();
            driver.Owner = this;
            Button b = MultiplayerButton();
            if (b != null) Track("menu.multiplayer", b.transform);
        }

        Button MultiplayerButton()
        {
            try { return menu != null && BtnMult != null ? BtnMult.GetValue(menu) as Button : null; }
            catch (Exception) { return null; }
        }

        // The kit's parts for this menu, found at the first screen, and the canvas the screens draw on (the menu's
        // shared parts and the screens' layer of its one mod canvas, MenuSessions.Ui). A part a game update moved
        // fails closed (DD0): no screen, and the Multiplayer button says multiplayer needs an update.
        bool Ready()
        {
            if (failedClosed) return false;
            if (kit != null && canvas != null) return true;
            if (menu == null) { Log.Warn("menu: no main menu for the multiplayer screens"); return false; }
            parts = owner.Ui.Parts(false);
            extra = parts.Missing == null ? new MenuParts(parts) : null;
            if (parts.Missing == null && extra.Missing == null) canvas = owner.Ui.Layer(false, UiLayer.Screens);
            if (parts.Missing != null || extra == null || extra.Missing != null || canvas == null)
            {
                FailClosed();
                return false;
            }
            kit = new MenuKit(parts, this);
            return true;
        }

        void FailClosed()
        {
            failedClosed = true;
            Log.Error("menu: the multiplayer screens cannot be built in this game version (part missing: " +
                      (parts != null && parts.Missing != null ? parts.Missing
                       : extra != null && extra.Missing != null ? extra.Missing : "?") +
                      "); the Multiplayer button says so");
            FailClosedLook();
        }

        // The fixture's "entry update": the same look, with the button's own look kept to put back on "off".
        void FailClosedForShot()
        {
            Button b = MultiplayerButton();
            if (b != null && buttonBefore == null)
            {
                buttonBefore = new ButtonLook { Interactable = b.interactable };
                foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
                {
                    buttonBefore.Texts.Add(new KeyValuePair<TMP_Text, string>(t, t.text));
                    buttonBefore.Styles.Add(t.fontStyle);
                    buttonBefore.AutoSize.Add(t.enableAutoSizing);
                    buttonBefore.Sizes.Add(t.fontSize);
                }
            }
            FailClosedLook();
        }

        void RestoreButton()
        {
            ButtonLook l = buttonBefore;
            buttonBefore = null;
            Button b = MultiplayerButton();
            if (l == null || b == null) return;
            b.interactable = l.Interactable;
            for (int i = 0; i < l.Texts.Count; i++)
            {
                TMP_Text t = l.Texts[i].Key;
                if (t == null) continue;
                t.enableAutoSizing = l.AutoSize[i];
                t.fontStyle = l.Styles[i];
                t.fontSize = l.Sizes[i];
                t.text = l.Texts[i].Value;
            }
            Log.Info("dev screen-menu: the Multiplayer button is as it was");
        }

        void FailClosedLook()
        {
            Button b = MultiplayerButton();
            if (b == null) return;
            b.interactable = false;
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                MenuKit.Style(t, true, false);
                // the menu's own size where it fits, smaller where the line is wider than the button
                t.fontSizeMax = t.fontSize;
                t.fontSizeMin = t.fontSize * 0.4f;
                t.enableAutoSizing = true;
                MenuKit.SetLine(t, "menu.needsUpdate");
            }
        }

        // ---- the screens ----

        public void Open(MenuScreen s)
        {
            Close();
            if (s == MenuScreen.None || !Ready()) return;
            MenuControls m = Controls;
            if (!MenuFlow.MayOpen(s, m))
            {
                Log.Info("menu: the " + s + " screen is not offered by this session (" + Actions.Capabilities + ")");
                return;
            }
            IMenuScreen screen;
            switch (s)
            {
                case MenuScreen.Multiplayer: screen = new MultiplayerScreen(this); break;
                case MenuScreen.HostSetup: screen = new HostSetupScreen(this); break;
                case MenuScreen.Join: screen = new JoinScreen(this); break;
                case MenuScreen.MpSaves: screen = new MpSavesScreen(this); break;
                default: return;
            }
            bool built;
            try { built = screen.Build(); }
            catch (Exception e)
            {
                Log.Error("menu: building the " + s + " screen threw: " + e);
                built = false;
            }
            if (!built)
            {
                Log.Warn("menu: the " + s + " screen was not built; whatever was made of it is closed");
                // its own close path: the frame goes and the menu comes back, nothing half-built is left
                try { screen.Close(); }
                catch (Exception e) { Log.Error("menu: closing the unbuilt " + s + " screen threw: " + e.Message); }
                RestoreMenu();
                return;
            }
            open = screen;
            openKind = s;
            mapDirty = true;
            owner.MapChanged();   // a screen opening always writes the map
            Log.Info("menu: screen " + s + " open" + (test != null ? " (test adapter: " + test.Shot.Id + ")" : ""));
        }

        // Closes the open screen without going anywhere (a screen moving on, a load starting, a new menu).
        public void Close()
        {
            IMenuScreen s = open;
            open = null;
            openKind = MenuScreen.None;
            if (s != null) s.Close();
            mapDirty = true;
        }

        // The open screen was closed by the player (Back, Esc): its parent screen opens.
        public void Back(MenuScreen from)
        {
            if (open != null && openKind == from) { open = null; openKind = MenuScreen.None; }
            MenuScreen to = MenuFlow.Back(from, savesFromLoadDialog);
            if (from == MenuScreen.MpSaves && savesFromLoadDialog)
            {
                savesFromLoadDialog = false;
                OpenGameLoadDialog();
                return;
            }
            if (to == MenuScreen.None) Log.Info("menu: back at the main menu");
            Open(to);
        }

        public void OpenSaves(bool fromLoadDialog)
        {
            savesFromLoadDialog = fromLoadDialog;
            Open(MenuScreen.MpSaves);
        }

        public void OnJoinAccepted()
        {
            if (test != null) { Log.Info("test adapter: the join would connect now"); return; }
            Action a = JoinAccepted;
            if (a == null) return;   // the session connects; the host's lobby opens by itself
            try { a(); }
            catch (Exception e) { Log.Error("menu: a listener of the accepted join failed: " + e); }
        }

        // Host setup's pages were left without a load (Prev, Esc on the first page): host setup comes back. Wired to
        // HostSetupPages.Left by MenuSessions.
        public void OnSetupLeft()
        {
            if (menu == null || !Session.AtMenu) return;
            Open(MenuScreen.HostSetup);
        }

        // ---- the game's own load dialog ----

        // "My saves": the game's own load dialog, opened as the host setup (Task 14a). Host setup closes while it is
        // up (the dialog closes on Esc by itself, so no mod dialog may sit over it) and comes back on its "My saves"
        // tab when it closes without a load. Null = the dialog is up or the screen that asked was replaced; a key =
        // refused, nothing closed.
        public string HostOwnSave()
        {
            if (test != null) return test.HostFromSave(null);
            Func<string> openDialog = OpenLoadDialog;
            if (openDialog == null) return M4SessionAdapter.NotYet;
            Close();
            string why;
            try { why = openDialog(); }
            catch (Exception e)
            {
                Log.Error("menu: opening the load dialog failed: " + e.Message);
                why = "host.couldNotStart";
            }
            if (why != null)
            {
                // the screen that asked is gone: the new one shows the refusal
                pendingRefusal = why;
                reopenOwnTab = true;
                Open(MenuScreen.HostSetup);
                return null;
            }
            WatchDialogClose();
            return null;
        }

        string pendingRefusal;
        bool reopenOwnTab;

        internal string TakePendingRefusal()
        {
            string p = pendingRefusal;
            pendingRefusal = null;
            return p;
        }

        internal bool TakeReopenOwnTab()
        {
            bool r = reopenOwnTab;
            reopenOwnTab = false;
            return r;
        }

        void WatchDialogClose()
        {
            Component dialog = GameLoadDialog();
            var window = dialog as Game.UI.Windows.LoadSaveDialogWindow;
            if (window == null) return;
            UnityEngine.Events.UnityAction once = null;
            once = () =>
            {
                window.OnHide.RemoveListener(once);
                // OnHide follows a load that runs in the same frame: a frame later the intent tells the two apart
                if (driver != null) driver.StartCoroutine(AfterDialog());
            };
            window.OnHide.AddListener(once);
        }

        IEnumerator AfterDialog()
        {
            yield return null;
            if (owner.Intents.HostIntent || !Session.AtMenu || menu == null) yield break;
            Log.Info("menu: the load dialog closed without a load; host setup comes back");
            reopenOwnTab = true;
            Open(MenuScreen.HostSetup);
        }

        Component GameLoadDialog()
        {
            try { return menu != null && DialogOfMenu != null ? DialogOfMenu.GetValue(menu) as Component : null; }
            catch (Exception) { return null; }
        }

        // The game's own Load game, as its menu button opens it (the shadow, the dialog in Loading mode).
        public void OpenGameLoadDialog()
        {
            Close();
            if (menu == null || LoadClick == null) return;
            try { LoadClick.Invoke(menu, null); }
            catch (Exception e) { Log.Error("menu: opening the game's Load Game failed: " + e.Message); }
        }

        // ---- the menu behind host setup's page ----

        // Fades the main menu out as the game does when it opens its own new-game pages (its CanvasGroup through
        // the game's own Fade: alpha to 0 over half a second, no clicks while it fades, then inactive), keeping its
        // look first (MenuFade).
        public void FadeMenuOut()
        {
            CanvasGroup g = MenuGroup();
            if (g == null || fade.Faded) return;
            bool tweening = false;
            try { tweening = DG.Tweening.DOTween.IsTweening(g); } catch (Exception) { tweening = true; }
            fade.Out(new MenuFade.Look
            {
                Alpha = g.alpha, BlocksRaycasts = g.blocksRaycasts, Active = g.gameObject.activeSelf,
            }, tweening);
            try { Extensions.MyExtensions.Fade(g, false, FadeSeconds); }
            catch (Exception e) { Log.Warn("menu: the menu's fade failed: " + e.Message); }
            Log.Info("menu: the main menu fades out behind host setup");
        }

        // Host setup's page closed: the menu's look exactly as it was (its fade stopped first), unless the game's own
        // new-game pages took it over.
        public void RestoreMenu()
        {
            CanvasGroup g = MenuGroup();
            bool wasFaded = fade.Faded, pages = GamePagesOpen();
            MenuFade.Look? back = fade.Close(pages);
            if (g == null || back == null)
            {
                if (wasFaded && pages) Log.Info("menu: the main menu is left to the game's new-game pages");
                return;
            }
            try { DG.Tweening.ShortcutExtensions.DOKill(g, false); }
            catch (Exception e) { Log.Warn("menu: the menu's fade could not be stopped: " + e.Message); }
            MenuFade.Look l = back.Value;
            g.gameObject.SetActive(l.Active);
            g.alpha = l.Alpha;
            g.blocksRaycasts = l.BlocksRaycasts;
            Log.Info("menu: the main menu is back (alpha " + l.Alpha.ToString("0.00") + ", clicks " +
                     l.BlocksRaycasts + ")");
        }

        CanvasGroup MenuGroup()
        {
            return menu != null ? menu.GetComponent<CanvasGroup>() : null;
        }

        bool GamePagesOpen()
        {
            try
            {
                var panel = menu != null && PanelOfMenu != null ? PanelOfMenu.GetValue(menu) as CanvasGroup : null;
                return panel != null && panel.gameObject.activeInHierarchy;
            }
            catch (Exception) { return false; }
        }

        // ---- agencies ----

        // The agencies the new-game page offers (its own order), as wire ids, with the game's own names.
        public List<KeyValuePair<string, string>> GameAgencies()
        {
            if (agencies != null) return agencies;
            var list = new List<KeyValuePair<string, string>>();
            if (parts == null) return list;
            agencies = list;
            object corp = parts.Original(GameParts.PAgencies);
            var items = GameParts.Field<IList>(corp, "corporationItems");
            if (items == null) return list;
            foreach (object item in items)
            {
                var def = GameParts.Field<ScriptableObjectScripts.CompanyDefinition>(item, "companyDefinition");
                string wire = def != null ? AgencyWire.From(def.ID) : null;
                if (wire == null || def.IsWorldGovernment) continue;
                var tid = GameParts.Field<string>(item, "translationId");
                list.Add(new KeyValuePair<string, string>(wire, AgencyName(tid, def.ID)));
            }
            return list;
        }

        public string AgencyName(string wire)
        {
            foreach (KeyValuePair<string, string> a in GameAgencies()) if (a.Key == wire) return a.Value;
            return wire != null ? wire.ToUpperInvariant() : "";
        }

        static string AgencyName(string translationId, string id)
        {
            if (!string.IsNullOrEmpty(translationId))
            {
                string key = string.Format(NameFmt, translationId);
                try
                {
                    string n = Language.LEManager.Get(key);
                    if (!string.IsNullOrEmpty(n) && n != key) return n;
                }
                catch (Exception) { }
            }
            return id.ToUpperInvariant();
        }

        // ---- the widget map (test seams only) ----

        public void Track(string id, Transform t)
        {
            if (!UiMap.ValidId(id) || t == null) return;
            tracked[id] = t;
            mapDirty = true;
        }

        // Every live, shown widget of these screens into the session's map (MenuSessions.Map, which the session
        // root writes), a frame after a screen changed and once a second while one is open, since layouts settle a
        // frame late. Only what changed is registered or removed; other screens' widgets are left as they are.
        void RefreshMap()
        {
            UiMap map = owner.Map;
            if (!map.Writes) return;
            bool changed = false;
            var gone = new List<string>();
            foreach (KeyValuePair<string, Transform> kv in tracked)
            {
                var rt = kv.Value as RectTransform;
                UiRect old;
                bool had = map.TryGet(kv.Key, out old);
                // a widget of a menu that is fading out or leaving (a save loading) is no target: it leaves the map
                if (rt == null || !UiWidgets.Clickable(rt))
                {
                    if (had) { map.Remove(kv.Key); changed = true; }
                    if (rt == null) gone.Add(kv.Key);
                    continue;
                }
                Rect r = UiWidgets.ScreenRect(rt);
                var now = new UiRect(r.xMin, Screen.height - r.yMax, r.width, r.height);
                if (had && Same(old, now)) continue;
                if (map.Register(kv.Key, now)) changed = true;
            }
            foreach (string id in gone) tracked.Remove(id);
            if (changed) owner.MapChanged();
        }

        static bool Same(UiRect a, UiRect b)
        {
            return Mathf.Abs(a.X - b.X) < 0.5f && Mathf.Abs(a.Y - b.Y) < 0.5f && Mathf.Abs(a.W - b.W) < 0.5f &&
                   Mathf.Abs(a.H - b.H) < 0.5f;
        }

        // ---- every frame (MenuDriver) ----

        float nextDev;

        internal void Tick()
        {
            if (open != null) open.Tick();
            float now = Time.realtimeSinceStartup;
            if (mapDirty || open != null && now >= nextMap)
            {
                mapDirty = false;
                nextMap = now + 1f;
                RefreshMap();
            }
            if (DevTriggers.Every(ref nextDev, now) && TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath))
                DevPoll();
        }

        // "screen-menu <screen> <state>" (MenuShot): the screens over the test adapter in that state, for one
        // in-game run that screenshots every state. "screen-menu off" puts the real session back.
        void DevPoll()
        {
            string text = DevTriggers.Take("screen-menu");
            if (text == null) return;
            // a test state that opened the game's own Load Game closes it too, as its Cancel would
            bool gameDialog = test != null && test.Shot.Screen == "loaddialog";
            if (text.Trim() == "off")
            {
                Close();
                test = null;
                RestoreButton();
                if (gameDialog)
                    LoadDialogMpButton.CloseGameDialog(GameLoadDialog() as Game.UI.Windows.LoadSaveDialogWindow);
                Log.Info("dev screen-menu: the real session's screens");
                return;
            }
            MenuShot shot = MenuShot.Parse(text);
            if (shot == null)
            {
                Log.Warn("dev screen-menu: no such state (one of: " +
                         string.Join(", ", new List<string>(MenuShot.Ids()).ToArray()) + ")");
                return;
            }
            Close();
            test = new MenuTestAdapter(shot);
            Log.Info("dev screen-menu: " + shot.Id);
            if (shot.Screen == "entry")
            {
                // the button only looks failed: the screens still open, and "off" puts its look back
                if (shot.State == "update") FailClosedForShot();
                else RestoreButton();
                return;
            }
            if (shot.Screen == "loaddialog")
            {
                // the button's hook may not be installed in this build: the dialog gets it here the same way
                OpenGameLoadDialog();
                LoadDialogMpButton.Apply(GameLoadDialog() as Game.UI.Windows.LoadSaveDialogWindow, this);
                return;
            }
            if (shot.Opens == MenuScreen.MpSaves) OpenSaves(false);
            else Open(shot.Opens);
        }

        // The Load Game dialog's "Multiplayer saves" button (LoadDialogMpButton): shown only at this menu, in the
        // dialog's Loading mode, while the session offers the list and solo play.
        internal bool OffersSavesButton
        {
            get { return menu != null && Session.AtMenu && !owner.Intents.DialogArmed && Controls.LoadDialogButton; }
        }

        internal MenuKit KitForDialogButton()
        {
            return Ready() ? kit : null;
        }
    }

    // One of the menu's screens.
    public interface IMenuScreen
    {
        bool Build();     // false: a part was refused (the screen is not shown)
        void Close();     // closes it without going anywhere
        void Tick();      // every frame while open
    }

    // The menu screens' frame: their per-frame work and dev triggers, on an object of the menu's own scene (so it goes
    // with the menu).
    public sealed class MenuDriver : MonoBehaviour
    {
        public MenuScreens Owner;

        void Update()
        {
            if (Owner == null) return;
            try { Owner.Tick(); }
            catch (Exception e) { Log.Error("menu: the screens' frame failed: " + e.Message); }
        }
    }
}
