using System.Net;

namespace SolarExpanseMultiplayer
{
    // The dev-only network simulation in Core terms (NetOptions lives in Net, which Core-only tools do not compile).
    // The game glue copies it into a NetOptions at the session seam.
    public sealed class SimOptions
    {
        public int SimLatencyMs, SimJitterMs, SimLossPercent;
        public int SimSeed = 1;          // a seed is not a simulation by itself
        public int SimRateKiBps;
    }

    // What to start: from the menus, or (test profiles only) from the dev flags. One shape, so every start passes
    // the same Check.
    public sealed class SessionPlan
    {
        public SessionRole Role;
        public string PlayerName;        // cleaned (PlayerNames.Clean); never empty
        public IPAddress Bind;           // host: BindChoice, or (TestSeams.Open) -semp-bind; BindAddress.Rule always
        public int Port;                 // 0 = any free port
        public string JoinCodeText;      // client; never logged, never in ToString()
        public ulong SteamHost;          // client: a Steam lobby's checked host, joined with no code (or 0)
        public bool Test;                // made from DevFlags with TestSeams.Open
        public SimOptions Sim;           // simulated lag, loss, rate: Test only
        public string JoinFault;         // Test only
        // client: a join from the menus or a Steam invite, which waits in the host's lobby at the menu until the host
        // lets it in (LobbyFollow); a dev-flag join goes straight into M4's join
        public bool Lobby;

        // null = ok; else why the plan must not start. A plan that is not a test plan never simulates a network or
        // plants a fault, whatever made it.
        public string Check()
        {
            if (Role != SessionRole.Host && Role != SessionRole.Client) return "the plan has no role";
            if (string.IsNullOrWhiteSpace(PlayerName)) return "the plan has no player name";
            if (!Test && Simulates(Sim)) return "a menu plan may not simulate the network";
            if (!Test && JoinFault != null) return "a menu plan may not plant a fault";
            if (Port != 0 && (Port < 1024 || Port > 65535)) return "the port must be 0 or 1024 to 65535";
            if (Role == SessionRole.Host)
            {
                if (!BindAddress.Allowed(Bind)) return "a host needs " + BindAddress.Rule;
                if (JoinCodeText != null) return "a host has no join code";
                if (SteamHost != 0) return "a host joins no lobby";
                return null;
            }
            if (SteamHost != 0)
                return JoinCodeText == null && SteamCode.IsIndividual(SteamHost) ? null
                    : "a lobby join needs a player's Steam account as its host and no join code";
            if (string.IsNullOrWhiteSpace(JoinCodeText) || JoinCodeText.Length > JoinCode.MaxTextLength)
                return "a client needs a join code";
            return null;
        }

        // The old code's session, as a plan: null when no session is wanted, the flags had a problem, or the seams are
        // closed (no test profile, or the player-path profile). A -semp-join-file code is read by the game glue, which
        // sets JoinCodeText before Check.
        public static SessionPlan FromDevFlags(DevFlags f, string profile, bool playerPath, string playerName)
        {
            if (f == null || f.Problem != null || !f.SessionWanted || !TestSeams.Open(profile, playerPath)) return null;
            return new SessionPlan
            {
                Role = f.Host ? SessionRole.Host : SessionRole.Client,
                PlayerName = PlayerNames.Clean(playerName),
                Bind = f.Bind,
                Port = f.Port,
                JoinCodeText = f.Host ? null : f.JoinCode,
                Test = true,
                Sim = new SimOptions
                {
                    SimLatencyMs = f.SimLagMs, SimJitterMs = f.SimJitterMs, SimLossPercent = f.SimLossPercent,
                    SimRateKiBps = f.SimRateKiBps,
                },
                JoinFault = f.JoinFault,
            };
        }

        public static SessionPlan HostFromMenu(string playerName, IPAddress bind)
        {
            return new SessionPlan { Role = SessionRole.Host, PlayerName = PlayerNames.Clean(playerName), Bind = bind };
        }

        public static SessionPlan JoinFromMenu(string playerName, string joinCodeText)
        {
            return new SessionPlan
            {
                Role = SessionRole.Client, PlayerName = PlayerNames.Clean(playerName), JoinCodeText = joinCodeText,
                Lobby = true,
            };
        }

        // A join through a Steam lobby (SteamLobby.JoinReady): the host is the lobby's checked owner and the host's
        // door admits lobby members, so there is no code.
        public static SessionPlan JoinSteamLobby(string playerName, ulong steamHost)
        {
            return new SessionPlan
            {
                Role = SessionRole.Client, PlayerName = PlayerNames.Clean(playerName), SteamHost = steamHost,
                Lobby = true,
            };
        }

        // The join code is a secret: it is left out of anything that prints the plan (and so is the host's Steam id).
        public override string ToString()
        {
            return "SessionPlan " + Role + " as '" + PlayerName + "'" + (Bind != null ? " on " + Bind : "") +
                   (Port != 0 ? ":" + Port : "") + (Test ? " (test)" : "");
        }

        static bool Simulates(SimOptions n)
        {
            return n != null &&
                   (n.SimLatencyMs != 0 || n.SimJitterMs != 0 || n.SimLossPercent != 0 || n.SimRateKiBps != 0);
        }
    }
}
