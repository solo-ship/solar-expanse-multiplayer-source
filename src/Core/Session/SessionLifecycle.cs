namespace SolarExpanseMultiplayer
{
    // Whether this game may host or join again from the menus, and whether a session that ended may be reset.
    // A session that ended while still at the menu (a lobby, hosted or joined, or a join whose world never loaded)
    // changed nothing in the game: once its parts are dropped (Session.ResetMenuSession) the player hosts or joins
    // again at once. A session whose world loaded has changed the loaded game (the rule hooks' per-agency state, the
    // rivals' AI switches, M4's join parts that are made once per process): hosting after it, or joining after
    // hosting one, needs a restart. A joiner's own next join after its game keeps M4's JoinAgain path.
    public sealed class SessionLifecycle
    {
        // A session's world loaded in this process (a host's after Start, a joiner's snapshot load, a dev host).
        public bool WorldRan { get; private set; }

        public void WorldLoaded() { WorldRan = true; }

        // An ended session may be dropped now: no world of it loaded, none is on (no host, no live client), the game
        // is at the menu, and it is not the dev flags' own session (a test profile's joiner starts again by itself).
        public bool MayReset(bool atMenu, bool hosting, bool clientLive, bool devSession)
        {
            return atMenu && !WorldRan && !hosting && !clientLive && !devSession;
        }

        // Why the menus cannot host now (a UiText key); null = they may, once an ended menu session is reset.
        public string HostBlock(bool atMenu, bool hosting, bool clientLive)
        {
            if (!atMenu) return "menu.notAtMenu";
            if (hosting) return "host.alreadyHosting";
            if (clientLive) return "join.alreadyJoining";
            return WorldRan ? "menu.restartFirst" : null;
        }

        // Why the menus cannot join now; null = they may. A kept joiner joins again (M4's JoinAgain), even after its
        // own game; a game that hosted a world needs a restart first.
        public string JoinBlock(bool atMenu, bool hosting, bool clientLive, bool clientKept)
        {
            if (!atMenu) return "menu.notAtMenu";
            if (hosting || clientLive) return "join.alreadyJoining";
            return WorldRan && !clientKept ? "menu.restartFirst" : null;
        }
    }
}
