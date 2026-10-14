using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The game as the joiner's driver sees it (src/Game/JoinClient.cs in the game; fakes in the tests and the fake client). Every
    // call is on the main thread.
    public interface IJoinerWorld
    {
        // null when it would not fit or the scan could not run (SummaryProblem says which)
        ContentSummary OwnSummary { get; }
        // when OwnSummary is null: SummaryTooBig, else it counts as ContentUnavailable
        int SummaryProblem { get; }
        ItemList OwnItems(int category);
        string Agency { get; }                           // from -semp-agency (or the dev trigger)
        void SetAgency(string id);                       // the next agency to claim (the seat the host keeps for it)
        bool AtMenu { get; }
        void GoToMenu();                                 // full rejoin from a running game: LoadScene("Menu"), keeping the link; MenuReady follows
        bool RefsResolve(IList<string> refs, out string missing);
        int Deserialise(byte[] json, out object saveData, out string error);   // the strict deserialise, at the menu only; 0 or a JoinCodes value
        int Sanity(object saveData, JoinHeader h, out string error);
        void ShowSummary(JoinHeader h);
        bool Declined { get; }
        void StartLoad(object saveData, JoinHeader h);   // route A; returns at once; reports through Loaded / LoadFailed
        void CancelLoad();                               // abort before ExtractAllFromSaveData (the coroutine stops; back to the menu)
        void BackToMenu(int code, string reason);
        // The join attempt is over for good (no reconnect due) and the player is on the way to the menu: the game
        // glue takes the session's hooks down so a solo game started there is the game's own again.
        void SessionOver();
        bool InSession { get; }                          // Design decision 16 (b)
        bool Paused { get; }                             // speed 0 and settled
        long GameTicks { get; }
        long NextDay { get; }
        long OriginTicks { get; }                        // OriginTicks from the last JoinHeader
        Fingerprint TakeFingerprint();                   // one visit, paused; null = failed (then a full rejoin)
        void ResumeInSession();                          // quick rejoin: follow the host's Clock again; nothing reloads
    }

    // The joiner's side of the join (plan "Interfaces, The drivers"; Design decisions 2, 10, 12, 14 and 16). Main thread only, except
    // the receiver (the link's reader thread, its own lock) and the walls' work item (the background worker, its own result slot).
    //
    // The path: Welcomed -> (GoToMenu, MenuReady when not at the menu) -> ContentSummary and AgencyClaim -> the host's answers ->
    // the receiver takes the header and the chunks on the reader thread -> the gunzip and the scan on the worker -> at the menu the
    // references, the strict deserialise and the sanity checks -> the summary for five seconds -> StartLoad -> Loaded. Every wall
    // that fails sends JoinStage(Failed, code) and goes back to the menu; nothing is half-loaded and nothing is retried after a
    // refusal or a failed check. A lost link before loading may retry with the rejoin key (ShouldRetry; at most
    // JoinLimits.MaxReconnects in one join attempt, counted afresh only after JoinLimits.StableInGameMs connected in
    // the game; never after the host broke the protocol, refused the reconnect or said goodbye); an abort after
    // StartLoad cancels the load first, then goes back with Aborted (or the host's own code when it refused or left).
    // A joiner never loads anything the host did not send through every wall, and a quick rejoin loads nothing at all.
    //
    // Deadlines run on the injected monotonic clock: JoinerTotalMs from the Welcome, then JoinLimits.TotalMsFor the
    // header's compressed size from its header (the host allows the same), to Loaded;
    // JoinerNoProgressMs of silence from the host while waiting on it; JoinerNoProgressMs for the worker's walls. While the host says
    // the joiner waits for a batch (a JoinWait, before its header) the silence and the total are suspended (time
    // behind other players is not the joiner's): JoinerQueueWaitMs caps the wait in all (QueueWait, which the host
    // never counts), and once JoinWait{Ahead = 0} says its batch has started the header must come within JoinerBatchHeaderMs. While the worker runs and while the summary shows, JoinStage(Checking) goes out once a second, and
    // JoinStage(Loading) once a second while loading, so the host's own no-progress deadline sees a live joiner.
    //
    // The claim (ruled 2026-10-11, with Task 9a): EVERY path sends AgencyClaim(world.Agency) right after ContentSummary: a
    // first join, every retry, and a full rejoin (which claims the agency the host keeps for it). The host cannot tell a restarted
    // process from a retry, so it never sends an unprompted ClaimResult(Ok). No agency (none, not an id, or a read that throws) is
    // refused locally before anything is sent (NoAgency). A refused claim is made again only with another agency (the dev trigger),
    // up to JoinLimits.MaxClaims. The receiver arms on the ClaimResult(Ok) that answers the claim.
    public sealed class JoinerFlow
    {
        const int HeartbeatMs = 1000;

        enum Phase { Idle, ToMenu, Checking, Receiving, Walls, Summary, Loading, InGame, Dropped, Offer, Failed }

        // Walls 3 and 4 (the bounded gunzip and the scan) for one snapshot, run on the background worker (Background.cs: it touches
        // nothing but this slot). The main thread reads the other fields only after Done, which is volatile and written last, so the
        // worker's writes are visible by then. When the join is abandoned (Forget), Abandoned is set: an item still queued returns at
        // once and drops its buffer (up to 16 MiB), and one already running stops after its gunzip.
        sealed class Walls
        {
            byte[] compressed;
            readonly long declared;
            public byte[] Json;
            public ScanResult Scan;
            public int Code;
            public string Error;
            public long GunzipMs, ScanMs;
            public volatile bool Done;
            public volatile bool Abandoned;

            public Walls(byte[] compressed, long declared) { this.compressed = compressed; this.declared = declared; }

            public void Run()
            {
                try
                {
                    if (Abandoned) { compressed = null; return; }
                    long t0 = Stopwatch.GetTimestamp();
                    int code; string error;
                    byte[] json = BoundedGunzip.Decompress(compressed, declared, out code, out error);
                    compressed = null;
                    GunzipMs = Ms(t0);
                    if (json == null) { Code = code != 0 ? code : JoinCodes.Decompress; Error = error ?? "the snapshot could not be unpacked"; return; }
                    if (Abandoned) return;
                    t0 = Stopwatch.GetTimestamp();
                    ScanResult r = OdinScan.Scan(json, ScanLimits.FromCorpus());
                    ScanMs = Ms(t0);
                    Scan = r;
                    if (!r.Ok)
                    {
                        Code = r.Code == ScanCode.TypeNotAllowed ? JoinCodes.TypeRefused : JoinCodes.Malformed;
                        Error = "the scan refused it (scan code " + r.Code + "): " + r.Error + (r.RefusedType != null ? " [" + r.RefusedType + "]" : "");
                        return;
                    }
                    Json = json;
                }
                catch (Exception e) { Code = JoinCodes.Malformed; Error = "the checks failed (" + e.GetType().Name + ")"; }
                finally { Done = true; }
            }
        }

        readonly IJoinerWorld world;
        readonly IBackground bg;
        readonly Func<long> nowMs;

        Phase phase = Phase.Idle;
        Func<Message, bool> send;
        SnapshotReceiver receiver;
        // 1 from just before a claim goes out until the receiver hears the ClaimResult that answers it (TakeClaim,
        // on the reader thread); claimDue is the main thread's own copy, cleared when the flow hears that answer.
        int claimOwed;
        bool claimDue, detailsSent;
        bool shouldRetry;
        int retries;                       // reconnects granted in this join attempt (JoinLimits.MaxReconnects)
        long inGameMs;                     // when the game was last reached (a load or a quick rejoin)
        int chunksSeen;                    // the receiver's chunk count at the last progress
        int totalMs = JoinLimits.JoinerTotalMs;
        long startMs, progressMs, wallsMs, summaryUntil, beatMs;
        bool waitingForBatch, batchUp;   // waiting for a batch (a JoinWait came); its batch has started (JoinWait{Ahead = 0})
        int ahead;                       // the last JoinWait's Ahead, read only while waitingForBatch
        long queuedMs, batchUpMs;
        bool claimOk, claimRefused;
        int claimCode;                     // the code of the last refused claim (ClaimRefusedCode)
        string lastClaim;
        int claims;
        Walls walls;
        JoinHeader header;
        object saveData;
        int gcBefore;
        bool settleTaken;
        Fingerprint settled;
        string status = "";
        int endCode;                       // EndCode
        bool endedHere;                    // EndedHere
        int backCode;                      // BackCode, BackInGame, BackByHost
        bool backInGame, backByHost;
        int reloadCode;                    // ReloadCode
        string[] report;                   // the host's refusal lines, cleaned, for the screen (null when none)
        bool outOfStep;                    // the game left the stream (OutOfStep) and has not loaded since

        public JoinerFlow(IJoinerWorld world, IBackground bg, Func<long> nowMs)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (bg == null) throw new ArgumentNullException("bg");
            if (nowMs == null) throw new ArgumentNullException("nowMs");
            this.world = world;
            this.bg = bg;
            this.nowMs = nowMs;
        }

        // The link's Bulk for the current link (a new one per Welcome; null before the first). Welcomed hands it to attach.
        public SnapshotReceiver Receiver { get { return receiver; } }
        public bool InGame { get { return phase == Phase.InGame || phase == Phase.Dropped || phase == Phase.Offer; } }
        // In game with the host listening: the host hears this player's speed requests exactly from its Loaded (or a
        // quick rejoin's answer) until the link drops (I-12), so the clock asks only then (ClientRequest).
        public bool Playing { get { return phase == Phase.InGame; } }
        // The link was lost before loading (or in game): the session client may reconnect with its rejoin key. False after a refusal by
        // the host or a failed check (plan Design decision 10).
        public bool ShouldRetry { get { return shouldRetry; } }
        public string Status { get { return status; } }
        // How the last join attempt ended, while the link that follows is closed: the code it ended with (0 while
        // none ended, or it ended well), and whether this joiner stopped it (it told the host Failed) rather than the
        // host (JoinRefused). The host then closes the link with the same code, which is not a kick
        // (JoinText.LinkEnded).
        public int EndCode { get { return endCode; } }
        public bool EndedHere { get { return endedHere; } }
        // The lines of the host's last refusal (its report) or its goodbye, each cleaned and capped, for the status
        // label under Status; null when there are none. A new link or join clears them.
        public string[] Report { get { return report; } }
        // How the session last ended for good (no reconnect due), for the screens' session-over and refusal dialogs:
        // the code it went back to the menu with (0 while none ended), whether a game was in progress, and whether
        // the host's goodbye ended it. A new join clears them.
        public int BackCode { get { return backCode; } }
        public bool BackInGame { get { return backInGame; } }
        public bool BackByHost { get { return backByHost; } }
        // A full rejoin is reloading the world (the host's RejoinCodes reason), until the game is reached again or
        // the join ends; 0 when none.
        public int ReloadCode { get { return reloadCode; } }
        // In game but the link is down (waiting to reconnect), or reconnected and offering a quick rejoin.
        public bool Dropped { get { return phase == Phase.Dropped; } }
        public bool Rejoining { get { return phase == Phase.Offer; } }
        // The reconnects granted so far in this join attempt: the next one's number, for its wait (RetryWaitSeconds).
        public int Retries { get { return retries; } }

        // For the progress view (JoinProgress): the stage this join waits on now, the snapshot received so far in
        // percent (-1 before its header), the players ahead in the host's join queue, and whether the game fell out of
        // step with the host and is rejoining from a fresh copy (until that copy has loaded).
        public JoinStep Step
        {
            get
            {
                switch (phase)
                {
                    case Phase.Checking: return waitingForBatch && ahead > 0 ? JoinStep.Queued : JoinStep.Checking;
                    case Phase.Receiving: return JoinStep.Receiving;
                    case Phase.Walls:
                    case Phase.Summary: return JoinStep.Checking;
                    case Phase.Loading: return JoinStep.Loading;
                    default: return JoinStep.None;
                }
            }
        }
        public int ReceivedPct
        {
            get
            {
                if (phase != Phase.Receiving || header == null || header.ChunkCount <= 0) return -1;
                return Math.Min(100, (int)((long)receiver.Received * 100 / header.ChunkCount));
            }
        }
        public int Ahead { get { return waitingForBatch ? ahead : 0; } }
        public bool OutOfStepRejoin { get { return outOfStep; } }

        // The claim, for the agency picker (M8 Track U): the last claim was refused and no claim is out (the picker
        // opens again; a new agency from the world is claimed by Update, up to JoinLimits.MaxClaims), its code, the
        // agency the host accepted (null until then), and how many claims this join may still make.
        public bool ClaimRefused
        {
            get
            {
                return claimRefused && !claimOk && !claimDue && (phase == Phase.Checking || phase == Phase.Receiving);
            }
        }
        public int ClaimRefusedCode { get { return claimRefused ? claimCode : 0; } }
        public string ClaimedAgency { get { return claimOk ? lastClaim : null; } }
        public int ClaimsLeft { get { return Math.Max(0, JoinLimits.MaxClaims - claims); } }
        public string LastClaim { get { return lastClaim; } }

        // A new join from the menu (the player's, or the dev trigger's): its reconnects are counted afresh.
        // A join that ended (Failed) is a fresh start again, so this attempt's own first connection can end it too.
        public void NewAttempt()
        {
            retries = 0; report = null; endCode = 0; endedHere = false; outOfStep = false;
            backCode = 0; backInGame = false; backByHost = false; reloadCode = 0;
            if (phase == Phase.Failed) phase = Phase.Idle;
        }

        // The wait before reconnect n: 2, 4, 8, 16 and 32 s, so a host that is gone is not hammered.
        public static int RetryWaitSeconds(int retry)
        {
            return 1 << (retry < 1 ? 1 : retry > JoinLimits.MaxReconnects ? JoinLimits.MaxReconnects : retry);
        }

        // The Welcome arrived on a new link. player: this joiner's id (the header must name it); send: that link's Send (false = the
        // link is gone); attach: sets the link's Bulk sink (Link.Bulk / LinkClient.Bulk). The receiver is attached BEFORE anything is
        // sent, so a ClaimResult(Ok) racing back on the reader thread always finds it.
        // rejoin && InSession after a drop in game: RejoinOffer first; else to the menu if needed, then the summary.
        public void Welcomed(int player, Func<Message, bool> send, Action<IBulkSink> attach, bool rejoin)
        {
            if (send == null) throw new ArgumentNullException("send");
            if (attach == null) throw new ArgumentNullException("attach");
            if (phase == Phase.Loading) Abort("a new connection replaced the one the load came from", false, Phase.Idle, true);
            bool dropped = phase == Phase.Dropped || phase == Phase.Offer;
            Forget();
            this.send = send;
            shouldRetry = false;
            report = null;
            endCode = 0;
            endedHere = false;

            ContentSummary own = world.OwnSummary;
            string version = own != null ? ModInfo.ClipGameVersion(own.GameVersion) : "";   // this game's version, as the host's header carries it
            int me = player;
            receiver = new SnapshotReceiver(TakeClaim, version, me, h => HeaderRules.Check(h, version, me));
            attach(receiver);
            startMs = progressMs = nowMs();
            totalMs = JoinLimits.JoinerTotalMs;

            if (rejoin && dropped && world.InSession) { Offer(); return; }
            settled = null;
            settleTaken = false;
            if (!world.AtMenu) { ToMenu(); return; }
            StartJoin();
        }

        // After GoToMenu: the full path continues from the menu.
        public void MenuReady()
        {
            if (phase != Phase.ToMenu) return;
            StartJoin();
        }

        public void Received(Message m)
        {
            if (m == null || phase == Phase.Idle || phase == Phase.Failed) return;
            progressMs = nowMs();

            // A Bye never comes here: the link ends on it, and its closing event brings it (Lost with hostLeft).
            var refused = m as JoinRefused;
            if (refused != null) { HostRefused(refused.Code, refused.Lines); return; }

            if (phase == Phase.Checking || phase == Phase.Receiving)
            {
                var request = m as ContentRequest;
                if (request != null)
                {
                    // the host compares one summary per attempt, so it asks once
                    if (detailsSent) { HostBroke("a second content request"); return; }
                    detailsSent = true;
                    SendDetails(request.Categories);
                    return;
                }
                var claim = m as ClaimResult;
                if (claim != null)
                {
                    // one answer per claim: an Ok to no claim never armed the sink, and the host broke the rules
                    if (!claimDue) { HostBroke("a claim answer with no claim to answer"); return; }
                    claimDue = false;
                    claimOk = claim.Ok;
                    claimRefused = !claim.Ok;
                    claimCode = claim.Ok ? 0 : claim.Code;
                    if (!claim.Ok)
                    {
                        status = JoinText.Refused(claim.Code);
                        // only a seat the host keeps for this player (by its rejoin key) is named in a refusal: the
                        // next claim is for that seat, which the host still judges against its ledger
                        if (claim.Agency != null && claim.Agency != lastClaim) TakeKeptSeat(claim.Agency);
                    }
                    return;
                }
                var wait = m as JoinWait;
                if (wait != null && phase == Phase.Checking)
                {
                    if (!waitingForBatch) { waitingForBatch = true; queuedMs = progressMs; }
                    if (wait.Ahead == 0 && !batchUp) { batchUp = true; batchUpMs = progressMs; }
                    ahead = wait.Ahead;
                    status = wait.Ahead > 0 ? JoinText.Waiting(wait.Ahead) : JoinText.Status(JoinStageKind.Checking);
                    return;
                }
                return;   // the JoinHeader: the sink took it on the reader thread already, and Update picks it up from there
            }

            var result = m as RejoinResult;
            if (result != null && phase == Phase.Offer)
            {
                Action<RejoinResult> answered = RejoinAnswered;
                if (answered != null) answered(result);
                if (result.Code == RejoinCodes.Resume)
                {
                    Log.Info("join: resume from #" + result.ResumeNextSeq + " - the host replays what this game missed");
                    phase = Phase.InGame;
                    inGameMs = nowMs();
                    settled = null;
                    settleTaken = false;
                    world.ResumeInSession();
                    status = JoinText.RejoinQuick();
                    return;
                }
                if (result.Quick)
                {
                    Log.Info("join: quick rejoin - resumed without a reload");
                    phase = Phase.InGame;
                    inGameMs = nowMs();   // not a reset: a host could resume the joiner and drop it, every time
                    settled = null;
                    settleTaken = false;
                    world.ResumeInSession();
                    status = JoinText.RejoinQuick();
                    return;
                }
                Log.Info("join: full rejoin (" + result.Code + ") - to the menu first");
                reloadCode = result.Code;
                status = JoinText.RejoinReload(result.Code);
                ToMenu();
            }
        }

        // From the link's bulk progress event (at most every 64 chunks, and the last): the status line. The deadline's
        // progress is every chunk taken (UpdateTransfer).
        public void Progress()
        {
            if (phase != Phase.Checking && phase != Phase.Receiving) return;
            progressMs = nowMs();
            JoinHeader h = receiver.Header;
            if (h != null) status = JoinText.Downloading(receiver.Received, h.ChunkCount);
        }

        // The link closed. Before loading: the attempt is dropped and a retry with the key may follow. While loading: the load is
        // cancelled, then back to the menu. In game: the settle watch starts (the print is taken at once when already paused).
        // Every retry counts towards JoinLimits.MaxReconnects for this join attempt; past it the join ends.
        public void Lost(string reason) { Lost(reason, false, false); }

        public void Lost(string reason, bool violation) { Lost(reason, violation, false); }

        // violation: this side closed the link because the host broke the protocol (Link.Violation, on the closing
        // event). Such a host is never reconnected to (Protocol, no retry): it could loop the joiner for ever.
        // refused: the host answered with a Reject (NetEvent.Refused). A refused reconnect is not tried again
        // (RejoinRefused); a refused first connection only ends the session (NeverIn; the session client says why).
        public void Lost(string reason, bool violation, bool refused) { Lost(reason, violation, refused, false); }

        public void Lost(string reason, bool violation, bool refused, bool hostLeft)
        {
            Lost(reason, violation, refused, hostLeft, 0);
        }

        // hostLeft: a Bye from the host ended the link (NetEvent.Bye): it closed its game or ended this connection on
        // purpose, so nothing is reconnected (HostLeft); byeCode (NetEvent.ByeCode) says why, for the player's words.
        // A protocol breach or a refusal still decides first, and a join already over (Failed: a refusal is often
        // followed by the host closing the link) is not touched. A goodbye with no join in progress (Idle, no
        // reconnect due: a first connection, the dev trigger's from a running game included) never goes to the menu,
        // which would throw that game away; it only ends the session (NeverIn).
        public void Lost(string reason, bool violation, bool refused, bool hostLeft, int byeCode)
        {
            string why = TextSanitiser.Clean(reason, 200, "");
            if (hostLeft && !violation && !refused && phase != Phase.Failed)
            {
                if (phase != Phase.Idle || shouldRetry) HostLeft(why, byeCode);
                else NeverIn(why, byeCode > 0 ? byeCode : JoinCodes.HostLeft);
                send = null;
                return;
            }
            switch (phase)
            {
                case Phase.Failed:
                    break;
                case Phase.Idle:
                case Phase.Dropped:
                    // a reconnect that ended before its Welcome
                    if (refused && shouldRetry)
                    {
                        Log.Info("join: the host refused the reconnect (" + why + ")");
                        Stop(JoinCodes.RejoinRefused, "the host refused the reconnect: " + why);
                    }
                    else if (shouldRetry || violation) Again(violation, why);
                    else if (phase == Phase.Idle) NeverIn(why, refused ? JoinCodes.NotLetIn : JoinCodes.NoConnection);
                    break;
                case Phase.Loading:
                    if (violation)
                        Abort("the host broke the protocol while loading: " + why, false, Phase.Failed, false,
                            JoinCodes.Protocol);
                    else if (RetryLeft()) Abort("the link was lost while loading: " + why, false, Phase.Idle, true);
                    else Abort("the link was lost while loading, " + retries + " times in this join", false,
                        Phase.Failed, false, JoinCodes.Reconnects);
                    break;
                case Phase.InGame:
                    phase = Phase.Dropped;
                    settled = null;
                    settleTaken = false;
                    Again(violation, why);
                    TrySettle();
                    break;
                case Phase.Offer:
                    phase = Phase.Dropped;   // the settled print still stands for the next offer
                    Again(violation, why);
                    break;
                default:
                    // the sink refused a header or chunk and the link closed for it: a failed check, never a retry
                    if (receiver.ErrorCode != 0) { Refuse(receiver.ErrorCode, receiver.Error); break; }
                    if (violation) { Refuse(JoinCodes.Protocol, "the host broke the protocol: " + why); break; }
                    if (!RetryLeft()) { GiveUp(why); break; }
                    Log.Info("join: the link was lost before loading (" + why + "); a retry may follow");
                    End(Phase.Idle, true);
                    break;
            }
            send = null;
        }

        // A first connection that ended before its Welcome with no reconnect due (the host unreachable, or refusing
        // the Hello or saying goodbye to it): the join is over before it began. A game the player is in stays (no
        // BackToMenu, which would throw it away), but the session ends, or its clock would hold a solo game paused.
        // code says why for the session-over dialog, with the link's own reason (the host's refusal) as its report:
        // a join that never got in must not end with nothing on screen.
        void NeverIn(string why, int code)
        {
            Log.Info("join: no connection to the host (" + why + "); the session is over");
            End(Phase.Failed, false);
            endCode = code;
            endedHere = false;
            report = why.Length > 0 ? new[] { TextSanitiser.Clean(why, JoinLimits.MaxReportLineChars, "") } : null;
            try { world.SessionOver(); }
            catch (Exception e) { Log.Warn("join: SessionOver threw " + e.GetType().Name); }
        }

        // One more reconnect for this join attempt, while JoinLimits.MaxReconnects allows.
        bool RetryLeft() { return ++retries <= JoinLimits.MaxReconnects; }

        // A lost link outside a running join (a dropped game, or a reconnect that never got its Welcome): retried while
        // the count allows; a host that broke the protocol is not.
        void Again(bool violation, string why)
        {
            if (!violation && RetryLeft()) { shouldRetry = true; return; }
            if (!violation) { GiveUp(why); return; }
            Log.Info("join: the host broke the protocol (" + why + "); no reconnect");
            Stop(JoinCodes.Protocol, "the host broke the protocol: " + why);
        }

        // The reconnects of this join attempt are used up: no more, and a join that has not loaded goes to the menu.
        void GiveUp(string why)
        {
            Log.Info("join: the link was lost " + retries + " times in this join (the last: " + why
                + "); no more reconnects");
            Stop(JoinCodes.Reconnects, "the link to the host was lost " + retries + " times");
        }

        // No more reconnects, for code: back to the menu, and a game in progress goes too (no stranded paused game),
        // told in the words for a session that is over.
        void Stop(int code, string reason)
        {
            shouldRetry = false;
            bool game = InGame;
            End(Phase.Failed, false);
            status = game ? JoinText.EndedInGame(code) : JoinText.Refused(code);
            if (outOfStep && code == JoinCodes.Reconnects) status = CommandText.KeptFallingOutOfStep();
            BackToMenu(code, reason, game, false);
        }

        // The player left the session on purpose (Leave, DD18; its goodbye carries the leave code): no reconnect and no
        // drop, back to the menu, a load in progress cancelled first. The link's own close that follows finds the join
        // already over.
        public void LeftOnPurpose()
        {
            if (phase == Phase.Loading)
            {
                Abort("the player left the session", false, Phase.Failed, false, JoinCodes.Left);
                return;
            }
            bool game = InGame;
            End(Phase.Failed, false);
            status = JoinText.Refused(JoinCodes.Left);
            BackToMenu(JoinCodes.Left, "the player left the session", game, false);
        }

        // The command stream found this game out of step with the host (DD7): the session drops its link and takes the
        // full-rejoin path (the world is no longer in session, so the reconnect loads a fresh snapshot). Each such
        // rejoin counts as a reconnect: past JoinLimits.MaxReconnects before the count resets, it stops at the menu.
        public void OutOfStep()
        {
            outOfStep = true;
            status = JoinText.Refused(JoinCodes.OutOfStep);
        }

        // The last stream entry this game applied, for the quick-rejoin offer (the host gives a quick rejoin only to a
        // client that missed nothing); 0 when nothing is wired.
        public Func<uint> LastResult;
        // M7: the host's answer to a rejoin offer, before the flow acts on it (the session's command client resumes, or
        // gives its suspended stream up, by it).
        public Action<RejoinResult> RejoinAnswered;

        uint LastApplied()
        {
            Func<uint> f = LastResult;
            if (f == null) return 0;
            try { return f(); }
            catch (Exception) { return 0; }
        }

        // Once a frame. Nothing is allocated when no join or drop is in progress (Idle, Failed, InGame, a drop whose print is taken).
        public void Update()
        {
            switch (phase)
            {
                case Phase.Idle:
                case Phase.Failed:
                    return;
                case Phase.InGame:
                    // a minute in the game, connected and with no drop, ends the join attempt's reconnects
                    if (retries > 0 && nowMs() - inGameMs >= JoinLimits.StableInGameMs) retries = 0;
                    return;
                case Phase.Dropped:
                    if (!settleTaken) TrySettle();
                    return;
            }
            long now = nowMs();
            if (phase == Phase.Checking && waitingForBatch)
            {
                // waiting for a batch: the total and the silence are suspended; the queue cap, then the header after the batch's start.
                // A header that has just arrived is taken below (UpdateTransfer), and the total restarts there.
                bool noHeader = receiver.Header == null;
                if (noHeader && batchUp && now - batchUpMs >= JoinLimits.JoinerBatchHeaderMs)
                { Refuse(JoinCodes.JoinerTimeout, "the batch started but no header came within " + JoinLimits.JoinerBatchHeaderMs / 1000 + " s"); return; }
                if (noHeader && !batchUp && now - queuedMs >= JoinLimits.JoinerQueueWaitMs)
                { Refuse(JoinCodes.QueueWait, "waited " + JoinLimits.JoinerQueueWaitMs / 60000 + " minutes for a batch"); return; }
            }
            else if (now - startMs >= totalMs)
            {
                Timeout("the join took longer than " + totalMs / 1000 + " s");
                return;
            }
            switch (phase)
            {
                case Phase.Checking:
                case Phase.Receiving:
                    UpdateTransfer(now);
                    return;
                case Phase.Walls:
                    UpdateWalls(now);
                    return;
                case Phase.Summary:
                    UpdateSummary(now);
                    return;
                case Phase.Loading:
                    if (now - beatMs >= HeartbeatMs) Beat(JoinStageKind.Loading, now);
                    return;
                case Phase.Offer:
                    if (now - progressMs >= JoinLimits.JoinerNoProgressMs) Refuse(JoinCodes.JoinerTimeout, "the host did not answer the rejoin offer");
                    return;
            }
        }

        // From SnapshotLoad, after every step of Design decision 12 succeeded. S3 (DECISIONS 2026-10-11, accepted): the game side calls
        // this only once the load has SETTLED (the plan queue empty, the porkchop windows done, a few frames), so the joiner matches the
        // game's own reload of the snapshot; this flow trusts the call and only checks the date against the header.
        public void Loaded(long dateTicks)
        {
            if (phase != Phase.Loading) return;
            long diff = dateTicks - header.DateTicks;
            if (diff < 0) diff = -diff;
            if (diff > JoinLimits.DateToleranceTicks) { Refuse(JoinCodes.DateMismatch, "the game loaded at " + dateTicks + ", the header says " + header.DateTicks); return; }
            Log.Info("join: loaded at " + dateTicks + " (header " + header.DateTicks + ")");
            phase = Phase.InGame;
            reloadCode = 0;
            outOfStep = false;   // a fresh copy of the host's world
            inGameMs = nowMs();   // the reconnects start afresh after StableInGameMs here, not at once
            settled = null;
            settleTaken = false;
            status = JoinText.Status(JoinStageKind.Loaded);
            SendOrLose(new JoinStage { Stage = JoinStageKind.Loaded, DateTicks = dateTicks });   // a lost link here is a drop in game
        }

        public void LoadFailed(int code, string reason)
        {
            if (phase != Phase.Loading) return;
            Refuse(code >= 1 && code <= 99 ? code : JoinCodes.LoadThrew, reason ?? "the load failed");
        }

        // ---- the steps ----

        void ToMenu()
        {
            phase = Phase.ToMenu;
            world.GoToMenu();
        }

        // The full path from the menu: the summary, then the claim, on every path (ruled 2026-10-11).
        void StartJoin()
        {
            ContentSummary own = world.OwnSummary;
            if (own == null)
            {
                // Design decision 7: no summary (one that would not fit, or a scan that could not run) is refused here
                // and NOTHING is sent. Only a summary refused for its size says so; anything else is
                // ContentUnavailable.
                int why = JoinCodes.ContentUnavailable;
                try { if (world.SummaryProblem == JoinCodes.SummaryTooBig) why = JoinCodes.SummaryTooBig; }
                catch (Exception) { }
                LocalRefusal(why, JoinText.Refused(why));
                return;
            }
            string agency = AgencyNow();
            if (!JoinCheck.IsAgencyId(agency))
            {
                // Every path claims, so a joiner with no agency cannot join: refused here, before anything is sent.
                LocalRefusal(JoinCodes.NoAgency, "no agency chosen");
                return;
            }
            own.GameVersion = ModInfo.ClipGameVersion(own.GameVersion);
            receiver.Reset();
            chunksSeen = 0;
            claimOk = claimRefused = false;
            claimCode = 0;
            lastClaim = null;
            claims = 0;
            claimDue = detailsSent = false;
            waitingForBatch = batchUp = false;
            header = null;
            phase = Phase.Checking;
            progressMs = nowMs();
            status = JoinText.Status(JoinStageKind.Checking);
            if (!SendOrLose(own)) return;
            LogSummary(own);
            Claim(agency);
        }

        // Refused before anything is sent (the summary would not fit, no agency): back to the menu, no retry.
        void LocalRefusal(int code, string why)
        {
            Log.Info("join: could not join (" + code + "): " + why);
            End(Phase.Failed, false);
            status = JoinText.Refused(code);
            BackToMenu(code, why);
        }

        // Only after a refusal, and only with another agency (the dev trigger): never a second claim while one is unanswered.
        bool WantClaim()
        {
            if (claimOk || !claimRefused || claims >= JoinLimits.MaxClaims) return false;
            string a = AgencyNow();
            return JoinCheck.IsAgencyId(a) && a != lastClaim;
        }

        void Claim(string agency)
        {
            if (!JoinCheck.IsAgencyId(agency) || claims >= JoinLimits.MaxClaims) return;
            claims++;
            lastClaim = agency;
            claimRefused = false;
            // BEFORE it goes out: a fast host's ClaimResult(Ok) reaches the sink on the reader thread
            Interlocked.Exchange(ref claimOwed, 1);
            claimDue = true;
            SendOrLose(new AgencyClaim { Agency = agency });
        }

        // The receiver asks this for every ClaimResult, whatever it says: true once for the answer to the claim that is
        // out, and never again until the next claim, so only an Ok that answers a claim arms the sink.
        bool TakeClaim() { return Interlocked.Exchange(ref claimOwed, 0) == 1; }

        void HostBroke(string what) { Refuse(JoinCodes.Protocol, "the host broke the protocol: " + what); }

        void SendDetails(int[] categories)
        {
            ContentSummary own = world.OwnSummary;
            if (own == null || categories == null) return;
            var items = new Dictionary<int, ItemList>();
            foreach (int c in categories) items[c] = world.OwnItems(c);
            ContentDetail[] parts = ContentReport.Details(own, items, categories);
            foreach (ContentDetail p in parts)
                if (!SendOrLose(p)) return;
        }

        void UpdateTransfer(long now)
        {
            // Every chunk the receiver took is the host's progress: the link's own progress event comes only every 64
            // chunks (2 MiB), which a slow upload takes longer than the no-progress deadline to send. Read here, where
            // the receiver is polled every frame anyway, rather than adding events to the link.
            int got = receiver.Received;
            if (got != chunksSeen) { chunksSeen = got; progressMs = now; }
            if (!waitingForBatch && now - progressMs >= JoinLimits.JoinerNoProgressMs) { Refuse(JoinCodes.JoinerTimeout, "the host sent nothing for " + JoinLimits.JoinerNoProgressMs / 1000 + " s"); return; }
            if (WantClaim())
            {
                Claim(AgencyNow());
                if (phase != Phase.Checking && phase != Phase.Receiving) return;
            }
            if (phase == Phase.Checking)
            {
                JoinHeader h = receiver.Header;
                if (h != null)
                {
                    phase = Phase.Receiving;
                    header = h;
                    waitingForBatch = batchUp = false;
                    progressMs = now;
                    startMs = now;   // its batch is serving it: the whole total is the batch's from here
                    totalMs = JoinLimits.TotalMsFor(h.CompressedSize);
                    string bad = BoundaryRules.Check(h.DateTicks, h.OriginTicks, BoundaryRules.Of(h));
                    if (bad != null) { Refuse(JoinCodes.BoundariesInvalid, "the header's day boundaries: " + bad); return; }
                    Log.Info("join: receiving " + h.CompressedSize + " bytes in " + h.ChunkCount + " chunks");
                    status = JoinText.Downloading(0, h.ChunkCount);
                }
            }
            int err = receiver.ErrorCode;
            if (err != 0) { Refuse(err, "the transfer: " + receiver.Error); return; }
            if (phase == Phase.Receiving && receiver.Complete) StartWalls(now);
        }

        // Walls 3 and 4 go to the worker (never this thread: a hostile 64 MiB stream takes seconds); Update polls the result.
        void StartWalls(long now)
        {
            byte[] gz = receiver.TakeCompressed();
            if (gz == null) { Refuse(JoinCodes.Malformed, "the transfer ended without its bytes"); return; }
            Log.Info("join: received, hash ok");
            var w = new Walls(gz, header.UncompressedSize);
            gcBefore = GC.CollectionCount(0);
            phase = Phase.Walls;
            wallsMs = now;
            walls = w;
            try { bg.Run(w.Run); }
            catch (Exception e)
            {
                // ThreadBackground refuses with InvalidOperationException when its queue is full (or a thread cannot start): a refusal.
                Refuse(JoinCodes.Aborted, "the background worker refused the checks (" + e.GetType().Name + ")");
                return;
            }
            Beat(JoinStageKind.Checking, now);
        }

        void UpdateWalls(long now)
        {
            Walls w = walls;
            if (!w.Done)
            {
                if (now - wallsMs >= JoinLimits.JoinerNoProgressMs) { Refuse(JoinCodes.JoinerTimeout, "the checks took longer than " + JoinLimits.JoinerNoProgressMs / 1000 + " s"); return; }
                if (now - beatMs >= HeartbeatMs) Beat(JoinStageKind.Checking, now);
                return;
            }
            walls = null;
            if (w.Code != 0) { Refuse(w.Code, w.Error); return; }

            // Walls 5-7 on the main thread, at the menu. A check that throws fails closed with that wall's code.
            int fail = 0;
            string why = null;
            try { fail = MainThreadWalls(w, out why); }
            catch (Exception e) { why = "a check threw " + e.GetType().Name; }
            if (why != null) { Refuse(fail != 0 ? fail : wallStep, why); return; }
            phase = Phase.Summary;
            summaryUntil = now + JoinLimits.SummaryShowMs;
            status = JoinText.Summary(HostName(header), header.DateTicks, header.Config != null ? header.Config.Company : null);
        }

        int wallStep;   // the wall MainThreadWalls is in, for a check that throws

        // 0 and why null when walls 5-7 pass and the summary is up; else the failing wall's code and why.
        int MainThreadWalls(Walls w, out string why)
        {
            why = null;
            wallStep = JoinCodes.RefUnresolved;
            string missing;
            if (!world.RefsResolve(w.Scan.Refs, out missing)) { why = "a reference does not resolve: " + missing; return JoinCodes.RefUnresolved; }
            // Premise: the snapshot is deserialised ONLY at the main menu, where the singletons a cast operator or
            // callback could reach do not exist. Anywhere else this refuses before Odin sees a byte.
            wallStep = JoinCodes.StrictDeserialise;
            if (!world.AtMenu) { why = "not at the main menu: the snapshot is deserialised only there"; return JoinCodes.StrictDeserialise; }
            long t0 = Stopwatch.GetTimestamp();
            object data; string error;
            int code = world.Deserialise(w.Json, out data, out error);
            long deserialiseMs = Ms(t0);
            if (code != 0 || data == null) { why = error ?? "the deserialise gave nothing"; return code != 0 ? code : JoinCodes.StrictDeserialise; }
            wallStep = JoinCodes.SanityFailed;
            code = world.Sanity(data, header, out error);
            if (code != 0) { why = error ?? "the save failed a sanity check"; return code; }
            saveData = data;
            Log.Info("join: checks: gunzip " + w.GunzipMs + " ms (crc ok); scan " + w.ScanMs + " ms, " + w.Scan.Types.Count + " types, " + w.Scan.Refs.Count
                + " refs, depth " + w.Scan.MaxDepthSeen + "; refs ok; deserialise " + deserialiseMs + " ms; sanity ok; gc +" + (GC.CollectionCount(0) - gcBefore));
            world.ShowSummary(header);
            return 0;
        }

        // The summary shows for SummaryShowMs (Design decision 14); the dev trigger "decline" refuses it. Then the load, from the menu.
        void UpdateSummary(long now)
        {
            bool declined;
            try { declined = world.Declined; }
            catch (Exception e) { Refuse(JoinCodes.Aborted, "the summary check threw " + e.GetType().Name); return; }
            if (declined) { Refuse(JoinCodes.Declined, "declined at the summary"); return; }
            if (now < summaryUntil)
            {
                if (now - beatMs >= HeartbeatMs) Beat(JoinStageKind.Checking, now);
                return;
            }
            // Route A loads from the menu only: a player who pressed Continue or New Game while the summary showed gets no load.
            bool atMenu;
            try { atMenu = world.AtMenu; }
            catch (Exception) { atMenu = false; }
            if (!atMenu) { Refuse(JoinCodes.Aborted, "left the menu before the load"); return; }
            if (!SendOrLose(new JoinStage { Stage = JoinStageKind.Loading })) return;   // a dead link starts no load
            beatMs = now;
            phase = Phase.Loading;
            status = JoinText.Status(JoinStageKind.Loading);
            object data = saveData;
            saveData = null;
            try { world.StartLoad(data, header); }
            catch (LoadRefused e)
            {
                // The game side refused before anything loaded, with its own code (nothing to cancel, but no harm).
                try { world.CancelLoad(); } catch (Exception) { }
                Refuse(e.Code >= 1 && e.Code <= 99 ? e.Code : JoinCodes.LoadThrew, e.Message);
            }
            catch (Exception e)
            {
                // Left "loading", the heartbeats would hold the host's pause until the total deadline: a load failure instead.
                try { world.CancelLoad(); } catch (Exception) { }
                Refuse(JoinCodes.LoadThrew, "the load could not start (" + e.GetType().Name + ")");
            }
        }

        void Beat(int stage, long now)
        {
            beatMs = now;
            SendOrLose(new JoinStage { Stage = stage });
        }

        // A Paused or a print that throws is "no print": the safe outcome, a full rejoin.
        void TrySettle()
        {
            if (settleTaken) return;
            bool paused;
            try { paused = world.Paused; }
            catch (Exception) { paused = false; }
            if (!paused) return;
            settleTaken = true;
            settled = TakePrint();
            Log.Info(settled != null ? "join: drop settled, print taken" : "join: drop settled, but no print could be taken");
        }

        Fingerprint TakePrint()
        {
            try { return world.TakeFingerprint(); }
            catch (Exception) { return null; }
        }

        // A player who closed the game and came back with its rejoin key may have started it with another agency: the
        // seat it holds is the only one it can have, so it is claimed (WantClaim sends it on the next update).
        void TakeKeptSeat(string agency)
        {
            if (!JoinCheck.IsAgencyId(agency)) return;
            try { world.SetAgency(agency); }
            catch (Exception e) { Log.Warn("join: the kept agency could not be set: " + e.GetType().Name); return; }
            Log.Info("join: the host keeps " + agency + " for this player; claiming it");
            status = JoinText.TakingKeptSeat(agency);
        }

        // world.Agency, or null when it throws (then nothing is claimed; the host's claim deadline decides).
        string AgencyNow()
        {
            try { return world.Agency; }
            catch (Exception) { return null; }
        }

        // A rejoin of a process still in the session: the print now against the settled one (R-1), then the offer.
        void Offer()
        {
            phase = Phase.Offer;
            long t0 = Stopwatch.GetTimestamp();
            Fingerprint now = TakePrint();
            string difference = "no settled print";
            bool held = settled != null && now != null && FingerprintRules.Same(settled, now, out difference);
            Log.Info("join: rejoin offer (in session true, self-check " + (held ? "held" : "changed: " + difference) + ", fingerprint " + Ms(t0) + " ms)");
            SendOrLose(new RejoinOffer { InSession = true, SelfCheckHeld = held, DateTicks = world.GameTicks,
                NextDay = world.NextDay, Print = now, LastResult = LastApplied() });
        }

        // ---- endings ----

        // The host said goodbye (code: why, 0 when it did not say). A join that has not loaded goes back to the menu
        // with the host's code (HostLeft when none); a load is cancelled first, so nothing is extracted. A loaded game
        // (in game, dropped or offering a rejoin) goes back to the menu the same way (the user's decision: no paused
        // game left open, nothing to reconnect to), as does one on its way from a running game to the menu for a full
        // rejoin (ToMenu: the player was in a game). Told why in its own words (JoinText.GoodbyeInGame, by the code:
        // the generic refusal texts are for a join, and "pick another agency" would be wrong here). The host's words
        // stay in the log and the client's own status line, not repeated as a report line.
        void HostLeft(string why, int code)
        {
            Log.Info("join: the host said goodbye (code " + code + ", " + why + "); no reconnect");
            shouldRetry = false;
            report = null;
            int back = code > 0 ? code : JoinCodes.HostLeft;
            if (InGame || phase == Phase.ToMenu)
            {
                // Failed ends the attempt: no settle print, no drop state, no retry for the next Update to find
                End(Phase.Failed, false);
                status = JoinText.GoodbyeInGame(code);
                BackToMenu(back, "the host left: " + why, true, true);
                return;
            }
            if (phase == Phase.Loading)
            {
                Abort("the host left while loading: " + why, false, Phase.Failed, false, back);
                return;
            }
            End(Phase.Failed, false);
            status = JoinText.Refused(back);
            BackToMenu(back, "the host left: " + why, false, true);
        }

        // The host refused (JoinRefused). Nothing to tell it; no retry. In game this is not the join's business.
        void HostRefused(int code, string[] lines)
        {
            if (phase == Phase.InGame || phase == Phase.Dropped) return;
            string reason = lines != null && lines.Length > 0 ? string.Join("\n", lines) : JoinText.Refused(code);
            report = null;
            endCode = code;
            endedHere = false;
            if (lines != null && lines.Length > 0)
            {
                // kept cleaned for the screen (ScreenLines shows the first few and counts the rest)
                report = new string[lines.Length];
                for (int i = 0; i < lines.Length; i++)
                {
                    report[i] = TextSanitiser.Clean(lines[i], JoinLimits.MaxReportLineChars, "");
                    Log.Info("join: refused by the host (" + code + "): " + report[i]);
                }
            }
            else Log.Info("join: refused by the host (" + code + ")");
            // the host's own code, not "cancelled": the player is told why
            if (phase == Phase.Loading)
            {
                Abort("the host refused or left while loading (" + code + ")", false, Phase.Failed, false, code);
                return;
            }
            End(Phase.Failed, false);
            status = JoinText.Refused(code);
            BackToMenu(code, reason);
        }

        // A wall or a deadline before the load (or a failure the load reported): the host hears Failed(code), the game goes back to
        // the menu with the reason, and the attempt is over (no retry). The attempt ends BEFORE the game is called, so a game call
        // that throws can never leave a phase behind.
        void Refuse(int code, string why)
        {
            string clean = TextSanitiser.Clean(why, 300, "");
            Log.Info((header != null ? "join: refused the snapshot (" : "join: could not join (") + code + "): " + clean);
            Send(new JoinStage { Stage = JoinStageKind.Failed, Code = code });
            endCode = code;
            endedHere = true;
            End(Phase.Failed, false);
            status = JoinText.Refused(code);
            BackToMenu(code, clean);
        }

        // After StartLoad and before Loaded: the coroutine is cancelled first (it never extracts after an abort), then back to the menu.
        // code: what the player is told (Aborted, unless the link ended the join for good).
        void Abort(string why, bool tellHost, Phase to, bool retry, int code = JoinCodes.Aborted)
        {
            string clean = TextSanitiser.Clean(why, 300, "");
            Log.Info("join: load aborted: " + clean);
            try { world.CancelLoad(); }
            catch (Exception e) { Log.Warn("join: CancelLoad threw " + e.GetType().Name); }
            if (tellHost) Send(new JoinStage { Stage = JoinStageKind.Failed, Code = JoinCodes.Aborted });
            if (tellHost) { endCode = JoinCodes.Aborted; endedHere = true; }
            End(to, retry);
            status = JoinText.Refused(code);
            BackToMenu(code, clean);
        }

        // Every return to the menu. When no reconnect is due the session ends with it; with one due (a load
        // aborted by a lost link) the session carries on and the reconnect loads again.
        void BackToMenu(int code, string reason, bool inGame = false, bool goodbye = false)
        {
            if (!shouldRetry)
            {
                backCode = code;
                backInGame = inGame;
                backByHost = goodbye;
            }
            try { world.BackToMenu(code, reason); }
            catch (Exception e) { Log.Warn("join: BackToMenu threw " + e.GetType().Name); }
            if (shouldRetry) return;
            try { world.SessionOver(); }
            catch (Exception e) { Log.Warn("join: SessionOver threw " + e.GetType().Name); }
        }

        void Timeout(string why)
        {
            if (phase == Phase.Loading) Abort(why, true, Phase.Failed, false);
            else Refuse(JoinCodes.JoinerTimeout, why);
        }

        void End(Phase to, bool retry)
        {
            Forget();
            if (to == Phase.Failed) reloadCode = 0;
            phase = to;
            shouldRetry = retry;
        }

        // Drops everything of the current attempt (the worker's late result included) and disarms the sink.
        void Forget()
        {
            if (walls != null) walls.Abandoned = true;   // the worker skips (or stops) the item and drops its buffer
            walls = null;
            saveData = null;
            header = null;
            waitingForBatch = batchUp = false;
            Interlocked.Exchange(ref claimOwed, 0);
            if (receiver != null) receiver.Reset();
            chunksSeen = 0;
        }

        // ---- sending ----

        bool Send(Message m)
        {
            Func<Message, bool> s = send;
            if (s == null) return false;
            try { return s(m); }
            catch (Exception) { return false; }
        }

        // Send's result is checked. A message that cannot go out means the link is gone: the lost-link path, never ignored.
        bool SendOrLose(Message m)
        {
            if (Send(m)) return true;
            Lost("a " + m.GetType().Name + " could not be sent");
            return false;
        }

        // ---- small helpers ----

        void LogSummary(ContentSummary s)
        {
            byte[] body; string why;
            int bytes = Envelope.TryEncode(s, 0, out body, out why) ? body.Length : -1;
            Log.Info("join: summary sent (" + bytes + " bytes, " + (s.Plugins != null ? s.Plugins.Length : 0) + " plugins, " + (s.Assemblies != null ? s.Assemblies.Length : 0)
                + " assemblies, managed " + Short(s.ManagedHash) + ", overall " + Short(s.Overall) + ")");
        }

        static string Short(string hash) { return hash != null && hash.Length > 16 ? hash.Substring(0, 16) : hash ?? "none"; }

        static string HostName(JoinHeader h)
        {
            if (h.Seats != null)
                foreach (AgencySeat s in h.Seats)
                    if (s != null && s.Player == 0) return s.Name;
            return null;
        }

        static long Ms(long startTimestamp) { return (Stopwatch.GetTimestamp() - startTimestamp) * 1000 / Stopwatch.Frequency; }
    }
}
