using System;
using System.Collections.Generic;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The in-game screens of a session (M8 Task 20): the strip under the speed buttons (SpeedHud) and the PLAYERS
    // screen (PlayersScreen) the pause menu's PLAYERS button opens. One instance, owned by MenuSessions (the early
    // track's session object), with an explicit reset on every world change; it holds no static state. Both screens
    // read only ISessionActions (its View, Capabilities and Notices): the real session, or, behind the test seams, a
    // fixture in a named state (dev triggers "screen-hud <state>" and "screen-players <state>"), so a state the
    // early session cannot reach is still put on screen for its screenshot. Every clickable widget goes into the
    // session's one shared UiMap, MenuSessions.Map (it holds widgets only on a test profile).
    public sealed class HudScreens
    {
        const float RefreshEverySeconds = 0.25f, PlayersEverySeconds = 1f, RetryPartsSeconds = 2f;
        const int MaxPartsTries = 3;

        readonly MenuSessions owner;
        readonly UiMap map;
        readonly HashSet<string> problems = new HashSet<string>();
        readonly Dictionary<string, RectTransform> pendingMap = new Dictionary<string, RectTransform>();

        GameParts parts;
        Canvas hudCanvas, canvas;   // the shared canvas's layers: the HUD's (the strip), the screens' (PLAYERS)
        SpeedHud hud;
        PlayersScreen players;
        float[] scales;
        HudFixtureSession hudFixture;
        float nextRefresh, nextPlayers, nextPartsTry, nextDevPoll;
        int partsTries;
        bool dirty;
        AgencyInfo worldAgencies;
        // the frame's four steps, made once (Update runs every frame and allocates nothing for them)
        readonly Action devPoll, updateHud, updatePlayers, writeMap;

        public HudScreens(MenuSessions owner)
        {
            this.owner = owner;
            map = owner.Map;
            devPoll = DevPoll;
            updateHud = UpdateHud;
            updatePlayers = UpdatePlayers;
            writeMap = WriteMap;
            owner.PlayersClicked += () => Safely("the PLAYERS screen", () => OpenPlayers(null));
            owner.Actions.Changed += () => dirty = true;
        }

        ISessionActions HudSource { get { return hudFixture != null ? (ISessionActions)hudFixture : owner.Actions; } }

        // ---- the world ----

        // A save loaded or the menu came back: every object of the old world is gone with its scene; nothing of it
        // is kept (the parts, the canvas, the screens, the speeds, the agencies' names, the fixtures). The shared
        // canvas itself is its owner's (MenuSessions.Ui).
        public void OnWorldChanged()
        {
            Safely("the reset", () =>
            {
                if (players != null) players.Close();
                if (hud != null) hud.Destroy();
            });
            players = null;
            hud = null;
            hudCanvas = null;
            canvas = null;
            parts = null;
            scales = null;
            worldAgencies = null;
            hudFixture = null;
            pendingMap.Clear();
            if (map.RemovePrefix("hud.") + map.RemovePrefix("players.") > 0) owner.MapChanged();
            nextPartsTry = 0f;
            partsTries = 0;
        }

        // ---- every frame (MenuSessions.Update) ----

        public void Update()
        {
            Safely("the dev triggers", devPoll);
            Safely("the speed strip", updateHud);
            Safely("the PLAYERS screen", updatePlayers);
            Safely("the widget map", writeMap);
            dirty = false;
        }

        void UpdateHud()
        {
            SessionView v = HudSource.View;
            bool wanted = v != null && v.Speed != null && (hudFixture != null || v.Phase == SessionPhase.InGame);
            if (!wanted)
            {
                if (hud != null) Hide();
                return;
            }
            if (!Ready()) return;
            if (hud == null)
            {
                hud = SpeedHud.Build(parts, hudCanvas, CarryOn);
                if (hud == null) { Problem("the speed strip could not be built (the log says which part)"); return; }
                hud.Show(false);
            }
            hud.YieldToTooltip();
            float now = Time.realtimeSinceStartup;
            if (!dirty && now < nextRefresh) return;
            nextRefresh = now + RefreshEverySeconds;
            bool hover = (hudFixture != null && hudFixture.Hover) || hud.Hovered(Input.mousePosition);
            HudModel m = HudModel.From(v, HudSource.Capabilities, hover, NetClock.NowMs, Scales());
            // the pause menu and a mod dialog cover the game: the strip is not drawn over them
            if (!HudModel.Drawn(m, PauseOpen(), ModalRegistry.AnyOpen)) { Hide(); return; }
            hud.Show(true);
            hud.Apply(m);
            if (hud.CarryOn != null && hud.CarryOn.gameObject.activeInHierarchy)
                Register("hud.carryon", (RectTransform)hud.CarryOn.transform);
            else Unregister("hud.carryon");
        }

        void Hide()
        {
            if (hud != null) hud.Show(false);
            Unregister("hud.carryon");
        }

        // Asked again at the click: the view may have changed since the button was drawn.
        void CarryOn(int player)
        {
            ISessionActions s = HudSource;
            if (!ActionRules.MayCarryOn(s.View, s.Capabilities, player))
            {
                Log.Warn("ui: carry on without " + player + " refused: the session no longer allows it");
                dirty = true;
                return;
            }
            s.CarryOnWithout(player);
            dirty = true;
        }

        void UpdatePlayers()
        {
            if (players == null || !players.IsOpen) return;
            float now = Time.realtimeSinceStartup;
            if (!dirty && now < nextPlayers) return;
            nextPlayers = now + PlayersEverySeconds;
            players.Refresh();
        }

        // The pause menu's PLAYERS button (MenuSessions.PlayersClicked), or a dev trigger with a fixture.
        void OpenPlayers(HudFixtureSession fixture)
        {
            ISessionActions s = fixture != null ? (ISessionActions)fixture : owner.Actions;
            if (!CapabilityRules.Has(s.Capabilities, Capability.Players))
            {
                Log.Info("ui: the PLAYERS screen is not offered by this session");
                return;
            }
            if (!Ready()) { Problem("the PLAYERS screen has no parts to build with"); return; }
            if (players == null)
            {
                players = new PlayersScreen(parts, canvas, Agencies(), Register, AskEnd);
                players.Closed += () => { if (map.RemovePrefix("players.") > 0) owner.MapChanged(); };
            }
            if (!players.Open(s))
            {
                Problem("the PLAYERS screen could not be built (the log says which part)");
                return;
            }
            if (fixture != null && fixture.AskRemove >= 0) players.AskRemoveFor(fixture.AskRemove);
            if (fixture != null && fixture.AskEnd) players.AskEnd();
            nextPlayers = Time.realtimeSinceStartup + PlayersEverySeconds;
        }

        // The PLAYERS screen's End session: the session dialogs' one End question (Task 21's, drawn over the PLAYERS
        // screen), asked on the session the screen shows. PlayersScreen checks again at the click before it asks.
        bool AskEnd(ISessionActions s) { return owner.Dialogs.AskEndSession(s); }

        // ---- the parts, the canvas, the game's speeds ----

        // The game scene's shared parts and two layers of its one mod canvas (MenuSessions.Ui): the strip with the
        // notices under every screen, PLAYERS with the lobby under the session's dialogs. A part a game update moved
        // makes the screens refuse (nothing half-built is shown). Tried again a few times, a few seconds apart, in
        // case the scene was not ready yet, then left alone for this world.
        bool Ready()
        {
            if (parts != null && parts.Missing == null && hudCanvas != null && canvas != null) return true;
            float now = Time.realtimeSinceStartup;
            if (now < nextPartsTry || partsTries >= MaxPartsTries) return false;
            partsTries++;
            nextPartsTry = now + RetryPartsSeconds;
            parts = owner.Ui.Parts(true);
            if (parts.Missing != null) { Problem("a part is missing: " + parts.Missing); return false; }
            hudCanvas = owner.Ui.Layer(true, UiLayer.Hud);
            canvas = owner.Ui.Layer(true, UiLayer.Screens);
            return hudCanvas != null && canvas != null;
        }

        AgencyInfo Agencies()
        {
            if (worldAgencies == null) worldAgencies = new AgencyInfo();
            return worldAgencies;
        }

        // The six speeds of this world's content (the Economic asset; DD10), as the speed panel's own refresh reads
        // them; the default six when they cannot be read.
        float[] Scales()
        {
            if (scales != null) return scales;
            try
            {
                var eco = MonoBehaviourSingleton<Manager.GameManager>.Instance.Economic;
                float b = eco.baseTimeScale;
                scales = new[] { 0f, b * eco.multiTime2, b * eco.multiTime3, b * eco.multiTime4, b * eco.multiTime5,
                                 b * eco.multiTime6 };
            }
            catch (Exception e)
            {
                Problem("the game's speeds could not be read (" + e.Message + "); the default six are used");
                scales = HudModel.DefaultScales;
            }
            return scales;
        }

        bool PauseOpen()
        {
            var pause = parts != null ? parts.Original(GameParts.PPause) as Component : null;
            return pause != null && pause.gameObject.activeInHierarchy;
        }

        // ---- the widget map (test profiles only: UiMap writes nothing otherwise) ----

        void Register(string id, RectTransform rt)
        {
            if (!map.Writes) return;
            pendingMap[id] = rt;
        }

        void Unregister(string id)
        {
            if (map.Remove(id)) owner.MapChanged();
            pendingMap.Remove(id);
        }

        // A frame after a widget was placed (a layout group places it at the end of the frame), its rectangle in the
        // window's client pixels, top-left origin, goes into the session's one shared map (MenuSessions.Map); the map's
        // file is written by its owner (MapChanged), never here.
        void WriteMap()
        {
            if (!map.Writes || pendingMap.Count == 0) return;
            foreach (KeyValuePair<string, RectTransform> kv in pendingMap)
            {
                if (kv.Value == null || !kv.Value.gameObject.activeInHierarchy) { map.Remove(kv.Key); continue; }
                Rect r = UiWidgets.ScreenRect(kv.Value);
                map.Register(kv.Key, new UiRect(r.xMin, Screen.height - r.yMax, r.width, r.height));
            }
            pendingMap.Clear();
            owner.MapChanged();
        }

        // ---- the dev triggers (test seams only) ----

        // "screen-hud <state>": the strip shows that fixture's state (ScreenFixtures.HudStates), or the real session
        // again with "live". "screen-players <state>": the PLAYERS screen opens on that fixture's state
        // (ScreenFixtures.PlayersStates), or on the real session with "live"; "close" closes it.
        void DevPoll()
        {
            if (!DevTriggers.Every(ref nextDevPoll, Time.realtimeSinceStartup)) return;
            if (!TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath)) return;
            string hudState = DevTriggers.Take("screen-hud");
            if (hudState != null)
            {
                if (hudState == "live") hudFixture = null;
                else
                {
                    HudFixtureSession f = ScreenFixtures.Hud(hudState, NetClock.NowMs, Boot.Profile,
                                                                MenuSessions.PlayerPath);
                    if (f == null) Log.Warn("ui: screen-hud: no state " + hudState);
                    else { hudFixture = f; f.Changed += () => dirty = true; }
                }
                Log.Info("ui: screen hud " + hudState);
                dirty = true;
            }
            string playersState = DevTriggers.Take("screen-players");
            if (playersState != null)
            {
                Log.Info("ui: screen players " + playersState);
                if (playersState == "close") { if (players != null) players.Close(); }
                else if (playersState == "live") OpenPlayers(null);
                else
                {
                    HudFixtureSession f = ScreenFixtures.Players(playersState, NetClock.NowMs, Boot.Profile,
                                                                        MenuSessions.PlayerPath);
                    if (f == null) Log.Warn("ui: screen-players: no state " + playersState);
                    else { f.Changed += () => dirty = true; OpenPlayers(f); }
                }
            }
        }

        // ---- failures ----

        void Problem(string what)
        {
            if (problems.Count < 20 && problems.Add(what)) Log.Warn("ui: " + what);
        }

        // Never throws into the session's frame; each distinct failure is logged once.
        void Safely(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                string line = what + " failed: " + e.GetType().Name + ": " + e.Message;
                if (problems.Count < 20 && problems.Add(line)) Log.Error("ui: " + line + "\n" + e.StackTrace);
            }
        }
    }
}
