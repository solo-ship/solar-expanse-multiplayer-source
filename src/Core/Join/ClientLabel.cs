namespace SolarExpanseMultiplayer
{
    // Whether the on-screen label shows the joiner's session text (its client status, the join's words and the
    // host's report lines). A session that ended keeps its last words on the menu, where the player reads why it
    // ended; the next save loaded after that is a solo game, which shows no session text. A new join (the dev "join"
    // trigger) is a new session and shows it again. One per SessionClient, main thread only.
    public sealed class ClientLabel
    {
        bool over, hidden;

        public bool Shows { get { return !hidden; } }

        // The session is over (IJoinerWorld.SessionOver): its words stay until a game is loaded.
        public void SessionOver() { over = true; }

        // A save loaded: after the session's end it is the player's own game.
        public void SaveLoaded() { if (over) hidden = true; }

        public void NewSession() { over = false; hidden = false; }
    }
}
