using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The joining game: connects with the join code from the main menu, joins through JoinerFlow (the content check,
    // the claim, the snapshot, the load), reconnects with its rejoin key after a dropped link (2, 4, 8, 16, 32 s) only
    // when the flow says so (ShouldRetry: never after the host refused, a check failed or the host broke the
    // protocol; at most JoinLimits.MaxReconnects times in one join, Welcomes included), measures the round trip from
    // its keep-alive pings. One event that throws must not cost the rest of its batch (a lost Joined leaves a client
    // whose messages are ignored, a lost Closed one that never reconnects): each event is handled in its own try, with
    // a throttled ERROR. In a test profile the current rejoin key is kept in <data>\rejoin-key.txt (written through a
    // temp file after every Welcome, never logged or copied), so a restarted game can come back with -semp-rejoin; the
    // host's address and certificate still come from the join file. A Steam join code goes through Steam's relay to the
    // host's Steam account (SteamTransport.Connect, the account checked when Steam connects); a home-network code over
    // TLS as before. A Steam lobby join (StartLobby) goes the Steam way to the lobby's checked host with no code.
    public sealed class SessionClient
    {
        readonly LineThrottle eventErrors = new LineThrottle(1.0);
        JoinCode code;           // a home-network code, or
        SteamCode steamCode;     // a Steam join code, or
        ulong lobbyHost;         // a Steam lobby's checked host (no code: the host's door admits lobby members)
        SteamTransport steam;    // this joiner's Steam connections (made on the first Steam join)
        LinkClient link;
        string rejoin;
        bool helloUsedKey;   // the current connection presented a rejoin key (not the invite)
        float retryAt = -1f, holdUntil = -1f, nextDevPoll;
        bool givenUp;   // "not connected" was logged; a new connect attempt clears it
        PublishedFile keyFile;
        readonly List<NetEvent> events = new List<NetEvent>();

        public int PlayerId { get; private set; }
        public bool Connected { get; private set; }
        public double RttMs { get; private set; }         // smoothed round trip; -1 until measured
        public System.Action Joined, Left;
        public System.Action<Message> Received;
        public string Status { get; private set; }
        // The session's plan (the dev flags' or the menus'): this player's name, and whether test seams may act. Set
        // by Session before Start.
        public SessionPlan Plan;
        // The host's name as its Welcome gave it (cleaned), for the session view; null before the first Welcome.
        public string HostName { get; private set; }

        // The joiner's driver and its game (Task 11). Made by the constructor, so every event has them.
        public readonly JoinClient World;
        public readonly JoinerFlow Flow;
        public readonly ClientLabel Label = new ClientLabel();   // whether the label still shows this session
        // The host's lobby at the menu (M8 DD5): a join from the menus or a Steam invite waits there until let in.
        public readonly ClientLobby Lobby;
        bool leaving;   // the player's own Leave: its goodbye is out, nothing reconnects

        public SessionClient()
        {
            World = new JoinClient(false, null);
            Flow = new JoinerFlow(World, new ThreadBackground(), () => NetClock.NowMs);
            World.Flow = Flow;
            Lobby = new ClientLobby(this);
        }

        public void Start(string codeText)
        {
            RttMs = -1;
            lobbyHost = 0;
            string error = "no join code";
            if (codeText == null || !JoinRoute.Parse(codeText, out code, out steamCode, out error))
            {
                NeverStarted("client: bad join code (" + error + ")");
                return;
            }
            string versions = VersionProblem(steamCode);
            if (versions != null)
            {
                NeverStarted("client: not joining: " + versions);
                return;
            }
            if (Plan.Test && Boot.Dev != null && Boot.Dev.Rejoin)
            {
                // A restarted game: the key it kept, never the invite (which another player may hold by now).
                rejoin = ReadKey();
                if (rejoin == null)
                {
                    NeverStarted("client: -semp-rejoin, but no usable rejoin key in the test profile");
                    return;
                }
                Log.Info("client: rejoining with the kept key (-semp-rejoin)");
            }
            Flow.NewAttempt();
            Lobby.NewJoin(Plan.Lobby);
            leaving = false;
            Connect();
        }

        // Nothing to connect to: the session is over before it began, so it comes down (IJoinerWorld.SessionOver), or
        // its clock would hold a solo game started from the menu paused for a host that never comes.
        void NeverStarted(string why)
        {
            Status = why;
            Log.Error(Status);
            World.SessionOver();
        }

        // A join through a Steam lobby (SessionPlan.SteamHost): the lobby is joined and its owner checked against its
        // data (SteamLobby), and the versions were checked from that data; the Hello carries no invite.
        public void StartLobby(ulong hostId)
        {
            RttMs = -1;
            if (!SteamCode.IsIndividual(hostId))
            {
                NeverStarted("client: not joining: the lobby names no player's Steam account as its host");
                return;
            }
            lobbyHost = hostId; rejoin = null;
            Flow.NewAttempt();
            Lobby.NewJoin(Plan.Lobby);
            leaving = false;
            Connect();
        }

        void Connect()
        {
            var hello = new Hello
            {
                Protocol = ModInfo.Protocol, ModVersion = ModInfo.Version, GameBuild = Session.GameBuildId,
                GameVersion = ModInfo.ClipGameVersion(Application.version), Name = Plan.PlayerName,
            };
            if (Plan.Test) HelloFault.Apply(hello, Plan.JoinFault);   // a test plan's planted fault only
            if (rejoin != null) hello.Rejoin = rejoin;
            else if (lobbyHost == 0) hello.Invite = HostGate.Hex(steamCode != null ? steamCode.Invite : code.Invite);
            helloUsedKey = rejoin != null;
            givenUp = false;
            link = new LinkClient(Session.Options());
            if (lobbyHost != 0) { ConnectThroughSteam(hello, lobbyHost); return; }
            if (steamCode != null) { ConnectThroughSteam(hello, steamCode.HostId); return; }
            link.Connect(code.Address, code.Port, code.Fingerprint, hello);
            Status = "client: connecting to " + code.Address + ":" + code.Port;
            Log.Info(LogPrivacy.ClientConnecting(code.Address, code.Port, rejoin != null, Boot.Profile == null));
        }

        // The relay route: a Steam connection to the host's account (the code's or the lobby's pin), then the same
        // Hello as over TLS. Without a usable Steam the link fails at once with the reason (the usual failure path, no
        // retry loop of its own).
        void ConnectThroughSteam(Hello hello, ulong hostId)
        {
            SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
            SteamPipe pipe = null;
            string why = "Steam is not set up in this game";
            if (net != null)
            {
                net.Ready.Refresh();
                why = net.Ready.Problem;
                if (net.Ready.Usable)
                {
                    net.Ready.StartRelay();
                    if (steam == null || !steam.Started) steam = net.NewTransport("joiner", Session.Options());
                    pipe = steam != null ? steam.Connect(hostId) : null;
                    why = pipe == null ? "Steam gave no connection (the steam joiner lines say why)" : null;
                }
            }
            Status = "client: connecting to host " + (net != null ? net.Short(hostId) : "?")
                + " through Steam's relay";
            Log.Info(Status + (rejoin != null ? " (rejoin)" : "") + (why != null ? "; cannot: " + why : ""));
            link.ConnectSteam(pipe, hello);
        }

        // A Steam code names the protocol and the build it was made for; a home-network code finds out at the Hello.
        static string VersionProblem(SteamCode steamCode)
        {
            return steamCode == null ? null
                : steamCode.VersionProblem(ModInfo.Protocol, ModInfo.Version, Session.GameBuildId);
        }

        // A new join from the menus after this client's last session ended (M8 Track U): like the dev trigger "join",
        // a fresh attempt with a code JoinRoute.Parse accepted (a home-network or a Steam code), and nothing connects
        // otherwise. null = connecting; else why not (never the code itself).
        public string JoinAgain(string codeText, SessionPlan plan)
        {
            string error = "no join code";
            JoinCode c;
            SteamCode sc;
            if (codeText == null || !JoinRoute.Parse(codeText, out c, out sc, out error))
            {
                Log.Error("client: bad join code (" + error + ")");
                return error;
            }
            string versions = VersionProblem(sc);
            if (versions != null) { Log.Error("client: not joining: " + versions); return versions; }
            if (link != null) link.Close("a new join");
            Plan = plan;
            Session.ReopenClientSession();   // a new session, with its own clock
            code = c; steamCode = sc; rejoin = null; retryAt = -1f; holdUntil = -1f; RttMs = -1;
            lobbyHost = 0;
            Flow.NewAttempt();
            Lobby.NewJoin(plan.Lobby);
            leaving = false;
            Connect();
            return null;
        }

        // The same after a session ended, through a Steam lobby (SessionPlan.SteamHost). null = connecting.
        public string JoinLobbyAgain(ulong hostId, SessionPlan plan)
        {
            if (!SteamCode.IsIndividual(hostId)) return "the lobby names no player's Steam account as its host";
            if (link != null) link.Close("a new join");
            Plan = plan;
            Session.ReopenClientSession();
            lobbyHost = hostId; rejoin = null; retryAt = -1f; holdUntil = -1f; RttMs = -1;
            Flow.NewAttempt();
            Lobby.NewJoin(plan.Lobby);
            leaving = false;
            Connect();
            return null;
        }

        public bool Send(Message m) { return link != null && link.Send(m); }

        // The player's own Leave (DD18): the join ends here (no reconnect, back to the menu) and the goodbye carries the
        // leave code, so the host keeps the seat with no drop pause (SessionHost.LeftForGood -> ClockHost.PlayerLeft).
        public void Leave()
        {
            LinkClient l = link;
            if (l == null || leaving) return;
            leaving = true;
            retryAt = -1f;
            Lobby.LeftOnPurpose();
            Log.Info("client: leaving the session (goodbye with the leave code)");
            try { Flow.LeftOnPurpose(); }
            catch (System.Exception e) { Log.Error("client: the join flow could not end: " + e.Message); }
            l.Close("the player left the session", JoinCodes.Left);
        }

        // The command stream found this game out of step (DD7): its world leaves the session, the flow counts the
        // rejoin as a reconnect, and the link is closed with the reason (the host sees a drop). The usual reconnect
        // then loads a fresh snapshot, never this world.
        public void OutOfStep(string why)
        {
            string reason = "out of step: " + TextSanitiser.OneLine(why);
            Log.Info("client: " + reason + "; rejoining from a fresh copy");
            World.LeaveSession();
            Flow.OutOfStep();
            LinkClient l = link;
            if (l != null && Connected) l.Close(TextBudget.FitBytes(reason, 150));
        }

        public void Update()
        {
            if (link != null)
            {
                events.Clear();
                link.Inbox.Drain(NetClock.NowMs, 200, events);
                foreach (NetEvent e in events)
                {
                    try { Handle(e); }
                    catch (System.Exception ex) { EventFailed(e, ex); }
                }
            }
            float now = Time.realtimeSinceStartup;
            if (retryAt >= 0 && now >= retryAt && now >= holdUntil) { retryAt = -1f; Connect(); }
            if (keyFile != null && keyFile.Pending) keyFile.Poll(NetClock.NowMs / 1000.0);
            try { Flow.Update(); }
            catch (System.Exception ex) { Log.Error("client: the join flow's update failed: " + ex); }
            try { Lobby.Update(); }
            catch (System.Exception ex) { Log.Error("client: the lobby's update failed: " + ex); }
            World.Update();
            if (DevTriggers.Every(ref nextDevPoll, now)) DevPoll();
        }

        // Test profiles only (DevTriggers reads nothing without one): "disconnect" (drop the link; the usual reconnect
        // follows), "drop <seconds>" (drop it and wait that long before the first reconnect: the long drop), "join"
        // (connect again with the join file's invite: the fake host's hostile list against a real joiner), "decline"
        // (refuse the summary), "agency <id>" (the agency to claim next).
        void DevPoll()
        {
            if (DevTriggers.Take("disconnect") != null && link != null) link.Close("dev disconnect");
            string drop = DevTriggers.Take("drop");
            int seconds;
            if (drop != null && int.TryParse(drop, out seconds) && seconds >= 0 && seconds <= 3600 && link != null)
            {
                holdUntil = Time.realtimeSinceStartup + seconds;
                link.Close("dev drop");
            }
            if (DevTriggers.Take("join") != null)
            {
                string text = Session.JoinCodeText();
                string error = "no join code";
                JoinCode c;
                SteamCode sc;
                if (text == null || !JoinRoute.Parse(text, out c, out sc, out error))
                    Log.Error("client: dev join: the join file holds no usable code (" + error + ")");
                else
                {
                    if (link != null) link.Close("dev join");
                    Session.ReopenClientSession();   // after a session end: a new session, with its own clock
                    code = c; steamCode = sc; rejoin = null; retryAt = -1f; holdUntil = -1f;
                    lobbyHost = 0;
                    Lobby.NewJoin(false);
                    leaving = false;
                    Flow.NewAttempt();
                    Connect();
                }
            }
            if (DevTriggers.Take("decline") != null) World.Decline();
            string agency = DevTriggers.Take("agency");
            if (agency != null)
            {
                if (Regex.IsMatch(agency, @"^[a-z0-9_]{1,64}\z")) World.SetAgency(agency);
                else Log.Warn("client: dev agency: not an agency id");
            }
        }

        // One bad event is logged (at most one line a second, with how many more were held back) and the rest go on.
        void EventFailed(NetEvent e, System.Exception ex)
        {
            int held;
            if (eventErrors.Allow(NetClock.NowMs / 1000.0, out held))
                Log.Error("client: handling a " + e.Kind + " event failed" +
                          (held > 0 ? " (" + held + " more failures since the last line)" : "") + ": " + ex);
        }

        void Handle(NetEvent e)
        {
            switch (e.Kind)
            {
                case NetEventKind.Joined:
                    PlayerId = e.PlayerId;
                    rejoin = link.RejoinKey;
                    Connected = true;
                    Status = "client: player " + PlayerId + " with " + e.Name;
                    Log.Info(LogPrivacy.ClientJoined(PlayerId, e.Name, Boot.Profile == null));
                    HostName = TextSanitiser.Clean(e.Name, PlayerNames.MaxChars, "");
                    WriteKey(rejoin);
                    try { if (Joined != null) Joined(); }
                    finally
                    {
                        // The receiver goes onto THIS link before anything is sent (JoinerFlow attaches it first).
                        LinkClient l = link;
                        System.Action<IBulkSink> attach = s => l.Bulk = s;
                        // a lobby join waits at the menu until the host lets it in (ClientLobby); any other goes on
                        if (!Lobby.Hold(PlayerId, Send, attach)) Flow.Welcomed(PlayerId, Send, attach, helloUsedKey);
                    }
                    break;
                case NetEventKind.Closed:
                case NetEventKind.Failed:
                    bool was = Connected;
                    Connected = false;
                    Lobby.Closed(e.Bye, e.ByeCode);
                    Log.Info("client: " + (was ? "disconnected" : "could not join") + ": "
                        + TextSanitiser.OneLine(e.Reason));
                    // The flow hears it first: whether a retry is due is its call (a refusal, a refused reconnect,
                    // a failed check, a host that said goodbye or broke the protocol is never retried; reconnects
                    // are capped).
                    try { Flow.Lost(e.Reason ?? "closed", e.Violation, e.Refused, e.Bye, e.ByeCode); }
                    catch (System.Exception ex) { Log.Error("client: the join flow could not take the lost link: "
                        + ex); }
                    // The rejoin is scheduled before the game is told: a callback that throws must not cost the
                    // reconnect.
                    if (!e.Refused && !leaving && rejoin != null && Flow.ShouldRetry)
                    {
                        int wait = JoinerFlow.RetryWaitSeconds(Flow.Retries);
                        retryAt = Time.realtimeSinceStartup + wait;
                        Status = "client: reconnecting in " + wait + " s (try " + Flow.Retries + " of "
                            + JoinLimits.MaxReconnects + ")";
                    }
                    else
                    {
                        // A close that follows the join's own end (this game's refusal or the host's) is not a kick
                        Status = "client: not connected (" + JoinText.LinkEnded(TextSanitiser.OneLine(e.Reason), e.Bye,
                            e.ByeCode, Flow.EndCode, Flow.EndedHere) + ")";
                        // Once per transition (a second Closed/Failed for the same give-up is not logged again).
                        if (!givenUp) { givenUp = true; Log.Info(Status); }
                    }
                    if (was && Left != null) Left();
                    break;
                case NetEventKind.BulkProgress:
                    Flow.Progress();
                    break;
                case NetEventKind.Message:
                    var p = e.Message as Pong;
                    if (p != null)
                    {
                        double rtt = NetClock.NowMs - p.T;
                        if (rtt >= 0 && rtt < 60000) RttMs = RttMs < 0 ? rtt : RttMs * 0.8 + rtt * 0.2;
                        break;
                    }
                    Lobby.Received(e.Message);
                    try { if (Received != null) Received(e.Message); }
                    finally { if (JoinMessageRoute.ForTheFlow(e.Message)) Flow.Received(e.Message); }
                    break;
            }
        }

        // ---- the kept rejoin key (test profiles only; never logged) ----

        string KeyPath
        {
            get { return Boot.Profile != null && Boot.DataDir != null ? Path.Combine(Boot.DataDir, "rejoin-key.txt")
                : null; }
        }

        void WriteKey(string key)
        {
            string path = KeyPath;
            if (path == null || !RejoinKeyText.IsKey(key)) return;
            if (keyFile == null) keyFile = new PublishedFile(path, 0.5, 10);
            if (!keyFile.Write(key, NetClock.NowMs / 1000.0))
                Log.Warn("client: could not write rejoin-key.txt yet (" + TextSanitiser.OneLine(keyFile.LastError)
                    + "); retrying");
        }

        string ReadKey()
        {
            string path = KeyPath;
            if (path == null) return null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > RejoinKeyText.MaxFileBytes) return null;
                var buf = new char[RejoinKeyText.MaxFileBytes + 1];
                int n;
                using (var r = new StreamReader(path)) n = r.ReadBlock(buf, 0, buf.Length);
                return RejoinKeyText.Parse(new string(buf, 0, n));
            }
            catch (System.Exception e)
            {
                Log.Error("client: could not read rejoin-key.txt: " + e.GetType().Name);
                return null;
            }
        }

        public void Stop()
        {
            retryAt = -1f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (link != null) link.Close("the player closed the game");
            Log.Info("client: closed (took " + watch.ElapsedMilliseconds + " ms on the main thread)");
        }
    }
}
