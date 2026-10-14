using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The data the host publishes on its Steam lobby, and the strict reader joiners use on it. A joiner sees lobbies
    // of strangers, so every value is length-capped and restricted to a small alphabet before anything uses it.
    public sealed class LobbyData
    {
        public const string KeyHost = "semp_host", KeyProto = "semp_proto", KeyMod = "semp_mod",
            KeyBuild = "semp_build", KeySession = "semp_sess", KeyAccess = "semp_access",
            KeyPlayers = "semp_players", KeyMax = "semp_max", KeyJoinable = "semp_join";
        public const int MaxHost = 20, MaxMod = 16, MaxBuild = 32, MaxSession = 32, MaxNumber = 4, MaxPlayers = 64;
        public const string BadData = "steam.lobby.bad_data", Incompatible = "steam.lobby.version",
            Closed = "steam.lobby.closed", Full = "steam.lobby.full", NotHost = "steam.lobby.not_host";

        public ulong HostId;
        public int Protocol;
        public string Mod, Build, Session;
        public LobbyAccess Access;
        public int Players, MaxPlayersAllowed;
        public bool Joinable;

        public static string[] AllKeys
        {
            get
            {
                return new[] { KeyHost, KeyProto, KeyMod, KeyBuild, KeySession, KeyAccess, KeyPlayers, KeyMax,
                               KeyJoinable };
            }
        }

        public Dictionary<string, string> ToPairs()
        {
            return new Dictionary<string, string>
            {
                { KeyHost, HostId.ToString() }, { KeyProto, Protocol.ToString() }, { KeyMod, Mod },
                { KeyBuild, Build }, { KeySession, Session }, { KeyAccess, ((int)Access).ToString() },
                { KeyPlayers, Players.ToString() }, { KeyMax, MaxPlayersAllowed.ToString() },
                { KeyJoinable, Joinable ? "1" : "0" },
            };
        }

        // The keys whose wanted value differs from what was last written (a key never written counts), in a fixed
        // order, so the host writes only what changed.
        public static List<KeyValuePair<string, string>> Changed(IDictionary<string, string> written,
                                                                 IDictionary<string, string> wanted)
        {
            var keys = new List<string>(wanted.Keys);
            keys.Sort(StringComparer.Ordinal);
            var changed = new List<KeyValuePair<string, string>>();
            foreach (string k in keys)
            {
                string old;
                if (written == null || !written.TryGetValue(k, out old) || old != wanted[k])
                    changed.Add(new KeyValuePair<string, string>(k, wanted[k]));
            }
            return changed;
        }

        // Reads every key through get (null = absent). Any missing, oversized or oddly-shaped value refuses the
        // whole lobby; error is a text key.
        public static bool TryRead(Func<string, string> get, out LobbyData d, out string error)
        {
            d = null;
            error = BadData;
            ulong host; int proto, access, players, max, join;
            if (!Number(get(KeyHost), MaxHost, out host) || !SteamCode.IsIndividual(host)) return false;
            if (!Small(get(KeyProto), out proto) || !Small(get(KeyAccess), out access) || access > 2) return false;
            if (!Small(get(KeyPlayers), out players) || players > MaxPlayers) return false;
            if (!Small(get(KeyMax), out max) || max < 1 || max > MaxPlayers) return false;
            if (!Small(get(KeyJoinable), out join) || join > 1) return false;
            string mod = get(KeyMod), build = get(KeyBuild), sess = get(KeySession);
            if (!Token(mod, MaxMod) || !Token(build, MaxBuild) || !Token(sess, MaxSession)) return false;
            d = new LobbyData { HostId = host, Protocol = proto, Mod = mod, Build = build, Session = sess,
                Access = (LobbyAccess)access, Players = players, MaxPlayersAllowed = max, Joinable = join == 1 };
            error = null;
            return true;
        }

        // Digits only, bounded length, so parsing cannot accept signs and spaces.
        static bool Number(string s, int maxLen, out ulong v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s) || s.Length > maxLen) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return ulong.TryParse(s, out v);
        }

        static bool Small(string s, out int v)
        {
            ulong u;
            v = 0;
            if (!Number(s, MaxNumber, out u)) return false;
            v = (int)u;
            return true;
        }

        // Letters, digits and . _ - only: a version, build tag or session id, never free text.
        static bool Token(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length > maxLen) return false;
            foreach (char c in s)
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z'
                      || c == '.' || c == '_' || c == '-')) return false;
            return true;
        }

        // The joiner's pre-check before entering a lobby; null = go ahead, else the text key. The owner must be
        // the host the lobby names, so a lobby cannot borrow another account's name.
        public static string Compatible(LobbyData d, ulong owner, int myProtocol, string myMod, string myBuild)
        {
            if (d == null) return BadData;
            if (owner != d.HostId) return NotHost;
            if (d.Protocol != myProtocol || d.Mod != myMod || d.Build != myBuild) return Incompatible;
            if (!d.Joinable) return Closed;
            if (d.Players >= d.MaxPlayersAllowed) return Full;
            return null;
        }
    }
}
