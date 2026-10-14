using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // A session that is only a view (test seams only): the lobby's and the picker's dev triggers open a screen on one
    // of these, so a state the early session cannot reach (the full track's Ready, Start and Kick, co-op) can be
    // screenshot, and the screen's clicks act on the view as the real session would answer them. Every call is
    // recorded; nothing reaches a network or a game.
    public sealed class FixtureSession : ISessionActions
    {
        readonly NoticeQueue notices = new NoticeQueue();
        public Capability Caps;
        public SessionView Current = new SessionView();
        public readonly List<string> Calls = new List<string>();
        // agency -> the claim refusal code the "host" answers with (the M4 claim's refusal)
        public readonly Dictionary<string, int> RefuseClaim = new Dictionary<string, int>(StringComparer.Ordinal);
        public string Overlay;              // a confirmation the state opens over the screen: "anyway" or "kick"
        int codes;

        public event Action Changed;

        public Capability Capabilities { get { return Caps; } }
        public SessionView View { get { return Current; } }
        public NoticeQueue Notices { get { return notices; } }

        void Raise()
        {
            Action a = Changed;
            if (a != null) a();
        }

        public string HostFromSave(string ownSaveName) { Calls.Add("host save"); return "menu.notYet"; }
        public string HostNewGame(NewGameSetup setup) { Calls.Add("host new"); return "menu.notYet"; }
        public string HostMpSave(string mpSaveName) { Calls.Add("host mp"); return "menu.notYet"; }
        public string Join(string joinCodeText, string playerName) { Calls.Add("join"); return "menu.notYet"; }
        public string PlaySolo(string mpSaveName, string agencyId) { Calls.Add("solo"); return "menu.notYet"; }

        // The claim as M4 answers it: sent at once, a refusal comes back later as the status line, and the refused
        // agency is no longer claimable.
        public string PickAgency(string agencyId)
        {
            Calls.Add("pick " + agencyId);
            AgencyRow a = Current.Agency(agencyId);
            if (a == null) return "join.badAgency";
            int code;
            if (RefuseClaim.TryGetValue(agencyId, out code))
            {
                a.Claimable = false;
                Current.Status = StatusLine.Make("join.refused." + code);
                Raise();
                return null;
            }
            PlayerRow me = LobbyText.Me(Current);
            if (me != null)
            {
                foreach (AgencyRow x in Current.Agencies) if (x != null) x.Holders.Remove(me.Id);
                me.Agency = agencyId;
                a.Holders.Add(me.Id);
            }
            Current.Status = null;
            Raise();
            return null;
        }

        public void SetReady(bool ready)
        {
            Calls.Add("ready " + ready);
            PlayerRow me = LobbyText.Me(Current);
            if (me != null) me.Ready = ready;
            Raise();
        }

        public void Start(bool anyway) { Calls.Add(anyway ? "start anyway" : "start"); }

        public void Kick(int player)
        {
            Calls.Add("kick " + player);
            Current.Players.RemoveAll(r => r != null && r.Id == player);
            foreach (AgencyRow a in Current.Agencies) if (a != null) a.Holders.Remove(player);
            Current.StartLeavesOut.Remove(player);
            Raise();
        }

        public void Ban(int player) { Calls.Add("ban " + player); }

        public void SetAgencyRule(string agencyId, AgencyStanding rule)
        {
            Calls.Add("rule " + agencyId + " " + rule);
            AgencyRow a = Current.Agency(agencyId);
            if (a != null) a.State = rule;
            Raise();
        }

        public void CarryOnWithout(int player) { Calls.Add("carry on " + player); }
        public void Leave() { Calls.Add("leave"); }
        public void EndSession() { Calls.Add("end"); }

        public string NewCode()
        {
            Calls.Add("new code");
            Current.JoinCode = LobbyFixtures.Code(++codes);
            Raise();
            return null;
        }
    }

    // The named states the lobby and the picker are shown in for their screenshots (the mock-up boards' states).
    public static class LobbyFixtures
    {
        public static readonly string[] LobbyStates =
            { "host-early", "joiner-early", "host-full", "host-ready", "host-coop", "joiner-coop", "empty",
              "start-anyway", "kick" };
        public static readonly string[] PickerStates = { "joiner", "refused", "coop", "states", "host-new" };

        // Track U's capabilities on M4 (M4SessionAdapter): the host also has every player's ping
        public const Capability Early = Capability.HostSave | Capability.HostNewGame | Capability.JoinByCode |
                                        Capability.PickAgency | Capability.Players;
        // the full track's lobby at the menu
        public const Capability Full = Early | Capability.LobbyAtMenu | Capability.Ready | Capability.Start |
                                       Capability.StartAnyway | Capability.Kick | Capability.AgencyStates |
                                       Capability.NamesEverywhere | Capability.PingAll;

        static readonly string[] Agencies = { "nasa", "esa", "cnsa", "roscosmos", "solex" };

        // Not a join code: the screens show it in a password field, and a test may show it.
        public static string Code(int n) { return "SEMP-TEST-" + n.ToString("0000") + "-NOT-A-REAL-CODE"; }

        public static FixtureSession Lobby(string state)
        {
            switch (state)
            {
                case "host-early": return HostEarly();
                case "joiner-early": return JoinerEarly();
                case "host-full": return HostFull(false);
                case "host-ready": return HostFull(true);
                case "host-coop": return Coop(true);
                case "joiner-coop": return Coop(false);
                case "empty": return Empty();
                case "start-anyway": { FixtureSession s = HostFull(false); s.Overlay = "anyway"; return s; }
                case "kick": { FixtureSession s = HostFull(false); s.Overlay = "kick"; return s; }
                default: return null;
            }
        }

        public static FixtureSession Picker(string state)
        {
            switch (state)
            {
                case "joiner": return PickerJoiner(false);
                case "refused": return PickerJoiner(true);
                case "coop": { FixtureSession s = Coop(true); s.Current.Phase = SessionPhase.Lobby; return s; }
                case "states": return PickerStatesFixture();
                case "host-new": return PickerHostNew();
                default: return null;
            }
        }

        static PlayerRow P(int id, string name, string agency, int state = PlayerRow.InGame, int ping = -1)
        {
            return new PlayerRow { Id = id, Name = name, Agency = agency, State = state, PingMs = ping };
        }

        static AgencyRow A(string id, AgencyStanding state, bool claimable)
        {
            return new AgencyRow { Id = id, State = state, Claimable = claimable, Playable = true, EpochAllows = true };
        }

        // Every agency of the game; the held ones Human with their holders.
        static void Seat(SessionView v, AgencyStanding free, bool sharing)
        {
            foreach (string id in Agencies) v.Agencies.Add(A(id, free, true));
            foreach (PlayerRow r in v.Players)
            {
                AgencyRow a = v.Agency(r.Agency);
                if (a == null) continue;
                a.State = AgencyStanding.Human;
                a.Holders.Add(r.Id);
                a.Claimable = sharing;
            }
        }

        // M4's host in game: the players it knows, a joiner still loading, every ping (the host times them).
        static FixtureSession HostEarly()
        {
            var v = new SessionView { Phase = SessionPhase.InGame, IsHost = true, MyId = 0, JoinCode = Code(0),
                                      SaveTitle = "multiplayertest", SaveDate = "13 Mar 2022" };
            PlayerRow host = P(0, "Alex", "nasa"); host.Host = true; host.Me = true;
            PlayerRow sam = P(1, "Sam", "esa", PlayerRow.Joining, 31); sam.JoinPct = 45;
            v.Players.AddRange(new[] { host, sam, P(2, "Jordan", "cnsa", PlayerRow.InGame, 210) });
            HeldOnly(v);
            return new FixtureSession { Caps = Early | Capability.PingAll, Current = v };
        }

        // M4's client in game: names only from notices ("Player N" otherwise), its own round trip only.
        static FixtureSession JoinerEarly()
        {
            var v = new SessionView { Phase = SessionPhase.InGame, IsHost = false, MyId = 2 };
            PlayerRow host = P(0, "Alex", "nasa", PlayerRow.InGame, 38); host.Host = true;
            PlayerRow me = P(2, "Sam", "esa", PlayerRow.InGame, 38); me.Me = true;
            v.Players.AddRange(new[] { host, P(1, "Player 1", "cnsa"), me });
            HeldOnly(v);
            return new FixtureSession { Caps = Early, Current = v };
        }

        // As M4SessionAdapter fills it in game: only the agencies players hold.
        static void HeldOnly(SessionView v)
        {
            foreach (PlayerRow r in v.Players)
            {
                AgencyRow a = v.Agency(r.Agency);
                if (a == null) { a = new AgencyRow { Id = r.Agency, State = AgencyStanding.Human, Playable = true,
                                                     EpochAllows = true }; v.Agencies.Add(a); }
                a.Holders.Add(r.Id);
            }
        }

        // The full track's lobby at the menu, one player per agency, a new game: Roscosmos left out by the host.
        static FixtureSession HostFull(bool allReady)
        {
            var v = new SessionView { Phase = SessionPhase.Lobby, IsHost = true, MyId = 0, JoinCode = Code(0),
                                      NewGame = true };
            PlayerRow host = P(0, "Alex", "nasa"); host.Host = true; host.Me = true; host.State = PlayerRow.Lobby;
            PlayerRow sam = P(1, "Sam", "esa", PlayerRow.Lobby, 31); sam.Ready = true;
            PlayerRow jordan = P(2, "Jordan", "cnsa", PlayerRow.Lobby, 210); jordan.Ready = allReady;
            v.Players.AddRange(new[] { host, sam, jordan });
            Seat(v, AgencyStanding.AiRival, false);
            v.Agency("roscosmos").State = AgencyStanding.NotInGame;
            v.Agency("solex").LastHolder = "Priya";
            if (!allReady) { v.StartBlockedWhy = "lobby.blocked.notReady"; v.StartLeavesOut.Add(2); }
            return new FixtureSession { Caps = Full, Current = v };
        }

        // Co-op: seven players in four agencies, more than the list shows (it scrolls), Solex an AI rival.
        static FixtureSession Coop(bool asHost)
        {
            var v = new SessionView { Phase = SessionPhase.Lobby, IsHost = asHost, MyId = asHost ? 0 : 2,
                                      JoinCode = asHost ? Code(0) : null, SaveTitle = "multiplayertest",
                                      SaveDate = "13 Mar 2022" };
            var rows = new List<PlayerRow>
            {
                P(0, "Alex", "nasa"), P(1, "Maya", "nasa", PlayerRow.Lobby, 24),
                P(2, "Sam", "esa", PlayerRow.Lobby, 31), P(3, "Leo", "esa", PlayerRow.Lobby, 88),
                P(4, "Jordan", "cnsa", PlayerRow.Lobby, 210), P(5, "Priya", "roscosmos", PlayerRow.Lobby, 19),
                P(6, "Maximilian-Alexander", "roscosmos", PlayerRow.Lobby, 140),
            };
            rows[0].Host = true; rows[0].State = PlayerRow.Lobby;
            foreach (PlayerRow r in rows) r.Ready = r.Id != 4 && r.Id != 6;
            rows[asHost ? 0 : 2].Me = true;
            v.Players.AddRange(rows);
            Seat(v, AgencyStanding.AiRival, true);
            v.StartBlockedWhy = "lobby.blocked.notReady";
            v.StartLeavesOut.AddRange(new[] { 4, 6 });
            return new FixtureSession { Caps = Full, Current = v };
        }

        static FixtureSession Empty()
        {
            var v = new SessionView { Phase = SessionPhase.Lobby, IsHost = true, MyId = 0, JoinCode = Code(0),
                                      NewGame = true };
            PlayerRow host = P(0, "Alex", "nasa", PlayerRow.Lobby); host.Host = true; host.Me = true;
            v.Players.Add(host);
            Seat(v, AgencyStanding.AiRival, false);
            return new FixtureSession { Caps = Full, Current = v };
        }

        // A joiner at the menu after the join code: M4 has no roster, so every agency of its own content is offered.
        static FixtureSession PickerJoiner(bool refused)
        {
            var v = new SessionView { Phase = SessionPhase.Lobby };
            foreach (string id in Agencies) v.Agencies.Add(A(id, AgencyStanding.AiRival, true));
            var s = new FixtureSession { Caps = Early, Current = v };
            s.RefuseClaim["esa"] = JoinCodes.AgencyTaken;
            s.RefuseClaim["cnsa"] = JoinCodes.AgencyTaken;
            if (refused)
            {
                v.Agency("esa").Claimable = false;
                v.Status = StatusLine.Make("join.refused." + JoinCodes.AgencyTaken);
            }
            return s;
        }

        // Every card state at once: taken, refused, not in the game, the start date, not playable, idle, last holder.
        static FixtureSession PickerStatesFixture()
        {
            var v = new SessionView { Phase = SessionPhase.Lobby, MyId = 1 };
            PlayerRow host = P(0, "Alex", "nasa", PlayerRow.Lobby); host.Host = true;
            PlayerRow me = P(1, "Sam", "", PlayerRow.Lobby); me.Me = true;
            v.Players.AddRange(new[] { host, me });
            v.Agencies.Add(new AgencyRow { Id = "nasa", State = AgencyStanding.Human, Playable = true, EpochAllows = true,
                                           Holders = { 0 } });
            v.Agencies.Add(A("esa", AgencyStanding.Idle, true));
            AgencyRow cnsa = A("cnsa", AgencyStanding.AiRival, true); cnsa.EpochAllows = false;
            v.Agencies.Add(cnsa);
            AgencyRow ros = A("roscosmos", AgencyStanding.NotInGame, false); ros.LastHolder = "Priya";
            v.Agencies.Add(ros);
            AgencyRow solex = A("solex", AgencyStanding.AiRival, true); solex.Playable = false;
            v.Agencies.Add(solex);
            return new FixtureSession { Caps = Full, Current = v };
        }

        // The host's own pick before a new game's pages (M8 Track U): every agency free.
        static FixtureSession PickerHostNew()
        {
            var v = new SessionView { Phase = SessionPhase.Idle, IsHost = false };
            foreach (string id in Agencies) v.Agencies.Add(A(id, AgencyStanding.AiRival, true));
            return new FixtureSession { Caps = Early, Current = v };
        }
    }
}
