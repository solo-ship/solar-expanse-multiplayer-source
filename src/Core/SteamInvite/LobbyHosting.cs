using System;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // The host's facts for its lobby's data.
    public sealed class LobbyHostFacts
    {
        public ulong HostId;
        public int Protocol;
        public string Mod, Build, SessionId;
        public LobbyAccess Access;
        public int Players, Max;
        public bool Open;
        // a code-only game keeps its lobby shut: players come with the Steam join code
        public bool Joinable { get { return Open && Players < Max && Access != LobbyAccess.CodeOnly; } }

        // What the host publishes: the Steam rules' LobbyData, so joiners read it with its strict reader.
        public LobbyData ToData()
        {
            return new LobbyData
            {
                HostId = HostId, Protocol = Protocol, Mod = Mod ?? "", Build = Build ?? "", Session = SessionId ?? "",
                Access = Access, Players = Players, MaxPlayersAllowed = Max, Joinable = Joinable,
            };
        }
    }

    // Steam lobby ids, and the lobby Steam names on the command line when a friend's invite starts the game.
    public static class SteamLobbyIds
    {
        public const string ConnectLobbyArg = "+connect_lobby";

        // Steam's id layout for a lobby: universe 1 (public), account type 8 (chat), the lobby flag in the instance
        // (bit 18 of the 20 instance bits), and an account number.
        public static bool IsLobby(ulong id)
        {
            return (id >> 56) == 1 && ((id >> 52) & 0xF) == 8 && ((id >> 32) & 0x40000UL) != 0
                && (id & 0xFFFFFFFFUL) != 0;
        }

        // "+connect_lobby <id>" in the game's arguments (Steam adds it when an accepted invite starts the game); 0 when
        // absent or not a lobby id. The first one counts.
        public static ulong FromArgs(string[] args)
        {
            if (args == null) return 0;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], ConnectLobbyArg, StringComparison.OrdinalIgnoreCase))
                    return Parse(args[i + 1]);
            return 0;
        }

        // The same from one line (Steam's launch command line).
        public static ulong FromLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length > 1024) return 0;
            return FromArgs(line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        static ulong Parse(string s)
        {
            ulong id;
            if (s == null || s.Length == 0 || s.Length > 20) return 0;
            foreach (char c in s) if (c < '0' || c > '9') return 0;
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && IsLobby(id) ? id : 0;
        }
    }
}
