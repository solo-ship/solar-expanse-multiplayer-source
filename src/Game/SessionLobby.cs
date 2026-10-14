using System;
using UnityEngine.SceneManagement;

namespace SolarExpanseMultiplayer
{
    // The host's session from the menus (M8 DD5, the beta): the lobby opens at the main menu with the listener and the
    // Steam lobby up before any world exists (OpenLobby); the world half (the clock, the commands, M4's join) comes
    // once the host's world has loaded after its Start (StartLobbyWorld); and the host can end it from any phase
    // (EndHostSession). The dev flags' host (StartFromPlan, after its save has loaded) is unchanged.
    public static partial class Session
    {
        // The host's lobby, from OpenLobby until the session ends; null otherwise.
        public static HostLobby Lobby { get; private set; }

        // The host's world is loading after its Start (the start pop-up's pause is dropped then, ClockHooks.Starting).
        public static bool LobbyStarting { get { return Lobby != null && Lobby.Starting; } }

        // Opens a lobby at the menu: the plan's listener (and Steam's relay and lobby), the room, nothing loaded. Null
        // = open; else a UiText key (the log has the detail). A session that ended at the menu is reset first; hosting
        // after a session whose world loaded needs a restart (SessionLifecycle).
        public static string OpenLobby(SessionPlan plan, LobbySetup setup)
        {
            string busy = HostBlock();
            if (busy != null) return busy;
            ResetMenuSession("a new lobby");
            if (started || Client != null) return "menu.restartFirst";
            if (plan == null || setup == null || setup.Options == null) return "host.couldNotStart";
            string why = plan.Role != SessionRole.Host ? "not a host's plan" : plan.Check() ?? setup.Options.Validate();
            if (why == null && (content == null || !content.Taken)) why = "the menu's content scan has not run yet";
            if (why == null && !GameHooks.IsInstalled(HookPlan.ClockSync)) why = "the clock patch is not installed";
            if (why != null)
            {
                Log.Error("session: no lobby: " + why);
                return "host.couldNotStart";
            }
            if (!BuildSupported()) return "host.couldNotStart";
            Plan = plan;
            var h = new SessionHost();
            try
            {
                h.Start(plan);
                Host = h;
                Lobby = new HostLobby(h, setup, content, plan.PlayerName);
            }
            catch (Exception e)
            {
                Host = null;
                Lobby = null;
                Plan = null;
                try { h.Stop(); } catch (Exception) { }
                Log.Error("session: the lobby could not open: " + e);
                return "host.couldNotStart";
            }
            refused = null;
            Log.Info("session: lobby open at the menu (" + (setup.NewGame ? "a new game" : "a save") + ") as " +
                     plan);
            return null;
        }

        // A world loaded while the lobby's start was on: the session's world half, then the start set goes in.
        static void StartLobbyWorld(bool success)
        {
            HostLobby lobby = Lobby;
            if (!success)
            {
                lobby.WorldFailed();
                return;
            }
            started = true;
            life.WorldLoaded();
            string why = null;
            if (!SpeedListOk()) why = refused;
            else if (!GameHooks.EnsureRuleHooks()) why = GameHooks.SessionHookRefusal;
            else if (!AsteroidKindsOk()) why = refused;
            if (why != null)
            {
                EndHostSession("the session could not start: " + why);
                return;
            }
            try
            {
                HostClock = new ClockHost(Host);
                MakeJoinParts();
                if (time.LoadCount == 0) time.OnSaveLoaded();
                days.Attach();
                StartCommands();
                Join = new JoinHost(Host, HostClock, content, time, print, days, pause, Commands, true);
                lobby.WorldLoaded(Join);
                Log.Info("session: the host's world is up; the session runs");
            }
            catch (Exception e)
            {
                Log.Error("session: the session's world half could not start: " + e);
                EndHostSession("the session could not start");
            }
        }

        // The menu came back while the lobby's world was on its way (a load that failed or was cancelled): everyone is
        // back in the lobby; or the host's game left a running session for the menu: the session ends there.
        static void LobbyAtMenu()
        {
            HostLobby lobby = Lobby;
            if (lobby == null) return;
            if (lobby.Starting && !lobby.HasWorld) { lobby.WorldFailed(); return; }
            if (lobby.HasWorld) EndHostSession("the host's game went back to the menu");
        }

        // The host ends its session (End session, a world that could not start, the menu after a running game): every
        // player gets the host's goodbye and goes back to the menu, the Steam lobby closes, and this game's session
        // hooks go quiet so the menu and a solo game afterwards are the game's own.
        public static void EndHostSession(string why)
        {
            SessionHost h = Host;
            if (h == null) return;
            Log.Info("session: the host ends the session (" + TextSanitiser.OneLine(why) + ")");
            if (Lobby != null) Lobby.End();
            try { h.Stop(); }
            catch (Exception e) { Log.Error("session: stopping the host failed: " + e.Message); }
            ClockSync.LocalChange = null; ClockSync.LocalPlanner = null; ClockSync.ClampLocal = null;
            ClockSync.PlannerGone = null;
            try { ClockSync.EndSession(); }
            catch (Exception e) { Log.Error("session: the clock's end failed: " + e.Message); }
            try { if (Commands != null) Commands.Steps.Loaded(); }
            catch (Exception e) { Log.Error("session: forgetting the step carry failed: " + e.Message); }
            HumanAgencies.Stop();
            ClockRules.ResetListed();
            Commands = null; Humans = null; Join = null; HostClock = null; Host = null; Lobby = null; Plan = null;
        }

        // A session that ended at the menu before any world of it loaded (a lobby left or closed, a join refused or
        // given up before its load): everything it made goes (the client and its link, its clock and commands, the
        // rules' and the clock's hooks, the plan), so this game hosts or joins again with no restart. Only a session
        // M4 itself ended (no live client, no host) is reset. False when there was nothing to reset or it may not be.
        internal static bool ResetMenuSession(string why)
        {
            if (!started && Client == null) return false;
            if (!life.MayReset(atMenu, Host != null, InSession, SessionInProfile)) return false;
            SessionClient c = Client;
            if (c != null)
            {
                if (glueReceived != null) { c.Received -= glueReceived; c.Joined -= glueJoined; c.Left -= glueLeft; }
                glueReceived = null; glueJoined = null; glueLeft = null;
                try { c.Stop(); }
                catch (Exception e) { Log.Error("session: stopping the old client failed: " + e.Message); }
            }
            try { if (ClientClock != null) ClientClock.Detach(); }
            catch (Exception e) { Log.Error("session: detaching the clock failed: " + e.Message); }
            ClockSync.LocalChange = null; ClockSync.LocalPlanner = null; ClockSync.ClampLocal = null;
            ClockSync.PlannerGone = null;
            try { ClockSync.EndSession(); }
            catch (Exception e) { Log.Error("session: the clock's end failed: " + e.Message); }
            try { if (Commands != null) Commands.Steps.Loaded(); }
            catch (Exception e) { Log.Error("session: forgetting the step carry failed: " + e.Message); }
            HumanAgencies.Stop();
            ClockRules.ResetListed();
            Client = null; ClientClock = null; Commands = null; Humans = null; Feed = null; Plan = null;
            Join = null; HostClock = null;
            refused = null;
            started = false;
            // a joined Steam lobby still open from it is left too (its own watch would leave it a little later)
            SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
            try { if (net != null) net.Lobby.CancelJoin("the last session was reset"); }
            catch (Exception e) { Log.Error("session: leaving the old Steam lobby failed: " + e.Message); }
            if (Early != null) Early.Actions.SessionReset();
            Log.Info("session: the session that ended at the menu is reset (" + TextSanitiser.OneLine(why) +
                     "); this game may host or join again");
            return true;
        }

        // Back to the main menu from a running game (the host's End session outside the pause screen's own exit).
        internal static void ToMenu()
        {
            try { if (SceneManager.GetActiveScene().name != "Menu") SceneManager.LoadScene("Menu"); }
            catch (Exception e) { Log.Error("session: going back to the menu failed: " + e.Message); }
        }
    }
}
