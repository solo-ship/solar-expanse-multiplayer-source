using System;

namespace SolarExpanseMultiplayer
{
    // The session root's Steam part (M9): a friend's invite or Join Game reaches this game as a checked lobby
    // (SteamLobby.JoinReady), and the session joins its host and waits in its lobby; "Leave this game to join?" is
    // the screens' question (LeaveGameFirstAsked) and its answer comes back through AnswerLeaveGameFirst.
    public sealed partial class M4SessionAdapter
    {
        // ---- Steam (M9): a friend's invite or Join Game ----

        // "Leave this game to join <name>?" (Steam's LeaveGameFirst): this game is in a game or a session when the
        // invite was accepted. The screens ask and answer with AnswerLeaveGameFirst.
        public event Action<LobbyJoinRequest> LeaveGameFirstAsked;
        // A Steam lobby join that failed before the session began, with its reason (the screens word each problem).
        public event Action<LobbyJoinProblem, string> SteamJoinFailed;
        // A friend's invite or Join Game reached this game (it is checked next; JoinReady joins it).
        public event Action<LobbyJoinRequest> SteamJoinRequested;

        bool steamWired, leavingForSteam;

        void WireSteam()
        {
            if (steamWired) return;
            SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
            if (net == null) return;
            steamWired = true;
            SteamLobby lobby = net.Lobby;
            lobby.JoinReady += t => Safely(() => SteamJoin(net, t));
            lobby.LeaveGameFirst += r => Safely(() => Raise(LeaveGameFirstAsked, r));
            lobby.JoinRequested += r => Safely(() => Raise(SteamJoinRequested, r));
            lobby.JoinFailed += (p, w) => Safely(() =>
            {
                Action<LobbyJoinProblem, string> a = SteamJoinFailed;
                if (a != null) a(p, w);
            });
        }

        static void Raise<T>(Action<T> a, T arg) { if (a != null) a(arg); }

        // The answer to "Leave this game to join?": yes leaves the session (or ends the host's) or the solo game, and
        // the join goes on once the game is back at the main menu; no stays.
        public void AnswerLeaveGameFirst(bool yes)
        {
            SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
            if (net == null) return;
            if (!yes) { net.Lobby.DeclineJoin(); return; }
            leavingForSteam = true;
            if (Session.Host != null) EndSession();
            else if (Session.Client != null && Session.Live) Leave();
            else Session.ToMenu();
            Log.Info("menu: leaving this game for the Steam invite");
        }

        void StepLeaveForSteam()
        {
            if (!leavingForSteam) return;
            SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
            if (net == null || net.Lobby.JoinState != LobbyJoinState.AskingToLeave) { leavingForSteam = false; return; }
            if (!net.Lobby.InGame && net.Lobby.ConfirmJoin()) leavingForSteam = false;
        }

        // A checked Steam lobby (SteamLobby.JoinReady): the session joins its host through Steam and waits in its
        // lobby, where the player claims an agency. A test profile's dev agency (LobbyDevJoin) is claimed and readied
        // by itself, since no screen may be there to do it.
        void SteamJoin(SteamNet net, LobbyJoinTarget t)
        {
            string preset = net.DevAgency;
            string why = Session.Host != null ? "this game is hosting"
                : !Session.AtMenu ? "not at the main menu" : null;
            if (why == null) why = StartJoin(SessionPlan.JoinSteamLobby(OwnName(), t.HostId), preset);
            if (why != null)
            {
                Log.Warn("menu: the Steam lobby join did not start: " + why);
                net.Lobby.CancelJoin("the session did not start");
                return;
            }
            Log.Info("menu: joining the Steam lobby of " + net.Short(t.HostId) +
                     (preset != null ? " as " + preset + " (dev agency)" : "; the lobby comes next"));
            Rebuild();
        }

        // The agency picker for a joiner whose host runs no lobby (a dev host): once per wait.
        bool pickerAsked;

        public bool TakePickerWanted()
        {
            SessionClient c = Session.Client;
            bool want = c != null && Session.Live && c.Lobby.Holding && c.Lobby.Follow.Legacy;
            if (!want) { pickerAsked = false; return false; }
            if (pickerAsked) return false;
            pickerAsked = true;
            return true;
        }
    }
}
