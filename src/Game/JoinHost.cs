using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Data.ScriptableObject;
using ScriptableObjectScripts;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;

namespace SolarExpanseMultiplayer
{
    // The host's game as JoinService sees it (IJoinHostWorld) and its link to the players (IJoinLink), over SessionHost
    // and ClockHost (plan "Game glue"). It owns the JoinService and feeds ClockHost the in-game filter (I-12). Its own
    // parts: the joined agencies' AI off on the host once their players have loaded (AiOffOne, never BlockEnableAI),
    // again after every host load (Core's JoinedAgencies decides which); the end of the game, watched once per
    // GameManager instance; the content as it is at each snapshot (the header's PostLoad). Dev triggers (test
    // profiles): "pause-at-day <N>", "stall" (hold the snapshot chunks back), "go", "poke" (queue one production item
    // for the host's agency: a structural change while paused), "snapshot" (a snapshot and its self-check, logged only)
    // and "print" (a print, compared with the previous one), "kick <player> [<code>]" (SessionHost.Kick), "ban
    // <player>" (SessionHost.Ban), "carry-on" (ClockHost.CarryOn) and "reload [<save>]" (the game's own load of the
    // current save). Owned by Session; main thread.
    public sealed class JoinHost : IJoinHostWorld, IJoinLink
    {
        readonly SessionHost host;
        readonly ClockHost clock;
        readonly ContentScan content;
        readonly HostTime time;
        readonly StateFingerprint print;
        readonly DayEventLog days;
        readonly HostPauseDriver pause;
        readonly JoinService service;
        readonly CommandSession commands;
        readonly AgencyLedger ledger;
        readonly JoinedAgencies humans = new JoinedAgencies();   // the agencies players have loaded as (the session's)
        readonly List<KeyValuePair<int, Message>> held = new List<KeyValuePair<int, Message>>();
        bool stalled, endFired;
        GameManager watched;
        Action<GameManager.EGameEndCondition> endHandler;
        float nextDevPoll;
        Fingerprint lastDevPrint;
        DevSnapshotCheck devCheck;

        // lobby: the host's lobby (HostLobby) decides when a player's join begins, and calls Confirmed itself: a lobby
        // member waits at the menu until it is let in, which a link's own Confirmed would cut short.
        public JoinHost(SessionHost host, ClockHost clock, ContentScan content, HostTime time, StateFingerprint print,
            DayEventLog days, HostPauseDriver pause, CommandSession commands, bool lobby)
        {
            this.host = host; this.clock = clock; this.content = content; this.time = time; this.print = print;
            this.days = days;
            this.pause = pause;
            if (commands == null) throw new ArgumentNullException("commands");
            this.commands = commands;
            // the command driver's seats are the join's ledger (the one ownership check reads it), so the ledger is
            // made first; the stream then joins the join driver as its ICommandStream
            ledger = new AgencyLedger(JoinLimits.MaxPlayers);
            commands.Attach(ledger, player => service != null && service.InGame(player), this);
            service = new JoinService(this, this, ledger,
                                      new JoinBatcher(JoinLimits.NoProgressMs, JoinLimits.JoinerTotalMs),
                                      new ThreadBackground(e => Log.Error("join: a background check failed: "
                                          + e.GetType().Name)),
                                      () => NetClock.NowMs, commands.Stream);
            if (!lobby) host.Confirmed += service.Confirmed;
            host.Received += service.Received;
            host.CommandReceived += OnCommand;
            // M7 DD-W12: a replay pauses everyone with the player's name until it is released or fails
            commands.CatchUpPause = (player, on) => clock.SetCatchingUp(player, on);
            // the clock asks InGame BEFORE it hands a leave on: JoinService.Left ends the player's in-game state
            clock.UseJoinService(service.InGame, service.Left);
            OnSaveLoaded();
            Log.Info("join: the host takes joins (content " + (content.HostSummary != null ? "scanned"
                : "NOT scanned: joins are refused") + ")");
        }

        // The dev flags' host: every link's Confirmed starts its join at once.
        public JoinHost(SessionHost host, ClockHost clock, ContentScan content, HostTime time, StateFingerprint print,
            DayEventLog days, HostPauseDriver pause, CommandSession commands)
            : this(host, clock, content, time, print, days, pause, commands, false)
        {
        }

        public string Status { get { return service.Status; } }

        // The lobby's hand-over (HostLobby): a member let in, or a released member back on a new link.
        public void Confirmed(int player, string name, bool rejoin) { service.Confirmed(player, name, rejoin); }
        public bool InGame(int player) { return service.InGame(player); }
        public bool HoldsSeat(int player) { return service.HoldsSeat(player); }
        // The host's own kick of a player in the game: its seat is freed, its keys go (JoinService.Kick).
        public void KickPlayer(int player, int code) { service.Kick(player, code); }

        // The command driver runs before the join driver: requests a batch held are applied from the frame after the
        // batch ends, never inside its end (DD5, step 6).
        public void Update()
        {
            // a throw in the commands must not stop the join driver, which holds the pause and the batches
            try { commands.Update(); }
            catch (Exception e) { Log.Error("command: the driver's update threw: " + ExceptionText.Chain(e)); }
            service.Update();
            if (devCheck != null && devCheck.Poll()) devCheck = null;
            if (DevTriggers.Every(ref nextDevPoll, Time.realtimeSinceStartup)) DevPoll();
        }

        // Every host load after the session started (and once at its start): the end-of-game watch for this
        // GameManager, and the humans' agencies off again (the load's SetupPlayerAndRivalAIs may have switched them
        // on).
        public void OnSaveLoaded()
        {
            endFired = false;
            try { WatchEnd(); }
            catch (Exception e) { Log.Error("join: could not watch for the end of the game: " + e.Message); }
            AiOffForJoined("again after a host load");
            SeatTheHost();
        }

        // A host load inside the session (after OnSaveLoaded): the rules' per-load state goes, and the command stream
        // starts again, so every client rejoins fully (DD7).
        public void HostLoaded()
        {
            commands.OnHostLoaded();
            service.HostLoaded();
        }

        // The host's own seat in the ledger (player 0), so its own clicks pass the one ownership check before any
        // joiner has claimed (the join path sets the same list again at each claim and batch).
        void SeatTheHost()
        {
            try
            {
                if (!ledger.SetPlayable(PlayableAgencies(), HostAgency))
                    Log.Warn("command: a joiner holds the agency the host plays; the host's own clicks are refused");
            }
            catch (Exception e) { Log.Error("command: the host's seat could not be set: " + e.GetType().Name); }
        }

        // ActionRequest and ObjectRequest from a player. A request the protocol forbids closes its link and revokes
        // its keys; a well-formed request that is not allowed is refused with a code and the link stays.
        void OnCommand(int player, Message m, long arrivalMs)
        {
            var request = m as ActionRequest;
            if (request != null)
            {
                string violation = commands.FromPlayer(player, request, arrivalMs);
                if (violation != null) host.KickViolation(player, violation);
                return;
            }
            var wanted = m as ObjectRequest;
            if (wanted != null) { commands.ObjectWanted(player, wanted); return; }
            var heal = m as HealRequest;
            if (heal != null) { commands.HealWanted(player, heal); return; }
            var progress = m as ResumeProgress;
            if (progress != null) commands.ResumeProgressFrom(player, progress);
        }

        void WatchEnd()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (gm == null || ReferenceEquals(gm, watched)) return;   // one subscription per GameManager instance
            if (endHandler == null) endHandler = c => { endFired = true; Log.Info("join: the host's game ended (" + c
                + "): no more joins"); };
            gm.EndOfGame += endHandler;
            watched = gm;
        }

        // ---- IJoinHostWorld ----

        public ContentSummary OwnSummary { get { return content.HostSummary; } }
        public ItemList OwnItems(int category) { return content.MenuItems(category); }

        public IList<string> PlayableAgencies()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            var ids = new List<string>();
            var active = new List<bool>();
            var enabled = new List<bool>();
            var wg = new List<bool>();
            foreach (Company c in gm.Companies)
            {
                if (c == null) continue;
                ids.Add(c.ID);
                active.Add(c.gameObject.activeSelf);
                enabled.Add(c.enabled);
                wg.Add(c.Definition != null && c.Definition.IsWorldGovernment);
            }
            return HostStartConfig.Playable(ids, active, enabled, wg, gm.Player.ID);
        }

        public string HostAgency
        {
            get { return AgencyWire.From(MonoBehaviourSingleton<GameManager>.Instance.Player.ID); }
        }

        public StartConfig HostConfig
        {
            get
            {
                GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
                GameManager.StartGameConfiguration c = gm.StartGameConfig;
                var rivals = new List<string>();
                if (c.enabledRivalCorporations != null)
                    foreach (CompanyDefinition d in c.enabledRivalCorporations) if (d != null) rivals.Add(d.ID);
                return HostStartConfig.From(gm.Player.ID, rivals, (int)c.GameDifficulty,
                                            c.PlanetarySystemDescriptor != null ? c.PlanetarySystemDescriptor.ID : null,
                                            c.StartGameEpoch != null ? StartConfigMap.EpochIndex(c.StartGameEpoch.ID)
                                                : -1, c.setupAIs, c.gameplayID);
            }
        }

        public bool GameOver { get { return endFired || ClockSync.Locked; } }

        public SnapshotData TakeSnapshot() { return SnapshotSource.Take(time, PostLoad); }

        // The speed is 0 and nothing else is moving it (the "settled" test of a pause).
        public static bool GamePaused
        {
            get
            {
                return ClockSync.Ready && ClockSync.Scale == 0f && !ClockSync.CountDown && !ClockSync.GameSilentPause;
            }
        }

        public bool Paused { get { return GamePaused; } }
        public bool WorldStarted { get { return WorldStartSetUp.HasRun; } }

        public void SetJoining(int player, bool on)
        {
            clock.SetJoining(player, on);
            if (on) days.Arm();   // the first 10 day events after the batch, for join-compare.ps1
        }

        public void PlayerInGame(int player, string agency)
        {
            clock.PlayerInGame(player);
            if (agency == null) return;
            humans.Add(agency);
            AiOffForJoined("player " + player + " loaded");
        }

        public void Kick(int player, int code, bool removed) { host.Kick(player, code, removed); }
        public long GameTicks { get { return time.NowTicks; } }
        public int LoadCount { get { return time.LoadCount; } }
        public long NextDay { get { return time.NextDay; } }
        public long OriginTicks { get { return time.OriginTicks; } }
        public Fingerprint TakeFingerprint() { return print.Take(time.OriginTicks); }

        // ---- IJoinLink ----

        public bool Send(int player, Message m)
        {
            // dev "stall"
            if (stalled && m is SnapshotChunk) { held.Add(new KeyValuePair<int, Message>(player, m)); return true; }
            return host.Send(player, m);
        }

        public void Broadcast(Message m) { host.Broadcast(m); }

        // ---- the joined agencies' AI ----

        // The content as it is now, at every snapshot (paused, about 0.6 s): a mod may make definitions after the load.
        CategoryHash[] PostLoad() { return content.AfterLoad(); }

        void AiOffForJoined(string why)
        {
            if (humans.Count == 0) return;
            try
            {
                List<Company> all = MonoBehaviourSingleton<GameManager>.Instance.Companies;
                var ids = new List<string>();
                foreach (Company c in all) if (c != null && c.ID != null) ids.Add(c.ID);
                List<string> unknown;
                foreach (string id in humans.GameIds(ids, out unknown))
                    foreach (Company c in all) if (c != null && c.ID == id) AiOffFor(c, why);
                foreach (string w in unknown) Log.Warn("ai: " + w
                    + " is not an agency of the host's game; nothing switched off");
            }
            catch (Exception e) { Log.Error("ai: could not switch the joined agencies off: " + e.Message); }
        }

        void AiOffFor(Company c, string why)
        {
            try
            {
                WorldSwitches.AiOffOne(c);
                int run, paused, loading;
                Trees.Count(c, out run, out paused, out loading);
                Log.Info("ai: off for " + c.ID + " (" + why + "); trees running " + run + ", paused " + paused
                    + ", loading " +
                         loading + "; BlockEnableAI " + Company.BlockEnableAI);
            }
            catch (Exception e) { Log.Error("ai: could not switch off " + c.ID + ": " + e.Message); }
        }

        // ---- dev triggers (test profiles only: DevTriggers reads nothing without one) ----

        void DevPoll()
        {
            string day = DevTriggers.Take("pause-at-day");
            if (day != null)
            {
                int d = PauseAtDay.ParseDay(day);
                if (d > 0) pause.Arm(d, "dev trigger");
                else Log.Warn("join: dev pause-at-day: not a day 1..800");
            }
            if (DevTriggers.Take("stall") != null)
            {
                stalled = true;
                Log.Info("join: dev stall: snapshot chunks are held back until \"go\"");
            }
            if (DevTriggers.Take("go") != null)
            {
                stalled = false;
                int sent = 0;
                foreach (KeyValuePair<int, Message> kv in held) if (host.Send(kv.Key, kv.Value)) sent++;
                Log.Info("join: dev go: " + sent + " of " + held.Count + " held chunks sent");
                held.Clear();
            }
            if (DevTriggers.Take("poke") != null) Poke();
            if (DevTriggers.Take("snapshot") != null) DevSnapshot();
            if (DevTriggers.Take("print") != null) DevPrint();
            string kick = DevTriggers.Take("kick");
            int player, code;
            if (kick != null)
            {
                if (DevHostCommands.Kick(kick, out player, out code))
                {
                    Log.Info("join: dev kick: player " + player + " with code " + code);
                    service.Kick(player, code);   // the real kick: nothing happens for a player not connected
                }
                else Log.Warn("join: dev kick: not \"<player 1-4> [<code 1-99>]\"");
            }
            string ban = DevTriggers.Take("ban");
            if (ban != null)
            {
                if (DevHostCommands.Kick(ban, out player, out code))
                {
                    Log.Info("join: dev ban: player " + player);
                    host.Ban(player);   // kicked, and a Steam player's account refused at the door from now on
                }
                else Log.Warn("join: dev ban: not \"<player 1-4>\"");
            }
            if (DevTriggers.Take("carry-on") != null)
            {
                Log.Info("join: dev carry-on: the host carries on without the dropped player");
                clock.CarryOn();
            }
            string reload = DevTriggers.Take("reload");
            if (reload != null) DevReload(reload);
        }

        // The game's own load of the current save (or a named one) while the session runs: what a host's reload does
        // to the session (its joiners, its clock, the label) can be tested without clicking in the game.
        static void DevReload(string text)
        {
            try
            {
                var saves = SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance;
                string why;
                string save = DevHostCommands.ReloadSave(text, saves.LastSaveName, out why);
                if (save == null) { Log.Warn("join: dev reload: " + why); return; }
                Log.Info("join: dev reload: loading " + save + " through the game's own load");
                if (!saves.Load(save)) Log.Error("join: dev reload: the game refused to load " + save);
            }
            catch (Exception e) { Log.Error("join: dev reload failed: " + e.Message); }
        }

        // One production item queued for the host's agency at its first site with facilities: a structural change in
        // the production area, the S12 and Task 13 control for a quick rejoin that must not be quick.
        static void Poke()
        {
            try
            {
                Company player = MonoBehaviourSingleton<GameManager>.Instance.Player;
                FacilityBaseDescriptor desc = null;
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                foreach (MyIDScriptableObject o in all.AllMyIDScriptableObjects)
                {
                    var f = o as FacilityBaseDescriptor;
                    if (f != null && !f.ShowOnOrbit3dView && !f.SpaceStationBuilding) { desc = f; break; }
                }
                if (desc == null) { Log.Error("join: dev poke: no facility descriptor"); return; }
                foreach (ObjectInfo oi in MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos)
                {
                    if (oi == null) continue;
                    ObjectInfoData d = oi.GetObjectInfoData(player);
                    if (d == null || d.ListFacility == null || d.ListFacility.Count == 0) continue;
                    int before = d.ListProductionQuery.Count;
                    d.AddProductItemToQuery(new MyIDScriptableObjectProductionItemCountToBuild
                    {
                        MyIDScriptableObjectProductionItem = desc, countToBuild = 1,
                    });
                    Log.Info("join: dev poke: queued 1 x " + desc.ID + " for " + player.ID + " at body " + oi.id
                        + " (production queue " + before +
                             " -> " + d.ListProductionQuery.Count + ")");
                    return;
                }
                Log.Error("join: dev poke: no site with facilities for " + player.ID);
            }
            catch (Exception e) { Log.Error("join: dev poke failed: " + e.Message); }
        }

        void DevSnapshot()
        {
            if (devCheck != null) { Log.Warn("join: dev snapshot: one is being checked already"); return; }
            int gc0 = GC.CollectionCount(0);
            SnapshotData s = SnapshotSource.Take(time, PostLoad);
            if (s == null) return;
            devCheck = new DevSnapshotCheck(s, gc0);
        }

        void DevPrint()
        {
            Fingerprint p = print.Take(time.OriginTicks);
            if (p == null) return;
            if (lastDevPrint == null) Log.Info("join: dev print taken (the first: nothing to compare with)");
            else
            {
                string first;
                bool same = FingerprintRules.Same(lastDevPrint, p, out first);
                Log.Info("join: dev print: " + (same ? "the same as the previous print at every level"
                    : "DIFFERS from the previous print: " + first));
            }
            lastDevPrint = p;
        }
    }

    // The dev "snapshot" trigger's check: the host's own walls on a snapshot (its hash, the bounded gunzip against the
    // declared size, the scan), on a worker thread as JoinService runs them, the result logged. Nothing is sent or
    // written.
    sealed class DevSnapshotCheck
    {
        readonly SnapshotData snap;
        readonly int gc0;
        readonly long started = NetClock.NowMs;
        volatile bool done;
        string sha, error, refused;

        public DevSnapshotCheck(SnapshotData s, int gc0)
        {
            snap = s;
            this.gc0 = gc0;
            new Thread(Run) { IsBackground = true, Name = "SEMP dev snapshot check" }.Start();
        }

        void Run()
        {
            try
            {
                sha = Sha256Hex.Of(snap.Compressed);
                int code;
                string why;
                byte[] json = BoundedGunzip.Decompress(snap.Compressed, snap.Uncompressed, out code, out why);
                if (json == null) error = "gunzip (" + code + "): " + why;
                else
                {
                    ScanResult r = OdinScan.Scan(json, ScanLimits.FromCorpus());
                    if (!r.Ok) { error = "scan: " + r.Error; refused = r.RefusedType; }
                }
            }
            catch (Exception e) { error = "the check threw " + e.GetType().Name; }
            finally { done = true; }
        }

        // True once the result is logged.
        public bool Poll()
        {
            if (!done) return false;
            Log.Info("join: dev snapshot " + snap.Compressed.Length + " bytes (" + snap.Uncompressed + "), sha256 "
                + (sha ?? "-").Substring(0, Math.Min(16, (sha ?? "-").Length)) +
                     ", self-check " + (error == null ? "ok" : "REFUSED " + TextSanitiser.OneLine(refused ?? error))
                         + ", " + (NetClock.NowMs - started) +
                     " ms, gc +" + (GC.CollectionCount(0) - gc0));
            return true;
        }
    }
}
