using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Caps for the lobby messages (protocol 7). The names are cut to a byte budget as well as a character count, so the
    // largest roster is a known size whatever script the names are in.
    public static class LobbyLimits
    {
        public const int MaxNameChars = 24, MaxNameBytes = 48, MaxTitleBytes = 96, MaxDateChars = 40;
        public const int MaxHoldersPerAgency = SessionLimits.TechnicalCeiling;
        public const int MaxRosterBytes = 16 * 1024, MaxCopyHeaderBytes = 16 * 1024;

        // A display name (a Steam persona name or a Hello name) as the roster carries it: clean, at most 24 characters and
        // 48 bytes of UTF-8, never empty.
        public static string Name(string raw, string fallback)
        {
            string s = TextSanitiser.Clean(raw, MaxNameChars, fallback);
            return TextBudget.FitBytes(s, MaxNameBytes);
        }

        internal static bool NameFits(string s)
        {
            return s == null || (s.Length <= MaxNameChars && TextBudget.Utf8Bytes(s) <= MaxNameBytes);
        }
    }

    // The refusal codes of the lobby's door and claims (JoinCodes' 0..99 space, after the join codes' 55), and the code a
    // Bye carries when a player leaves on purpose. Each has a line in UiStrings (join.refused.<code>).
    public static class LobbyCodes
    {
        public const int LobbyFull = JoinCodes.LobbyFull, LateJoinOff = JoinCodes.LateJoinOff, Banned = JoinCodes.Banned,
            EpochNotAllowed = JoinCodes.EpochNotAllowed, NotClaimable = JoinCodes.NotClaimable,
            KickedFromLobby = JoinCodes.KickedFromLobby, BadKey = JoinCodes.BadKey, Left = JoinCodes.Left;
    }

    public sealed class LobbyOptionsData
    {
        public int MaxPlayers; public bool NewGame; public string SaveId, Difficulty, Epoch, StarSystem;
        public long StartingMoney; public bool AiRivals, LateJoin;

        public string Validate()
        {
            if (MaxPlayers < 1 || MaxPlayers > SessionLimits.TechnicalCeiling) return "MaxPlayers outside 1.." + SessionLimits.TechnicalCeiling;
            if (!string.IsNullOrEmpty(SaveId) && !SaveNames.IsSaveId(SaveId)) return "Options.SaveId is not 16 hex digits";
            foreach (string id in new[] { Difficulty, Epoch, StarSystem })
                if (id != null && !LobbyIds.IsContentId(id)) return "an option is not a content id";
            if (StartingMoney < 0 || StartingMoney > LobbyOptions.MaxStartingMoney) return "StartingMoney outside its range";
            return null;
        }
    }

    public sealed class RosterPlayer
    {
        public int Id; public string Name, Agency; public bool Ready;
        public int Check;                  // 0 not yet, 1 checking, 2 matches, 3 differs
        public int State;                  // 0 lobby, 1 joining, 2 in game, 3 dropped, 4 away, 5 rejoining
        public int JoinPct = -1, JoinSecsLeft = -1, PingMs = -1;
    }

    public sealed class RosterAgency
    {
        public string Id; public int State;       // AgencyStanding as an int: 1 human, 2 AI rival, 3 idle, 4 not in the game
        public bool Claimable; public List<int> Holders = new List<int>(); public string LastHolder;
    }

    // Host -> clients: the lobby as it is now, on every change (at most 4 a second). Everything a client shows comes from
    // here, so every field is checked and every name is cleaned and cut again on arrival.
    public sealed class Roster : Message
    {
        public const int MaxPlayers = SessionLimits.TechnicalCeiling, MaxAgencies = 64;

        public int Seq; public SessionPhase Phase; public int HostId; public string SaveId;
        public List<RosterPlayer> Players = new List<RosterPlayer>();
        public List<RosterAgency> Agencies = new List<RosterAgency>();
        public LobbyOptionsData Options; public string SaveTitle, SaveDate;

        public override string Validate()
        {
            if (Seq < 0) return "Seq is negative";
            if ((int)Phase < (int)SessionPhase.Idle || (int)Phase > (int)SessionPhase.Ending) return "Phase unknown";
            if (HostId < 0 || HostId > HostGate.MaxPlayerIds) return "HostId out of range";
            if (Players == null || Players.Count > MaxPlayers) return "the roster has too many players";
            if (Agencies == null || Agencies.Count > MaxAgencies) return "the roster has too many agencies";
            if (Options == null) return "the roster has no options";
            if (!string.IsNullOrEmpty(SaveId) && !SaveNames.IsSaveId(SaveId)) return "the roster's save id is not valid";
            string e = Options.Validate() ?? Text(SaveDate, "SaveDate", LobbyLimits.MaxDateChars, false);
            if (e != null) return e;
            if (SaveTitle != null && (SaveTitle.Length > SaveCopyHeader.MaxTitleChars || TextBudget.Utf8Bytes(SaveTitle) > LobbyLimits.MaxTitleBytes))
                return "SaveTitle too long";
            foreach (RosterPlayer p in Players)
            {
                if (p == null) return "a roster player is empty";
                if (p.Id < 0 || p.Id > HostGate.MaxPlayerIds) return "a roster player's Id is out of range";
                if (!LobbyLimits.NameFits(p.Name)) return "a roster player's name is too long";
                if (!string.IsNullOrEmpty(p.Agency) && !LobbyIds.IsContentId(p.Agency)) return "a roster player's agency is not valid";
                if (p.Check < 0 || p.Check > 3 || p.State < 0 || p.State > 5) return "a roster player is not valid";
                if (p.JoinPct < -1 || p.JoinPct > 100 || p.JoinSecsLeft < -1 || p.JoinSecsLeft > 86400) return "a roster player's join progress is not valid";
                if (p.PingMs < -1 || p.PingMs > 600000) return "a roster player's ping is not valid";
            }
            int holders = 0;
            foreach (RosterAgency a in Agencies)
            {
                if (a == null || !LobbyIds.IsContentId(a.Id) || a.State < 1 || a.State > 4) return "a roster agency is not valid";
                if (!LobbyLimits.NameFits(a.LastHolder)) return "a roster agency's last holder is too long";
                if (a.Holders == null || a.Holders.Count > LobbyLimits.MaxHoldersPerAgency) return "a roster agency has too many holders";
                holders += a.Holders.Count;
                foreach (int h in a.Holders) if (h < 0 || h > HostGate.MaxPlayerIds) return "a roster agency's holder is out of range";
            }
            if (holders > MaxPlayers) return "the roster's agencies have more holders than there are seats";
            foreach (RosterPlayer p in Players) if (p.Name != null) p.Name = LobbyLimits.Name(p.Name, "");
            foreach (RosterAgency a in Agencies) if (a.LastHolder != null) a.LastHolder = LobbyLimits.Name(a.LastHolder, "");
            if (SaveTitle != null) SaveTitle = TextBudget.FitBytes(TextSanitiser.Clean(SaveTitle, SaveCopyHeader.MaxTitleChars, ""), LobbyLimits.MaxTitleBytes);
            if (SaveDate != null) SaveDate = TextSanitiser.Clean(SaveDate, LobbyLimits.MaxDateChars, "");
            return null;
        }
    }

    // Client -> host: take this agency ("" lets go of the one held). Not counted in the join's claim limit.
    public sealed class LobbyClaim : Message
    {
        public string Agency;
        public override string Validate()
        {
            if (Agency == null) return "Agency missing";
            return Agency.Length == 0 || LobbyIds.IsContentId(Agency) ? null : "Agency is not an agency id";
        }
    }

    public sealed class LobbyReady : Message
    {
        public bool Ready;
        public override string Validate() { return null; }
    }

    // Client -> host, after the door has passed: the player's key hash for this save (HMAC-SHA256 of the player's secret over
    // pin || saveId), which keeps a seat and a ban across sessions. It has to match the roster's current save id.
    public sealed class PlayerKey : Message
    {
        public string Key, SaveId;
        public override string Validate() { return Hex(Key, "Key", 64, true) ?? Hex(SaveId, "SaveId", 16, true); }
    }
}
