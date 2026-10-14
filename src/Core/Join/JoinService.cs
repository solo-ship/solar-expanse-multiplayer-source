using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The batch's snapshot as the game glue takes it (Task 10's SnapshotSource): one compressed array for every joiner of the batch.
    // Header holds what only the game knows: the date, the origin, the six day boundaries, the save version as the game reports it
    // (raw: JoinService clips and cleans it) and PostLoad. JoinService fills the rest per joiner (YourPlayer, Seats, Config) and from
    // the bytes themselves (the sizes, the chunk count, the hash), so a header never claims what the bytes are not.
    public sealed class SnapshotData { public byte[] Compressed; public long Uncompressed; public JoinHeader Header; }

    // The host's game as JoinService sees it (src/Game/JoinHost.cs in the game; fakes in the tests and the fake client). Main thread.
    public interface IJoinHostWorld
    {
        ContentSummary OwnSummary { get; }       // the host's content at its own load (never sent: I-7)
        ItemList OwnItems(int category);
        IList<string> PlayableAgencies();        // active, enabled, not the World Government
        string HostAgency { get; }
        StartConfig HostConfig { get; }          // Company = the host's own agency
        bool GameOver { get; }
        SnapshotData TakeSnapshot();             // main thread, paused; null = failed (logged)
        bool Paused { get; }                     // the game's speed is 0 and no countdown or silent pause (the "settled" test)
        // the game's own start set-up of this world has run: a new game re-rolls its deposits and explored rows a frame
        // after its load, so a snapshot before that is of a world the host never plays on
        bool WorldStarted { get; }
        void SetJoining(int player, bool on);
        void PlayerInGame(int player, string agency);   // clock: fresh; that agency's AI off on the host (orchestrator's ruling)
        // close the player's link; removed = the host's own decision, not a join's end (it picks the goodbye's words);
        // HostKick.RevokesKeys says when its keys go too (HostGate.Revoke)
        void Kick(int player, int code, bool removed);
        long GameTicks { get; }                  // TimeController.CurrentTime ticks; read every Update while a drop is recorded
        int LoadCount { get; }                   // host save loads so far (HostReloaded)
        long NextDay { get; }                    // the host's nextDay ticks
        long OriginTicks { get; }
        Fingerprint TakeFingerprint();           // one StateReader.Visit into an AreaHasher; main thread, paused; null = failed
    }

    // Send never throws; false = not sent (over its cap, or the link is gone). JoinService looks at every answer that matters.
    public interface IJoinLink { bool Send(int player, Message m); void Broadcast(Message m); }

    // The host's side of the join protocol (plan "The drivers"; Design decisions 3 and 7-10, 16; spec sections 1, 4 and 6). Pure and
    // single-threaded: every method runs on the main thread. The one piece of work that runs elsewhere, the host's check of its own
    // snapshot (hash, gunzip, scan), goes through IBackground and writes only its own result object, which Update polls.
    //
    // A player goes: Confirmed -> content summary (10 s) -> same, or a detail request and a refusal with the report -> a claim (the
    // joiner sends it straight after its summary, so it is held until the content is judged; 30 s from "same", five tries; a seat
    // the ledger keeps is answered Ok only for its own agency; never a ClaimResult unprompted) -> queued -> a batch: the joining pause, ONE snapshot for everyone in it, a header and chunks within the
    // window per joiner, its stages -> Loaded at the header's date -> in game. A batch ends when every member has loaded or left; only
    // then does the pause lift. A member that fails, drops or misses a deadline leaves with Notice(CouldNotJoin) and never holds the
    // pause. An in-game player that drops gets a drop record; on its rejoin RejoinRules decides quick (no reload, no batch) or full
    // (back to the menu, then the join path from the summary, its agency kept).
    //
    // Who is to blame decides what counts. A failure inside a batch that is the joiner's (a failed check, a drop, a deadline, a
    // violation, a wrong date) counts towards JoinLimits.MaxFailedJoins (so a joiner that expires and re-queues
    // cannot hold every batch's pause for ever); the third is TooManyFailures, which kicks and revokes the keys, and the player is
    // refused at once from then on. Failures before any batch hold no pause and do not count. A fault of the host's own (no or a bad
    // snapshot, its own check refusing it, no start configuration, a header its own rules refuse, a background that would not take
    // the work) is HostSnapshotFailed and never counts against the joiner.
    //
    // Update allocates nothing when no join or drop is in progress (plan Global Constraints, Allocation).
    public sealed partial class JoinService
    {
        enum Phase { Summary, Detail, Claim, Queued, Batch, InGame, Dropped }
        enum Prep { Idle, WaitPause, WaitReady, Checking, Sending }

        // After a full rejoin's answer the joiner goes back to the menu first (Design decision 10), which takes longer than a first
        // join's 10 s from the Welcome: its summary gets the no-progress allowance instead.
        const int AfterFullRejoinMs = JoinLimits.NoProgressMs;
        const int MinReportBudget = 64;   // a refusal report is halved down to this before only its code is sent
        const string IdleStatus = "join: idle";
        static readonly int[] None = new int[0];
        static readonly string[] NoHumans = new string[0];   // outside a command session no agency is played but the host's

        sealed class Joiner
        {
            public int Id;
            public Phase Phase;
            public long PhaseMs;            // when the current phase began (the summary, detail and claim deadlines)
            public long ConfirmedMs;        // the current link's Confirmed: a rejoin's drop is timed to it
            public bool MayOffer;           // a rejoin link: a RejoinOffer may come first instead of the summary
            public bool AfterFull;          // the summary comes from the menu after a full rejoin
            public ContentSummary Own, Summary;
            public ContentDiff Diff;
            public int[] Requested;
            public List<ContentDetail>[] Parts;
            public bool[] LastSeen;
            public int DetailBytes, Claims, Stage;
            public string HeldClaim;        // a claim that came before the content was judged the same
            public int Refusal;             // the last claim the ledger refused: the code the claim deadline ends with
            public JoinHeader Header;
            public string HeaderProblem;
            public SnapshotSender Sender;
            // the drop of an in-game player (Design decision 16)
            public bool HaveDrop, Watching, Moved, Reloaded, SettledTried;
            public long DropMs, DropTicks;
            public int DropLoads;
            public Fingerprint Settled;
        }

        // The host's own check of its snapshot (Design decision 3), run through IBackground. The worker writes the three results, then
        // `done` with a full fence; the main thread reads `done` first, so the results are visible once it says 1.
        sealed class SelfCheck
        {
            readonly byte[] gz;
            readonly long declared;
            int done;
            volatile string sha, error, refusedType;

            public SelfCheck(byte[] gz, long declared) { this.gz = gz; this.declared = declared; }

            public bool Done { get { return Interlocked.CompareExchange(ref done, 0, 0) == 1; } }
            public string Sha { get { return sha; } }
            public string Error { get { return error; } }
            public string RefusedType { get { return refusedType; } }

            // Never throws: whatever goes wrong is a refusal.
            public void Run()
            {
                string s = null, e = null, t = null;
                try
                {
                    s = Sha256Hex.Of(gz);
                    int code; string why;
                    byte[] json = BoundedGunzip.Decompress(gz, declared, out code, out why);
                    if (json == null) e = "gunzip: " + why;
                    else
                    {
                        ScanResult r = OdinScan.Scan(json, ScanLimits.FromCorpus());
                        if (!r.Ok) { e = "scan: " + r.Error; t = r.RefusedType; }
                    }
                }
                catch (Exception x) { e = "the self-check failed (" + x.GetType().Name + ")"; }
                sha = s; error = e; refusedType = t;
                Interlocked.Exchange(ref done, 1);
            }
        }

        readonly IJoinHostWorld world;
        readonly IJoinLink link;
        readonly AgencyLedger ledger;
        readonly JoinBatcher batcher;
        readonly IBackground bg;
        readonly Func<long> nowMs;
        readonly Func<int, string> nameOf;
        readonly Joiner[] players = new Joiner[HostGate.MaxPlayerIds + 1];
        readonly int[] failures = new int[HostGate.MaxPlayerIds + 1];      // failed joins this session, per player id
        readonly bool[] everLoaded = new bool[HostGate.MaxPlayerIds + 1];  // its seat is the ledger's for the session
        readonly bool[] joining = new bool[HostGate.MaxPlayerIds + 1];     // its joining pause is on
        readonly bool[] queueWaitForgiven = new bool[HostGate.MaxPlayerIds + 1];   // its one uncounted in-batch QueueWait is used
        readonly string[] names = new string[HostGate.MaxPlayerIds + 1];

        Prep prep;
        int batchNo, joinedCount, failedCount, endCode;
        long batchStartMs, snapMs, gcBefore, readyStartMs, readyWaitMs;
        int startWaits;   // frames this batch's snapshot waited for the game's start set-up
        string endReason;
        int[] members = None;
        SnapshotData snap;
        SelfCheck check;
        // The command stream (M5), null outside a command session. Its batch hooks pair up: BatchEnded follows every
        // BatchStarting, however the batch ends.
        readonly ICommandStream stream;
        bool streamBegan;
        string[] humanAtSnap;                         // the stream's human set as the snapshot saw it (the header's)
        readonly List<string> claimedNew = new List<string>();   // seats this batch's BatchStarting claimed
        readonly List<int> loadedInBatch = new List<int>();

        public JoinService(IJoinHostWorld world, IJoinLink link, AgencyLedger ledger, JoinBatcher batcher, IBackground bg, Func<long> nowMs)
            : this(world, link, ledger, batcher, bg, nowMs, null)
        {
        }

        // nowMs: a monotonic millisecond clock (never DateTime.Now); every deadline here is measured on it.
        public JoinService(IJoinHostWorld world, IJoinLink link, AgencyLedger ledger, JoinBatcher batcher,
            IBackground bg, Func<long> nowMs, ICommandStream stream)
        {
            this.stream = stream;
            if (world == null) throw new ArgumentNullException("world");
            if (link == null) throw new ArgumentNullException("link");
            if (ledger == null) throw new ArgumentNullException("ledger");
            if (batcher == null) throw new ArgumentNullException("batcher");
            if (bg == null) throw new ArgumentNullException("bg");
            if (nowMs == null) throw new ArgumentNullException("nowMs");
            this.world = world; this.link = link; this.ledger = ledger; this.batcher = batcher; this.bg = bg; this.nowMs = nowMs;
            nameOf = NameOf;
        }

        // ---- the public surface ----

        // I-12: only these players steer the clock and only their drop pauses the game. False from Confirmed until Loaded, true after,
        // false again from a drop until a quick rejoin or a completed full rejoin. The host (player 0) is always in its own game.
        // ClockHost (Task 10) asks this before it acts on a SpeedRequest, and on a Left it must ask BEFORE calling Left here (Left ends
        // the player's in-game state).
        public bool InGame(int player)
        {
            if (player == 0) return true;
            Joiner j = Get(player);
            return j != null && j.Phase == Phase.InGame;
        }

        // The ledger holds an agency for this player: from its claim until a join that never loaded ends, and for the
        // whole session once it has loaded. The host always holds its own.
        public bool HoldsSeat(int player) { return player == 0 || ledger.AgencyOf(player) != null; }

        public string Status
        {
            get
            {
                if (prep == Prep.Idle) return IdleStatus;
                int pending = 0;
                foreach (int id in members) { Joiner j = players[id]; if (j != null && j.Phase == Phase.Batch) pending++; }
                return "join: batch " + batchNo + ": " + pending + " joining, " + joinedCount + " loaded, " + batcher.Waiting + " waiting";
            }
        }

        // From SessionHost.Confirmed (N-1): the first frame after the Welcome arrived. A rejoin's drop is timed to this moment.
        public void Confirmed(int player, string name, bool rejoin)
        {
            if (player < 1 || player >= players.Length) { Log.Warn("join: Confirmed for player " + player + ", outside 1.." + (players.Length - 1) + "; ignored"); return; }
            long now = nowMs();
            names[player] = TextSanitiser.Clean(name, 24, "Player " + player);
            if (failures[player] >= JoinLimits.MaxFailedJoins)
            {
                Log.Info("join: player " + player + " refused: its joins failed " + failures[player] + " times this session");
                SendRefusal(player, JoinCodes.TooManyFailures, null);
                KickSafe(player, JoinCodes.TooManyFailures);
                return;
            }
            Joiner j = players[player];
            // The same player on a new link with no Left in between (N-2): what the old link was doing is over, as if it had dropped.
            bool replaced = j != null && j.Phase != Phase.Dropped && !j.HaveDrop;
            if (replaced) LeftFor(j, now);
            j = players[player];
            // a rejoin is a replacing link as well; the stream hears of it once
            if (rejoin && j != null && j.HaveDrop)
            {
                StreamLinkReplaced(player);
                j.Phase = Phase.Summary; j.PhaseMs = now; j.ConfirmedMs = now; j.MayOffer = true; j.AfterFull = false;
                Log.Info("join: player " + player + " is back on a new link after its drop");
                return;
            }
            if (replaced) StreamLinkReplaced(player);
            players[player] = new Joiner { Id = player, Phase = Phase.Summary, PhaseMs = now, ConfirmedMs = now, MayOffer = rejoin };
        }

        // From SessionHost.Left: the player's link closed. An in-game player gets a drop record (the time, the game ticks and the load
        // count) and, when the game is paused, its settled print at once; anyone else leaves the join path.
        public void Left(int player)
        {
            Joiner j = Get(player);
            if (j == null) return;
            LeftFor(j, nowMs());
            StreamCall("Left", () => stream.Left(player));
        }

        // From the game glue when the host loads a save inside a command session: the stream starts again and every
        // client rejoins fully.
        public void HostLoaded()
        {
            StreamCall("HostLoaded", () => stream.HostLoaded());
        }

        // The host removes a player (a kick). A kick frees the seat even for a player that has loaded or dropped, so
        // the agency can be claimed again; only a drop that nobody kicks keeps its seat for the rejoin.
        public void Kick(int player, int code)
        {
            Joiner j = Get(player);
            if (j != null) Leave(j, code, "kicked by the host", false, false, null, false);
            if (player > 0 && player < everLoaded.Length) everLoaded[player] = false;
            string seat = ledger.AgencyOf(player);
            ledger.Forget(player);
            int claimed = claimedBy.IndexOf(player);   // released here, not again at the batch end
            if (claimed >= 0) { claimedBy.RemoveAt(claimed); claimedNew.RemoveAt(claimed); }
            if (seat != null && player > 0) StreamCall("ReleaseSeat", () => stream.ReleaseSeat(seat));
            KickSafe(player, code, true);
        }

        // Only the join kinds; any other message is not the join's business here.
        public void Received(int player, Message m)
        {
            Joiner j = Get(player);
            if (j == null || m == null) return;
            long now = nowMs();
            ContentSummary summary; ContentDetail detail; AgencyClaim claim; SnapshotAck ack; JoinStage stage; RejoinOffer offer;
            if ((summary = m as ContentSummary) != null) OnSummary(j, summary, now);
            else if ((detail = m as ContentDetail) != null) OnDetail(j, detail);
            else if ((claim = m as AgencyClaim) != null) OnClaim(j, claim, now);
            else if ((ack = m as SnapshotAck) != null) OnAck(j, ack, now);
            else if ((stage = m as JoinStage) != null) OnStage(j, stage, now);
            else if ((offer = m as RejoinOffer) != null) OnOffer(j, offer, now);
        }

        // Deadlines, the drop watch, the batch: its start, the snapshot, the self-check poll, the headers, the chunks within the window,
        // the expiries and the end. Nothing is allocated when nobody is joining and no drop is watched.
        public void Update()
        {
            long now = nowMs();
            if (liftOwed) RetryLifts();
            for (int id = 1; id < players.Length; id++)
            {
                Joiner j = players[id];
                if (j == null) continue;
                if (j.Watching) Watch(j, now);
                switch (j.Phase)
                {
                    case Phase.Summary:
                        if (now - j.PhaseMs >= (j.AfterFull ? AfterFullRejoinMs : JoinLimits.SummaryDeadlineMs))
                            Leave(j, JoinCodes.Deadline, "no content summary in time", false, true, null, true);
                        break;
                    case Phase.Detail:
                        if (now - j.PhaseMs >= JoinLimits.NoProgressMs) Leave(j, JoinCodes.Deadline, "no content details in time", false, true, null, true);
                        break;
                    case Phase.Claim:
                        if (now - j.PhaseMs >= JoinLimits.ClaimDeadlineMs) ClaimTimedOut(j);
                        break;
                }
            }
            // a batch whose last member loaded (or left) since the last frame ends first, so the next one can start in this frame
            if (prep != Prep.Idle && batcher.Done) FinishBatch(now);
            if (prep == Prep.Idle && !batcher.Running && batcher.Waiting > 0) StartBatch(now);
            if (prep == Prep.WaitPause)
            {
                if (IsPaused()) BeginWait(now);
                else if (now - batchStartMs >= JoinLimits.NoProgressMs) EndBatch(JoinCodes.HostSnapshotFailed, "the game did not pause within 30 s", now);
            }
            if (prep == Prep.WaitReady) WaitReady(now);
            if (prep == Prep.Checking) Poll(now);
            if (prep == Prep.Sending) { Expire(now); SendChunks(); }
            if (prep != Prep.Idle && batcher.Done) FinishBatch(now);
        }

        // ---- calls into the game that may throw: a throw is logged and never wedges joining ----
        // A throw is logged once per kind (not per frame) and answered with the safe value: not paused, time moved, a load happened,
        // no next day known; a pause flag goes off only once the lift succeeded.

        const int KindPaused = 0, KindTicks = 1, KindLoads = 2, KindNextDay = 3, KindSetJoining = 4, KindKick = 5, KindPlayerInGame = 6, KindGameOver = 7, KindReady = 8, KindStarted = 9;
        int warned;
        bool liftOwed;

        void Warn(int kind, string what, Exception e)
        {
            int bit = 1 << kind;
            if ((warned & bit) != 0) return;
            warned |= bit;
            Log.Warn("join: the game's " + what + " threw " + e.GetType().Name + ": " + TextSanitiser.OneLine(e.Message) + " (logged once; the safe answer is used)");
        }

        bool IsStarted()
        {
            try { return world.WorldStarted; }
            catch (Exception e) { Warn(KindStarted, "WorldStarted", e); return false; }
        }

        bool IsPaused()
        {
            try { return world.Paused; }
            catch (Exception e) { Warn(KindPaused, "Paused", e); return false; }
        }

        bool Ticks(out long t)
        {
            try { t = world.GameTicks; return true; }
            catch (Exception e) { Warn(KindTicks, "GameTicks", e); t = 0; return false; }
        }

        bool Loads(out int n)
        {
            try { n = world.LoadCount; return true; }
            catch (Exception e) { Warn(KindLoads, "LoadCount", e); n = 0; return false; }
        }

        long NextDaySafe()
        {
            try { return world.NextDay; }
            catch (Exception e) { Warn(KindNextDay, "NextDay", e); return 0; }
        }

        bool SetJoiningSafe(int id, bool on)
        {
            try { world.SetJoining(id, on); return true; }
            catch (Exception e) { Warn(KindSetJoining, "SetJoining", e); return false; }
        }

        void KickSafe(int id, int code, bool removed = false)
        {
            try { world.Kick(id, code, removed); }
            catch (Exception e) { Warn(KindKick, "Kick", e); }
        }

        void PlayerInGameSafe(int id, string agency)
        {
            try { world.PlayerInGame(id, agency); }
            catch (Exception e) { Warn(KindPlayerInGame, "PlayerInGame", e); }
        }

        // ---- small helpers ----

        Joiner Get(int player) { return player > 0 && player < players.Length ? players[player] : null; }

        string NameOf(int player) { return player > 0 && player < names.Length ? names[player] : null; }

        // A Notice carries the sanitised name, budgeted in bytes before it is sent (I-13).
        Notice Note(int kind, int player, int code)
        {
            return new Notice { Kind = kind, Player = player, Name = TextBudget.FitBytes(NameOf(player) ?? "", 96), Code = code };
        }

        static string List(int[] ids)
        {
            var sb = new StringBuilder();
            foreach (int id in ids) sb.Append(sb.Length > 0 ? "," : "").Append(id);
            return sb.ToString();
        }
    }
}
