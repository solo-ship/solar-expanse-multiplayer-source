namespace SolarExpanseMultiplayer
{
    // What the invite strip asks of the game: the live Steam lobby and session, in one place so the strip itself holds
    // only widgets. Main thread only. Steamworks stays in src\Game\Steam: every Steam call goes through SteamLobby.
    public interface IInviteSource
    {
        InviteInputs Read();
        string OpenInvite();                 // null = Steam's invite window was asked for; else why not
        void SetAccess(LobbyAccess how);
        string SteamCode { get; }            // the host's Steam join code; never logged
    }

    public sealed class LiveInviteSource : IInviteSource
    {
        static SteamNet Net { get { return SempRoot.Instance != null ? SempRoot.Instance.Steam : null; } }

        public string SteamCode { get { return Session.Host != null ? Session.Host.SteamCodeText : null; } }

        public InviteInputs Read()
        {
            SteamNet net = Net;
            var i = new InviteInputs { IsHost = Session.Host != null };
            if (net == null) return i;
            SteamLobby lobby = net.Lobby;
            i.SteamUsable = lobby.Usable;
            i.LobbyReady = i.IsHost ? lobby.Hosting : lobby.JoinedLobby != 0;
            i.OverlayOn = lobby.OverlayOn;
            i.Access = lobby.Access;
            i.SteamCode = SteamCode;
            return i;
        }

        // The host's lobby, or (for a member) the lobby it joined: SteamLobby.OpenInviteOverlay decides which.
        public string OpenInvite()
        {
            SteamNet net = Net;
            return net == null ? "Steam can't be used right now" : net.Lobby.OpenInviteOverlay();
        }

        public void SetAccess(LobbyAccess how)
        {
            SteamNet net = Net;
            if (net != null && Session.Host != null) net.Lobby.SetAccess(how);
        }
    }
}
