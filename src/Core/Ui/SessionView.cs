using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Everything the screens read about the session; they read nothing else. Rebuilt on each Roster and Clock by
    // M4SessionAdapter (from M4's state) or SessionRoot (from Roster and Clock). A field the source cannot fill keeps
    // its "unknown" value (JoinPct -1, PingMs -1, Name "Player N").
    public sealed class SessionView
    {
        public SessionPhase Phase; public bool IsHost; public int MyId = -1;
        public List<PlayerRow> Players = new List<PlayerRow>();   // grouped by agency for the lobby and PLAYERS screen
        public SpeedMarks Speed;                                  // null outside a game
        public List<PauseLine> Pauses = new List<PauseLine>();
        public StatusLine Status;                                 // the one status line: a key and its arguments
        public List<AgencyRow> Agencies = new List<AgencyRow>();   // every agency, for the lobby and the picker
        public string SaveTitle, SaveDate;                        // the hosted save: title, date (null for a new game)
        public bool NewGame;                                      // a new game, not a hosted save
        public string JoinCode;                                   // host only, else null; never logged or printed
        public string EndedWhy;                                   // the UiText key once the run has ended, else null
        public string[] EndedReport;                              // the host's refusal lines (peer text), or null
        public string EndedHost;                                  // the host's name (peer text) for it, or null
        public bool ClockLocked;                                  // the host's clock cannot change (end-game impact)
        public string StartBlockedWhy;                            // a UiText key: why Start is off, or null
        public List<int> StartLeavesOut = new List<int>();        // the player ids StartAnyway would leave out
        public int PlayerLimit;                                   // the host's player limit from the roster; 0 = not known
        public bool LateJoin = true;                              // the host lets players join a running game (roster)
        public SessionPhase RoomPhase;                            // the host's lobby phase (its room, or the roster)
        public JoinProgressFacts Progress;                        // a start's or a rejoin's progress, or null
        // The Multiplayer saves list and the ban list join here with Tasks 6 and 9 (additive).

        // The speed strip and the pause reasons from the host's Clock (null Clock: outside a game, both cleared).
        // The roster rows' votes are filled in by SpeedMarks.From. A pause's SinceMs comes from the tracker, which the
        // caller keeps between calls, so the strip's "(1:12)" keeps counting while the same reason holds.
        public void ApplyClock(Clock clock, PauseTracker tracker, long nowMs)
        {
            Speed = SpeedMarks.From(clock, Players);
            ClockLocked = clock != null && clock.Locked;
            Pauses = new List<PauseLine>();
            if (clock == null || clock.Pauses == null)
            {
                if (tracker != null) tracker.Keep(new List<PauseLine>());
                return;
            }
            foreach (Pause p in clock.Pauses)
            {
                if (p == null) continue;
                long since = tracker != null ? tracker.SinceFor(p.Player, p.Reason, nowMs) : nowMs;
                Pauses.Add(new PauseLine { Player = p.Player, Why = p.Reason, SinceMs = since });
            }
            if (tracker != null) tracker.Keep(Pauses);
        }

        public AgencyRow Agency(string id)
        {
            foreach (AgencyRow a in Agencies) if (a != null && a.Id == id) return a;
            return null;
        }

        // The players holding an agency, by id: the rows its Holders name (an id with no row is left out).
        public List<PlayerRow> PlayersOn(string agencyId)
        {
            var found = new List<PlayerRow>();
            AgencyRow a = Agency(agencyId);
            if (a == null || a.Holders == null) return found;
            foreach (int id in a.Holders)
            {
                PlayerRow r = Row(id);
                if (r != null && !found.Contains(r)) found.Add(r);
            }
            found.Sort(ById);
            return found;
        }

        // Never prints the join code: a view may end up in a log line.
        public override string ToString()
        {
            return "SessionView(" + Phase + ", " + Players.Count + " players)";
        }

        public PlayerRow Row(int id)
        {
            foreach (PlayerRow r in Players) if (r != null && r.Id == id) return r;
            return null;
        }

        // Players in the order the lobby and the PLAYERS screen list them: agencies in the order they first appear,
        // each agency's players by id (co-op shares a row group), players with no agency yet last.
        public static List<PlayerRow> GroupByAgency(IList<PlayerRow> rows)
        {
            var groups = new List<string>();
            var byAgency = new Dictionary<string, List<PlayerRow>>();
            var none = new List<PlayerRow>();
            if (rows != null)
            {
                foreach (PlayerRow r in rows)
                {
                    if (r == null) continue;
                    if (string.IsNullOrEmpty(r.Agency)) { none.Add(r); continue; }
                    List<PlayerRow> g;
                    if (!byAgency.TryGetValue(r.Agency, out g))
                    {
                        g = new List<PlayerRow>(); byAgency[r.Agency] = g; groups.Add(r.Agency);
                    }
                    g.Add(r);
                }
            }
            var all = new List<PlayerRow>();
            foreach (string a in groups) { byAgency[a].Sort(ById); all.AddRange(byAgency[a]); }
            none.Sort(ById);
            all.AddRange(none);
            return all;
        }

        static int ById(PlayerRow a, PlayerRow b) { return a.Id.CompareTo(b.Id); }
    }

    public sealed class PlayerRow
    {
        // State: 0 lobby, 1 joining, 2 in game, 3 dropped, 4 away (left on purpose), 5 rejoining (as Roster's).
        public const int Lobby = 0, Joining = 1, InGame = 2, Dropped = 3, Away = 4, Rejoining = 5;

        public int Id; public string Name; public string Agency; public bool Host, Me, Ready;
        public int State, Check, JoinPct = -1, JoinSecsLeft = -1, PingMs = -1;   // -1 = not known
        public float Vote; public bool HasVote;   // a loaded player's choice; HasVote false while joining or away

        // A player the source knows only by id (a client before protocol 7): "Player N", in game, nothing else known.
        public static PlayerRow Unknown(int id)
        {
            return new PlayerRow { Id = id, Name = "Player " + id, Agency = "", State = InGame };
        }
    }

    // Why the game is paused beyond a plain pause: Pause.Reason / PauseWhy, since when (the caller's clock, ms).
    public sealed class PauseLine { public int Player; public int Why; public long SinceMs; }

    // Remembers when each (player, reason) pause was first seen, so its age survives the view being rebuilt on every
    // Clock; a reason that is gone is forgotten, and a later one starts again.
    public sealed class PauseTracker
    {
        readonly Dictionary<long, long> first = new Dictionary<long, long>();

        static long KeyOf(int player, int why) { return ((long)player << 32) | (uint)why; }

        public long SinceFor(int player, int why, long nowMs)
        {
            long since;
            if (first.TryGetValue(KeyOf(player, why), out since)) return since;
            first[KeyOf(player, why)] = nowMs;
            return nowMs;
        }

        // Forgets every reason not in the current list.
        public void Keep(IList<PauseLine> current)
        {
            var keys = new HashSet<long>();
            foreach (PauseLine p in current) keys.Add(KeyOf(p.Player, p.Why));
            var gone = new List<long>();
            foreach (long k in first.Keys) if (!keys.Contains(k)) gone.Add(k);
            foreach (long k in gone) first.Remove(k);
        }
    }
}
