using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The joiner's game as JoinerFlow sees it. Every member catches its own exceptions and reports them (a log line,
    // and where the flow has a way to hear it, a failure): a throw into the flow would leave it waiting for its 300 s
    // deadline. StartLoad is the one exception: the flow turns its throw into CancelLoad and LoadThrew. Main thread
    // only. It owns this joiner's SnapshotLoad and its JoinSessionState (in session or not).
    //
    // The content summary, the content after the load and the fingerprint come from the scan code shared with the host
    // (ContentScan, StateFingerprint) through the seams below, which Session wires on this instance. Unwired, a network
    // join refuses before anything is sent (ContentUnavailable), and a rejoin is always a full one (no print).
    public sealed class JoinClient : IJoinerWorld
    {
        public Func<ContentSummary> MenuSummary;                 // the summary at the menu
        // why MenuSummary gives none: SummaryTooBig or ContentUnavailable
        public Func<int> MenuProblem;
        public Func<int, ItemList> MenuItems;                    // one category's item list, for ContentDetail
        public Func<long, Fingerprint> Print;                    // one print keyed from the host's origin

        // the categories after the load (SnapshotLoad compares them with the header's)
        public Func<CategoryHash[]> PostLoadScan
        {
            get { return load.PostLoadScan; }
            set { load.PostLoadScan = value; }
        }

        public Func<string> SpeedListCheck
        {
            get { return load.SpeedListCheck; }
            set { load.SpeedListCheck = value; }
        }

        readonly bool dev;                  // the dev feed's world: a stub summary, the post-load content not checked
        readonly SnapshotLoad load;
        readonly JoinSessionState session;
        string agency;
        bool declined, summaryWarned;
        long origin;
        int menuRetries;
        float menuRetryAt = -1f;
        bool menuPending;                   // a LoadScene("Menu") is under way (GoToMenu): another would load it twice

        // Set by the session's owner: the join attempt is over for good, take the session down
        // (Session.EndClientSession).
        public Action SessionEnded;

        public JoinerFlow Flow;             // set by the owner right after construction

        // The commands' glue (Session wires these): a snapshot load is starting with this header (before the
        // extract), the loaded world is running, the game is back at the menu.
        public Action<JoinHeader> LoadStarting;
        public Action LoadDone, Menu;

        public JoinClient(bool devFeed, string devAgency)
        {
            dev = devFeed;
            load = new SnapshotLoad(devFeed);
            DevFlags f = Boot.Dev;
            agency = devAgency ?? (f != null ? f.Agency : null);
            session = new JoinSessionState();   // per process: a restart (-semp-rejoin) starts outside the session
        }

        public bool Loading { get { return load.Loading; } }

        public PrewarmSkip PrewarmSkip { get { return load.PrewarmSkip; } }

        // ---- content ----

        public ContentSummary OwnSummary
        {
            get
            {
                if (dev) return DevSummary();
                Func<ContentSummary> s = MenuSummary;
                if (s == null)
                {
                    if (!summaryWarned) Log.Error("join: no content scan in this build: no summary, nothing sent");
                    summaryWarned = true;
                    return null;
                }
                try { return s(); }
                catch (Exception e) { Log.Error("join: the content summary failed: " + e.Message); return null; }
            }
        }

        public int SummaryProblem
        {
            get
            {
                Func<int> p = MenuProblem;
                if (dev || p == null) return JoinCodes.ContentUnavailable;
                try { return p(); }
                catch (Exception) { return JoinCodes.ContentUnavailable; }
            }
        }

        public ItemList OwnItems(int category)
        {
            Func<int, ItemList> f = MenuItems;
            if (f == null || dev) return new ItemList { Ids = new string[0], Hashes = new string[0], Complete = true };
            try { return f(category); }
            catch (Exception e)
            {
                Log.Error("join: the item list failed: " + e.Message);
                return new ItemList { Ids = new string[0], Hashes = new string[0], Complete = false };
            }
        }

        static ContentSummary DevSummary()
        {
            string zeros = new string('0', 64);
            return new ContentSummary
            {
                GameBuild = Session.GameBuildId, GameVersion = ModInfo.ClipGameVersion(Application.version),
                ModVersion = ModInfo.Version, Loader = Boot.Mode == "BepInEx" ? "bepinex" : "doorstop",
                ManagedHash = zeros, ManagedCount = 0, Plugins = new PluginEntry[0], Assemblies = new PluginEntry[0],
                Categories = new CategoryHash[0], Overall = zeros,
            };
        }

        public string Agency { get { return agency; } }
        // the next id to claim: the agency picker's (M8 Track U), or the dev trigger "agency <id>"
        public void SetAgency(string id) { agency = id; }

        // The seats of the last join header shown (every human agency and its player's name, the host's included),
        // for the session view; null before one. Validated and cleaned with the header.
        public AgencySeat[] Seats { get; private set; }

        // ---- the menu ----

        public bool AtMenu { get { try { return load.AtMenu; } catch (Exception) { return false; } } }

        // A full rejoin from a running game: back to the menu keeping the link; Session.OnMenuReady then calls
        // Flow.MenuReady. A failure is logged and retried once a second, up to 5 times; after that the flow's own
        // deadline ends the attempt.
        public void GoToMenu()
        {
            session.LeftToMenu();
            ToldMenu();
            menuRetries = 0;
            TryMenu();
        }

        void TryMenu()
        {
            try
            {
                if (load.Loading) load.CancelLoad();
                Log.Info("join: to the menu first");
                SceneManager.LoadScene("Menu");
                menuPending = true;
                menuRetryAt = -1f;
            }
            catch (Exception e)
            {
                Log.Error("join: could not go to the menu: " + e.Message);
                menuRetryAt = ++menuRetries <= 5 ? Time.realtimeSinceStartup + 1f : -1f;
            }
        }

        // ---- walls 5-7 ----

        // Every reference the scan collected must resolve through the game's own resolver at the menu: an id this game
        // does not have would otherwise come out of the deserialise as a silently empty slot.
        public bool RefsResolve(IList<string> refs, out string missing)
        {
            missing = null;
            if (refs == null) return true;
            try
            {
                var resolver = new Manager.ScriptableObjectStringReferenceResolver();
                foreach (string r in refs)
                {
                    object o;
                    bool ok;
                    try { ok = r != null && r.IndexOf('/') > 0 && resolver.TryResolveReference(r, out o) && o != null; }
                    catch (Exception) { ok = false; }
                    if (!ok) { missing = TextSanitiser.Clean(r, 160, "?"); return false; }
                }
                return true;
            }
            catch (Exception e) { missing = "the resolver failed (" + e.GetType().Name + ")"; return false; }
        }

        public int Deserialise(byte[] json, out object saveData, out string error)
        {
            try { return load.Deserialise(json, out saveData, out error); }
            catch (Exception e)
            {
                saveData = null;
                error = "the deserialise threw " + e.GetType().Name;
                return JoinCodes.StrictDeserialise;
            }
        }

        public int Sanity(object saveData, JoinHeader h, out string error)
        {
            try { return load.Sanity(saveData, h, agency, out error); }
            catch (Exception e) { error = "the sanity check threw " + e.GetType().Name; return JoinCodes.SanityFailed; }
        }

        public void ShowSummary(JoinHeader h)
        {
            try
            {
                declined = false;
                Seats = h.Seats;
                string host = null;
                if (h.Seats != null) foreach (AgencySeat s in h.Seats) if (s != null && s.Player == 0) host = s.Name;
                string company = h.Config != null ? h.Config.Company : null;
                string text = JoinText.Summary(TextSanitiser.Clean(host, 24, "the host"), h.DateTicks, company);
                Log.Info("join: summary: " + text);
            }
            catch (Exception e) { Log.Warn("join: the summary could not be shown: " + e.Message); }
        }

        public bool Declined { get { return declined; } }
        public void Decline() { declined = true; }

        // ---- the load ----

        public void StartLoad(object saveData, JoinHeader h)
        {
            origin = h.OriginTicks;
            JoinerFlow flow = Flow;
            Action<JoinHeader> starting = LoadStarting;
            if (starting != null) starting(h);
            load.StartLoad(saveData, h, t =>
            {
                session.Joined();
                Action done = LoadDone;
                if (done != null) done();
                if (flow != null) flow.Loaded(t);
            },
                           (code, why) => { if (flow != null) flow.LoadFailed(code, why); });
        }

        public void CancelLoad()
        {
            try { load.CancelLoad(); }
            catch (Exception e) { Log.Error("join: cancelling the load failed: " + e.Message); }
        }

        public void BackToMenu(int code, string reason)
        {
            session.LeftToMenu();
            ToldMenu();
            menuRetryAt = -1f;   // an ending beats a menu step still waiting to be retried
            try { load.BackToMenu(code, reason, menuPending); }
            catch (Exception e) { Log.Error("join: going back to the menu failed: " + e.Message); }
        }

        // The flow ended the join for good: what the join changed is put back, then the session comes down.
        public void SessionOver()
        {
            try { load.SessionOver(); }
            catch (Exception e) { Log.Error("join: putting the game's switches back failed: " + e.Message); }
            Action ended = SessionEnded;
            if (ended != null) ended();
        }

        // The save-loaded event (Session, before Drift): this joiner's apply step, and the session's load count.
        public void OnSaveLoaded(bool success)
        {
            session.SaveLoaded();
            try { load.OnSaveLoaded(success); }
            catch (Exception e) { Log.Error("join: the apply step failed: " + e); }
        }

        // Every "main menu ready".
        public void AtMenuNow()
        {
            menuPending = false;
            session.LeftToMenu();
            ToldMenu();
            try { load.AtMenuNow(); }
            catch (Exception e) { Log.Error("join: the menu step failed: " + e.Message); }
        }

        void ToldMenu()
        {
            Action menu = Menu;
            if (menu == null) return;
            try { menu(); }
            catch (Exception e) { Log.Error("join: the commands could not leave the world: " + e.GetType().Name); }
        }

        // ---- the session ----

        public bool InSession { get { return session.InSession; } }

        // The commands found this world out of step with the host's: it is no longer in session, so the reconnect that
        // follows loads a fresh snapshot (a full rejoin) instead of offering this world back.
        public void LeaveSession() { session.LeftToMenu(); }

        public bool Paused
        {
            get
            {
                try
                {
                    return ClockSync.Ready && ClockSync.Scale == 0f && !ClockSync.CountDown
                        && !ClockSync.GameSilentPause &&
                           !load.Loading;
                }
                catch (Exception) { return false; }
            }
        }

        public long GameTicks
        {
            get
            {
                try
                {
                    var tc = MonoBehaviourSingleton<TimeController>.Instance;
                    return tc != null ? tc.CurrentTime.Ticks : 0L;
                }
                catch (Exception) { return 0L; }
            }
        }

        StateField<TimeController, DateTime> nextDayRef;
        public long NextDay
        {
            get
            {
                try
                {
                    if (nextDayRef == null) nextDayRef = new StateField<TimeController, DateTime>("nextDay");
                    var tc = MonoBehaviourSingleton<TimeController>.Instance;
                    return tc != null ? nextDayRef.Get(tc).Ticks : 0L;
                }
                catch (Exception) { return 0L; }
            }
        }

        public long OriginTicks { get { return origin; } }

        public Fingerprint TakeFingerprint()
        {
            Func<long, Fingerprint> p = Print;
            if (p == null)
            {
                Log.Info("join: no fingerprint in this build: the rejoin will be a full one");
                return null;
            }
            try { return p(origin); }
            catch (Exception e) { Log.Warn("join: the fingerprint failed: " + e.Message); return null; }
        }

        // Quick rejoin: nothing reloads. The ClockClient follows the host again on its own (its first request after a
        // Joined is a restore), and the agency, AI and autosave switches from the load stay.
        public void ResumeInSession() { Log.Info("join: resumed in session as " + agency + " (no reload)"); }

        // Once a frame from the owner: the menu retry.
        public void Update()
        {
            if (menuRetryAt >= 0f && Time.realtimeSinceStartup >= menuRetryAt) TryMenu();
        }
    }

    // ------------------------------------------------------------------ the dev feed (test profiles, joiners only; no
    // network)

    // A copy of a snapshot fed to the joiner's own walls with no network, so the walls and the load can be checked in
    // the real game against planted snapshots. The dev trigger "feed <name> [binder]" in a joiner's test profile reads
    // <data>\feed\<name>.json.gz and <name>.header.txt (key=value lines, DevFeedHeader):
    //   - without "binder": an in-process host answers the summary and the claim and hands the header and every chunk
    //     to the REAL SnapshotReceiver; the real JoinerFlow then runs every wall, the summary and the load;
    //   - "binder": the bounded gunzip, then the strict deserialise at the menu WITHOUT the scan, so the binder and the
    //     strict policy are seen to hold on their own; nothing loads.
    // The dev trigger "feed-abort" drops the feed's link while it loads (the flow then cancels the load). Owned by
    // Session; each feed gets its own JoinClient and JoinerFlow, dropped when the flow reports Loaded or Failed.
    public sealed class JoinDevFeed
    {
        static readonly Regex Name = new Regex(@"^[a-z0-9_-]{1,32}\z");
        JoinerFlow flow;
        JoinClient world;
        JoinClient lastWorld;   // the ended feed's world, until the next main menu ready
        SnapshotReceiver sink;
        JoinHeader header;
        byte[] gz;
        bool answer, over;
        float nextPoll;

        public bool Active { get { return flow != null; } }
        public string Status { get { return flow != null ? "dev feed: " + flow.Status : null; } }

        // Once a frame on a joiner. The trigger files are looked at once a second.
        public void Update(bool networkBusy)
        {
            if (flow != null)
            {
                try
                {
                    if (answer) Answer();
                    if (flow != null) { flow.Update(); world.Update(); }
                    if (over) End();
                }
                catch (Exception e) { Log.Error("dev feed: update failed: " + e); End(); }
            }
            if (!DevTriggers.Every(ref nextPoll, Time.realtimeSinceStartup)) return;
            if (flow != null && DevTriggers.Take("feed-abort") != null)
            {
                flow.Lost("dev feed abort");   // while loading: the flow cancels the load and goes back to the menu
                over = true;
            }
            string text = DevTriggers.Take("feed");
            if (text == null) return;
            if (flow != null) { Log.Error("dev feed: one is running already"); return; }
            if (networkBusy) { Log.Error("dev feed: a network join is in progress"); return; }
            try { Start(text); }
            catch (Exception e) { Log.Error("dev feed: could not start: " + e.Message); End(); }
        }

        // The menu step goes to the feed's world even after the feed has ended: a load it abandoned clears the game's
        // schedule flag only now (SnapshotLoad.AtMenuNow).
        public void OnMenuReady()
        {
            JoinClient w = world ?? lastWorld;
            lastWorld = null;
            if (w != null) w.AtMenuNow();
            if (flow != null) flow.MenuReady();
        }

        public void OnSaveLoaded(bool success) { if (world != null) world.OnSaveLoaded(success); }

        public bool Loading { get { return world != null && world.Loading; } }

        public PrewarmSkip PrewarmSkip { get { return world != null ? world.PrewarmSkip : null; } }

        void Start(string text)
        {
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool binderOnly = parts.Length == 2 && parts[1] == "binder";
            if (parts.Length < 1 || parts.Length > 2 || !Name.IsMatch(parts[0]) || (parts.Length == 2 && !binderOnly))
            {
                Log.Error("dev feed: use 'feed <name> [binder]'");
                return;
            }
            if (Boot.DataDir == null) { Log.Error("dev feed: no data folder"); return; }
            string dir = Path.Combine(Boot.DataDir, "feed");
            var info = new FileInfo(Path.Combine(dir, parts[0] + ".json.gz"));
            if (!info.Exists || info.Length < 1 || info.Length > JoinLimits.MaxCompressed)
            {
                Log.Error("dev feed: " + parts[0] + ".json.gz is missing or over the cap");
                return;
            }
            byte[] bytes = File.ReadAllBytes(info.FullName);
            Dictionary<string, string> kv = ReadKeyValues(Path.Combine(dir, parts[0] + ".header.txt"));
            if (kv == null) return;
            long declared = bytes.Length >= 4 ? (long)BitConverter.ToUInt32(bytes, bytes.Length - 4) : 0;
            string u;
            if (kv.TryGetValue("uncompressed", out u)) long.TryParse(u, out declared);
            Log.Info("dev feed: " + parts[0] + " (" + bytes.Length + " bytes, " + declared + " declared)" +
                     (binderOnly ? ", binder only (no scan)" : ""));
            if (binderOnly) { BinderOnly(bytes, declared); return; }

            string epochId, error;
            int epoch = kv.TryGetValue("epochId", out epochId) ? StartConfigMap.EpochIndex(epochId) : -1;
            JoinHeader h = DevFeedHeader.Parse(kv, bytes.Length, declared, Sha256Hex.Of(bytes), Application.version,
                                               epoch, "Player " + Boot.Profile, out error);
            if (h == null) { Log.Error("dev feed: " + error); return; }
            world = new JoinClient(true, h.Config.Company);
            flow = new JoinerFlow(world, new ThreadBackground(), () => NetClock.NowMs);
            world.Flow = flow;
            header = h;
            gz = bytes;
            answer = false;
            over = false;
            flow.Welcomed(h.YourPlayer, HostReceives, Attach, false);
        }

        // What a link's Bulk setter does: the receiver gets an ack sender (here, the in-process host takes every ack).
        void Attach(IBulkSink s)
        {
            sink = (SnapshotReceiver)s;
            sink.SendAck = m => true;
        }

        void BinderOnly(byte[] bytes, long declared)
        {
            int code;
            string error;
            byte[] json = BoundedGunzip.Decompress(bytes, declared, out code, out error);
            if (json == null) { Log.Info("dev feed: binder: the gunzip refused (" + code + "): " + error); return; }
            var probe = new SnapshotLoad(true);
            object data;
            code = probe.Deserialise(json, out data, out error);
            Log.Info("dev feed: binder: " + (code == 0 ? "deserialised (code 0)" : "refused (" + code + "): " + error));
        }

        // The in-process host hears what the joiner sends.
        bool HostReceives(Message m)
        {
            var stage = m as JoinStage;
            if (stage != null)
            {
                if (stage.Stage == JoinStageKind.Failed || stage.Stage == JoinStageKind.Loaded)
                {
                    Log.Info("dev feed: the host heard stage " + stage.Stage + " code " + stage.Code);
                    over = true;
                }
                return true;
            }
            var claim = m as AgencyClaim;
            if (claim != null) { answer = true; Log.Info("dev feed: the host heard the claim for " + claim.Agency); }
            else Log.Info("dev feed: the host heard " + m.GetType().Name);
            return true;
        }

        // The frame after the claim, as a network host would: ClaimResult(Ok) to the sink (arming it) and to the flow,
        // then the header and every chunk.
        void Answer()
        {
            answer = false;
            var ok = new ClaimResult { Ok = true, Agency = header.Config.Company };
            sink.OnControl(ok);
            flow.Received(ok);
            if (!sink.OnHeader(header))
            {
                Log.Error("dev feed: the receiver refused the header (" + sink.ErrorCode + "): " + sink.Error);
                End();
                return;
            }
            flow.Received(header);
            int n = ChunkMath.Count(gz.Length);
            for (int i = 0; i < n; i++)
                if (!sink.OnChunk(i, gz, i * JoinLimits.ChunkSize, ChunkMath.Length(gz.Length, i)))
                {
                    Log.Error("dev feed: the receiver refused chunk " + i + " (" + sink.ErrorCode + "): " + sink.Error);
                    End();
                    return;
                }
            flow.Progress();
            gz = null;
        }

        void End()
        {
            if (world != null) lastWorld = world;
            flow = null; world = null; sink = null; header = null; gz = null;
            answer = false; over = false;
        }

        static Dictionary<string, string> ReadKeyValues(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8192)
            {
                Log.Error("dev feed: the header file is missing or over 8 KiB");
                return null;
            }
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                string l = line.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                int eq = l.IndexOf('=');
                if (eq > 0) kv[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
            }
            return kv;
        }
    }
}
