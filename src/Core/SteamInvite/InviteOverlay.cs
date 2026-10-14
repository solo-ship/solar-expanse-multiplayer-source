namespace SolarExpanseMultiplayer
{
    // Which lobby Steam's invite dialog opens for: any member may invite friends (the user's decision), so the host
    // invites to the lobby it hosts and a joiner to the lobby it is in. null = open it for lobby; else why not, for
    // the screen (the Steam join code is the way round it).
    public static class InviteOverlay
    {
        public const string SteamDown = "Steam can't be used right now";
        public const string NoLobby = "the lobby is not ready yet";
        public const string OverlayOff =
            "Steam's overlay is off (start the game through Steam, or turn the overlay on in Steam)";

        public static string Pick(bool steamAlive, ulong hosted, ulong joined, bool overlayOn, out ulong lobby)
        {
            lobby = 0;
            if (!steamAlive) return SteamDown;
            ulong l = hosted != 0 ? hosted : joined;
            if (l == 0) return NoLobby;
            if (!overlayOn) return OverlayOff;
            lobby = l;
            return null;
        }
    }
}
