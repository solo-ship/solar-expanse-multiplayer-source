namespace SolarExpanseMultiplayer
{
    public enum SessionRole { Host = 1, Client = 2 }

    public enum SessionPhase { Idle = 0, Lobby = 1, Starting = 2, Loading = 3, InGame = 4, Ending = 5 }

    // The legal phase moves of one session, in one table. The way forward is Idle, Lobby, Starting, Loading, InGame,
    // Ending, Idle; a dropped client's rejoin reloads the snapshot, so InGame may go back to Loading; a session may
    // also be ended from any live phase (a refusal, a cancel, a drop), and nothing but
    // Ending leads back to Idle, so a half-torn-down session is never reused.
    public static class SessionPhases
    {
        public static bool Allowed(SessionPhase from, SessionPhase to)
        {
            switch (from)
            {
                case SessionPhase.Idle: return to == SessionPhase.Lobby;
                case SessionPhase.Lobby: return to == SessionPhase.Starting || to == SessionPhase.Ending;
                case SessionPhase.Starting: return to == SessionPhase.Loading || to == SessionPhase.Ending;
                case SessionPhase.Loading: return to == SessionPhase.InGame || to == SessionPhase.Ending;
                case SessionPhase.InGame: return to == SessionPhase.Loading || to == SessionPhase.Ending;
                case SessionPhase.Ending: return to == SessionPhase.Idle;
                default: return false;   // a value outside the enum moves nowhere
            }
        }
    }
}
