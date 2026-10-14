namespace SolarExpanseMultiplayer
{
    // The host's lobby options from the host setup screen (Beta B3): an adapter that implements this takes the
    // player limit and the late-join switch before it opens the lobby (a save's or a new game's). The setup screen asks
    // for it with `Actions as IHostOptions`, like IMpSaves. Out-of-range values are clamped, never refused.
    public interface IHostOptions
    {
        void SetHostOptions(int maxPlayers, bool lateJoin);
    }

    public static class HostOptionRules
    {
        public static int ClampPlayers(int maxPlayers)
        {
            if (maxPlayers < 1) return SessionLimits.PlayerLimitNoCoop;
            return maxPlayers > SessionLimits.PlayerLimitNoCoop ? SessionLimits.PlayerLimitNoCoop : maxPlayers;
        }
    }
}
