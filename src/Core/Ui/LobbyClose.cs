namespace SolarExpanseMultiplayer
{
    // What closing the lobby screen does (its Leave or Close button, Esc, the window's close).
    public enum LobbyCloseAction { Close, Keep, AskEndLobby, Leave }

    // At the menu, before the game runs, the lobby screen IS the session: the host closing it is asked "Close your
    // lobby?" (yes ends the session and leaves the Steam lobby), and a joiner closing it leaves that lobby (its
    // goodbye), so neither stays in a lobby it can no longer see. Once the start is under way the screen stays (only
    // a joiner's own Leave button leaves). Over a running game the screen is only a view: the button keeps its old
    // meaning (a joiner's Leave), Esc and the close only hide it.
    public static class LobbyCloseRules
    {
        public static LobbyCloseAction On(SessionView v, Capability caps, bool viaButton)
        {
            if (v == null) return LobbyCloseAction.Close;
            bool menuLobby = CapabilityRules.Has(caps, Capability.LobbyAtMenu);
            if (menuLobby && v.Phase == SessionPhase.Lobby)
                return v.IsHost ? LobbyCloseAction.AskEndLobby : LobbyCloseAction.Leave;
            if (menuLobby && (v.Phase == SessionPhase.Starting || v.Phase == SessionPhase.Loading))
                return !v.IsHost && viaButton ? LobbyCloseAction.Leave : LobbyCloseAction.Keep;
            return viaButton && menuLobby && !v.IsHost ? LobbyCloseAction.Leave : LobbyCloseAction.Close;
        }
    }
}
