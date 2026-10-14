using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace SolarExpanseMultiplayer
{
    // The hosting game: listens (TLS, this profile's certificate), keeps one open invite in <data>\join-code.txt (a new
    // one after each join), and passes players' messages on. The join code holds a secret: it is written to the
    // profile's data folder only, never to the log. When Steam is usable it also listens on Steam's relay, and the same
    // invite goes into <data>\join-code-steam.txt as a Steam join code (the host's Steam id instead of an address): one
    // invite, two ways in, spent by whichever is used first.
    // Players are tracked by link id as well as player id: a Closed event for a link that is no longer the player's
    // current one (an old link closing as a rejoin arrives) is ignored, so it never reads as a drop.
    // M4 (N-1): a Joined event only means the Welcome was queued. The invite or key is committed by the gate on the first
    // frame after it, and then Confirmed arrives: that is when the next join code is written. A rejoin that replaces a
    // link the host still thought open raises Rejoined (N-2), not Joined.
    // One event that throws must not cost the rest of its batch (a lost Closed leaves a ghost player, a lost Joined a
    // player whose messages are ignored for good): each event is handled in its own try, with a throttled ERROR.
    public sealed class SessionHost
    {
        const double CodeRetryEverySeconds = 0.5, CodeRetryForSeconds = 10;
        LinkHost link;
        HostGate gate;
        X509Certificate2 cert;
        PublishedFile codeFile;   // join-code.txt: kept at the newest invite, retried if a joiner has it open
        PublishedFile steamFile;  // join-code-steam.txt: the same invite as a Steam join code
        SteamNet steamNet;
        HostSteamDoor steamDoor;  // bans, the accounts the door let through and their Steam names
        ulong steamHostId;        // this host's Steam id while it listens on the relay (0 = no Steam join code)
        NetOptions opt;
        volatile bool stopped;
        readonly LineThrottle doorLines = new LineThrottle(1.0);
        readonly LineThrottle eventErrors = new LineThrottle(1.0);
        readonly List<NetEvent> events = new List<NetEvent>();
        readonly Dictionary<int, string> players = new Dictionary<int, string>();   // connected: player id -> name
        readonly Dictionary<int, int> linkOf = new Dictionary<int, int>();          // player id -> current link id
        int failedNotLogged;     // "connection failed" notes since the last log line (a flood of strangers logs once a second)
        float lastFailedLog = -10f;
        string lastFailedReason;

        public System.Action<int> Joined, Left;          // player id
        public System.Action<int> LeftForGood;           // player id: left on purpose (its goodbye said so), after Left
        public System.Action<int> Rejoined;              // player id: back on a new link while the host still had the old one (the player never left)
        public System.Action<int, string, bool> Confirmed;   // player id, sanitised name, whether it is a rejoin: the first frame after the Welcome arrived
        public System.Action<int, Message> Received;     // player id, message
        public System.Action<int, Message> Sent;         // player id, a message that went to that player (Send)
        // M5: a player's command message (ActionRequest, ObjectRequest), with the time the network thread read it
        // (NetEvent.AtMs): the request limiter must see arrivals as they came, not bunched by a slow frame
        public System.Action<int, Message, long> CommandReceived;
        public string Status { get; private set; }
        public string JoinCodeFile { get { return Path.Combine(Boot.DataDir, "join-code.txt"); } }
        public string SteamCodeFile { get { return Path.Combine(Boot.DataDir, "join-code-steam.txt"); } }

        // The current invite's code, for the host's own screen (M8 Track U); never logged, never in a status line.
        public string JoinCodeText { get; private set; }
        // The same invite as a Steam join code while this host listens on Steam's relay, else null; for the lobby
        // screen, under the same rules as JoinCodeText.
        public string SteamCodeText { get; private set; }
        // The last Clock this host sent, for the session view's speed strip; null before the first.
        public Clock LastClock { get; private set; }
        // Each connected player's smoothed round trip in ms (PingMs), from the answers to the link's keep-alive pings.
        readonly Dictionary<int, double> rtt = new Dictionary<int, double>();

        // plan: the session's (from the dev flags or the menus). join-code.txt is a test seam: written only while the
        // test seams are open; otherwise the code lives in JoinCodeText for the screen.
        public void Start(SessionPlan plan)
        {
            // The code files are test seams: a normal player's codes live only in JoinCodeText and SteamCodeText.
            if (TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath))
            {
                codeFile = new PublishedFile(JoinCodeFile, CodeRetryEverySeconds, CodeRetryForSeconds);
                steamFile = new PublishedFile(SteamCodeFile, CodeRetryEverySeconds, CodeRetryForSeconds);
            }
            DeleteJoinCode();   // never leave an old code (and its secret) lying about
            // the player path of a test profile takes the player's key too, so it can be checked in a test copy
            cert = Boot.Profile == null || MenuSessions.PlayerPath ? PlayerCertificate()
                                                                   : HostCert.LoadOrCreate(Boot.DataDir);
            gate = new HostGate(ModInfo.Protocol, ModInfo.Version, Session.GameBuildId, ModInfo.ClipGameVersion(UnityEngine.Application.version), 4, HostGate.SecureRandom);
            opt = Session.Options();
            link = new LinkHost(plan.Bind, plan.Port, cert, gate, plan.PlayerName, opt);
            link.Start();
            string pinStart = HostGate.Hex(CertPin.Sha256(cert)).Substring(0, 16);
            Log.Info(LogPrivacy.HostListening(link.Address, link.Port, pinStart, Session.GameBuildId,
                                              Boot.Profile == null));
            StartSteam();
            WriteJoinCode();
            Status = codeFile != null ? "host: waiting for players (join code in data\\test-" + Boot.Profile +
                                        "\\join-code.txt)"
                                      : "host: waiting for players (join code on screen)";
        }

        // A player's install keeps its key protected for this Windows user; without that there is no hosting rather
        // than a plain key on disk (a test profile keeps the plain file its tools read).
        static X509Certificate2 PlayerCertificate()
        {
            bool created;
            X509Certificate2 c = KeyFiles.HostCertificate(Boot.DataDir, out created);
            if (c == null) throw new System.InvalidOperationException("the host key could not be kept protected");
            return c;
        }

        // The host's "New code": a new invite replaces the open one, so the code shown before stops working.
        public void NewCode()
        {
            if (link == null) return;
            WriteJoinCode();
        }

        // Connected players by id with their names as the link gave them (a copy).
        public Dictionary<int, string> Players() { return new Dictionary<int, string>(players); }

        public bool IsConnected(int player) { return players.ContainsKey(player); }

        // The verified Steam account a player came in with, or 0 (a home-network player).
        public ulong SteamIdOf(int player) { return gate != null ? gate.SteamIdOf(player) : 0; }

        // A player's smoothed round trip in whole ms, or -1 until the first answer came back.
        public int PingMs(int player)
        {
            double r;
            return rtt.TryGetValue(player, out r) && r >= 0 ? (int)System.Math.Round(r) : -1;
        }

        // T comes back from the player's game, so it is untrusted: only a round trip of 0 to under 60 s counts (the
        // client's own rule), anything else is ignored. Smoothed as the client's RttMs: the first sample as it is,
        // then old * 0.8 + new * 0.2. It includes up to a frame on each side (the Pong waits in the inbox).
        void TimePong(int player, Pong p)
        {
            double sample = NetClock.NowMs - p.T;
            if (!(sample >= 0 && sample < 60000)) return;
            double old;
            rtt[player] = rtt.TryGetValue(player, out old) ? old * 0.8 + sample * 0.2 : sample;
        }

        void DeleteJoinCode()
        {
            try
            {
                foreach (string f in new[] { JoinCodeFile, SteamCodeFile })
                {
                    if (File.Exists(f)) File.Delete(f);
                    if (File.Exists(f + ".tmp")) File.Delete(f + ".tmp");
                }
            }
            catch (System.Exception e) { Log.Warn("host: could not delete the old join code file: " + e.Message); }
        }

        // A new one-time invite into join-code.txt. A joiner can have the file open at that moment (the replace then fails
        // with an IO error): the invite is kept and Update retries every 500 ms for 10 s, so the file ends up holding the
        // current invite and the run never loses it silently. The secret is never logged.
        void WriteJoinCode()
        {
            byte[] invite = gate.NewInvite();
            var code = new JoinCode { Address = link.Address, Port = link.Port,
                                      Fingerprint = CertPin.Sha256(cert), Invite = invite };
            JoinCodeText = code.Encode();
            if (codeFile == null) Log.Info("host: new join code made (one-time invite)");
            else if (codeFile.Write(JoinCodeText, Now))
                Log.Info("host: new join code written (one-time invite)");
            else
                Log.Warn("host: could not write the join code (" + TextSanitiser.OneLine(codeFile.LastError) + "); retrying every 500 ms for 10 s");
            SteamCodeText = null;
            if (steamHostId == 0) return;
            string steam = SteamCode.Format(new SteamCode
            {
                HostId = steamHostId, Invite = invite, Protocol = ModInfo.Protocol,
                BuildTag = SteamCode.Tag(ModInfo.Version, Session.GameBuildId),
            });
            SteamCodeText = steam;
            if (steamFile == null)
                Log.Info("host: new Steam join code made (host " + steamNet.Short(steamHostId)
                    + ", the same one-time invite, no address)");
            else if (steamFile.Write(steam, Now))
                Log.Info("host: new Steam join code written (host " + steamNet.Short(steamHostId)
                    + ", the same one-time invite, no address)");
            else
                Log.Warn("host: could not write the Steam join code (" + TextSanitiser.OneLine(steamFile.LastError)
                    + "); retrying every 500 ms for 10 s");
        }

        static double Now { get { return NetClock.NowMs / 1000.0; } }

        void RetryJoinCode()
        {
            if (steamFile != null)
            {
                switch (steamFile.Poll(Now))
                {
                    case PublishStep.Written:
                        Log.Info("host: new Steam join code written after retrying");
                        break;
                    case PublishStep.GaveUp:
                        Log.Error("host: could not write join-code-steam.txt for 10 s ("
                            + TextSanitiser.OneLine(steamFile.LastError) + "); nobody can join through Steam with it");
                        break;
                }
            }
            if (codeFile == null) return;
            switch (codeFile.Poll(Now))
            {
                case PublishStep.Written:
                    Log.Info("host: new join code written (one-time invite) after retrying");
                    break;
                case PublishStep.GaveUp:
                    Log.Error("host: could not write join-code.txt for 10 s (" + TextSanitiser.OneLine(codeFile.LastError) + "); the current invite is not in the file, so nobody can join with it");
                    break;
            }
        }

        public void Update()
        {
            events.Clear();
            link.Inbox.Drain(NetClock.NowMs, 200, events);
            foreach (NetEvent e in events)
            {
                try { Handle(e); }
                catch (System.Exception ex) { EventFailed(e, ex); }
            }
            if (events.Count > 0) Status = "host: " + players.Count + " player(s) connected";
            FlushFailed();
            if (steamDoor != null && steamNet != null) steamDoor.Refresh(steamNet.Lobby.Name);   // names Steam fetched
            RetryJoinCode();
            if (gate.InviteSpent) WriteJoinCode();   // a spend by a Revoke (a rule-breaker's invite) sends no Confirmed; the next code is published all the same
        }

        // One bad event is logged (at most one line a second, with how many more were held back) and the rest go on.
        void EventFailed(NetEvent e, System.Exception ex)
        {
            int held;
            if (eventErrors.Allow(Now, out held))
                Log.Error("host: handling a " + e.Kind + " event failed" + (held > 0 ? " (" + held + " more failures since the last line)" : "") + ": " + ex);
        }

        // At most one "connection failed" line a second. Failures inside the window are only counted; the first tick after
        // it ends reports them (even with no new failure), so the number is always the true one.
        void FlushFailed()
        {
            if (failedNotLogged == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - lastFailedLog < 1f) return;
            lastFailedLog = now;
            Log.Info("host: " + (failedNotLogged == 1 ? "a connection failed before joining: " + lastFailedReason
                : failedNotLogged + " connections failed before joining since the last line (latest: " + lastFailedReason + ")"));
            failedNotLogged = 0;
        }

        void Handle(NetEvent e)
        {
            int l;
            switch (e.Kind)
            {
                case NetEventKind.Joined:
                    if (players.ContainsKey(e.PlayerId))
                    {
                        // The same player on a new link: the transport closed the old one without a Closed event
                        // (Replaces), or the old link's own Closed is still on its way and will be ignored (its link
                        // id is no longer the player's). Either way the player never left: keep their state, announce nothing.
                        linkOf[e.PlayerId] = e.Link;
                        players[e.PlayerId] = e.Name;
                        Log.Info("host: player " + e.PlayerId + " moved to a new link" + (e.Replaces ? "" : " (the old one closed by itself)"));
                        if (Rejoined != null) Rejoined(e.PlayerId);   // N-2: the clock treats the player as fresh again
                        break;
                    }
                    players[e.PlayerId] = e.Name;
                    linkOf[e.PlayerId] = e.Link;
                    Log.Info(LogPrivacy.HostJoined(e.PlayerId, e.Name, Boot.Profile == null));
                    if (Joined != null) Joined(e.PlayerId);   // the next join code waits for Confirmed: the invite is only reserved so far
                    break;
                case NetEventKind.Closed:
                    if (e.PlayerId > 0 && linkOf.TryGetValue(e.PlayerId, out l) && l == e.Link)
                    {
                        Log.Info("host: player " + e.PlayerId + " left: " + TextSanitiser.OneLine(e.Reason));
                        WorldRowsLog.Write("host", e.Reason);   // a world area that could not heal: the host's side
                        players.Remove(e.PlayerId);
                        linkOf.Remove(e.PlayerId);
                        rtt.Remove(e.PlayerId);
                        if (Left != null) Left(e.PlayerId);
                        // the player's own goodbye with the leave code: gone for good, not a drop
                        if (e.Bye && e.ByeCode == JoinCodes.Left && LeftForGood != null) LeftForGood(e.PlayerId);
                    }
                    break;
                case NetEventKind.Confirmed:
                    // The first frame after the Welcome arrived, on the player's current link: the invite is spent (or the key).
                    if (linkOf.TryGetValue(e.PlayerId, out l) && l == e.Link)
                    {
                        try { if (Confirmed != null) Confirmed(e.PlayerId, e.Name, e.Rejoin); }
                        finally { if (gate.InviteSpent) WriteJoinCode(); }   // the next joiner's invite, whatever the callback did. Only when SPENT: HasOpenInvite is false while another joiner merely holds it (I-B)
                    }
                    break;
                case NetEventKind.Failed:
                    failedNotLogged++;
                    lastFailedReason = TextSanitiser.OneLine(e.Reason);
                    break;
                case NetEventKind.Message:
                    // answers to our keep-alive pings: timed for the player's current link only
                    var pong = e.Message as Pong;
                    if (pong != null)
                    {
                        if (linkOf.TryGetValue(e.PlayerId, out l) && l == e.Link) TimePong(e.PlayerId, pong);
                        break;
                    }
                    // Only a player's current link is heard.
                    if (linkOf.TryGetValue(e.PlayerId, out l) && l == e.Link)
                    {
                        // the command path first, in its own try: a throw in the join's or the clock's handler must
                        // not lose a request whose player waits for its answer
                        try
                        {
                            if (CommandReceived != null && (e.Message is ActionRequest || e.Message is ObjectRequest || e.Message is HealRequest || e.Message is ResumeProgress))
                                CommandReceived(e.PlayerId, e.Message, e.AtMs);
                        }
                        finally { if (Received != null) Received(e.PlayerId, e.Message); }
                    }
                    break;
            }
        }

        public int Broadcast(Message m)
        {
            var clock = m as Clock;
            if (clock != null) LastClock = clock;
            return link.Broadcast(m);
        }

        // One player's current link (M4: the join's messages). Never throws; false = not sent (no such player, over the
        // cap).
        public bool Send(int player, Message m)
        {
            int l;
            bool sent = link != null && linkOf.TryGetValue(player, out l) && link.Send(l, m);
            System.Action<int, Message> heard = Sent;
            if (sent && heard != null)
            {
                // a listener's throw must never read as a send that failed
                try { heard(player, m); }
                catch (System.Exception e) { Log.Error("host: a listener of sent messages failed: " + e.Message); }
            }
            return sent;
        }

        // Closes one player's current link (the join service's refusals, deadlines and kicks). HostKick.RevokesKeys
        // decides whether the player's rejoin keys go too (too many failures, a broken rule, the host's own kick). The
        // reason is fixed text, budgeted like any Bye; the code goes in the Bye too, and picks the joiner's words.
        public void Kick(int player, int code, bool removed)
        {
            int l;
            if (link == null || !linkOf.TryGetValue(player, out l)) return;
            bool revoke = HostKick.RevokesKeys(code, removed);
            // The host's kick takes the keys and frees the seat, but the account may come straight back in through the
            // Steam lobby (HostKick.ByTheHost); only a ban shuts the door. A broken rule also bars its lobby Hello.
            Log.Info("host: player " + player + " kicked (" + code + ")" + (revoke ? "; its keys are revoked" : ""));
            string words = removed ? JoinText.KickReason(code) : JoinText.JoinEndReason(code);
            link.Kick(l, TextBudget.FitBytes(words, 150), revoke, code);
        }

        // A player that broke the command protocol (a malformed request, a request number going back, a flood past
        // what an honest client can have outstanding): its link closes with the protocol code and its rejoin keys
        // are revoked, as for any other protocol violation (docs/SECURITY.md). The detail stays in this log.
        public void KickViolation(int player, string why)
        {
            int l;
            if (link == null || !linkOf.TryGetValue(player, out l)) return;
            Log.Warn("host: player " + player + " broke the command protocol (" + TextSanitiser.OneLine(why)
                + "); kicked, its keys are revoked");
            link.Kick(l, TextBudget.FitBytes("protocol violation (" + JoinCodes.Protocol + ")", 150), true,
                JoinCodes.Protocol);
        }

        // Steam (M9): players may also come in through Steam's relay. Each connection the transport accepts goes to
        // the same Hello check, gate and table as a TLS one (LinkHost.AddSteamLink): the same pending cap, the same
        // invite or rejoin key. door decides before Steam accepts a connection.
        public bool ListenSteam(SteamTransport transport, System.Func<IncomingConnection, string> door)
        {
            if (link == null || transport == null) return false;
            LinkHost l = link;
            if (!transport.Listen(door, pipe => l.AddSteamLink(pipe))) return false;
            steam = transport;
            return true;
        }
        SteamTransport steam;

        // When Steam is usable: the relay listen beside the home-network one, and this account's id for the Steam
        // join code. Anything that goes wrong leaves the home-network path as it was.
        void StartSteam()
        {
            try
            {
                SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
                if (net == null) return;
                net.Ready.Refresh();
                if (!net.Ready.Usable)
                {
                    Log.Info("host: no Steam join code: " + (net.Ready.Why ?? net.Ready.Problem));
                    return;
                }
                ulong me = net.OwnId();
                if (me == 0) { Log.Info("host: no Steam join code: no Steam player account to name in it"); return; }
                net.Ready.StartRelay();
                SteamTransport t = net.NewTransport("host", opt);
                if (t == null) return;
                steamNet = net;
                steamDoor = new HostSteamDoor(net.Lobby, BanFile, () => NetClock.NowMs);
                HostSteamDoor door = steamDoor;
                link.LobbyMember = door.IsMember;
                link.SteamName = door.NameOf;
                link.SteamHandshakeEnded = door.HandshakeEnded;
                if (!ListenSteam(t, Door))
                {
                    t.Stop();
                    net.Forget(t);
                    Log.Warn("host: no Steam join code: the relay listen failed (the steam host lines say why)");
                    return;
                }
                steamHostId = me;
                Log.Info("host: listening on Steam's relay as " + net.Short(me) + " (ICE reads " + t.ListenIce
                    + (steamFile != null ? "; Steam join code in data\\test-" + Boot.Profile + "\\join-code-steam.txt)"
                                         : "; Steam join code on screen)"));
                // the session's Steam lobby: friends see it and are invited to it; its members pass the door
                net.Lobby.Host(LobbyAccess.FriendsOnly, JoinLimits.MaxPlayers, () => players.Count + 1);
            }
            catch (System.Exception e)
            {
                steamHostId = 0;
                Log.Error("host: starting the Steam listen failed (" + e.GetType().Name + "); home network only");
            }
        }

        // Main thread (the transport's state callback), before Steam accepts the connection: Steam's verified identity
        // and the session's state only. Admitting lets the connection reach the Hello, where the gate still wants the
        // code's invite, the player's own rejoin key, or membership of this host's Steam lobby. Any lobby member is let
        // through (whoever a player invites may join, as in most Steam games; the host kicks or bans). A Steam peer
        // that is not a member yet is held a few seconds and asked again (DoorGrace), for a friend who connected
        // before Steam told this host about their lobby entry.
        string Door(IncomingConnection c)
        {
            LinkHost l = link;
            var facts = new DoorFacts
            {
                RemoteId = c.RemoteId, IndividualAccount = c.IndividualAccount, InfoFlags = c.Flags,
                SessionOpen = l != null && !stopped, KnownPlayer = gate.KnowsSteam(c.RemoteId),
                CodeOpen = steamHostId != 0 && !gate.InviteSpent,
                Pending = l != null ? l.PendingCount() : 0, MaxPending = opt.MaxPending,
            };
            SteamLobby lobby = steamNet != null ? steamNet.Lobby : null;
            HostSteamDoor book = steamDoor;
            if (book == null) return SteamDoor.Words(DoorVerdict.Closed);
            bool askAgain;
            System.Func<ulong, string> steamName = null;
            if (lobby != null) steamName = lobby.Name;
            DoorDecision d = book.Judge(facts, lobby, c.WaitedSeconds, steamName, out askAgain);
            int held;
            if (askAgain)
            {
                if (c.WaitedSeconds == 0 && doorLines.Allow(Now, out held))
                    Log.Info("host: Steam connection from " + steamNet.Short(c.RemoteId) + " is not in the lobby yet; "
                        + "asking again for up to " + DoorGrace.Seconds + " s");
                return SteamTransport.AskAgain;
            }
            if (doorLines.Allow(Now, out held))
                Log.Info("host: Steam connection from " + steamNet.Short(c.RemoteId) + " "
                    + (d.Admitted ? "let through to the handshake (" + d.Reason + ")"
                                  : "refused at the door: " + SteamDoor.Words(d.Verdict))
                    + (c.WaitedSeconds > 0 ? " after " + Seconds(c.WaitedSeconds) + " s" : "")
                    + (held > 0 ? " (" + held + " more door decisions since the last line)" : ""));
            return d.Admitted ? null : SteamDoor.Words(d.Verdict);
        }

        static string Seconds(double s) { return s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }

        // The host's ban (the screens' Ban, the dev trigger "ban <player>"): the player is kicked, and its Steam
        // account is refused at the door in this session and every later one. A home-network player has no account to ban.
        public void Ban(int player)
        {
            ulong id = gate != null ? gate.SteamIdOf(player) : 0;
            string name;
            if (!players.TryGetValue(player, out name) && steamDoor != null && id != 0) name = steamDoor.NameOf(id);
            Kick(player, JoinCodes.Banned, true);
            if (id == 0 || steamDoor == null)
            {
                Log.Info("host: player " + player + " kicked; no Steam account to ban (not a Steam player)");
                return;
            }
            bool saved = steamDoor.Ban(id, name);
            Log.Info("host: player " + player + "'s Steam account " + steamNet.Short(id) + " banned"
                + (saved ? " (kept in steam-bans.txt)" : "") + "; " + LobbyStays);
        }

        // The host's unban list (Steam accounts banned in this or an earlier session), for the screens. Empty while
        // this host does not listen on Steam.
        public List<BannedPlayer> SteamBans()
        {
            return steamDoor != null ? steamDoor.Bans() : new List<BannedPlayer>();
        }

        // The host's unban: the account passes the door again, lobby Hello included.
        public bool Unban(ulong steamId)
        {
            if (steamDoor == null || !steamDoor.Unban(steamId)) return false;
            Log.Info("host: Steam account " + steamNet.Short(steamId) + " unbanned (steam-bans.txt rewritten)");
            return true;
        }

        public bool SteamDoorOpen { get { return steamDoor != null; } }

        public static string BanFile { get { return Path.Combine(Boot.DataDir, "steam-bans.txt"); } }

        // Steam has no call for a lobby owner to remove a member, so a kicked player may still sit in the lobby.
        const string LobbyStays = "Steam lets no lobby owner remove a member, so the door refuses it instead";

        public void Stop()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            stopped = true;
            if (link != null) link.Stop("the host closed the game");
            if (steam != null) steam.Stop();
            if (steamNet != null) steamNet.Lobby.EndHosting("the host session ended");
            if (codeFile != null) codeFile.Cancel();   // a retry must not write the file back after the host has gone
            if (steamFile != null) steamFile.Cancel();
            DeleteJoinCode();
            Log.Info("host: stopped (took " + watch.ElapsedMilliseconds + " ms on the main thread)");
        }
    }
}
