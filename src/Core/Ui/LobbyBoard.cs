using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One line of a screen: a UiText key and its arguments. Peer: an argument came from another player (a name), so
    // the screen puts the whole line through PeerText. Warn: the line reports a refusal or a problem.
    public sealed class LobbyLine
    {
        static readonly object[] NoArgs = new object[0];

        public readonly string Key;
        public readonly object[] Args;
        public readonly bool Peer, Warn;

        public LobbyLine(string key, object[] args = null, bool peer = false, bool warn = false)
        {
            Key = key; Args = args ?? NoArgs; Peer = peer; Warn = warn;
        }

        public string Text() { return UiText.Get(Key, Args); }

        public override string ToString() { return Key; }
    }

    // The words the lobby and the picker share: agency names and lists of player names.
    public static class LobbyText
    {
        // The table's name for one of the game's own agencies; any other id is shown as it is (peer text: a modded
        // agency's id came from the content, and on a client from the host).
        public static LobbyLine Agency(string id)
        {
            switch (id)
            {
                case "nasa": return new LobbyLine("lobby.agency.nasa");
                case "esa": return new LobbyLine("lobby.agency.esa");
                case "cnsa": return new LobbyLine("lobby.agency.cnsa");
                case "roscosmos": return new LobbyLine("lobby.agency.roscosmos");
                case "solex": return new LobbyLine("lobby.agency.solex");
                case null: case "": return new LobbyLine("lobby.agency.none");
                default: return new LobbyLine("lobby.plain", new object[] { id }, true);
            }
        }

        // A name as the screens show it: cleaned again (a view may come from anywhere), never empty.
        public static string Name(PlayerRow r)
        {
            if (r == null) return PlayerNames.Clean(null);
            return PlayerNames.Clean(r.Name);
        }

        // "Sam", "Sam and Jo", "Sam, Jo and Kim": the table's own joining words.
        public static string Names(IList<string> names)
        {
            if (names == null || names.Count == 0) return "";
            string acc = names[0];
            for (int i = 1; i < names.Count - 1; i++) acc = UiText.Get("lobby.names.list", acc, names[i]);
            return names.Count == 1 ? acc : UiText.Get("lobby.names.last", acc, names[names.Count - 1]);
        }

        // The players of this view holding an agency, as names; "You" for this player.
        public static List<string> HolderNames(SessionView v, AgencyRow a, bool youAsWord, bool leaveOutMe)
        {
            var names = new List<string>();
            if (v == null || a == null || a.Holders == null) return names;
            foreach (int id in a.Holders)
            {
                PlayerRow r = v.Row(id);
                bool me = r != null ? r.Me : id == v.MyId;
                if (me && leaveOutMe) continue;
                if (me && youAsWord) names.Add(UiText.Get("lobby.you"));
                else names.Add(r != null ? Name(r) : PlayerRow.Unknown(id).Name);
            }
            return names;
        }

        // This player's own row: the one marked Me, else the one with the view's id.
        public static PlayerRow Me(SessionView v)
        {
            if (v == null || v.Players == null) return null;
            foreach (PlayerRow r in v.Players) if (r != null && r.Me) return r;
            return v.MyId >= 0 ? v.Row(v.MyId) : null;
        }

        public static PlayerRow Host(SessionView v)
        {
            if (v == null || v.Players == null) return null;
            foreach (PlayerRow r in v.Players) if (r != null && r.Host) return r;
            return null;
        }

        // A refusal the status line reports (join.refused.<code>): its code, else -1.
        public static int RefusalCode(StatusLine s)
        {
            const string prefix = "join.refused.";
            if (s == null || s.Key == null || !s.Key.StartsWith(prefix, StringComparison.Ordinal)) return -1;
            int code;
            return int.TryParse(s.Key.Substring(prefix.Length), out code) && code >= 0 ? code : -1;
        }

        // The status line as a lobby line: a refusal or a warning is shown as one.
        public static LobbyLine Status(StatusLine s)
        {
            if (s == null || s.Key == null) return null;
            bool warn = s.Severity >= StatusLine.Warning || RefusalCode(s) >= 0;
            return new LobbyLine(s.Key, s.Args, false, warn);
        }
    }

    // A button of a screen: whether it is there, whether it acts, and its words.
    public sealed class LobbyButton
    {
        public bool Shown, Enabled;
        public string Key;

        public static LobbyButton Of(bool shown, bool enabled, string key)
        {
            return new LobbyButton { Shown = shown, Enabled = shown && enabled, Key = key };
        }
    }

    public sealed class LobbyPlayer
    {
        public int Id;
        public string Name;                 // cleaned peer text
        public LobbyLine Role;              // "You", "The host", ...; null for nobody in particular
        public LobbyLine Agency;
        public LobbyLine Third;             // Ready / Not ready / Host, or the player's state without Ready
        public bool ThirdOn;                // ready (or the host): the column's "on" state
        public LobbyLine Ping;
        public int PingMs;
        public bool Me, Host, Kick, AgencyButton;
    }

    public sealed class LobbyGroup
    {
        public string AgencyId;             // "" for the players with no agency yet
        public LobbyLine Name, Count;
        public List<LobbyPlayer> Players = new List<LobbyPlayer>();
    }

    // One agency under the list: who holds it, or its state, and the host's switch for a free one.
    public sealed class LobbyCard
    {
        public string Id;
        public LobbyLine Name, Status, Last;
        public bool Held, Toggle;
        public LobbyLine ToggleLabel;
        public AgencyStanding ToggleTo;
    }

    public sealed class LobbyOption { public LobbyLine Label, Value; }

    // What the lobby screen shows, made only from the session's view and its capabilities, so the screen holds no
    // rule of its own. A control whose capability is off is not shown (Shown false), whatever the view says.
    public sealed class LobbyBoard
    {
        // At most five players without sharing (DD5: the host included); sharing an agency (co-op) has no cap.
        public const int SoloCap = JoinLimits.MaxPlayers;

        public LobbyLine Title, Count, Status;
        public bool Untested, Sharing, ReadyColumn, CanPickOwn, ReadyOn;
        public int Cap, EmptySlots, PlayerCount;
        public List<LobbyGroup> Groups = new List<LobbyGroup>();
        public List<LobbyCard> Cards = new List<LobbyCard>();
        public bool CodeShown, NewCode;
        public string JoinCode;             // shown only in a password field; never logged
        public List<LobbyOption> Options = new List<LobbyOption>();
        public List<LobbyLine> Notes = new List<LobbyLine>();
        public LobbyButton Leave, Close, Ready, Start, StartAnyway;
        public LobbyButton JoinGame;        // a joiner in the lobby of a running game: opens the agency picker
        public bool Running;                // that case: the host's game runs, no Ready or Start will come
        public List<int> LeftOut = new List<int>();
        public LobbyLine AnywayBody;        // the Start anyway confirmation's words

        public static LobbyBoard Build(SessionView v, Capability caps)
        {
            var b = new LobbyBoard();
            if (v == null) v = new SessionView();
            PlayerRow me = LobbyText.Me(v), host = LobbyText.Host(v);
            bool isHost = v.IsHost;
            b.ReadyColumn = CapabilityRules.Has(caps, Capability.Ready);
            b.Sharing = Shares(v);
            b.Cap = b.Sharing ? 0 : CapFor(v.PlayerLimit);
            b.Untested = b.Sharing;
            b.CanPickOwn = CapabilityRules.Has(caps, Capability.PickAgency) && v.Phase == SessionPhase.Lobby &&
                           AnyClaimable(v);

            string hostName = host != null ? LobbyText.Name(host) : null;
            b.Title = hostName != null ? new LobbyLine("lobby.title", new object[] { hostName }, true)
                                       : new LobbyLine("lobby.titlePlain");
            Group(v, caps, b, me);
            b.Count = b.Cap > 0 ? new LobbyLine("lobby.players.count", new object[] { b.PlayerCount, b.Cap })
                                : new LobbyLine("lobby.players.countOpen", new object[] { b.PlayerCount });
            b.EmptySlots = b.Cap > 0 ? Math.Max(0, b.Cap - b.PlayerCount) : 0;
            AddCards(v, caps, b);

            b.CodeShown = isHost && CapabilityRules.Has(caps, Capability.JoinByCode) &&
                          !string.IsNullOrEmpty(v.JoinCode);
            b.NewCode = b.CodeShown;
            b.JoinCode = b.CodeShown ? v.JoinCode : null;
            AddOptions(v, b, isHost, hostName);

            bool atMenu = CapabilityRules.Has(caps, Capability.LobbyAtMenu);
            // the host's own button at the menu closes its lobby (it asks first, LobbyCloseRules)
            b.Leave = LobbyButton.Of(atMenu, true, isHost ? "lobby.closeLobby.button" : "lobby.leave");
            b.Close = LobbyButton.Of(!atMenu, true, "lobby.close");
            b.ReadyOn = me != null && me.Ready;
            bool hasAgency = me != null && !string.IsNullOrEmpty(me.Agency);
            // a late joiner waits in the lobby of a running game: its pick is its way in, at once
            b.Running = !isHost && v.RoomPhase == SessionPhase.InGame && me != null && me.State == PlayerRow.Lobby;
            b.Ready = LobbyButton.Of(b.ReadyColumn && !isHost && !b.Running, hasAgency,
                                     b.ReadyOn ? "lobby.unready" : "lobby.ready");
            b.JoinGame = LobbyButton.Of(b.Running, AnyClaimable(v), "lobby.joinGame");
            b.Start = LobbyButton.Of(CapabilityRules.Has(caps, Capability.Start) && isHost, v.StartBlockedWhy == null,
                                     "lobby.start");
            if (v.StartLeavesOut != null) b.LeftOut.AddRange(v.StartLeavesOut);
            b.StartAnyway = LobbyButton.Of(CapabilityRules.Has(caps, Capability.StartAnyway) && isHost,
                                           b.LeftOut.Count > 0, "lobby.startAnyway");
            b.AnywayBody = new LobbyLine("lobby.anyway.body", new object[] { LobbyText.Names(Named(v, b.LeftOut)) },
                                         true);
            b.Status = StatusOf(v, caps, b, me, hostName);
            return b;
        }

        // The player limit the host chose (the roster's), never above the five-player cap; unknown keeps the cap.
        public static int CapFor(int limit)
        {
            return limit >= 1 && limit < SoloCap ? limit : SoloCap;
        }

        // Sharing is on when an agency is held by two players, or one held by someone may still be claimed.
        static bool Shares(SessionView v)
        {
            foreach (AgencyRow a in v.Agencies)
            {
                if (a == null || a.Holders == null || a.Holders.Count == 0) continue;
                if (a.Holders.Count > 1 || a.Claimable) return true;
            }
            return false;
        }

        static bool AnyClaimable(SessionView v)
        {
            foreach (AgencyRow a in v.Agencies) if (a != null && a.Claimable) return true;
            return false;
        }

        // The players grouped by agency: agencies in the view's own order (then by id), each agency's players by id,
        // and the players with no agency yet last.
        static void Group(SessionView v, Capability caps, LobbyBoard b, PlayerRow me)
        {
            var order = new List<string>();
            foreach (AgencyRow a in v.Agencies) if (a != null && a.Id != null && !order.Contains(a.Id)) order.Add(a.Id);
            var byAgency = new Dictionary<string, List<PlayerRow>>(StringComparer.Ordinal);
            foreach (PlayerRow r in v.Players)
            {
                if (r == null) continue;
                string key = r.Agency ?? "";
                List<PlayerRow> list;
                if (!byAgency.TryGetValue(key, out list)) byAgency[key] = list = new List<PlayerRow>();
                list.Add(r);
            }
            var keys = new List<string>(byAgency.Keys);
            keys.Sort((x, y) =>
            {
                if (x.Length == 0 || y.Length == 0) return (x.Length == 0 ? 1 : 0) - (y.Length == 0 ? 1 : 0);
                int ix = order.IndexOf(x), iy = order.IndexOf(y);
                if (ix < 0) ix = int.MaxValue;
                if (iy < 0) iy = int.MaxValue;
                return ix != iy ? ix.CompareTo(iy) : string.CompareOrdinal(x, y);
            });
            foreach (string key in keys)
            {
                List<PlayerRow> rows = byAgency[key];
                rows.Sort((x, y) => x.Id.CompareTo(y.Id));
                var g = new LobbyGroup
                {
                    AgencyId = key,
                    Name = key.Length > 0 ? LobbyText.Agency(key) : new LobbyLine("lobby.group.none"),
                    Count = rows.Count == 1 ? new LobbyLine("lobby.group.one")
                                            : new LobbyLine("lobby.group.many", new object[] { rows.Count }),
                };
                foreach (PlayerRow r in rows) g.Players.Add(Player(v, caps, b, r));
                b.PlayerCount += rows.Count;
                b.Groups.Add(g);
            }
        }

        static LobbyPlayer Player(SessionView v, Capability caps, LobbyBoard b, PlayerRow r)
        {
            var p = new LobbyPlayer
            {
                Id = r.Id, Name = LobbyText.Name(r), Me = r.Me, Host = r.Host, PingMs = r.PingMs,
                Agency = LobbyText.Agency(r.Agency),
            };
            if (r.Me && r.Host) p.Role = new LobbyLine("lobby.role.youHost");
            else if (r.Host) p.Role = new LobbyLine("lobby.role.host");
            else if (r.Me) p.Role = new LobbyLine("lobby.role.you");
            if (b.ReadyColumn)
            {
                p.ThirdOn = r.Host || r.Ready;
                p.Third = new LobbyLine(r.Host ? "lobby.col.host" : r.Ready ? "lobby.col.ready" : "lobby.col.notReady");
            }
            else
            {
                p.ThirdOn = r.State == PlayerRow.InGame;
                p.Third = State(r);
            }
            if (r.Me && v.IsHost) p.Ping = new LobbyLine("lobby.conn.thisPc");
            else if (r.PingMs >= 0) p.Ping = new LobbyLine("hud.players.ping", new object[] { r.PingMs });
            else p.Ping = new LobbyLine("lobby.conn.none");
            p.Kick = CapabilityRules.Has(caps, Capability.Kick) && v.IsHost && !r.Me && !r.Host;
            p.AgencyButton = r.Me && b.CanPickOwn;
            return p;
        }

        public static LobbyLine State(PlayerRow r)
        {
            switch (r.State)
            {
                case PlayerRow.Joining:
                    if (r.JoinPct < 0) return new LobbyLine("lobby.state.joining");
                    return new LobbyLine("lobby.state.joiningPct", new object[] { Math.Min(r.JoinPct, 100) });
                case PlayerRow.InGame: return new LobbyLine("lobby.state.inGame");
                case PlayerRow.Dropped: return new LobbyLine("lobby.state.dropped", null, false, true);
                case PlayerRow.Away: return new LobbyLine("lobby.state.away");
                case PlayerRow.Rejoining: return new LobbyLine("lobby.state.rejoining");
                default: return new LobbyLine("lobby.state.lobby");
            }
        }

        static void AddCards(SessionView v, Capability caps, LobbyBoard b)
        {
            bool rules = CapabilityRules.Has(caps, Capability.AgencyStates) && v.IsHost && v.NewGame;
            foreach (AgencyRow a in v.Agencies)
            {
                if (a == null || a.Id == null) continue;
                var c = new LobbyCard { Id = a.Id, Name = LobbyText.Agency(a.Id) };
                c.Held = a.Holders != null && a.Holders.Count > 0;
                if (c.Held)
                    c.Status = new LobbyLine("lobby.plain",
                        new object[] { LobbyText.Names(LobbyText.HolderNames(v, a, true, false)) }, true);
                else
                {
                    c.Status = new LobbyLine(a.State == AgencyStanding.AiRival ? "lobby.card.ai"
                        : a.State == AgencyStanding.Idle ? "lobby.card.idle"
                        : a.State == AgencyStanding.NotInGame ? "lobby.card.out" : "lobby.card.open");
                    string last = TextSanitiser.Clean(a.LastHolder, PlayerNames.MaxChars, "");
                    if (last.Length > 0) c.Last = new LobbyLine("lobby.card.last", new object[] { last }, true);
                }
                if (rules && !c.Held && a.State != AgencyStanding.Human)
                {
                    c.Toggle = true;
                    c.ToggleTo = a.State == AgencyStanding.AiRival ? AgencyStanding.NotInGame : AgencyStanding.AiRival;
                    c.ToggleLabel = new LobbyLine(a.State == AgencyStanding.AiRival ? "lobby.card.leaveOut"
                                                                                 : "lobby.card.makeAi");
                }
                b.Cards.Add(c);
            }
        }

        static void AddOptions(SessionView v, LobbyBoard b, bool isHost, string hostName)
        {
            if (v.NewGame) Add(b, "lobby.opt.game", new LobbyLine("lobby.opt.newGame"));
            else if (!string.IsNullOrEmpty(v.SaveTitle))
                Add(b, "lobby.opt.save", new LobbyLine("lobby.plain",
                    new object[] { TextSanitiser.Clean(v.SaveTitle, 64, "") }, true));
            if (!string.IsNullOrEmpty(v.SaveDate))
                Add(b, "lobby.opt.date", new LobbyLine("lobby.plain",
                    new object[] { TextSanitiser.Clean(v.SaveDate, 32, "") }, true));
            Add(b, "lobby.opt.players", b.Cap > 0 ? new LobbyLine("lobby.opt.upTo", new object[] { b.Cap })
                                                  : new LobbyLine("lobby.opt.noLimit"));
            if (v.PlayerLimit > 0)
                Add(b, "lobby.opt.late", new LobbyLine(v.LateJoin ? "lobby.opt.on" : "lobby.opt.off"));
            Add(b, "lobby.opt.share", new LobbyLine(b.Sharing ? "lobby.opt.on" : "lobby.opt.off"));
            var ai = new List<string>();
            foreach (AgencyRow a in v.Agencies)
                if (a != null && a.State == AgencyStanding.AiRival && (a.Holders == null || a.Holders.Count == 0))
                    ai.Add(LobbyText.Agency(a.Id).Text());
            Add(b, "lobby.opt.ai", ai.Count > 0
                ? new LobbyLine("lobby.plain", new object[] { LobbyText.Names(ai) }, true)
                : new LobbyLine("lobby.opt.none"));
            // the host chooses who can join on the invite strip beside the options, so the row would say it twice
            if (!isHost) Add(b, "lobby.opt.who", new LobbyLine("lobby.opt.anyoneWithCode"));
            if (!isHost && hostName != null)
                b.Notes.Add(new LobbyLine("lobby.opt.noteJoiner", new object[] { hostName }, true));
            if (b.Sharing) b.Notes.Add(new LobbyLine("lobby.opt.noteShare"));
        }

        static void Add(LobbyBoard b, string label, LobbyLine value)
        {
            b.Options.Add(new LobbyOption { Label = new LobbyLine(label), Value = value });
        }

        static List<string> Named(SessionView v, IList<int> ids)
        {
            var names = new List<string>();
            foreach (int id in ids)
            {
                PlayerRow r = v.Row(id);
                names.Add(r != null ? LobbyText.Name(r) : PlayerRow.Unknown(id).Name);
            }
            return names;
        }

        // The one line in the footer: the session's own status first (a refusal, joining), then what the host or
        // the player waits for.
        static LobbyLine StatusOf(SessionView v, Capability caps, LobbyBoard b, PlayerRow me, string hostName)
        {
            LobbyLine s = LobbyText.Status(v.Status);
            if (s != null) return s;
            if (b.Running)
                return new LobbyLine(b.JoinGame.Enabled ? "lobby.status.running" : "lobby.status.runningFull");
            if (b.Start.Shown)
            {
                var waiting = new List<string>();
                if (b.ReadyColumn)
                    foreach (LobbyGroup g in b.Groups)
                        foreach (LobbyPlayer p in g.Players)
                            if (!p.ThirdOn) waiting.Add(p.Name);
                if (waiting.Count > 0)
                    return new LobbyLine(b.StartAnyway.Shown ? "lobby.status.waitingAnyway" : "lobby.status.waiting",
                                         new object[] { LobbyText.Names(waiting) }, true);
                if (v.StartBlockedWhy != null) return new LobbyLine(v.StartBlockedWhy);
                return new LobbyLine("lobby.status.allReady");
            }
            if (b.Ready.Shown)
            {
                if (me == null || string.IsNullOrEmpty(me.Agency)) return new LobbyLine("lobby.status.pickFirst");
                if (me.Ready)
                    return hostName != null
                        ? new LobbyLine("lobby.status.youReady", new object[] { hostName }, true)
                        : new LobbyLine("lobby.status.youReadyPlain");
                return new LobbyLine("lobby.status.pressReady");
            }
            if (v.IsHost) return new LobbyLine("lobby.status.hostOpen");
            return hostName != null ? new LobbyLine("lobby.status.joinerOpen", new object[] { hostName }, true)
                                    : new LobbyLine("lobby.status.joinerOpenPlain");
        }

        // What decides the screen's widgets (not their words): when it changes, the screen builds them again;
        // otherwise it only rewrites the words in place.
        public string Shape()
        {
            var sb = new System.Text.StringBuilder();
            foreach (LobbyGroup g in Groups)
            {
                sb.Append('[').Append(g.AgencyId);
                foreach (LobbyPlayer p in g.Players)
                    sb.Append(',').Append(p.Id).Append(p.Kick ? "k" : "").Append(p.AgencyButton ? "a" : "")
                      .Append(p.Me ? "m" : "");
                sb.Append(']');
            }
            sb.Append('|').Append(EmptySlots).Append('|');
            foreach (LobbyCard c in Cards) sb.Append(c.Id).Append(c.Toggle ? "t" : "").Append(c.Last != null ? "l" : "")
                                              .Append(',');
            sb.Append('|').Append(CodeShown).Append(ReadyColumn).Append(Options.Count).Append(Notes.Count);
            foreach (LobbyButton x in new[] { Leave, Close, Ready, Start, StartAnyway, JoinGame })
                sb.Append(x.Shown ? '1' : '0');
            return sb.ToString();
        }
    }
}
