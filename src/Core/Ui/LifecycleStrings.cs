using System;

namespace SolarExpanseMultiplayer
{
    // B6: the session's lifecycle at the menu (closing a lobby, leaving one for an invite, the restart rule), a
    // running game's lobby, and the one progress view of a start or an automatic rejoin (JoinProgress). Added to the
    // table by UiStrings.Build. Plain English (Australian/British); a name goes in as an argument.
    public static class LifecycleStrings
    {
        public static void Add(Action<string, string> P)
        {
            // the host closes its lobby screen
            P("lobby.closeLobby.button", "Close lobby");
            P("lobby.closeLobby.title", "Close your lobby?");
            P("lobby.closeLobby.body", "Players in it will be sent back to the menu.");

            // a joiner in the lobby of a running game
            P("lobby.joinGame", "Join game");
            P("lobby.status.running", "The game is running: pick an agency to join.");
            P("lobby.status.runningFull", "The game is running, but no agency is free to join with.");

            // an invite while this game is only in a lobby (no game loaded)
            P("dialog.leaveLobby.title", "Leave your lobby?");
            P("dialog.leaveLobby.body", "Leave your lobby to join {0}?");
            P("dialog.leaveLobby.bodyUnknown", "Leave your lobby to join a friend's game?");
            P("dialog.leaveLobby.host", "You are hosting this lobby. Leaving closes it, and players in it go back " +
                "to the menu.");
            P("dialog.leaveLobby.player", "You leave the host's lobby.");

            // the progress view: a start, a late join, an automatic rejoin
            P("dialog.progress.title.start", "Starting the game");
            P("dialog.progress.title.join", "Joining the game");
            P("dialog.progress.title.rejoin", "Reconnecting");
            P("dialog.progress.hostLoading", "Starting the game... Players join once your world is up.");
            P("dialog.progress.playersJoining", "Players joining: {0} of {1}");
            P("dialog.progress.waitingForHost", "Starting: the host is loading the game...");
            P("dialog.progress.dropped", "Connection lost. Reconnecting...");
            P("dialog.progress.waiting", "Waiting for the host...");
            P("dialog.progress.receiving", "Receiving the host's world: {0}%");
            P("dialog.progress.receivingNoPct", "Receiving the host's world...");
            P("dialog.progress.hide", "Hide");
        }
    }
}
