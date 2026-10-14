using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The lobby and the agency picker for one game process: owned by the early session (MenuSessions), bound to its
    // ISessionActions. It opens the lobby by itself once per session (Track U: over the game once the session runs;
    // the full track: at the menu when the lobby opens), remembers the picks the host refused (the picker marks them
    // and never offers them again), keeps both screens up to date, and, while the test seams are open, shows either
    // screen on a fixture session in a named state (dev triggers "screen-lobby <state>" and "screen-picker <state>"),
    // so every state can be screenshot.
    public sealed class LobbyScreens
    {
        const float TickSeconds = 1f;
        const int MaxAutoTries = 20;

        readonly ISessionActions live;
        readonly UiMap map;
        readonly Action mapChanged;      // MenuSessions.MapChanged: the map's owner writes its file
        readonly ScreenCanvas ui;        // the scene's parts and the screens' layer of its one mod canvas
        readonly PickerMemory memory = new PickerMemory();
        FixtureSession fixture;
        LobbyScreen lobby;
        AgencyPicker picker;
        Action<string> onPicked;
        Action onCancelled;
        bool shownThisSession;
        SessionPhase lastPhase = SessionPhase.Idle;
        int autoTries;
        float nextAuto, nextTick, nextDevPoll;
        int screenW, screenH;

        public LobbyScreens(ISessionActions actions, UiMap map, Action mapChanged, ScreenCanvas ui)
        {
            live = actions;
            this.map = map;
            this.mapChanged = mapChanged;
            this.ui = ui;
            if (live != null) live.Changed += LiveChanged;
        }

        public bool LobbyOpen { get { return lobby != null && lobby.Alive; } }
        public bool PickerOpen { get { return picker != null && picker.Alive; } }

        // ---- opening ----

        // The lobby on the real session; false when it cannot be built here (a part missing, logged).
        public bool OpenLobby() { return ShowLobby(live); }

        // The picker on the real session (a joiner after the join code, or the host's own agency before a new game's
        // pages). picked gets the agency once the claim is sent; cancelled when the player closes it without one.
        // A caller that passes null keeps the earlier caller's (the refusal dialog reopening the join's picker).
        public bool OpenPicker(Action<string> picked, Action cancelled)
        {
            if (picked != null) onPicked = picked;
            if (cancelled != null) onCancelled = cancelled;
            if (PickerOpen && picker.Actions == live) { picker.Rebind(onPicked, onCancelled); return true; }
            return ShowPicker(live);
        }

        // The lobby's own agency button: the picker over it, on the lobby's session.
        public void OpenPickerOver(ISessionActions actions)
        {
            if (actions == live) OpenPicker(onPicked, onCancelled);
            else ShowPicker(actions);
        }

        public void CloseAll()
        {
            if (PickerOpen) picker.Close();
            if (LobbyOpen) lobby.Close();
        }

        bool ShowLobby(ISessionActions actions)
        {
            if (LobbyOpen && lobby.Actions == actions) { lobby.Refresh(); return true; }
            if (LobbyOpen) lobby.Close();
            LobbyKit kit = NewKit();
            if (kit == null) return false;
            lobby = LobbyScreen.Open(kit, this, actions);
            if (lobby == null) { Log.Warn("ui: the lobby was not shown"); return false; }
            lobby.PublishMap();
            return true;
        }

        bool ShowPicker(ISessionActions actions)
        {
            if (PickerOpen && picker.Actions == actions) { picker.Refresh(); return true; }
            if (PickerOpen) picker.Close();
            LobbyKit kit = NewKit();
            if (kit == null) return false;
            picker = AgencyPicker.Open(kit, this, actions, memory, actions == live ? onPicked : null,
                                       actions == live ? onCancelled : null);
            if (picker == null) { Log.Warn("ui: the agency picker was not shown"); return false; }
            return true;
        }

        // A kit on this scene's shared parts and the screens' layer of its one mod canvas (MenuSessions.Ui, made again
        // after a scene change): the lobby and the picker sit over the HUD and under the session's dialogs.
        LobbyKit NewKit()
        {
            bool inGame = Object.FindObjectOfType<MenuSceneUI>() == null;
            GameParts parts = ui.Parts(inGame);
            if (parts.Missing != null) { Log.Warn("ui: lobby not built: part missing: " + parts.Missing); return null; }
            Canvas canvas = ui.Layer(inGame, UiLayer.Screens);
            if (canvas == null) return null;
            return new LobbyKit(parts, canvas, map, mapChanged);
        }

        // A second kit for a dialog over a screen (its own widgets in the map), on the same parts and canvas.
        public LobbyKit KitFor(LobbyKit like)
        {
            return like == null ? null : new LobbyKit(like.Parts, like.Canvas, map, mapChanged);
        }

        public void ScreenClosed(LobbyScreen s) { if (lobby == s) lobby = null; AfterClose(); }

        public void ScreenClosed(AgencyPicker p) { if (picker == p) picker = null; AfterClose(); }

        // A fixture lasts while one of its screens is open.
        void AfterClose()
        {
            if (fixture == null) return;
            bool used = (LobbyOpen && lobby.Actions == fixture) || (PickerOpen && picker.Actions == fixture);
            if (used) return;
            fixture.Changed -= FixtureChanged;
            fixture = null;
            memory.Forget();
        }

        // ---- the session's changes ----

        void LiveChanged() { Changed(live); AutoOpen(); }

        void FixtureChanged() { if (fixture != null) Changed(fixture); }

        void Changed(ISessionActions actions)
        {
            try
            {
                // A refused pick is only remembered (and marked when the picker shows): the refusal's own dialog, with
                // its way back to the picker, is the dialogs' screen (Task 21), so the two never open together.
                bool refused = memory.Observe(actions.View);
                if (refused) Log.Info("ui: the host refused the pick; it is marked in the picker");
                if (PickerOpen && picker.Actions == actions)
                {
                    if (refused) picker.Refused(); else picker.Refresh();
                }
                if (LobbyOpen && lobby.Actions == actions) lobby.Refresh();
            }
            catch (Exception e) { Log.Error("ui: the lobby's screens could not follow the session: " + e); }
        }

        // Once per session: over the running game (Track U) or at the menu's lobby (the full track). A session that
        // ended (Idle) may show it again next time; a try that found no parts is retried a few times.
        void AutoOpen()
        {
            SessionView v = live.View;
            if (v == null) return;
            if (LobbyReopen.Shut(lastPhase, v.Phase))
            {
                // the picker over it picked for that session, so it goes too
                if (PickerOpen && picker.Actions == live) picker.Close();
                if (LobbyOpen && lobby.Actions == live)
                {
                    Log.Info("ui: the lobby screen closed: the session is over");
                    lobby.Close();
                }
            }
            if (v.Phase == SessionPhase.Idle)
            {
                lastPhase = SessionPhase.Idle;
                shownThisSession = false;
                autoTries = 0;
                if (!PickerOpen) memory.Forget();
                return;
            }
            // back in the lobby after a Start that did not load: the screen may open again (LobbyReopen)
            if (LobbyReopen.Again(lastPhase, v.Phase)) { shownThisSession = false; autoTries = 0; }
            lastPhase = v.Phase;
            bool want = v.Phase == SessionPhase.InGame ||
                        v.Phase == SessionPhase.Lobby && CapabilityRules.Has(live.Capabilities, Capability.LobbyAtMenu);
            if (!want || shownThisSession || fixture != null) return;
            if (LobbyOpen) { shownThisSession = true; return; }
            if (Time.realtimeSinceStartup < nextAuto || autoTries >= MaxAutoTries) return;
            nextAuto = Time.realtimeSinceStartup + 1f;
            autoTries++;
            if (OpenLobby()) { shownThisSession = true; Log.Info("ui: lobby opened for the session"); }
            else if (autoTries == MaxAutoTries) Log.Warn("ui: the lobby could not be opened for this session");
        }

        // ---- every frame (MenuSessions.Update) ----

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if ((LobbyOpen || PickerOpen) && now >= nextTick)
            {
                nextTick = now + TickSeconds;
                if (LobbyOpen) lobby.Refresh();
                if (PickerOpen) picker.Refresh();
                if (Screen.width != screenW || Screen.height != screenH)
                {
                    screenW = Screen.width; screenH = Screen.height;
                    if (LobbyOpen) lobby.PublishMap();
                    if (PickerOpen) picker.PublishMap();
                }
            }
            if (DevTriggers.Every(ref nextDevPoll, now) && TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath))
                DevPoll();
        }

        // Test seams only. "screen-lobby <state>" and "screen-picker <state>" show the screen on a fixture session in
        // that state (LobbyFixtures), "live" on the real session, "close" closes both.
        void DevPoll()
        {
            string s = DevTriggers.Take("screen-lobby");
            if (s != null) Dev("lobby", s);
            s = DevTriggers.Take("screen-picker");
            if (s != null) Dev("picker", s);
        }

        void Dev(string screen, string state)
        {
            if (state == "close") { CloseAll(); Log.Info("dev screen-" + screen + ": closed"); return; }
            if (state == "live")
            {
                bool ok = screen == "lobby" ? OpenLobby() : OpenPicker(onPicked, onCancelled);
                Log.Info("dev screen-" + screen + " live: " + (ok ? "shown" : "not shown"));
                return;
            }
            FixtureSession f = screen == "lobby" ? LobbyFixtures.Lobby(state) : LobbyFixtures.Picker(state);
            if (f == null)
            {
                Log.Warn("dev screen-" + screen + ": no state '" + TextSanitiser.OneLine(state) + "'");
                return;
            }
            CloseAll();
            if (fixture != null) fixture.Changed -= FixtureChanged;
            memory.Forget();
            fixture = f;
            fixture.Changed += FixtureChanged;
            bool shown = screen == "lobby" ? ShowLobby(f) : ShowPicker(f);
            if (shown && screen == "lobby" && f.Overlay == "anyway") lobby.ConfirmAnyway();
            if (shown && screen == "lobby" && f.Overlay == "kick")
            {
                int id = lobby.FirstKickable();
                if (id >= 0) lobby.ConfirmKick(id);
            }
            Log.Info("dev screen-" + screen + " " + state + ": " + (shown ? "shown" : "not shown"));
        }
    }
}
