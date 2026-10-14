using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One player's line on the PLAYERS screen.
    public sealed class PlayersRow
    {
        public int Id; public bool Me, Host;
        public string Name;                  // peer text: the name, with "(you)" or "(host)"
        public string PlainName;             // the name alone
        public string NameKey;               // the line that adds "(you)" or "(host)" to it, or null
        public string Status;                // "Playing", "Joining (45%)", "Dropped, reconnecting"...
        public bool Warn;                    // the status is not plain play (joining, dropped, planning...)
        public int PingMs = -1;              // the bars' ping; -1 = not known
        public int Bars;                     // SignalBars.For, or Max for this PC
        public string Ping;                  // "~45 ms", "This PC" or "-" (never colour alone)
        public bool CanRemove, CanBan, CanCarryOn;
    }

    // The players holding one agency (co-op shares a group), or the players with no agency yet (AgencyId "").
    public sealed class PlayersGroup
    {
        public string AgencyId, Name, Count;
        public List<PlayersRow> Rows = new List<PlayersRow>();
    }

    // A yes-or-no question over the PLAYERS screen (removing a player, ending the session).
    public sealed class ConfirmModel
    {
        public string Title, Line, Hint, Yes, No;
        public int Player = -1;
    }

    // What the PLAYERS screen (DD27) shows, from the session view, the session's capabilities and its notices only:
    // the players grouped by agency with their status, signal bars and ping beside them, the session's log (the last
    // NoticeQueue.MaxHistory notices, newest first) and, behind their capabilities, the host's Remove, Carry on and
    // End session. The game side builds it with the parts kit (PlayersScreen).
    public sealed class PlayersModel
    {
        public string Title, CountLine, Untested, PingNote, LogTitle, Back, End, Remove, CarryOn, EmptyLog;
        public string Ban;                               // the host's Ban beside Remove
        public bool ShowBans;                            // the host's list of banned players (Unban)
        public string RemoveShort;                       // "Remove": when the board's words do not fit the button
        public string ColPlayer, ColStatus, ColConnection;   // the list's column headers
        public bool ShowEnd;
        public List<PlayersGroup> Groups = new List<PlayersGroup>();
        public List<string> Log = new List<string>();

        // agencyName: the game's own name for an agency's wire id (null or a missing name: the id in capitals).
        public static PlayersModel From(SessionView v, Capability caps, Func<string, string> agencyName,
                                        IList<NoticeItem> history)
        {
            var m = new PlayersModel
            {
                PingNote = UiText.Get("hud.players.pingNote"), LogTitle = UiText.Get("hud.players.log"),
                Back = UiText.Get("hud.players.back"), End = UiText.Get("hud.players.end"),
                Remove = UiText.Get("hud.players.remove"), CarryOn = UiText.Get("hud.players.carryOn"),
                EmptyLog = UiText.Get("hud.players.logEmpty"), RemoveShort = UiText.Get("hud.players.removeShort"),
                ColPlayer = UiText.Get("hud.players.colPlayer"), ColStatus = UiText.Get("hud.players.colStatus"),
                ColConnection = UiText.Get("hud.players.colConnection"), Ban = UiText.Get("hud.players.ban"),
            };
            if (v == null) v = new SessionView();
            PlayerRow host = null;
            foreach (PlayerRow r in v.Players) if (r != null && r.Host) { host = r; break; }
            m.Title = host != null ? UiText.Get("hud.players.titleOf", host.Name) : UiText.Get("hud.players.title");
            int count = 0;
            foreach (PlayerRow r in v.Players) if (r != null) count++;
            m.CountLine = UiText.Get("hud.players.count", count);
            if (count > SessionLimits.PlayerLimitNoCoop) m.Untested = UiText.Get("hud.players.untested");
            m.ShowEnd = ActionRules.MayEndSession(v, caps);
            m.ShowBans = v.IsHost && CapabilityRules.Has(caps, Capability.Ban);

            PlayersGroup group = null;
            foreach (PlayerRow r in SessionView.GroupByAgency(v.Players))
            {
                string agency = r.Agency ?? "";
                if (group == null || group.AgencyId != agency)
                {
                    group = new PlayersGroup
                    {
                        AgencyId = agency,
                        Name = agency.Length == 0 ? UiText.Get("hud.players.noAgency") : AgencyName(agency, agencyName),
                    };
                    m.Groups.Add(group);
                }
                group.Rows.Add(Row(r, v, caps));
            }
            foreach (PlayersGroup g in m.Groups)
                g.Count = g.Rows.Count == 1 ? UiText.Get("hud.players.one")
                    : UiText.Get("hud.players.sharing", g.Rows.Count);

            if (history != null)
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    NoticeItem n = history[i];
                    if (n == null) continue;
                    string line = UiText.Get(n.Key, n.Args ?? new object[0]);
                    m.Log.Add(n.Count > 1 ? UiText.Get("hud.players.logRepeat", line, n.Count) : line);
                }
            return m;
        }

        static PlayersRow Row(PlayerRow r, SessionView v, Capability caps)
        {
            var row = new PlayersRow { Id = r.Id, Me = r.Me, Host = r.Host };
            string name = string.IsNullOrEmpty(r.Name) ? PlayerRow.Unknown(r.Id).Name : r.Name;
            row.PlainName = name;
            row.NameKey = r.Me && r.Host ? "hud.players.youHost" : r.Me ? "hud.players.you"
                : r.Host ? "hud.players.host" : null;
            row.Name = row.NameKey != null ? UiText.Get(row.NameKey, name) : name;
            Status(row, r, v);
            if (v.IsHost && r.Me)
            {
                // the host's own game: no link to measure
                row.Bars = SignalBars.Max;
                row.Ping = UiText.Get("hud.players.thisPc");
            }
            else
            {
                row.PingMs = r.PingMs;
                row.Bars = SignalBars.For(r.PingMs);
                row.Ping = r.PingMs >= 0 ? UiText.Get("hud.players.ping", r.PingMs) : UiText.Get("hud.players.noPing");
            }
            row.CanRemove = ActionRules.MayKick(v, caps, r.Id);
            row.CanBan = ActionRules.MayBan(v, caps, r.Id);
            row.CanCarryOn = r.State == PlayerRow.Dropped && ActionRules.MayCarryOn(v, caps, r.Id);
            return row;
        }

        static void Status(PlayersRow row, PlayerRow r, SessionView v)
        {
            row.Warn = true;
            switch (r.State)
            {
                case PlayerRow.Lobby: row.Status = UiText.Get("hud.players.inLobby"); return;
                case PlayerRow.Joining:
                    int pct = Math.Min(100, r.JoinPct);
                    row.Status = r.JoinPct >= 0 && r.JoinSecsLeft >= 0
                        ? UiText.Get("hud.players.joiningEta", pct, r.JoinSecsLeft)
                        : r.JoinPct >= 0 ? UiText.Get("hud.players.joiningPct", pct)
                        : UiText.Get("hud.players.joining");
                    return;
                case PlayerRow.Dropped: row.Status = UiText.Get("hud.players.dropped"); return;
                case PlayerRow.Away: row.Status = UiText.Get("hud.players.away"); return;
                case PlayerRow.Rejoining: row.Status = UiText.Get("hud.players.rejoining"); return;
            }
            foreach (PauseLine p in v.Pauses)
                if (p != null && p.Player == r.Id && p.Why == PauseWhy.Planning)
                {
                    row.Status = UiText.Get("hud.players.planning");
                    return;
                }
            if (r.HasVote && r.Vote <= 0f) { row.Status = UiText.Get("hud.players.paused"); return; }
            row.Warn = false;
            row.Status = UiText.Get("hud.players.playing");
        }

        // A row's name for the screen: the name cut to the room the suffix leaves ("(you)", "(the host)" stay whole),
        // then the suffix; the name alone, cut to the room, when it has none.
        public static string NameFor(PlayersRow r, IGlyphs glyphs, float maxWidth)
        {
            if (r == null) return "";
            if (r.NameKey == null || glyphs == null) return PeerText.ForScreen(r.PlainName, glyphs, maxWidth);
            float suffix = glyphs.Width(UiText.Get(r.NameKey, ""));
            string cut = PeerText.ForScreen(r.PlainName, glyphs, maxWidth - suffix);
            return UiText.Get(r.NameKey, cut);
        }

        public static string AgencyName(string id, Func<string, string> agencyName)
        {
            string n = null;
            if (agencyName != null)
            {
                try { n = agencyName(id); }
                catch (Exception) { n = null; }
            }
            return string.IsNullOrEmpty(n) ? (id ?? "").ToUpperInvariant() : n;
        }

        // The host's question before removing a player: what happens to the player and to the agency's seat.
        public static ConfirmModel RemoveConfirm(SessionView v, int player, Func<string, string> agencyName)
        {
            PlayerRow r = v != null ? v.Row(player) : null;
            string name = HudModel.NameOf(v, player);
            var c = new ConfirmModel
            {
                Player = player, Title = UiText.Get("hud.players.removeTitle", name),
                Hint = UiText.Get("hud.players.removeHint", name), Yes = UiText.Get("hud.players.removeYes", name),
                No = UiText.Get("hud.players.no"),
            };
            string agency = r != null ? r.Agency ?? "" : "";
            var mates = new List<string>();
            if (agency.Length > 0)
                foreach (PlayerRow o in SessionView.GroupByAgency(v.Players))
                    if (o.Id != player && o.Agency == agency) mates.Add(HudModel.NameOf(v, o.Id));
            string an = AgencyName(agency, agencyName);
            if (agency.Length == 0) c.Line = UiText.Get("hud.players.removeNoAgency", name);
            else if (mates.Count == 0) c.Line = UiText.Get("hud.players.removeFrees", name, an);
            else if (mates.Count == 1) c.Line = UiText.Get("hud.players.removeMate", name, mates[0], an);
            else c.Line = UiText.Get("hud.players.removeMates", name, mates[0], mates.Count - 1, an);
            return c;
        }

        // The host's question before a ban: Steam keeps the player in its lobby, the game refuses them.
        public static ConfirmModel BanConfirm(SessionView v, int player)
        {
            string name = HudModel.NameOf(v, player);
            return new ConfirmModel
            {
                Player = player, Title = UiText.Get("hud.players.banTitle", name),
                Line = UiText.Get("hud.players.banLine"), Hint = UiText.Get("hud.players.banHint"),
                Yes = UiText.Get("hud.players.banYes", name), No = UiText.Get("hud.players.no"),
            };
        }

        // The list's shape as one string (who is in which group with which buttons, and how long the log is): the
        // screen rebuilds its rows only when this changes, and otherwise updates the texts in place.
        public string Signature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(ShowEnd).Append(ShowBans).Append('|').Append(Log.Count).Append('|');
            foreach (string line in Log) sb.Append(line).Append('\n');   // a merged notice's count changes its line
            sb.Append('|');
            foreach (PlayersGroup g in Groups)
            {
                sb.Append(g.AgencyId).Append(':');
                foreach (PlayersRow r in g.Rows)
                    sb.Append(r.Id).Append(r.CanRemove ? "r" : "").Append(r.CanBan ? "b" : "")
                      .Append(r.CanCarryOn ? "c" : "").Append(',');
                sb.Append(';');
            }
            return sb.ToString();
        }
    }
}
