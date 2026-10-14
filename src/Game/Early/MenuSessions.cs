using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // M8's early track (Track U): the session from the menus, over M4's session. One instance, owned by Session (the
    // session root until the full track's SessionRoot replaces both), made at the first main menu on an early-track
    // profile, with its own reset on every load. It holds the host intent ("host once loaded"), the menu and pause
    // screen events the screens listen to, the session actions (Actions, ISessionActions over M4) and, while the test
    // seams are open, the dev triggers that drive those actions without the screens.
    public sealed class MenuSessions
    {
        // The player-path profile: a test profile that stands in for the real path, where no test seam acts. Parsed
        // once at boot (DevFlags, only together with a valid -semp-profile).
        public static bool PlayerPath { get { return Boot.Dev != null && Boot.Dev.PlayerPath; } }

        public readonly M4SessionAdapter Actions;
        public readonly MenuScreens Screens;      // the menu's multiplayer screens (Task 18)
        public readonly LobbyScreens Lobby;       // the lobby and the agency picker (Task 19)
        public readonly HudScreens Hud;           // the speed strip and the PLAYERS screen (Task 20)
        public readonly DialogScreens Dialogs;    // the session dialogs and notices (Task 21)
        public readonly InviteScreens Invites;    // the Steam invitation questions (B3)
        public readonly SaveGlue Saves;           // the Multiplayer saves folder, save copies, solo play, the pause exits (Task 15)

        // The screens' one mod canvas per scene, in layers (the HUD and notices, the screens, the dialogs on top), and
        // the scene's one parts set: every screen draws on it, so their order is the layers' and never the order they
        // happened to be built in.
        public readonly ScreenCanvas Ui = new ScreenCanvas();

        // Every screen's widgets for the UI test tools (ui-map.json; writable only with a test or player-path
        // profile, UiMap.For). Screens register and remove their own; this object writes the file (MapChanged).
        public readonly UiMap Map = UiMap.For(Boot.Profile, PlayerPath);
        readonly MapSchedule mapSaves = new MapSchedule();

        // Each main menu's Multiplayer button is ready (MenuEntry); the Multiplayer screen is built on it.
        public event Action<MenuSceneUI> MenuShown;
        public event Action MultiplayerClicked;   // the menu's Multiplayer button
        public event Action PlayersClicked;       // the pause screen's PLAYERS button

        // The host setup's armed states and "host once loaded" (DD26): the setup hooks feed its triggers, and Session
        // starts the host's session at the load once Intents.HostIntent is armed. Never a static, so nothing outlives
        // this object.
        public readonly SetupIntents Intents = new SetupIntents();
        SessionPlan hostPlan;   // the plan the armed intent starts (PrepareHost), made before the load begins

        // The setup hooks (DD6, DD7): their decisions over Intents, the game's new-game pages and its load dialog.
        public readonly SetupFlow Setup;
        public readonly HostSetupPages Pages;
        public readonly HostSetupDialog Dialog;

        // The game's speed panel showing the session's speed (SpeedPanelHooks); the game's own pausing sources on a
        // dev trigger (their pauses are GamePauses').
        public readonly SpeedPanel Panel = new SpeedPanel();
        public readonly PauseSourcesDev PauseDev = new PauseSourcesDev();

        readonly HashSet<string> pauseProblems = new HashSet<string>();
        float nextDevPoll;

        public MenuSessions()
        {
            Actions = new M4SessionAdapter(this);
            Setup = new SetupFlow(Intents);
            // the host's start pop-up opens on its first frame, before its session is live: its pause is dropped too
            ClockSync.Hooks.Starting = () => Intents.HostIntent || Session.LobbyStarting;
            Pages = new HostSetupPages(this);
            Dialog = new HostSetupDialog(this);
            Screens = new MenuScreens(this);
            Lobby = new LobbyScreens(Actions, Map, MapChanged, Ui);
            Hud = new HudScreens(this);
            Dialogs = new DialogScreens(this);
            Invites = new InviteScreens(this);
            Saves = new SaveGlue(this);
            Wire();
            Log.Info("session: early track on; test seams " +
                     (TestSeams.Open(Boot.Profile, PlayerPath) ? "open" : "closed"));
        }

        // ---- the screens' hand-overs (each screen knows only its own; they meet here) ----

        void Wire()
        {
            // host setup: the game's new-game pages left without a load bring host setup back; "My saves" opens the
            // game's own load dialog as the setup
            Pages.Left += Screens.OnSetupLeft;
            // "My saves": the game's own load dialog; the save picked there is kept and the lobby opens with it
            Screens.OpenLoadDialog = () => Dialog.Open(true);
            // the lobby at the menu: the menu's own screens make way for the lobby screen (LobbyScreens opens it)
            Actions.LobbyOpened += () => Screens.Close();
            // join by code: the session connects and the host's lobby opens once it holds this player (LobbyScreens);
            // the pick is made there, from the host's roster. A refused claim's dialog reopens the picker.
            Dialogs.PickerWanted += () => Lobby.OpenPicker(null, null);
            // the session over dialog's "Multiplayer saves" (behind MpSaves, as the saves list itself is)
            Dialogs.MpSavesWanted += () => Screens.OpenSaves(false);
        }

        // ---- the host intent ----

        // The host's plan, made before a hosting load begins: null = ready; else a UiText key. The plan's bind: the
        // test seams' -semp-bind (loopback by default), or this PC's local-network address (BindChoice, the adapter
        // with a gateway first).
        public string PrepareHost()
        {
            hostPlan = null;
            IPAddress bind;
            int port;
            string why = HostBind(out bind, out port);
            if (why != null) return why;
            SessionPlan plan = SessionPlan.HostFromMenu(M4SessionAdapter.OwnName(), bind);
            plan.Port = port;
            string bad = plan.Check();
            if (bad != null) { Log.Error("menu: the host's plan was refused: " + bad); return "host.couldNotStart"; }
            hostPlan = plan;
            return null;
        }

        // The plan PrepareHost made, once (the lobby's host: Session.OpenLobby).
        public SessionPlan TakeHostPlan()
        {
            SessionPlan plan = hostPlan;
            hostPlan = null;
            return plan;
        }

        // At a save's load (Session): the plan to start when the intent is armed, else null. The intent is spent
        // either way; a load the game reports as failed starts nothing.
        public SessionPlan TakeHostIntent(bool success)
        {
            if (!Intents.HostIntent) return null;
            SessionPlan plan = hostPlan;
            hostPlan = null;
            Intents.SessionStarted();
            if (!success || plan == null)
            {
                Log.Info("menu: host intent spent without a session (" +
                         (success ? "no plan" : "the load failed") + ")");
                return null;
            }
            Log.Info("menu: host intent spent: the session starts");
            return plan;
        }

        static string HostBind(out IPAddress bind, out int port)
        {
            bind = null;
            port = 0;
            if (TestSeams.Open(Boot.Profile, PlayerPath) && Boot.Dev != null)
            {
                bind = Boot.Dev.Bind;
                port = Boot.Dev.Port;
                return null;
            }
            List<BindOption> options = BindChoice.From(Adapters());
            if (options.Count == 0) return "host.noLocalNetwork";
            bind = options[0].Address;
            return null;
        }

        // This PC's adapters as BindChoice reads them; one that cannot be read is left out.
        static List<AdapterInfo> Adapters()
        {
            var list = new List<AdapterInfo>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception e) { Log.Warn("menu: the network adapters could not be read: " + e.Message); return list; }
            foreach (NetworkInterface n in all)
            {
                try
                {
                    var a = new AdapterInfo
                    {
                        Name = n.Name, Description = n.Description, Type = (int)n.NetworkInterfaceType,
                        Up = n.OperationalStatus == OperationalStatus.Up, V4 = new List<IPAddress>(),
                    };
                    IPInterfaceProperties p = n.GetIPProperties();
                    foreach (GatewayIPAddressInformation g in p.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                            a.HasGateway = true;
                    foreach (UnicastIPAddressInformation u in p.UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork) a.V4.Add(u.Address);
                    list.Add(a);
                }
                catch (Exception) { }
            }
            return list;
        }

        // ---- the widget map ----

        // The one entry point for every screen (menu, lobby, HUD, dialogs) after it registered, moved or removed
        // widgets in Map, or opened: the file is written within a second. Cheap, never throws, and nothing at all
        // where the map may not write (a normal install). Main thread only, as every screen is.
        public void MapChanged() { if (Map.Writes) mapSaves.Changed(); }

        // A write that could not happen (no data folder yet, the file system refused) is tried again a second later.
        void SaveMapIfDue()
        {
            if (!mapSaves.Due(Time.realtimeSinceStartup)) return;
            if (Boot.DataDir == null) { mapSaves.Failed(); return; }
            Map.SetWindow(Screen.width, Screen.height);
            try { Map.Save(Path.Combine(Boot.DataDir, UiMap.FileName)); }
            catch (Exception e)
            {
                mapSaves.Failed();
                Log.Warn("ui: the widget map could not be written (tried again): " + e.Message);
            }
        }

        // ---- the game's events ----

        // Every main menu ready (Session.OnMenuReady), before MenuEntry: a host load that brought the menu back
        // instead of a world failed or was cancelled.
        public void OnMenuReady()
        {
            if (Intents.HostIntent) Log.Info("menu: host intent cleared (the menu came back)");
            Intents.MenuShown();
            Setup.OnMenuShown();
            Pages.OnMenuReady();
            Saves.OnMenuReady();
            hostPlan = null;
            NewWorld(false);
            Actions.OnWorldChanged();
            Hud.OnWorldChanged();
            Dialogs.OnScene(false);
            Invites.OnScene(false);
            Ui.Forget();   // last: the screens let go of the old scene's objects first
        }

        // Every save loaded, after Session took the host intent.
        public void OnSaveLoaded()
        {
            Saves.OnSaveLoaded();
            NewWorld(true);
            Actions.OnWorldChanged();
            Hud.OnWorldChanged();
            Dialogs.OnScene(true);
            Invites.OnScene(true);
            Ui.Forget();
        }

        // Each in its own try: a throw in one never costs the other or the session view.
        void NewWorld(bool loaded)
        {
            try { Panel.NewWorld(); }
            catch (Exception e) { Log.Error("menu: the speed panel's reset failed: " + e.Message); }
        }

        public void OnMenuShown(MenuSceneUI menu) { Raise(MenuShown, menu, "menu shown"); }

        public void OnMultiplayerClicked()
        {
            Log.Info("ui: click menu.multiplayer");
            // a session that ended at the menu (a lobby left, a join refused) is dropped here, so hosting or joining
            // from the screen needs no restart
            try { Session.ResetMenuSession("the Multiplayer screen opened"); }
            catch (Exception e) { Log.Error("menu: the last session could not be reset: " + e.Message); }
            if (MultiplayerClicked == null) Log.Info("menu: the Multiplayer screen is not in this build yet");
            else Raise(MultiplayerClicked, "Multiplayer");
        }

        public void OnPlayersClicked()
        {
            Log.Info("ui: click pause.players");
            if (PlayersClicked == null) Actions.LogView("players");
            else Raise(PlayersClicked, "PLAYERS");
        }

        // One line per distinct problem of the pause screen's hooks (they run on every open and close).
        public void PauseFailed(string what)
        {
            if (pauseProblems.Count < 20 && pauseProblems.Add(what)) Log.Error("pause screen: " + what);
        }

        static void Raise(Action a, string what)
        {
            try { a(); }
            catch (Exception e) { Log.Error("menu: a listener of " + what + " failed: " + e); }
        }

        static void Raise(Action<MenuSceneUI> a, MenuSceneUI menu, string what)
        {
            if (a == null) return;
            try { a(menu); }
            catch (Exception e) { Log.Error("menu: a listener of " + what + " failed: " + e); }
        }

        // ---- every frame (Session.Update) ----

        public void Update()
        {
            // first, each in its own try: a throw in the session view never costs the speed panel or the dev trigger
            try { Panel.Update(); }
            catch (Exception e) { PauseFailed("the speed panel failed: " + e.Message); }
            try { PauseDev.Update(); }
            catch (Exception e) { PauseFailed("the nopause dev trigger failed: " + e.Message); }
            Actions.Update();
            // a joiner whose host runs no lobby picks its agency once the wait is over
            if (Actions.TakePickerWanted()) Lobby.OpenPicker(null, null);
            Saves.Update();
            Pages.Update();
            Lobby.Update();
            Hud.Update();
            Dialogs.Update();
            Invites.Update();
            SaveMapIfDue();
            if (DevTriggers.Every(ref nextDevPoll, Time.realtimeSinceStartup) &&
                TestSeams.Open(Boot.Profile, PlayerPath))
                DevPoll();
        }

        // Test seams only: the session actions without the screens. "menu-host <save>" opens a lobby for that save,
        // "menu-join <profile>" joins with that test host's join-code.txt (into its lobby), "menu-agency
        // <id>" picks the agency (in a lobby: claims it), "menu-view" logs the session view; DevLobby's for the rest.
        void DevPoll()
        {
            string save = DevTriggers.Take("menu-host");
            if (save != null) Answer("menu-host", Actions.HostFromSave(save));
            string host = DevTriggers.Take("menu-join");
            if (host != null)
            {
                if (!LaunchArgs.SafeName(host)) Log.Warn("dev menu-join: not a profile name");
                else
                {
                    string file = Path.Combine(ModData.Dir(Boot.ModDir, host), "join-code.txt");
                    string code = Session.ReadJoinFile(file);
                    Answer("menu-join", code == null ? "join.noCode" : Actions.Join(code, "Player " + Boot.Profile));
                }
            }
            string agency = DevTriggers.Take("menu-agency");
            if (agency != null) Answer("menu-agency", Actions.PickAgency(agency));
            if (DevTriggers.Take("menu-view") != null) Actions.LogView("dev");
            DevLobby();
            Pages.TakeDevTriggers();
            Dialog.TakeDevTriggers();
        }

        // The lobby without the screens (test seams): "menu-newgame [<agency>]" hosts a new game with the game's own
        // defaults (no pages), "menu-ready [off]" readies a joiner, "menu-start [anyway]" starts, "menu-kick <id>",
        // "menu-leave" (a joiner) and "menu-end" (the host).
        void DevLobby()
        {
            string fresh = DevTriggers.Take("menu-newgame");
            if (fresh != null)
                Answer("menu-newgame", Actions.HostDefaultNewGame(fresh.Trim().Length > 0 ? fresh.Trim() : null));
            string ready = DevTriggers.Take("menu-ready");
            if (ready != null) { Actions.SetReady(ready.Trim() != "off"); Log.Info("dev menu-ready: done"); }
            string start = DevTriggers.Take("menu-start");
            if (start != null) { Actions.Start(start.Trim() == "anyway"); Log.Info("dev menu-start: asked"); }
            string kick = DevTriggers.Take("menu-kick");
            int id;
            if (kick != null && int.TryParse(kick.Trim(), out id) && id > 0)
            {
                Actions.Kick(id);
                Log.Info("dev menu-kick: done");
            }
            if (DevTriggers.Take("menu-leave") != null) { Actions.Leave(); Log.Info("dev menu-leave: done"); }
            if (DevTriggers.Take("menu-end") != null) { Actions.EndSession(); Log.Info("dev menu-end: done"); }
        }

        static void Answer(string what, string key)
        {
            if (key == null) Log.Info("dev " + what + ": ok");
            else Log.Warn("dev " + what + ": refused (" + key + "): " + UiText.Get(key));
        }
    }
}
