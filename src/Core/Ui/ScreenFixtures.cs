using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // A session that only shows a fixed state (test seams only): the HUD and the PLAYERS screen read it exactly as
    // they read the real one, so a state the early session cannot reach (co-op, a drop with Carry on, a joiner's
    // progress, the host's Remove) can be put on screen for its screenshot. Its actions change nothing but this
    // fixture, and each one is logged.
    public sealed class HudFixtureSession : ISessionActions
    {
        readonly NoticeQueue notices = new NoticeQueue();
        public readonly string State;
        public readonly List<string> Calls = new List<string>();
        public bool Hover;                  // the HUD treats the pointer as on the speed buttons
        public int AskRemove = -1;          // the PLAYERS screen opens with the Remove question for this player
        public bool AskEnd;                 // ... or with the End session question

        // profile, playerPath: the game's own (Boot.Profile, MenuSessions.PlayerPath); refused unless the test seams
        // are open, so no path of a player's game can make one.
        public HudFixtureSession(string state, Capability caps, SessionView view, string profile, bool playerPath)
        {
            if (!TestSeams.Open(profile, playerPath))
                throw new InvalidOperationException("the screens' fixtures are for the test seams only");
            State = state;
            Capabilities = caps;
            View = view;
        }

        public Capability Capabilities { get; set; }
        public SessionView View { get; private set; }
        public NoticeQueue Notices { get { return notices; } }
        public event Action Changed;

        void Did(string what)
        {
            Calls.Add(what);
            Log.Info("ui fixture " + State + ": " + what);
            Action c = Changed;
            if (c != null) c();
        }

        public string HostFromSave(string ownSaveName) { Did("host save"); return null; }
        public string HostNewGame(NewGameSetup setup) { Did("host new game"); return null; }
        public string HostMpSave(string mpSaveName) { Did("host mp save"); return null; }
        public string Join(string joinCodeText, string playerName) { Did("join"); return null; }
        public string PickAgency(string agencyId) { Did("pick " + agencyId); return null; }
        public void SetReady(bool ready) { Did("ready " + ready); }
        public void Start(bool anyway) { Did("start " + anyway); }
        public void Ban(int player) { Did("ban " + player); }
        public void SetAgencyRule(string agencyId, AgencyStanding rule) { Did("rule " + agencyId + " " + rule); }
        public void Leave() { Did("leave"); }
        public void EndSession() { Did("end session"); }
        public string PlaySolo(string mpSaveName, string agencyId) { Did("solo"); return null; }
        public string NewCode() { Did("new code"); return null; }

        // Removing a player takes its row and its agency's hold away, as the host's kick would.
        public void Kick(int player)
        {
            View.Players.RemoveAll(r => r != null && r.Id == player);
            View.Pauses.RemoveAll(p => p != null && p.Player == player);
            foreach (AgencyRow a in View.Agencies) a.Holders.Remove(player);
            Did("kick " + player);
        }

        // Carry on: the drop pause goes, the dropped seat stays (DD15).
        public void CarryOnWithout(int player)
        {
            View.Pauses.RemoveAll(p => p != null && p.Player == player && p.Why == PauseWhy.Dropped);
            Did("carry on without " + player);
        }
    }

    // The named states of the HUD and the PLAYERS screen, drawn from the approved mock-ups (boards 8, 9 and 14).
    // Each is built through the real view code (SessionView.ApplyClock, SpeedMarks.From) from a Clock and a roster.
    public static class ScreenFixtures
    {
        public static readonly string[] HudStates =
            { "differ", "planning", "same", "hover", "paused", "joining", "dropped", "status", "rejoin", "many",
              "locked" };
        public static readonly string[] PlayersStates =
            { "host", "joiner", "early", "remove", "removelast", "end", "log", "long" };

        // M4SessionAdapter's own set, so an "early" state shows what Track U shows.
        public const Capability Early = Capability.HostSave | Capability.HostNewGame | Capability.JoinByCode |
                                        Capability.PickAgency | Capability.Players;
        public const Capability Full = Early | Capability.PingAll | Capability.Kick | Capability.Ban |
                                       Capability.CarryOn | Capability.EndSession;

        const float X2 = 50000f, X4 = 100000f, X8 = 200000f;

        static PlayerRow P(int id, string name, string agency, int ping = -1, int state = PlayerRow.InGame)
        {
            return new PlayerRow { Id = id, Name = name, Agency = agency, PingMs = ping, State = state };
        }

        static Vote V(int player, float scale) { return new Vote { Player = player, Scale = scale }; }
        static Pause Why(int player, int why) { return new Pause { Player = player, Reason = why }; }

        // The board's four players: Alex (the host) on NASA, Sam on ESA, a third player on CNSA, Priya on ISRO.
        static List<PlayerRow> Four()
        {
            var rows = new List<PlayerRow>
            {
                P(0, "Alex", "nasa"), P(1, "Sam", "esa", 31), P(2, "Jordan", "cnsa", 140), P(3, "Priya", "isro", 19),
            };
            rows[0].Host = true;
            rows[0].Me = true;
            return rows;
        }

        static SessionView View(List<PlayerRow> rows, bool host, int me, float scale, Vote[] votes, Pause[] pauses,
                                long nowMs, long planningAgeMs = 72000)
        {
            var v = new SessionView { Phase = SessionPhase.InGame, IsHost = host, MyId = me, SaveTitle = "Fixture" };
            foreach (PlayerRow r in rows) { r.Me = r.Id == me; r.Host = r.Id == 0; }
            v.Players = SessionView.GroupByAgency(rows);
            var tracker = new PauseTracker();
            if (pauses != null)
                foreach (Pause p in pauses)
                    if (p.Reason == PauseWhy.Planning) tracker.SinceFor(p.Player, p.Reason, nowMs - planningAgeMs);
            v.ApplyClock(new Clock { Scale = scale, Votes = votes, Pauses = pauses }, tracker, nowMs);
            foreach (PlayerRow r in v.Players)
            {
                if (string.IsNullOrEmpty(r.Agency)) continue;
                AgencyRow a = v.Agency(r.Agency);
                if (a == null)
                {
                    a = new AgencyRow { Id = r.Agency, State = AgencyStanding.Human, Playable = true, EpochAllows = true };
                    v.Agencies.Add(a);
                }
                a.Holders.Add(r.Id);
            }
            return v;
        }

        // null for a name that is not a HUD state.
        public static HudFixtureSession Hud(string state, long nowMs, string profile, bool playerPath)
        {
            List<PlayerRow> rows = Four();
            Vote[] differ = { V(0, X4), V(1, X8), V(2, X2), V(3, X8) };
            Vote[] same = { V(0, X4), V(1, X4), V(2, X4), V(3, X4) };
            SessionView v;
            Capability caps = Early | Capability.PingAll;
            switch (state)
            {
                case "differ": v = View(rows, true, 0, X2, differ, null, nowMs); break;
                case "planning":
                    v = View(rows, true, 0, 0f, differ, new[] { Why(1, PauseWhy.Planning) }, nowMs);
                    break;
                case "same": case "hover": v = View(rows, true, 0, X4, same, null, nowMs); break;
                case "paused":
                    v = View(rows, true, 0, 0f, new[] { V(0, X4), V(1, 0f), V(2, X4), V(3, X4) }, null, nowMs);
                    break;
                case "joining":
                    PlayerRow kim = P(4, "Kim", "", -1, PlayerRow.Joining);
                    kim.JoinPct = 45;
                    kim.JoinSecsLeft = 30;
                    rows.Add(kim);
                    v = View(rows, true, 0, 0f, same, new[] { Why(4, PauseWhy.Joining) }, nowMs);
                    break;
                case "dropped":
                    rows[1].State = PlayerRow.Joining;
                    rows[2].State = PlayerRow.Dropped;
                    rows[2].PingMs = -1;
                    rows.Add(P(5, "Tom", "isro", 210));
                    caps |= Capability.CarryOn;
                    v = View(rows, true, 0, 0f, new[] { V(0, X4), V(5, X2), V(3, X4) },
                             new[] { Why(1, PauseWhy.Joining), Why(2, PauseWhy.Dropped) }, nowMs);
                    break;
                case "status":
                    v = View(rows, false, 1, X4, same, null, nowMs);
                    v.Status = StatusLine.Make(StatusLine.WaitingForClock);
                    caps = Early;
                    break;
                case "rejoin":
                    rows[1].State = PlayerRow.Rejoining;
                    v = View(rows, false, 1, 0f, same, new[] { Why(1, PauseWhy.Joining) }, nowMs);
                    caps = Early;
                    break;
                case "many":
                    rows.Add(P(5, "Tom", "isro", 210));
                    rows.Add(P(6, "Maya", "nasa", 24));
                    rows.Add(P(7, "Leo", "esa", 88));
                    v = View(rows, true, 0, X4, new[]
                    {
                        V(0, X4), V(1, X4), V(2, X4), V(3, X8), V(5, X4), V(6, X4), V(7, X8),
                    }, null, nowMs);
                    break;
                case "locked":
                    v = View(rows, true, 0, 0f, same, null, nowMs);
                    v.ClockLocked = true;
                    break;
                default: return null;
            }
            return new HudFixtureSession(state, caps, v, profile, playerPath) { Hover = state == "hover" };
        }

        // null for a name that is not a PLAYERS state.
        public static HudFixtureSession Players(string state, long nowMs, string profile, bool playerPath)
        {
            List<PlayerRow> rows;
            SessionView v;
            Capability caps = Full;
            switch (state)
            {
                case "host": case "remove": case "removelast": case "end": case "long":
                    rows = CoOp();
                    if (state == "long")
                        rows[2].Name = "Zoë 名前 <b>x</b> WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW";
                    v = View(rows, true, 0, 0f, CoOpVotes(), new[] { Why(7, PauseWhy.Planning),
                                                                     Why(2, PauseWhy.Dropped) }, nowMs);
                    break;
                case "joiner":
                    rows = CoOp();
                    foreach (PlayerRow r in rows) r.PingMs = r.Id == 1 || r.Id == 0 ? 45 : -1;
                    v = View(rows, false, 1, 0f, CoOpVotes(), new[] { Why(7, PauseWhy.Planning),
                                                                      Why(2, PauseWhy.Dropped) }, nowMs);
                    caps = Early;
                    break;
                case "early": case "log":
                    rows = Four();
                    rows[2].State = PlayerRow.Joining;
                    rows[2].JoinPct = 45;
                    rows[2].JoinSecsLeft = 30;
                    v = View(rows, true, 0, 0f, new[] { V(0, X4), V(1, X8), V(3, X4) },
                             new[] { Why(2, PauseWhy.Joining) }, nowMs);
                    caps = Early | Capability.PingAll;
                    break;
                default: return null;
            }
            var f = new HudFixtureSession(state, caps, v, profile, playerPath);
            Notices(f.Notices, state == "log" ? 60 : 6, nowMs);
            if (state == "remove") f.AskRemove = 1;
            if (state == "removelast") f.AskRemove = 2;
            if (state == "end") f.AskEnd = true;
            return f;
        }

        // Board 14: seven players over four agencies (co-op), Leo planning, the CNSA player dropped.
        static List<PlayerRow> CoOp()
        {
            return new List<PlayerRow>
            {
                P(0, "Alex", "nasa"), P(6, "Maya", "nasa", 24), P(1, "Sam", "esa", 31), P(7, "Leo", "esa", 88),
                P(2, "Jordan", "cnsa", -1, PlayerRow.Dropped), P(3, "Priya", "isro", 19), P(5, "Tom", "isro", 210),
            };
        }

        static Vote[] CoOpVotes()
        {
            return new[] { V(0, X4), V(6, X4), V(1, X8), V(7, X4), V(3, X2), V(5, X4) };
        }

        static readonly string[] NoticeKeys =
            { "join.notice.joining", "join.notice.joined", "join.notice.rejoined", "join.notice.waiting" };
        static readonly string[] NoticeNames = { "Sam", "Jordan", "Priya", "Tom", "Maya", "Leo" };

        static void Notices(NoticeQueue q, int count, long nowMs)
        {
            for (int i = 0; i < count; i++)
            {
                string key = i % 9 == 8 ? "join.notice.couldNotJoin" : NoticeKeys[i % NoticeKeys.Length];
                int kind = key == "join.notice.couldNotJoin" ? NoticeQueue.Warning : NoticeQueue.Routine;
                // spread over time, so the queue's flood rule never drops one from the screen's log
                q.Push(key, new object[] { NoticeNames[i % NoticeNames.Length] }, kind,
                       nowMs - (count - i) * NoticeQueue.WindowMs);
            }
        }
    }
}
