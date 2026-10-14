using System;

namespace SolarExpanseMultiplayer
{
    // Every way Steam can be unusable, as the player is told about it. Decided here (no Steamworks) so the rules and the
    // words are tested; SteamReady feeds in what it sees. The home-network (LAN) path never depends on Steam.
    public enum SteamState { Ready, NotRunning, Offline, NotLoggedOn, RelayDown, RelaySlow }

    public static class SteamStates
    {
        // The relay is called slow when it is still measuring after this long (it is given up on after SteamReady's own
        // timeout).
        public const float SlowAfterSeconds = 6f;

        public static readonly string[] Names = { "notrunning", "offline", "notloggedon", "relaydown", "relayslow" };

        public static string Name(SteamState s) { return s == SteamState.Ready ? "ready" : Names[(int)s - 1]; }

        public static bool TryParse(string name, out SteamState s)
        {
            s = SteamState.Ready;
            if (name == null) return false;
            for (int i = 0; i < Names.Length; i++)
                if (string.Equals(name, Names[i], StringComparison.Ordinal)) { s = (SteamState)(i + 1); return true; }
            return false;
        }

        // What Steam looks like: running (the game's manager has it initialised), signed in to the Steam network, has an
        // account id, and the relay: failed, or still measuring for relayWaited seconds (negative: not asked about).
        public static SteamState Decide(bool running, bool loggedOn, bool hasAccount, bool relayFailed, float relayWaited)
        {
            if (!running) return SteamState.NotRunning;
            if (!hasAccount) return SteamState.NotLoggedOn;
            if (!loggedOn) return SteamState.Offline;
            if (relayFailed) return SteamState.RelayDown;
            if (relayWaited >= SlowAfterSeconds) return SteamState.RelaySlow;
            return SteamState.Ready;
        }

        // Internet play (Steam) can be started in this state; a slow relay only warns.
        public static bool SteamUsable(SteamState s) { return s == SteamState.Ready || s == SteamState.RelaySlow; }

        // The home-network path is the same in every state.
        public static bool LanUsable(SteamState s) { return true; }

        // The player-facing line for the Multiplayer screen and the Steam options (docs/UX-COPY.md).
        public static string Line(SteamState s)
        {
            switch (s)
            {
                case SteamState.NotRunning:
                    return "Steam isn't running. Start Steam to play over the internet. Playing on your home network still works.";
                case SteamState.Offline:
                    return "Steam is offline. Go online in Steam to play over the internet. Playing on your home network still works.";
                case SteamState.NotLoggedOn:
                    return "You aren't signed in to Steam. Sign in to play over the internet. Playing on your home network still works.";
                case SteamState.RelayDown:
                    return "Steam's network can't be reached just now, so internet play isn't available. Playing on your home network still works.";
                case SteamState.RelaySlow:
                    return "Steam's network is slow to answer. Internet play may take a little longer to connect.";
                default:
                    return "";
            }
        }
    }
}
