namespace SolarExpanseMultiplayer
{
    // When the lobby screen may open again by itself: the phase came back to Lobby from Starting or Loading (the
    // host's world did not load, `lobby: the host's world did not load; back in the lobby`). It never reopens from
    // InGame or Ending, so a lobby the player closed over a running game stays closed.
    public static class LobbyReopen
    {
        public static bool Again(SessionPhase was, SessionPhase now)
        {
            return now == SessionPhase.Lobby && (was == SessionPhase.Starting || was == SessionPhase.Loading);
        }

        // When the lobby screen closes by itself: the session it showed is over (the view went idle), whether it was
        // a lobby, a join under way or a game. A screen left open on an ended session shows an empty lobby; why it
        // ended is the session dialogs' to say.
        public static bool Shut(SessionPhase was, SessionPhase now)
        {
            return now == SessionPhase.Idle && was != SessionPhase.Idle;
        }
    }
}
