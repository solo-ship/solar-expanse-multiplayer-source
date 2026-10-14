using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The plan's Core "AgencyInfo", renamed: the Game assembly already has an AgencyInfo (names and logos, src/Game/Ui/Players), and the
    // two share a namespace. One agency as the lobby is told about it. State is the BASE state (what it is with nobody holding it).
    public sealed class LobbyAgency
    {
        public string Id; public AgencyStanding State; public bool Playable; public bool EpochAllows; public string LastHolder;
    }

    // The host's lobby: who holds which agency, who is ready, the start rule, the door rules and the roster. Pure, single-threaded
    // (the host's session calls it from the main thread). Everything that came from a peer is validated here; nothing is trusted.
    //
    // Claims and the AI (DD6/DD22, the user's ruling): any agency IN the game may be claimed, and a claimed agency is human. The host
    // switches FREE agencies between AI rival and not in the game in a NEW game only (SetAgencyRule). In a loaded save an agency that is
    // not in the game stays unclaimable. In a new game a player's claim brings a not-in-game agency into the game as a human. The
    // host's own claim is always human, whatever any toggle says: a held agency is never switched.
    public sealed class LobbyHost
    {
        public const int MaxClaimsPerSecond = 4;   // DD5: the caller's rate rule (the newest kept); named here so it is one number

        sealed class Member
        {
            public int Id; public string Name, Agency, KeyHash; public bool Ready, Gone; public int Check;
        }

        sealed class Row
        {
            public string Id; public AgencyStanding Base; public bool Playable, EpochAllows; public string LastHolder;
            public int Holder = -1;
        }

        readonly LobbyOptions options;
        readonly List<Row> rows = new List<Row>();
        readonly Dictionary<int, Member> members = new Dictionary<int, Member>();
        readonly bool newGame;
        readonly int hostId;

        public SessionPhase Phase { get; set; }
        public SeatsFile Seats = new SeatsFile();     // the host's memory of this save; the caller reads and writes the file
        public AgencyLedger Ledger;                   // optional: Kick frees its seat there too
        public string SaveTitle, SaveDate;

        public LobbyHost(LobbyOptions options, IList<LobbyAgency> agencies, bool newGame, int hostId, string hostName)
        {
            if (options == null) throw new ArgumentNullException("options");
            this.options = options;
            this.newGame = newGame;
            this.hostId = hostId;
            Phase = SessionPhase.Lobby;
            if (agencies != null)
                foreach (LobbyAgency a in agencies)
                {
                    if (a == null || !LobbyIds.IsContentId(a.Id) || rows.Count >= SolarExpanseMultiplayer.Roster.MaxAgencies) continue;
                    if (rows.Exists(r => r.Id == a.Id)) continue;
                    rows.Add(new Row { Id = a.Id, Base = a.State, Playable = a.Playable, EpochAllows = a.EpochAllows, LastHolder = Clean(a.LastHolder) });
                }
            // the host's own game matches itself
            members[hostId] = new Member { Id = hostId, Name = RosterName(hostName), Check = 2 };
        }

        static string Clean(string name)
        {
            return name == null ? null : LobbyLimits.Name(name, null);
        }

        // Every name the roster carries is cut by bytes as well as characters (LobbyLimits.Name), or a joiner refuses
        // the whole roster: 17 CJK characters are within 24 characters but over the 48 bytes.
        static string RosterName(string name)
        {
            return LobbyLimits.Name(PlayerNames.Clean(name), PlayerNames.BuiltInFallback);
        }

        static string RosterTitle(string title)
        {
            return title == null ? null
                : TextBudget.FitBytes(TextSanitiser.Clean(title, SaveCopyHeader.MaxTitleChars, ""),
                                      LobbyLimits.MaxTitleBytes);
        }

        Row Find(string id)
        {
            if (id == null) return null;
            foreach (Row r in rows) if (r.Id == id) return r;
            return null;
        }

        public int PlayerCount { get { return members.Count; } }
        public string ClaimedAgency(int player) { Member m; return members.TryGetValue(player, out m) ? m.Agency : null; }
        public bool IsReady(int player) { Member m; return members.TryGetValue(player, out m) && m.Ready; }
        public bool Has(int player) { return members.ContainsKey(player); }

        // ---- the door ----

        // The seat of a returning player: the id of a member (dropped or still listed) with that key hash, or -1.
        public int SeatFor(string playerKeyHash)
        {
            if (!PlayerKeys.IsHex64(playerKeyHash)) return -1;
            foreach (Member m in members.Values) if (m.KeyHash != null && PlayerKeys.Same(m.KeyHash, playerKeyHash)) return m.Id;
            return -1;
        }

        // The ban and LateJoin rules at the door (DD5). 0 = let in. A banned key is refused in every phase and a malformed key never
        // gets as far as a lookup. A returning player (a kept seat) skips the LateJoin and Full rules; a new one does not.
        public bool Admit(string playerKeyHash, out int refuseCode)
        {
            refuseCode = 0;
            if (!PlayerKeys.IsHex64(playerKeyHash)) { refuseCode = LobbyCodes.BadKey; return false; }
            if (Seats.IsBanned(playerKeyHash)) { refuseCode = LobbyCodes.Banned; return false; }
            if (SeatFor(playerKeyHash) >= 0) return true;
            switch (Phase)
            {
                case SessionPhase.Lobby: break;
                case SessionPhase.Starting:
                case SessionPhase.Loading:
                case SessionPhase.InGame:
                    // a player arriving while the host's world loads waits at the menu like a late joiner
                    if (!options.LateJoin) { refuseCode = LobbyCodes.LateJoinOff; return false; }
                    break;
                default: refuseCode = LobbyCodes.LateJoinOff; return false;   // Idle, Ending: no door
            }
            if (members.Count >= Math.Min(options.MaxPlayers, SessionLimits.TechnicalCeiling)) { refuseCode = LobbyCodes.LobbyFull; return false; }
            return true;
        }

        // Seats a player who passed Admit. 0 = ok. A kept agency (the seats file) is claimed again when it is still free.
        public int Join(int player, string name, string playerKeyHash)
        {
            int code;
            if (player == hostId || player < 0 || player > HostGate.MaxPlayerIds) return LobbyCodes.NotClaimable;
            if (members.ContainsKey(player)) return LobbyCodes.NotClaimable;
            if (!Admit(playerKeyHash, out code)) return code;
            int old = SeatFor(playerKeyHash);
            if (old >= 0 && old != player) return LobbyCodes.NotClaimable;
            members[player] = new Member { Id = player, Name = RosterName(name), KeyHash = playerKeyHash };
            string kept = Seats.KeptAgency(playerKeyHash);
            if (kept != null) Claim(player, kept);
            return 0;
        }

        // The link dropped in the lobby: the seat is kept (SeatFor finds it); the claim stays, the ready flag does not.
        public void Drop(int player)
        {
            Member m;
            if (player == hostId || !members.TryGetValue(player, out m)) return;
            m.Gone = true; m.Ready = false;
        }

        // The player came back on the same key.
        public void Return(int player)
        {
            Member m;
            if (members.TryGetValue(player, out m)) m.Gone = false;
        }

        // ---- claims ----

        // 0 = ok, else a code. "" or null releases. First come, first served; a player holds one agency (a new claim replaces the old
        // one only when it succeeds). Claimable per DD22: playable, the epoch allows it, nobody holds it, and it is in the game (a
        // NotInGame agency only in a new game, where the claim brings it in as a human). Only in the lobby.
        public int Claim(int player, string agency)
        {
            Member m;
            if (!members.TryGetValue(player, out m)) return LobbyCodes.NotClaimable;
            if (string.IsNullOrEmpty(agency)) { Release(player); return 0; }
            if (Phase != SessionPhase.Lobby) return LobbyCodes.NotClaimable;
            return Take(m, agency);
        }

        // A late joiner's claim once the session runs (DD5 "Joining a running session"): only with LateJoin on, only an
        // agency that is in the running world (an AI rival or idle, nobody holding it); never a release.
        public int ClaimLate(int player, string agency)
        {
            Member m;
            if (!members.TryGetValue(player, out m) || string.IsNullOrEmpty(agency)) return LobbyCodes.NotClaimable;
            if (Phase != SessionPhase.InGame) return LobbyCodes.NotClaimable;
            if (!options.LateJoin) return LobbyCodes.LateJoinOff;
            return Take(m, agency);
        }

        int Take(Member m, string agency)
        {
            int player = m.Id;
            if (!LobbyIds.IsContentId(agency)) return JoinCodes.AgencyUnknown;
            Row r = Find(agency);
            if (r == null) return JoinCodes.AgencyUnknown;
            if (r.Holder == player) return 0;
            if (r.Holder >= 0) return JoinCodes.AgencyTaken;
            if (!r.Playable) return JoinCodes.AgencyNotPlayable;
            if (!r.EpochAllows) return LobbyCodes.EpochNotAllowed;
            if (r.Base == AgencyStanding.NotInGame && !(newGame && Phase == SessionPhase.Lobby))
                return LobbyCodes.NotClaimable;
            Release(player);
            r.Holder = player;
            m.Agency = r.Id;
            m.Ready = false;
            return 0;
        }

        public void Release(int player)
        {
            Member m;
            if (!members.TryGetValue(player, out m) || m.Agency == null) return;
            Row r = Find(m.Agency);
            if (r != null && r.Holder == player) r.Holder = -1;
            m.Agency = null;
            m.Ready = false;
        }

        // A player is ready only with an agency and a passed check; anything else stays not ready.
        public void SetReady(int player, bool ready)
        {
            Member m;
            if (!members.TryGetValue(player, out m)) return;
            m.Ready = ready && m.Agency != null && m.Check == 2;
        }

        public void SetCheck(int player, int check)
        {
            Member m;
            if (!members.TryGetValue(player, out m) || check < 0 || check > 3) return;
            m.Check = check;
            if (check != 2) m.Ready = false;
        }

        // The host switches a FREE agency between an AI rival and not in the game: a new game only, never a held agency (the host's own
        // claim is always human), never an idle one, and AiRival only while the master switch is on. false = nothing changed.
        public bool SetAgencyRule(string agency, AgencyStanding rule)
        {
            if (!newGame || (rule != AgencyStanding.AiRival && rule != AgencyStanding.NotInGame)) return false;
            Row r = Find(agency);
            if (r == null || r.Holder >= 0 || r.Base == AgencyStanding.Idle) return false;
            if (rule == AgencyStanding.AiRival && !options.AiRivals) return false;
            r.Base = rule;
            return true;
        }

        // ---- start ----

        // Start is on when the host holds an agency (alone is fine: friends can join later) and every member holds one, has a passed check and is ready.
        // whyKey is a UiText key when it is not.
        public bool CanStart(out string whyKey)
        {
            whyKey = null;
            Member host = members[hostId];
            if (host.Agency == null) { whyKey = "lobby.blocked.hostAgency"; return false; }
            foreach (Member m in members.Values) if (m.Agency == null) { whyKey = "lobby.blocked.noAgency"; return false; }
            foreach (Member m in members.Values) if (m.Check != 2) { whyKey = "lobby.blocked.check"; return false; }
            foreach (Member m in members.Values) if (!m.Ready && m.Id != hostId) { whyKey = "lobby.blocked.notReady"; return false; }
            return true;
        }

        // The members who start when the host starts anyway: those with an agency and a passed check (the host first, then by id).
        // Empty when the host holds no agency.
        public List<int> StartAnyway()
        {
            var ids = new List<int>();
            if (members[hostId].Agency == null) return ids;
            foreach (Member m in members.Values)
                if (m.Agency != null && m.Check == 2 && !m.Gone) ids.Add(m.Id);
            ids.Sort((a, b) => a == hostId ? (b == hostId ? 0 : -1) : (b == hostId ? 1 : a.CompareTo(b)));
            return ids;
        }

        // The members StartAnyway would leave out, for the screen. Empty while the host holds no agency: Start anyway
        // starts nobody then, so the screen offers neither start and shows why.
        public List<int> LeftOut()
        {
            var keep = StartAnyway();
            var left = new List<int>();
            if (keep.Count == 0) return left;
            foreach (int id in members.Keys) if (!keep.Contains(id)) left.Add(id);
            left.Sort();
            return left;
        }

        // ---- leaving ----

        // Frees the seat (the ledger too) and takes the keys: nothing is remembered for that key, so it is a new player if it returns.
        // The host cannot be kicked. Only the lobby's own state: a kick of a connected player goes through
        // JoinService.Kick as well, which closes the link and releases the command stream's seat (ReleaseSeat).
        public void Kick(int player)
        {
            Member m;
            if (player == hostId || !members.TryGetValue(player, out m)) return;
            Release(player);
            members.Remove(player);
            if (Ledger != null) Ledger.Forget(player);
            if (m.KeyHash != null) Seats.Forget(m.KeyHash);
        }

        // A player left the lobby before the start (its link closed, or it said goodbye): the seat is free at once (nobody
        // is in game yet, so there is no drop to wait for). Unlike Kick, the seats file and the bans are not touched.
        public void Remove(int player)
        {
            if (player == hostId || !members.ContainsKey(player)) return;
            Release(player);
            members.Remove(player);
        }

        // Every agency with the state it has now (Human while held), in the lobby's order.
        public List<KeyValuePair<string, AgencyStanding>> States()
        {
            var list = new List<KeyValuePair<string, AgencyStanding>>();
            foreach (Row r in rows) list.Add(new KeyValuePair<string, AgencyStanding>(r.Id, Effective(r)));
            return list;
        }

        public string NameOf(int player) { Member m; return members.TryGetValue(player, out m) ? m.Name : null; }

        public List<int> Ids()
        {
            var l = new List<int>(members.Keys);
            l.Sort();
            return l;
        }

        // Kick and ban the key for this save.
        public void Ban(int player)
        {
            Member m;
            if (player == hostId || !members.TryGetValue(player, out m)) return;
            string key = m.KeyHash;
            Kick(player);
            if (key != null) Seats.Ban(key);
        }

        // The session is about to start: remember who holds what, by key hash, for the next time.
        public void RememberSeats()
        {
            foreach (Member m in members.Values)
                if (m.Agency != null && m.KeyHash != null) Seats.Remember(m.Agency, m.Name, m.KeyHash);
        }

        // ---- the roster ----

        AgencyStanding Effective(Row r)
        {
            if (r.Holder >= 0) return AgencyStanding.Human;
            return r.Base == AgencyStanding.Human ? AgencyStanding.AiRival : r.Base;   // a former human seat nobody holds runs as an AI rival
        }

        bool Claimable(Row r)
        {
            return r.Holder < 0 && r.Playable && r.EpochAllows
                && (r.Base != AgencyStanding.NotInGame || newGame && Phase == SessionPhase.Lobby);
        }

        public SolarExpanseMultiplayer.Roster Roster(int seq)
        {
            var d = new SolarExpanseMultiplayer.Roster
            {
                Seq = seq, Phase = Phase, HostId = hostId, SaveId = options.NewGame ? "" : options.SaveId,
                Options = options.ToData(), SaveTitle = RosterTitle(SaveTitle),
                SaveDate = SaveDate == null ? null : TextSanitiser.Clean(SaveDate, LobbyLimits.MaxDateChars, ""),
            };
            var ids = new List<int>(members.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                if (d.Players.Count >= SolarExpanseMultiplayer.Roster.MaxPlayers) break;
                Member m = members[id];
                d.Players.Add(new RosterPlayer
                {
                    Id = id, Name = RosterName(m.Name), Agency = m.Agency ?? "", Ready = m.Ready, Check = m.Check,
                    State = m.Gone ? 3 : (Phase == SessionPhase.InGame ? 2 : 0),
                });
            }
            foreach (Row r in rows)
            {
                var a = new RosterAgency { Id = r.Id, State = (int)Effective(r), Claimable = Claimable(r),
                                           LastHolder = Clean(r.LastHolder ?? Seats.LastHolder(r.Id)) };
                if (r.Holder >= 0) a.Holders.Add(r.Holder);
                d.Agencies.Add(a);
            }
            return d;
        }
    }
}
