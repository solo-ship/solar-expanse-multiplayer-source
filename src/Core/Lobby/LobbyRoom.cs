using System;
using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The host's lobby over a whole session (M8 DD5): LobbyHost's claims, door and start rule, plus what goes on around
    // them. A member waits at the main menu in the lobby until the host lets it into the game ("released"): the start
    // set once the host's world has loaded, and later a late joiner as soon as its claim of a free agency holds. From
    // then on M4's join (the content check, the claim, the snapshot) runs for it as for any joiner, and the room only
    // follows its state for the roster. The room also keeps the claim rate rule (at most 4 acted on a second per
    // member, the newest kept), the human agencies the host's world starts with, and the roster's own rate. Pure and
    // single-threaded: the host's main thread.
    public sealed class LobbyRoom
    {
        public const int RosterEveryMs = 250;                                   // a Roster at most 4 times a second
        public const int ClaimEveryMs = 1000 / LobbyHost.MaxClaimsPerSecond;
        public const string NotNow = "host.couldNotStart";                     // Start outside the lobby

        readonly LobbyHost lobby;
        readonly LobbyOptions options;
        readonly int hostId;
        readonly bool newGame;
        readonly Dictionary<int, int> state = new Dictionary<int, int>();       // PlayerRow's states
        readonly Dictionary<int, long> lastClaimMs = new Dictionary<int, long>();
        readonly Dictionary<int, long> droppedSince = new Dictionary<int, long>();   // StillGone's first sight
        readonly Dictionary<int, string> heldClaim = new Dictionary<int, string>();
        readonly List<int> released = new List<int>();                         // let in, not yet taken by the glue
        readonly List<int> starting = new List<int>();
        readonly List<string> humans = new List<string>();
        string fixedHost;                                                       // a save's own player agency
        bool dirty = true, rosterSent;
        long lastRosterMs;
        int seq;

        public LobbyRoom(LobbyOptions options, IList<LobbyAgency> agencies, bool newGame, int hostId, string hostName)
        {
            if (options == null) throw new ArgumentNullException("options");
            this.options = options;
            this.newGame = newGame;
            this.hostId = hostId;
            lobby = new LobbyHost(options, agencies, newGame, hostId, hostName);
            state[hostId] = PlayerRow.Lobby;
        }

        public LobbyHost Lobby { get { return lobby; } }
        public LobbyOptions Options { get { return options; } }
        public SessionPhase Phase { get { return lobby.Phase; } }
        public bool NewGame { get { return newGame; } }
        public int HostId { get { return hostId; } }
        public string HostAgency { get { return lobby.ClaimedAgency(hostId); } }
        public bool HostAgencyFixed { get { return fixedHost != null; } }

        // A PlayerRow state (Lobby, Joining, InGame, Dropped, Away, Rejoining), or -1 for no such member.
        public int StateOf(int player)
        {
            int s;
            return lobby.Has(player) && state.TryGetValue(player, out s) ? s : -1;
        }

        // Still waiting at the menu: not let into the game yet.
        public bool InLobby(int player) { return player != hostId && StateOf(player) == PlayerRow.Lobby; }

        // Let into the game (the start set or a late joiner): M4's join is its path from now on.
        public bool Released(int player) { int s = StateOf(player); return player != hostId && s > PlayerRow.Lobby; }

        // In a save the host plays the save's own player agency: claimed here once and never changed.
        public int FixHostAgency(string agency)
        {
            int code = lobby.Claim(hostId, agency);
            if (code == 0) fixedHost = agency;
            Touch();
            return code;
        }

        // ---- the door ----

        // A new link's player (not a rejoin of a released member). 0 = in the lobby; else the code it is refused with.
        // identity: what this player is known by on this link (a Steam account, else the link's player id); the room's
        // key for it is per link, so a player who comes back on a new link is a new member (M4's rejoin keys keep the
        // in-game seats).
        public int Arrive(int player, string name, string identity)
        {
            if (player == hostId) return LobbyCodes.NotClaimable;
            if (lobby.Has(player))
            {
                Back(player);
                return 0;
            }
            int code = lobby.Join(player, name, KeyOf(identity, player));
            if (code != 0) return code;
            state[player] = PlayerRow.Lobby;
            lobby.SetCheck(player, 1);   // checking until its content summary is in (LobbyDoor)
            Touch();
            return 0;
        }

        public static string KeyOf(string identity, int player)
        {
            return Sha256Hex.Of(Encoding.UTF8.GetBytes((identity ?? "") + "|" + player));
        }

        // The content check at the door: the same content passes, anything else is refused (and the glue closes the
        // link).
        public void Checked(int player, bool same)
        {
            if (!lobby.Has(player)) return;
            lobby.SetCheck(player, same ? 2 : 3);
            Touch();
        }

        // The member's link closed. In the lobby its seat is free at once; once let in it is a drop and the seat is
        // kept for its rejoin (M4's rule; the host can free it with Kick).
        public void Gone(int player)
        {
            if (player == hostId || !lobby.Has(player)) return;
            heldClaim.Remove(player);
            int s = StateOf(player);
            if (s == PlayerRow.Lobby) Forget(player, false);
            else
            {
                lobby.Drop(player);
                if (s != PlayerRow.Away) state[player] = PlayerRow.Dropped;
            }
            Touch();
        }

        // A released member whose join ended before it loaded, its link closed: M4 released its seat (only a loaded
        // seat is kept for a rejoin), so the room lets it go too and its lobby claim is free. A retry comes on a new
        // link as a new member.
        public void JoinEnded(int player)
        {
            if (!Released(player)) return;
            Forget(player, false);
            Touch();
        }

        // Its goodbye said it left on purpose (it comes after Gone): away, its seat kept, no drop.
        public void LeftForGood(int player)
        {
            if (StateOf(player) == PlayerRow.Dropped) { state[player] = PlayerRow.Away; Touch(); }
        }

        // A released member is back on a new link (M4's rejoin).
        public void Back(int player)
        {
            int s = StateOf(player);
            if (s < 0) return;
            lobby.Return(player);
            if (s == PlayerRow.Dropped || s == PlayerRow.Away) state[player] = PlayerRow.Rejoining;
            Touch();
        }

        // How long a dropped member is shown as coming back: the joiner's reconnects (2 + 4 + 8 + 16 + 32 s of waits,
        // each with its connect) are over well inside it.
        public const long RejoinWindowMs = 90000;

        // A released member whose link is down (polled by the glue): its rejoin is expected only while its game is
        // still in the host's Steam lobby (a reconnect through Steam needs it; true where that is not known) and
        // within RejoinWindowMs of the drop. Past either, a Dropped member (a lost goodbye looks like a drop) shows
        // Away, not reconnecting; its seat stays kept, and a rejoin still makes it Rejoining.
        public void StillGone(int player, bool inSteamLobby, long nowMs)
        {
            if (StateOf(player) != PlayerRow.Dropped) { droppedSince.Remove(player); return; }
            long since;
            if (!droppedSince.TryGetValue(player, out since)) { since = nowMs; droppedSince[player] = nowMs; }
            if (inSteamLobby && nowMs - since < RejoinWindowMs) return;
            state[player] = PlayerRow.Away;
            droppedSince.Remove(player);
            Touch();
        }

        // A released member as M4's join sees it now (polled by the glue).
        public void Follow(int player, bool connected, bool inGame)
        {
            int s = StateOf(player);
            if (s <= PlayerRow.Lobby || player == hostId) return;
            int now;
            if (inGame) now = PlayerRow.InGame;
            else if (!connected) now = s == PlayerRow.Away ? PlayerRow.Away : PlayerRow.Dropped;
            else if (s == PlayerRow.Joining) now = PlayerRow.Joining;
            else now = PlayerRow.Rejoining;
            if (now == s) return;
            state[player] = now;
            if (connected) lobby.Return(player);
            Touch();
        }

        public void Kick(int player)
        {
            if (player == hostId || !lobby.Has(player)) return;
            lobby.Kick(player);
            Forget(player, true);
            Touch();
        }

        public void Ban(int player)
        {
            if (player == hostId || !lobby.Has(player)) return;
            lobby.Ban(player);
            Forget(player, true);
            Touch();
        }

        void Forget(int player, bool gone)
        {
            if (!gone) lobby.Remove(player);
            state.Remove(player);
            released.Remove(player);
            heldClaim.Remove(player);
            lastClaimMs.Remove(player);
            droppedSince.Remove(player);
        }

        // ---- claims and ready ----

        // A member's claim (its LobbyClaim, or the host's own pick); "" releases. The host acts on at most 4 a second
        // per member and keeps the newest: a claim inside the window is held for Pump. 0 = applied or held; else why
        // not (the roster answers a member either way; the code is for the host's own picks and the log).
        public int Claim(int player, string agency, long nowMs)
        {
            if (!lobby.Has(player)) return LobbyCodes.NotClaimable;
            if (player == hostId)
            {
                if (fixedHost != null || Phase != SessionPhase.Lobby) return LobbyCodes.NotClaimable;
                return Apply(player, agency, nowMs);
            }
            if (StateOf(player) != PlayerRow.Lobby) return LobbyCodes.NotClaimable;   // let in: M4's claim decides
            long last;
            if (lastClaimMs.TryGetValue(player, out last) && nowMs - last < ClaimEveryMs)
            {
                heldClaim[player] = agency ?? "";
                return 0;
            }
            return Apply(player, agency, nowMs);
        }

        // The held claims whose time has come.
        public void Pump(long nowMs)
        {
            if (heldClaim.Count == 0) return;
            foreach (int id in new List<int>(heldClaim.Keys))
            {
                long last;
                if (lastClaimMs.TryGetValue(id, out last) && nowMs - last < ClaimEveryMs) continue;
                string agency = heldClaim[id];
                heldClaim.Remove(id);
                if (StateOf(id) == PlayerRow.Lobby) Apply(id, agency, nowMs);
            }
        }

        int Apply(int player, string agency, long nowMs)
        {
            lastClaimMs[player] = nowMs;
            heldClaim.Remove(player);
            int code;
            if (Phase == SessionPhase.Lobby) code = lobby.Claim(player, agency);
            else if (Phase == SessionPhase.InGame && player != hostId)
            {
                code = lobby.ClaimLate(player, agency);
                if (code == 0) Release(player);
            }
            else code = LobbyCodes.NotClaimable;   // the host's world is loading: claims wait for it
            Touch();
            return code;
        }

        public void Ready(int player, bool ready)
        {
            if (StateOf(player) != PlayerRow.Lobby || Phase != SessionPhase.Lobby) return;
            lobby.SetReady(player, ready);
            Touch();
        }

        // The host switches a free agency between an AI rival and not in the game (a new game, in the lobby only).
        public bool SetAgencyRule(string agency, AgencyStanding rule)
        {
            if (Phase != SessionPhase.Lobby) return false;
            bool done = lobby.SetAgencyRule(agency, rule);
            if (done) Touch();
            return done;
        }

        // ---- the start ----

        // Why Start is off now (a UiText key), or null.
        public string StartBlocked()
        {
            if (Phase != SessionPhase.Lobby) return NotNow;
            string why;
            return lobby.CanStart(out why) ? null : why;
        }

        // The host's Start (anyway: "Start anyway", which leaves out whoever has no agency or no passed check). null =
        // the start set is fixed and the phase is Starting; else the UiText key why not. The members left out lose
        // their claims and stay in the lobby, free to join the running game later; the start set's agencies are the
        // human agencies the host's world is built with.
        public string Start(bool anyway)
        {
            if (Phase != SessionPhase.Lobby) return NotNow;
            string why;
            if (!anyway && !lobby.CanStart(out why)) return why;
            List<int> go = lobby.StartAnyway();
            if (!go.Contains(hostId)) return "lobby.blocked.hostAgency";
            foreach (int id in lobby.Ids())
                if (!go.Contains(id)) { lobby.Release(id); lobby.SetReady(id, false); }
            starting.Clear();
            starting.AddRange(go);
            humans.Clear();
            foreach (int id in go)
            {
                string a = lobby.ClaimedAgency(id);
                if (a != null && !humans.Contains(a)) humans.Add(a);
            }
            heldClaim.Clear();
            lobby.Phase = SessionPhase.Starting;
            Touch();
            return null;
        }

        // The start set's agencies (the host's first), fixed at Start; late joiners' agencies are added when let in.
        public List<string> HumanAgencies() { return new List<string>(humans); }

        // The start set's members, the host first.
        public List<int> StartSet() { return new List<int>(starting); }

        // The agencies the host's new world starts with as AI rivals: in the game and held by nobody.
        public List<string> AiRivals()
        {
            var list = new List<string>();
            foreach (KeyValuePair<string, AgencyStanding> s in lobby.States())
                if (s.Value == AgencyStanding.AiRival) list.Add(s.Key);
            return list;
        }

        // A new world's rivals (StartGameConfiguration.enabledRivalCorporations): every agency in it but the host's
        // own, the other humans first (DD22: a human agency left out of the list is not in the game at all), then the
        // AI rivals. A claimed agency starts as a rival and M4's join switches its AI off once its player has loaded.
        public List<string> WorldRivals()
        {
            var list = new List<string>();
            string host = HostAgency;
            foreach (string h in humans) if (h != host && !list.Contains(h)) list.Add(h);
            foreach (string a in AiRivals()) if (a != host && !list.Contains(a)) list.Add(a);
            return list;
        }

        // The host's world load has begun.
        public void Loading()
        {
            if (Phase == SessionPhase.Starting) { lobby.Phase = SessionPhase.Loading; Touch(); }
        }

        // The host's world did not come: everyone is back in the lobby, nobody ready.
        public void StartFailed()
        {
            if (Phase != SessionPhase.Starting && Phase != SessionPhase.Loading) return;
            lobby.Phase = SessionPhase.Lobby;
            foreach (int id in lobby.Ids()) lobby.SetReady(id, false);
            starting.Clear();
            humans.Clear();
            Touch();
        }

        // The host's world has loaded: the session runs, and the start set (but the host) is let in. Returns them.
        public List<int> WorldLoaded()
        {
            var go = new List<int>();
            if (Phase != SessionPhase.Starting && Phase != SessionPhase.Loading) return go;
            lobby.Phase = SessionPhase.InGame;
            state[hostId] = PlayerRow.InGame;
            foreach (int id in starting)
            {
                if (id == hostId || StateOf(id) != PlayerRow.Lobby) continue;   // gone since the start
                Release(id);
                go.Add(id);
            }
            Touch();
            return go;
        }

        void Release(int player)
        {
            state[player] = PlayerRow.Joining;
            lobby.SetReady(player, false);
            if (!released.Contains(player)) released.Add(player);
            string a = lobby.ClaimedAgency(player);
            if (a != null && !humans.Contains(a)) humans.Add(a);
        }

        // The members let in since the last call (the glue starts M4's join for each, once).
        public List<int> TakeReleased()
        {
            var list = new List<int>(released);
            released.Clear();
            return list;
        }

        public void End()
        {
            if (Phase == SessionPhase.Ending || Phase == SessionPhase.Idle) return;
            lobby.Phase = SessionPhase.Ending;
            Touch();
        }

        // ---- the roster ----

        public void Touch() { dirty = true; }

        // A new Roster is owed and its rate allows it now.
        public bool RosterDue(long nowMs) { return dirty && (!rosterSent || nowMs - lastRosterMs >= RosterEveryMs); }

        // The roster as it is now, with each member's state and ping; counts as sent.
        public Roster TakeRoster(long nowMs, Func<int, int> pingMs)
        {
            Roster r = Peek(pingMs);
            lastRosterMs = nowMs;
            rosterSent = true;
            dirty = false;
            return r;
        }

        // The same, for the host's own view; nothing is counted.
        public Roster Peek(Func<int, int> pingMs)
        {
            Roster r = lobby.Roster(++seq);
            foreach (RosterPlayer p in r.Players)
            {
                int s;
                p.State = state.TryGetValue(p.Id, out s) ? s : PlayerRow.Lobby;
                if (pingMs != null && p.Id != hostId)
                {
                    int ms = pingMs(p.Id);
                    p.PingMs = ms < 0 ? -1 : Math.Min(ms, 600000);
                }
            }
            return r;
        }
    }
}
