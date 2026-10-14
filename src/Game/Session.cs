using System;
using System.IO;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // Every 10 s in a session: the longest and the mean frame, gen-0 collections and memory, so a run with several
    // games on one PC shows how the PC copes. Plain fields per frame; one string every 10 s.
    public sealed class PerfLine
    {
        float start = -1f, last, max, sum;
        int frames, gc0;

        public void Frame()
        {
            float now = Time.realtimeSinceStartup;
            if (start < 0f) { start = now; last = now; gc0 = GC.CollectionCount(0); return; }
            float gap = now - last;
            last = now;
            if (gap > max) max = gap;
            sum += gap;
            frames++;
            if (now - start < 10f) return;
            int gc = GC.CollectionCount(0);
            float meanMs = frames > 0 ? sum * 1000f / frames : 0f;
            string mean = meanMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            Log.Info("session: perf frame max " + (int)(max * 1000f) + " ms mean " + mean + " ms, gc " + (gc - gc0) +
                     ", memory " + (GC.GetTotalMemory(false) / (1024 * 1024)) + " MB");
            start = now; max = 0f; sum = 0f; frames = 0; gc0 = gc;
        }
    }

    // The dev session (test profiles only): a host (-semp-host) starts once its save has loaded; a joiner (-semp-join
    // or -semp-join-file) starts at the main menu and joins from there. Since M8's early track the same session can
    // start from the menus (Early, StartFromPlan) on a valid test or player-path profile; both are a SessionPlan.
    // Every network event is handled here on the main thread (SempRoot.Update -> Session.Update); network threads only
    // fill the inbox.
    public static partial class Session
    {
        public static SessionHost Host { get; private set; }
        public static SessionClient Client { get; private set; }
        public static ClockHost HostClock { get; private set; }
        public static ClockClient ClientClock { get; private set; }
        static bool started;
        // whether a session's world has loaded in this process, and so what an ended session leaves behind
        static readonly SessionLifecycle life = new SessionLifecycle();
        static int errors;
        static string build;
        static float lastUpdate = -1f;

        // This game's build id (hash of Assembly-CSharp.dll), worked out once. Assembly.Location can be empty in Unity's
        // Mono, so the file is also looked for in <game>_Data\Managed.
        public static string GameBuildId
        {
            get
            {
                if (build == null)
                {
                    string path = typeof(Manager.TimeController).Assembly.Location;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
                    build = GameBuild.FromFile(path);
                }
                return build;
            }
        }

        // The join code from -semp-join, or from the file -semp-join-file names (so the secret is not on the command line).
        // The file must lie under the mod's data folder (where every test host writes its join-code.txt) and is read with
        // a hard size cap; its content is never logged.
        internal static string JoinCodeText()
        {
            DevFlags f = Boot.Dev;
            if (f.JoinCode != null) return f.JoinCode;
            return ReadJoinFile(f.JoinFile);
        }

        // A join code file under the mod's data folder (the dev flag's, or a test host's join-code.txt for the early
        // track's dev trigger), read with a hard size cap; null when it is unusable. Never logged.
        internal static string ReadJoinFile(string file)
        {
            try
            {
                string full = Path.GetFullPath(file);
                if (!PathRules.IsInside(Path.GetDirectoryName(Boot.DataDir), full))   // <mod>\data\, tested in PathRulesTests
                {
                    Log.Error("session: the join file must be under the mod's data folder");
                    return null;
                }
                // A join code is at most JoinCode.MaxTextLength characters; a little slack for a trailing newline.
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > JoinCode.MaxTextLength + 8)
                {
                    Log.Error("session: the join file is missing or too big (a join code is at most " + JoinCode.MaxTextLength + " characters)");
                    return null;
                }
                var buf = new char[JoinCode.MaxTextLength + 9];
                int n;
                using (var r = new StreamReader(full))
                    n = r.ReadBlock(buf, 0, buf.Length);
                string text = new string(buf, 0, n).Trim();
                if (text.Length > JoinCode.MaxTextLength)
                {
                    Log.Error("session: the join file is too long (a join code is at most " + JoinCode.MaxTextLength + " characters)");
                    return null;
                }
                return text;
            }
            catch (Exception e) { Log.Error("session: could not read the join file: " + e.Message.Replace('\r', ' ').Replace('\n', ' ')); return null; }
        }

        // The links' options: the running plan's simulated network (a test plan only; SessionPlan.Check refuses one on
        // any other plan, and this never reads it from another).
        public static NetOptions Options()
        {
            SessionPlan plan = Plan;
            SimOptions f = plan != null && plan.Test ? plan.Sim : null;
            if (f == null) return new NetOptions();
            return new NetOptions
            {
                SimLatencyMs = f.SimLatencyMs, SimJitterMs = f.SimJitterMs, SimLossPercent = f.SimLossPercent,
                SimRateKiBps = f.SimRateKiBps,
            };
        }

        // The session that started: from the dev flags (test profiles) or from the menus (M8 Track U); null before.
        public static SessionPlan Plan { get; private set; }

        // M8 Track U: the early session object (the Multiplayer button, the host intent, the session actions over
        // M4). One instance, made at the first main menu on an early-track profile and kept for the process; it has its
        // own reset on each load. null on a normal player's game.
        public static MenuSessions Early { get; private set; }

        // The joiner's dev feed (test profiles): planted snapshots through the joiner's walls with no network.
        public static JoinDevFeed Feed { get; private set; }

        // M4's join parts, made once per process for a test session (or a solo game with -semp-pause-at-day): the
        // content scan (at the first menu), the host's clock facts, the print, the day-event lines and the pause at a
        // day; the host's JoinHost. Each has its own reset per load (OnSaveLoaded).
        public static JoinHost Join { get; private set; }
        // M5: the commands' root (the host's, for now) and the session's human agencies, which survive every load (N1)
        public static CommandSession Commands { get; private set; }
        public static HumanSet Humans { get; private set; }
        static ContentScan content;
        static HostTime time;
        static StateFingerprint print;
        static DayEventLog days;
        static HostPauseDriver pause;
        static string refused;   // why the session did not start (an unsupported game build), for the label

        static bool SessionInProfile
        {
            get { return Boot.Profile != null && Boot.DataDir != null && Boot.Dev != null && Boot.Dev.SessionWanted; }
        }

        static void MakeJoinParts()
        {
            if (content == null) content = new ContentScan();
            if (time == null) time = new HostTime();
            if (print == null) print = new StateFingerprint();
            if (days == null) days = new DayEventLog();
            if (pause == null) pause = new HostPauseDriver(time);
        }

        // The joiner's game glue gets the scan and the print through its seams.
        static void Wire(JoinClient world)
        {
            ContentScan c = content;
            StateFingerprint p = print;
            world.MenuSummary = c.MenuSummary;
            world.MenuProblem = c.MenuProblem;
            world.MenuItems = c.MenuItems;
            world.PostLoadScan = c.AfterLoad;
            world.SpeedListCheck = SpeedListProblem;
            world.SessionEnded = EndClientSession;
            // -semp-join-fault fingerprint flips one block in every print (test profiles only: DevFlags is null
            // otherwise)
            string fault = Boot.Profile != null && Boot.Dev != null ? Boot.Dev.JoinFault : null;
            world.Print = origin => JoinFaultPrint.Apply(p.Take(origin), fault);
        }
        static readonly PerfLine perf = new PerfLine();

        // The joiner's session is over and the player is on the way to the menu (a goodbye, a give-up, a refusal, a
        // failed join): the clock follows and holds nothing, the game's speed calls and planner are its own again, so a
        // solo game started from the menu runs as any other.
        static void EndClientSession()
        {
            try { if (ClientClock != null) ClientClock.Detach(); }
            catch (Exception e) { Log.Error("session: detaching the clock failed: " + e.Message); }
            ClockSync.EndSession();
            ClientClock = null;
            // the commands end with the session: otherwise the captures, the greyed buttons and the day gate's step
            // carry stay live in the solo game loaded next
            try { if (Commands != null) Commands.Steps.Loaded(); }
            catch (Exception e) { Log.Error("session: forgetting the step carry failed: " + e.Message); }
            Commands = null;
            HumanAgencies.Stop();
            if (Client != null) Client.Label.SessionOver();   // its words stay on the menu, not in a solo game
            Log.Info("session: the client's session is over; the clock hooks are down");
        }

        // The dev "join" trigger after the client's session ended (fakehost --joiner-dev joins a real joiner again
        // after every refused case): a new join is a new session with a clock of its own. Without one the joined game
        // would run free and its speed calls would reach no host.
        internal static void ReopenClientSession()
        {
            SessionClient c = Client;
            if (c == null || ClientClock != null) return;
            // the last session's commands ended with it (EndClientSession): a new join gets a new stream and gate
            if (Commands == null) StartClientCommands(c);
            ClientClock = new ClockClient(c);
            CommandClientGlue glue = Commands.ClientGlue;
            ClientClock.GateHolding = () => glue.Holding;
            c.Label.NewSession();
            Log.Info("session: a new join after the session ended - the clock follows the host again");
        }

        // A joiner: the dev flags' (its session starts at the first menu), or one whose session started from the
        // menus' join (M8 Track U).
        static bool Joiner
        {
            get
            {
                return Boot.Profile != null && Boot.DataDir != null && Boot.Dev != null && Boot.Dev.SessionWanted &&
                       !Boot.Dev.Host || Plan != null && Plan.Role == SessionRole.Client;
            }
        }

        // The joiner's current snapshot load's habitability prewarm gate (the network join's or the dev feed's); null
        // on a host, a solo game and a joiner with no load in progress. Read by HabitabilityPrewarm's prefixes.
        public static PrewarmSkip PrewarmSkip
        {
            get
            {
                SessionClient c = Client;
                PrewarmSkip g = c != null ? c.World.PrewarmSkip : null;
                JoinDevFeed f = Feed;
                return g ?? (f != null ? f.PrewarmSkip : null);
            }
        }

        // A network join is connected or loading (the dev feed then stays off).
        static bool ClientBusy { get { return Client != null && (Client.Connected || Client.World.Loading); } }

        // Every "main menu ready" (MenuMarker's postfix). A joiner's session starts at the first one: a joiner starts
        // at the menu and loads nothing of its own. A later one is a return to the menu, from which a full rejoin goes
        // on. A host's session still starts at "save loaded".
        public static void OnMenuReady()
        {
            atMenu = true;
            try
            {
                ClockSync.NewWorld();   // a running world's planner windows are gone (ClockHooks.NewWorld)
                // the early track (its menu entry: a normal install, or a valid test or player-path profile)
                if (Early == null && GameHooks.IsInstalled(HookPlan.MenuEntry)) Early = new MenuSessions();
                if (Early != null) Early.OnMenuReady();
                LobbyAtMenu();   // the host's lobby: a world that never came, or a running session left for the menu
                // a session may start from the menus too, so an early-track profile is scanned here as well
                if (SessionInProfile || Early != null)
                {
                    // before any save is loaded, on the host and on a joiner alike, so both are scanned in the same
                    // state. Without session flags only the scan is made here: the rest of the join parts come with
                    // a session (StartFromPlan), so a solo game's load still finds no pause driver and arms its own
                    // for -semp-pause-at-day.
                    if (SessionInProfile) MakeJoinParts();
                    else if (content == null) content = new ContentScan();
                    content.TakeMenuOnce();
                    // after the scan, never before it (HookPlan.ModalPopups); tried once, a failure is only logged
                    GameHooks.EnsureDeferred(HookPlan.ModalPopups, false);
                    // the host's own summary, never the planted-fault copy a joiner sends
                    bool dumped;
                    string line = ContentSummaryFile.Dump(Boot.Dev, Boot.DataDir, content.HostSummary, out dumped);
                    if (line != null) { if (dumped) Log.Info(line); else Log.Error(line); }
                }
                if (!Joiner) return;
                if (!started) { Start(); return; }
                if (Client != null)
                {
                    Client.World.AtMenuNow();
                    Client.Flow.MenuReady();
                }
                if (Feed != null) Feed.OnMenuReady();
            }
            catch (Exception e) { Log.Error("session: menu ready failed: " + e); }
        }

        // success: the save-loaded event's flag (a failed load says false). On a joiner the join's apply step runs
        // here, BEFORE Drift.OnSaveLoaded (SempRoot calls this first), and a joiner never starts a session here.
        public static void OnSaveLoaded(bool success)
        {
            atMenu = false;
            // the old world's planner windows are gone (ClockHooks.NewWorld): on a host, a joiner and a solo game
            try { ClockSync.NewWorld(); }
            catch (Exception e) { Log.Error("session: the clock's step for the new world failed: " + e); }
            if (Joiner)
            {
                // the joiner's snapshot load: from now on this process has run a session's world
                if (Client != null && Client.World.Loading) life.WorldLoaded();
                try
                {
                    if (Client != null) Client.Label.SaveLoaded();   // after the session's end: a solo game
                    if (Client != null) Client.World.OnSaveLoaded(success);
                    if (Feed != null) Feed.OnSaveLoaded(success);
                    if (print != null) print.OnSaveLoaded();
                    if (days != null) { days.Attach(); days.Arm(); }   // the first 10 day events after the join's load
                }
                catch (Exception e) { Log.Error("session: the joiner's save-loaded step failed: " + e); }
                ResetEarly();
                return;
            }
            // a throw here must never reach the game's own save-loaded event
            try { HostSaveLoaded(success); }
            catch (Exception e) { Log.Error("session: the save-loaded step failed: " + e); }
            ResetEarly();
        }

        // The early session's own reset on every load (after the host intent was taken)
        static void ResetEarly()
        {
            try { if (Early != null) Early.OnSaveLoaded(); }
            catch (Exception e) { Log.Error("session: the early session's reset failed: " + e); }
        }

        // A host's or a solo game's load: the session starts at the first, a later one restarts its measurements.
        static void HostSaveLoaded(bool success)
        {
            if (time != null) time.OnSaveLoaded();
            if (print != null) print.OnSaveLoaded();
            if (days != null) days.Attach();
            // the host's lobby: its world, loaded after Start (the session's world half, then the start set goes in)
            if (!started && Lobby != null && Lobby.Starting) { StartLobbyWorld(success); return; }
            if (started)
            {
                // Another save loaded while the session runs: the date moved, so the rate measurement starts over.
                if (HostClock != null) HostClock.SaveLoaded();
                if (Join != null)
                {
                    Join.OnSaveLoaded();
                    Join.HostLoaded();   // a new command stream: every client rejoins fully
                }
                return;
            }
            // M8 Track U: a host chosen at the menu starts once its save has loaded (the intent is spent either way)
            SessionPlan fromMenu = Early != null ? Early.TakeHostIntent(success) : null;
            if (fromMenu != null) { StartFromPlan(fromMenu); return; }
            if (!SessionInProfile)
            {
                // a solo game in a test profile may pause at a day too (-semp-pause-at-day); nothing else of M4 runs
                if (Boot.Profile != null && Boot.Dev != null && Boot.Dev.PauseAtDay > 0 && pause == null)
                {
                    time = new HostTime();
                    time.OnSaveLoaded();
                    pause = new HostPauseDriver(time);
                    pause.Arm(Boot.Dev.PauseAtDay, "-semp-pause-at-day, solo");
                    if (Boot.Dev.DriftSpeed > 0f)
                        Log.Warn("join: -semp-drift-speed puts the solo speed back, so the pause may not hold");
                }
                return;
            }
            Start();
        }

        // The dev flags' session (test profiles only).
        static void Start() { StartFromPlan(DevPlan()); }

        // The dev flags as a plan; a joiner's code from -semp-join-file is read here. The names are M4's ("Host A",
        // "Player B"); PlayerNames.Clean in FromDevFlags cuts them to 24 characters, so a profile name over 17
        // characters (the launch rule allows 32) is shortened there; the names are only labels.
        static SessionPlan DevPlan()
        {
            DevFlags f = Boot.Dev;
            string name = (f != null && f.Host ? "Host " : "Player ") + Boot.Profile;
            SessionPlan plan = SessionPlan.FromDevFlags(f, Boot.Profile, MenuSessions.PlayerPath, name);
            if (plan != null && plan.Role == SessionRole.Client) plan.JoinCodeText = JoinCodeText();
            return plan;
        }

        public static void StartFromPlan(SessionPlan plan) { StartFromPlan(plan, null); }

        // Starts the plan's session: from the dev flags (Start) or from the menus (M8 Track U). agency: a joiner's
        // first claim from the agency picker; null keeps -semp-agency's. Every plan passes SessionPlan.Check first.
        public static void StartFromPlan(SessionPlan plan, string agency)
        {
            started = true;
            // a host here has a world loaded (its save, or the menus' host intent): treated as a session's world
            if (plan != null && plan.Role == SessionRole.Host) life.WorldLoaded();
            string why = plan == null ? "no session plan" : CheckPlan(plan);
            if (why != null) { Log.Error("session: not started: " + why); return; }
            Log.Info("session: starting " + plan);   // the plan never prints its join code
            // the plan's role, not -semp-host: a host chosen at the menus passes the same checks as a dev host
            bool hosting = plan.Role == SessionRole.Host;
            if (!BuildSupported() || (hosting && !SpeedListOk())) return;
            // I8: a rule hook that did not patch would leave a human agency to the game's single-player rules and the
            // games would part, so no session starts and the game stays single player. A host patches them here,
            // after the menu's content scan; a joiner patches them before its snapshot load.
            // An autoload host can load before the menu scan has run; the rules must not go in before it.
            if (hosting && (content == null || !content.Taken))
            {
                refused = "session: not started: the menu's content scan has not run yet";
                Log.Error(refused);
                return;
            }
            if (hosting && !GameHooks.EnsureRuleHooks())
            {
                refused = GameHooks.SessionHookRefusal;
                Log.Error(GameHooks.SessionHookRefusal);
                return;
            }
            if (hosting && !AsteroidKindsOk()) return;
            if (!GameHooks.IsInstalled(HookPlan.ClockSync)) { Log.Error("session: not started: the clock patch is not installed"); return; }
            if (!GameHooks.IsInstalled(HookPlan.TutorialCalls)) Log.Warn("session: the tutorial patch is not installed: the tutorial's speed calls will count as this player's choices");
            Plan = plan;   // from here the session is this plan's (the links read its options)
            try
            {
                SimOptions sim = plan.Test && plan.Sim != null ? plan.Sim : new SimOptions();
                if (sim.SimLatencyMs > 0 || sim.SimLossPercent > 0)
                    Log.Info("session: simulating " + sim.SimLatencyMs + " ms +- " + sim.SimJitterMs + " ms, " +
                             sim.SimLossPercent + "% loss (dev)");
                if (sim.SimRateKiBps > 0)
                    Log.Info("session: simulating an upload of " + sim.SimRateKiBps + " KiB/s (dev)");
                if (plan.Role == SessionRole.Host)
                {
                    // Host is set only once Start succeeded (a port in use must not leave a half-started host behind).
                    // If anything throws after the listener is open (the join code file, the clock), the host is stopped
                    // before it is dropped: the port and the certificate must not leak. The caller logs the error once.
                    var h = new SessionHost();
                    try
                    {
                        h.Start(plan);
                        Host = h;
                        HostClock = new ClockHost(h);
                        MakeJoinParts();
                        // this load's origin, when OnSaveLoaded had no clock facts yet to give it (no menu was seen)
                        if (time.LoadCount == 0) time.OnSaveLoaded();
                        days.Attach();
                        StartCommands();
                        // after ClockHost: it wires it
                        Join = new JoinHost(h, HostClock, content, time, print, days, pause, Commands);
                        if (plan.Test && Boot.Dev.PauseAtDay > 0) pause.Arm(Boot.Dev.PauseAtDay, "-semp-pause-at-day");
                    }
                    catch (Exception)
                    {
                        HumanAgencies.Stop();
                        Commands = null;
                        Humans = null;
                        Host = null;
                        HostClock = null;
                        Join = null;
                        try { h.Stop(); } catch (Exception) { }
                        throw;
                    }
                }
                else
                {
                    Client = new SessionClient();
                    Client.Plan = plan;
                    if (agency != null) Client.World.SetAgency(agency);
                    MakeJoinParts();
                    Wire(Client.World);
                    // the commands, before the clock that reads their gate
                    StartClientCommands(Client);
                    ClientClock = new ClockClient(Client);
                    CommandClientGlue glue = Commands.ClientGlue;
                    ClientClock.GateHolding = () => glue.Holding;
                    ClientClock.CatchUpSpeed = () => glue.CatchUp.WantedSpeed;
                    ClientClock.PausedLeft = () => glue.Client.PausedLandingLeft();
                    if (plan.Test) Feed = new JoinDevFeed();   // the dev feed is a test seam
                    if (plan.SteamHost != 0) Client.StartLobby(plan.SteamHost);   // a Steam lobby: no code
                    else Client.Start(plan.JoinCodeText);
                }
            }
            catch (Exception e)
            {
                // A clock that did not finish starting must not leave ClockSync's hooks pointing at it: the planner prefix would
                // swallow the game's own pause, and a client's ClampLocal would clamp every click (cloud review G-2).
                ClockSync.LocalChange = null; ClockSync.LocalPlanner = null; ClockSync.ClampLocal = null;
                ClockSync.PlannerGone = null;
                // The same for a client that did not finish starting: closed and dropped, as the host path does with its host, so
                // a half-built ClockClient does not go on holding the game paused every frame.
                SessionClient half = Client;
                if (half != null) { Commands = null; HumanAgencies.Stop(); }
                Client = null; ClientClock = null;
                if (half != null) { try { half.Stop(); } catch (Exception) { } }
                Log.Error("session: could not start: " + e);
            }
        }

        // SessionPlan.Check, with one test-only allowance: a dev joiner whose code could not be read (a missing join
        // file) still starts its client, which ends the session at once with M4's own "client: bad join code" line,
        // as before plans existed. The rest of such a plan is still checked in full.
        static string CheckPlan(SessionPlan plan)
        {
            if (!(plan.Test && plan.Role == SessionRole.Client && plan.JoinCodeText == null)) return plan.Check();
            plan.JoinCodeText = "-";
            try { return plan.Check(); }
            finally { plan.JoinCodeText = null; }
        }

        // A new join from the menus after this process's last client session ended (M8 Track U): the same client and
        // its wiring, a new attempt with the plan's code and the picked agency. null = connecting; else why not.
        public static string JoinAgain(SessionPlan plan, string agency)
        {
            if (Client == null) return "no client to join with";
            if (InSession) return "already in a session";
            string why = plan == null ? "no session plan" : plan.Check();
            if (why != null) return why;
            if (plan.Role != SessionRole.Client) return "not a join";
            if (plan.SteamHost != 0)
            {
                Plan = plan;
                if (agency != null) Client.World.SetAgency(agency);
                return Client.JoinLobbyAgain(plan.SteamHost, plan);
            }
            string error;
            JoinCode lan;
            SteamCode steam;
            if (!JoinRoute.Parse(plan.JoinCodeText, out lan, out steam, out error))
                return "bad join code (" + error + ")";
            Plan = plan;   // only a plan whose code parsed: the links read their options from it
            if (agency != null) Client.World.SetAgency(agency);
            return Client.JoinAgain(plan.JoinCodeText, plan);
        }

        // The host's commands: the human set starts as the host's own agency (N1: it then changes only in the seat
        // entries and survives every load), the rules are told who is human and how to raise their entries, and the
        // command root is made (JoinHost attaches its driver to the join). Never the rules with -semp-no-human-rules
        // (none of their hooks is patched then).
        static void StartCommands()
        {
            string own = AgencyWire.From(MonoBehaviourSingleton<Manager.GameManager>.Instance.Player.ID);
            if (!JoinCheck.IsAgencyId(own)) throw new InvalidOperationException("the host's agency has no wire id");
            Humans = new HumanSet(new[] { own });
            // the end of the game is the join's watch (JoinHost.GameOver); before the join exists nothing has ended
            Commands = new CommandSession(time, Humans, () => Join != null && Join.GameOver, Boot.Dev.ActionsFile,
                Boot.Dev.CommandFault);
            if (!Boot.Dev.NoHumanRules)
            {
                CommandSession commands = Commands;
                HumanAgencies.Start(Humans, commands, () => commands.Applying);
                HumanAgencies.SetKindProbe(() => commands.ApplyingKind, () => commands.ApplyingEntry);
                commands.RegisterFlights();
                // the world rules raise through the host's session; -semp-no-world-rules leaves them vanilla
                if (!Boot.Dev.NoWorldRules) commands.RegisterWorld(null);
                int waiting = FlightSources.NoteLoadPlansWaiting();
                if (waiting > 0) Log.Info("flight: " + waiting + " plans of the save's own load noted as the load's");
            }
            else Log.Info("rules: off (-semp-no-human-rules)");
        }

        // A client's commands (plan Task 10b): the host's stream from the link, in order, into its glue; its player id
        // from each Welcome; a lost link stops the stream until the next AgencyState; out of step takes the full-rejoin
        // path. Each snapshot load gives the glue the header's human agencies and starts the rules on them before the
        // extract (never with -semp-no-human-rules); the quick-rejoin offer carries the last entry applied.
        static Action<Message> glueReceived;
        static Action glueJoined, glueLeft;

        static void StartClientCommands(SessionClient c)
        {
            DevFlags f = Boot.Dev;
            Commands = CommandSession.ForClient(m => c.Send(m), why => c.OutOfStep(why), f.ActionsFile, f.CommandFault,
                f.NoDayGate, f.NoHumanRules);
            CommandClientGlue glue = Commands.ClientGlue;
            // a new join in the same process replaces the old session's glue on the link's events
            if (glueReceived != null) { c.Received -= glueReceived; c.Joined -= glueJoined; c.Left -= glueLeft; }
            glueReceived = glue.Received;
            glueJoined = () => glue.Joined(c.PlayerId);
            // M7 DD-W12: with the shared world on, a dropped link keeps the stream (Suspend) until the host's answer to the
            // rejoin: Resume replays what was missed; any other answer gives the stream up as M5 did (LinkLost)
            bool mayResume = !f.NoWorldRules && !f.NoResume;
            glueLeft = () => { if (mayResume) glue.LinkSuspended(); else glue.LinkLost(); };
            c.Received += glueReceived;
            c.Joined += glueJoined;
            c.Left += glueLeft;
            c.Flow.RejoinAnswered = r =>
            {
                if (mayResume && r.Code == RejoinCodes.Resume) glue.Resumed(r.ResumeNextSeq, r.ResumePausedTicks);
                else glue.LinkLost();
            };
            c.World.LoadStarting = glue.LoadStarting;
            c.World.LoadDone = glue.LoadDone;
            c.World.Menu = glue.AtMenu;
            c.Flow.LastResult = () => glue.Client.LastApplied;
            // M7: the shared world's client half (the world print, the year, heals, the resume's check)
            if (!f.NoWorldRules) glue.StartWorld(() => print == null ? null : print.Take(glue.Origin));
            // the ship-name counters (N7): this game's own name text for each content key
            glue.Counters.SetName = NameCounters.Set;
            glue.Counters.NameCount = (company, key) =>
            {
                string text = NameCounters.TextOf(key);
                return text == null ? -1 : NameCounters.Count(company, text);   // -1: never a host's count
            };
        }

        // DD15: a removal spawns at most two asteroids per kind the game renews, and an entry carries at most
        // MaxSpawns: a game whose mods add more kinds cannot be a session (checked once, here, so it never comes back
        // as a stream reset). The list is content, equal on every game by the content check.
        static bool AsteroidKindsOk()
        {
            int count;
            try
            {
                var objects = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance;
                var kinds = HarmonyLib.AccessTools.FieldRefAccess<Manager.ObjectInfoManager,
                    System.Collections.Generic.List<ScriptableObjectScripts.ObjectSubType>>(
                    "asteroidSubTypeNeedIfNotSpawn")(objects);
                if (kinds == null) throw new InvalidOperationException("no asteroid kinds list");
                count = kinds.Count;
            }
            catch (Exception e)
            {
                refused = "session: not started: the asteroid kinds cannot be read: " + e.GetType().Name;
                Log.Error(refused);
                return false;
            }
            string why = AsteroidSpawnState.KindsProblem(count);
            if (why != null)
            {
                refused = "session: not started: " + why;
                Log.Error(refused);
            }
            return why == null;
        }

        // The speeds this game runs at, read from its Economic, which only a loaded game has. The host reads them as
        // its session starts (its save is loaded); a joiner right after its snapshot loads (SnapshotLoad), once the
        // content check, which holds the two lists equal, has passed. A list the clock cannot use means no session.
        // Null when the list is set, else why not.
        static string SpeedListProblem()
        {
            float[] speeds;
            try { speeds = EconomicTimes.Speeds(ContentScan.Economic()); }
            catch (Exception e) { return "the game's speeds cannot be read: " + e.Message; }
            string why = ClockRules.SetListed(speeds);
            if (why == null)
                Log.Info("session: speed list " + string.Join(", ", Array.ConvertAll(speeds, s => s.ToString("0"))));
            return why;
        }

        static bool SpeedListOk()
        {
            string why = SpeedListProblem();
            if (why == null) return true;
            refused = "session: not started: " + why;
            Log.Error(refused);
            return false;
        }

        // KnownBuilds: no hosting and no joining on a game build this mod version was not verified on. In a test
        // profile the dev trigger file "fake-build" (in the profile's dev folder, read once here) stands in for the
        // build, so the refusal can be seen in the real game.
        static bool BuildSupported()
        {
            string fake = DevTriggers.Take("fake-build");   // null without a test profile
            string build = fake ?? GameBuildId;
            if (fake != null) Log.Warn("session: the build check uses the dev build " + TextSanitiser.OneLine(fake));
            string why = BuildSupport.Problem(GameBuildId, fake);
            if (why == null) return true;
            refused = why;
            Log.Error("session: not started: the game build " + TextSanitiser.OneLine(build) +
                      " is not one this mod version knows: " + why);
            return false;
        }

        public static void Update()
        {
            if (DevTriggers.Every(ref nextSoloPoll, Time.realtimeSinceStartup)) DevLoadSave();
            try
            {
                WatchFrameGap();
                if (Host != null || Client != null) perf.Frame();
                if (Host != null) Host.Update();
                if (Lobby != null) Lobby.Update();
                if (Join != null) Join.Update();
                if (Client != null)
                {
                    Client.Update();
                    // after the frame's messages: what came due applies now, even while the game is paused
                    if (Commands != null) Commands.Update();
                }
                if (Feed != null) Feed.Update(ClientBusy);
                if (HostClock != null) HostClock.Update();
                if (ClientClock != null) ClientClock.Update();
            }
            catch (Exception e)
            {
                errors++;
                if (errors <= 5 || errors % 1000 == 0) Log.Error("session: update failed (" + errors + "): " + e);
            }
            // its own try, last: a throw in the pause at a day never costs the session's own updates
            try { if (pause != null) pause.Update(); }
            catch (Exception e)
            {
                errors++;
                if (errors <= 5 || errors % 1000 == 0) Log.Error("session: the pause at a day failed (" + errors + "): "
                    + e);
            }
            // the early session last, in its own try: its view and dev triggers never cost the session's updates
            try { if (Early != null) Early.Update(); }
            catch (Exception e)
            {
                errors++;
                if (errors <= 5 || errors % 1000 == 0) Log.Error("session: the early session failed (" + errors + "): "
                    + e);
            }
        }

        // For the early track (M8 Track U): a session is on (InSession), and the game is at the main menu.
        internal static bool Live { get { return InSession; } }
        internal static bool AtMenu { get { return atMenu; } }
        // A session was started in this process (or tried) and not reset since (ResetMenuSession).
        internal static bool Started { get { return started; } }
        // Why the menus cannot host or join now (SessionLifecycle); null = they may.
        internal static string HostBlock() { return life.HostBlock(atMenu, Host != null, ClientBusy); }
        internal static string JoinBlock() { return life.JoinBlock(atMenu, Host != null, InSession, Client != null); }
        // This game is in a lobby at the menu, with no world of the session loaded (the leave-first question).
        internal static bool LobbyOnly
        {
            get { return Early != null && InviteDialogs.LobbyOnly(Early.Actions.View.Phase, atMenu); }
        }

        static bool atMenu;
        static bool logDates;   // after a solo "speed": the game date every 10 s, so a log shows time moving
        static float nextDateLog;
        static float nextSoloPoll;

        // A session is on: a host's, or a joiner's link (connecting, loading or connected, with its clock attached).
        static bool InSession { get { return Host != null || ClientClock != null || ClientBusy; } }

        // Dev trigger "load-save <name>": at the main menu, outside a session, the game's own load (the one Continue and
        // Load use; the splash needs no closing, the load replaces the menu scene) of a solo save. It checks that a
        // game after a session runs like vanilla.
        static void DevLoadSave()
        {
            DevSoloSpeed();
            string text = DevTriggers.Take("load-save");
            if (text == null) return;
            try
            {
                string why;
                string save = DevHostCommands.LoadSave(text, atMenu, InSession, out why);
                if (save == null) { Log.Warn("dev load-save: refused: " + why); return; }
                var saves = SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance;
                Log.Info("dev load-save: loading " + save + " through the game's own load");
                if (saves.Load(save)) atMenu = false;
                else Log.Error("dev load-save: the game refused to load " + save);
            }
            catch (Exception e) { Log.Error("dev load-save failed: " + e.Message); }
        }

        // Dev trigger "speed" in a game with no session (a solo game, or one after a session): the clock's own handlers
        // read it in a session, so without one nothing would. The same call as a click on the speed buttons.
        static void DevSoloSpeed()
        {
            LogSoloDate();
            if (InSession || atMenu) { logDates = false; return; }   // not taken: a session's clock reads it, and no game is loaded at the menu
            string text = DevTriggers.Take("speed");
            if (text == null) return;
            try
            {
                float v; string why;
                if (DevHostCommands.SoloSpeed(text, atMenu, InSession, Clock.MaxScale, out v, out why))
                {
                    Log.Info("dev speed: solo game, speed " + v);
                    ClockSync.PlayerSets(v);
                    logDates = true;
                }
                else Log.Warn("dev speed: refused: " + why);
            }
            catch (Exception e) { Log.Error("dev speed failed: " + e.Message); }
        }

        static void LogSoloDate()
        {
            float now = Time.realtimeSinceStartup;
            if (!logDates || now < nextDateLog) return;
            nextDateLog = now + 10f;
            try
            {
                var tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
                Log.Info("dev speed: game date " + tc.CurrentTime + ", speed " + tc.CurrentTimeScale + "; BlockEnableAI "
                         + Game.Company.BlockEnableAI + "; " + SoloProbe.Rivals());
            }
            catch (Exception e) { Log.Warn("dev speed: no game date: " + e.Message); }
        }

        // End of the frame (SempRoot.LateUpdate): the host sends its Clock with the speed the frame ended with (ClockHost.EndOfFrame).
        public static void LateUpdate()
        {
            try
            {
                if (HostClock != null) HostClock.EndOfFrame();
            }
            catch (Exception e)
            {
                errors++;
                if (errors <= 5 || errors % 1000 == 0) Log.Error("session: end of frame failed (" + errors + "): " + e);
            }
        }

        // A running session must never make the game stutter (the network lives on its own threads). A long gap between
        // two frames while a session is on is logged, so a stall caused by the link (e.g. a close lingering) shows up in
        // the log instead of being noticed by eye.
        static void WatchFrameGap()
        {
            if (Host == null && Client == null) return;   // no session: nothing to watch
            float now = Time.realtimeSinceStartup;
            if (lastUpdate >= 0f && now - lastUpdate > 0.5f)
                Log.Warn("session: a frame took " + (int)((now - lastUpdate) * 1000f) + " ms");
            lastUpdate = now;
        }

        public static void Stop()
        {
            try
            {
                if (Host != null) Host.Stop();
                if (Client != null) Client.Stop();
            }
            catch (Exception e) { Log.Error("session: stop failed: " + e.Message); }
            finally { ClockRules.ResetListed(); }
            HumanAgencies.Stop();   // the rules go back to the game's own with the session
        }

        public static string Status
        {
            get
            {
                if (refused != null) return refused;
                if (Host != null)
                {
                    string notice = Commands != null ? Commands.Status : null;
                    return Host.Status + (HostClock != null ? " | " + HostClock.Status : "") +
                           (Join != null ? " | " + Join.Status : "") + (notice != null ? " | " + notice : "");
                }
                if (Client != null)
                {
                    if (!Client.Label.Shows) return Feed != null ? Feed.Status : null;   // a solo game after it
                    string join = Feed != null && Feed.Status != null ? Feed.Status
                                : Client.Flow.Status.Length > 0 ? Client.Flow.Status : null;
                    string clock = ClientClock != null && ClientClock.Status != null ? " | " + ClientClock.Status : "";
                    string said = Commands != null ? Commands.Status : null;
                    return Client.Status + (join != null ? " | " + join : "") + clock
                        + (said != null ? " | " + said : "");
                }
                return null;
            }
        }

        // The lines under Status (SempRoot's label): a joiner's last refusal report or the host's goodbye; null
        // otherwise.
        public static string[] StatusReport
        {
            get
            {
                return refused == null && Host == null && Client != null && Client.Label.Shows ? Client.Flow.Report
                    : null;
            }
        }
    }
}
