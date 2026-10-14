using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Every client's driver for commands (plan Task 7b): it sends this player's requests within the send budget,
    // takes the host's stream in order, applies each entry through IActionWorld when its day comes, and holds the day
    // gate. It knows no Unity and no game class; the world and the link are handed in, so every path is tested here.
    //
    // Anything the client cannot make sense of in the stream (a gap, a bad restore, a print that stays different, a
    // landing that never comes) is OutOfStep, never a guess: the session then rejoins fully (DD7). Once out of step
    // the stream is ignored until the next AgencyState.
    public sealed partial class CommandClient
    {
        sealed class Pending
        {
            public ActionRequest Request;
            public string Key;      // the set key, null for a do: the budget frees it when this is answered
            public long SentMs;
        }

        readonly IActionWorld world;
        readonly Func<Message, bool> sendToHost;
        readonly ResultOrder order;
        readonly DayGate gate;
        readonly ClientSendBudget budget;
        readonly Func<long> nowMs;

        // requests sent and not yet answered, by request number
        readonly Dictionary<uint, Pending> pending = new Dictionary<uint, Pending>();
        // the objects asked for on the day being closed: an answer for any other is not ours, and one still different
        // after its restore is a real difference
        readonly HashSet<string> fetched = new HashSet<string>(StringComparer.Ordinal);
        // the prints of the day's close that differ (indexes), so each close is compared once
        readonly int[] differing = new int[ActionLimits.MaxPrintsPerDay];
        // when the last ObjectRequests went out: the host answers more than ObjectRequestsPerMinute a minute with
        // TooMany, and a flood of them would trip its connection limiter and close an honest client
        readonly long[] askedAt = new long[ActionLimits.ObjectRequestsPerMinute];
        int askedNext;
        Action<string> sink;

        uint lastReq;
        bool inStream, outOfStep, applying, holding;
        int applyingKind = -1;
        ActionRequest applyingEntry;
        long applyingAt = long.MinValue;
        long waitingFor = StepCut.None;
        string outWhy;
        int ignored, fetching;
        long checkedDay = -1, lastDay = long.MinValue;
        string refusedText;
        long refusedAt;

        public CommandClient(IActionWorld world, Func<Message, bool> sendToHost, ResultOrder order, DayGate gate,
                             ClientSendBudget budget, Func<long> nowMs)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (sendToHost == null) throw new ArgumentNullException("sendToHost");
            if (order == null) throw new ArgumentNullException("order");
            if (gate == null) throw new ArgumentNullException("gate");
            if (budget == null) throw new ArgumentNullException("budget");
            if (nowMs == null) throw new ArgumentNullException("nowMs");
            this.world = world;
            this.sendToHost = sendToHost;
            this.order = order;
            this.gate = gate;
            this.budget = budget;
            this.nowMs = nowMs;
            ForgetAsks();
        }

        void ForgetAsks()
        {
            for (int i = 0; i < askedAt.Length; i++) askedAt[i] = long.MinValue;
            askedNext = 0;
        }

        // This player's id on the host, as the join gave it (1 or more; 0 is the host's own entries and means unset):
        // an entry is mine only when it carries it (another player's request can have the same number).
        public int Player { get; set; }

        // Where the log lines go instead of the mod log (tests read them there).
        public void LogTo(Action<string> lines) { sink = lines; }

        bool Logging { get { return sink != null || Log.CurrentFile != null; } }

        void Say(string line)
        {
            if (sink != null) sink(line); else Log.Info(line);
        }

        // The session takes the full-rejoin path (DD7) when this is set. It stays set, through a lost link and a new
        // AgencyState, until FullRejoinStarted: a reconnect alone cannot look clean.
        public bool OutOfStep { get { return outOfStep; } }
        public string OutOfStepWhy { get { return outWhy; } }
        public uint LastApplied { get { return order.LastApplied; } }
        public bool Applying { get { return applying; } }
        // What Applying is applying, for a hold that must tell one kind from another (M7): the ActionKind and the
        // request, or -1 and null when nothing is, or in a restore (no request).
        public int ApplyingKind { get { return applyingKind; } }
        public ActionRequest ApplyingEntry { get { return applyingEntry; } }
        // The game time the host applied the entry being applied at (ItemIdGaps); long.MinValue outside one.
        public long ApplyingAtTicks { get { return applyingAt; } }
        // HostTick: an ordinary entry waits until this game's clock reaches the host's tick for it. The game glue turns
        // it on; off, an entry applies as soon as its day comes (the tests that drive the stream without a clock).
        public bool ApplyAtHostTick { get; set; }
        // The glue: the last fixed step was not taken (a hold or a parked step), so no entry waits for the clock.
        public bool Standing { get; set; }
        // The game time the head entry waits for, for the step's cut (StepCut.None when nothing waits).
        public long WaitingFor { get { return waitingFor; } }
        // Messages of the stream that came while no stream was running (before the first AgencyState, after a lost
        // link or out of step): counted, never applied.
        public int Ignored { get { return ignored; } }
        // The stream began (or began again) at this AgencyState: the session applies its money and counters.
        public event Action<AgencyState> StreamStarted;
        // An entry was applied (the glue writes the stream's comparison lines and refreshes what depends on it).
        public event Action<ActionResult> EntryApplied;

        // The status line: the last refusal for NoticeShowMs, else "waiting" once a request has been out for
        // PendingNoticeMs; null when there is nothing to say.
        public string Status
        {
            get
            {
                long now = nowMs();
                if (refusedText != null && now - refusedAt < ActionLimits.NoticeShowMs) return refusedText;
                if (pending.Count > 0 && now - OldestSent() > ActionLimits.PendingNoticeMs)
                    return CommandText.Waiting();
                return null;
            }
        }

        long OldestSent()
        {
            long oldest = long.MaxValue;
            foreach (Pending p in pending.Values) if (p.SentMs < oldest) oldest = p.SentMs;
            return oldest;
        }

        // The latest value of a set action not yet answered, so a window that keeps being clicked builds on it (DD1).
        public bool TryPendingValue(string key, out int value) { return budget.TryPendingValue(key, out value); }

        // ---- this player's requests ----

        // One click of the player, as the window saw it. Nothing is applied here: the host's entry does that.
        public void Click(ActionRequest a)
        {
            if (a == null) return;
            long now = nowMs();
            if (!inStream || suspended) { Refuse(RefuseCodes.NotInGame); return; }
            // a request the host would refuse as a violation never goes out (N5)
            if (a.Validate() != null) { Refuse(RefuseCodes.CannotSend); return; }
            // a finished mission is about 30 entries of M5's size: the host's bucket is this one's twin
            bool flight = ActionKind.IsFlight(a.Kind);
            if (flight && !flightBucket.Has(1, now)) { Refuse(RefuseCodes.TooMany); return; }
            string key = ActionShapes.Key(a);
            if (key == null)
            {
                if (IsRepeat(a, now))
                {
                    if (Logging) Say("command: repeat click dropped");
                    return;
                }
                if (pending.Count >= ActionLimits.MaxPendingPerClient) { Refuse(RefuseCodes.TooMany); return; }
            }
            // numbered for its size only: the number is spent when it is really sent. A request over its payload
            // cap cannot get here (Validate holds every field to its cap), but is charged the whole burst if one did
            a.Req = lastReq + 1;
            int size = EncodedSize(a);
            int verdict = budget.Offer(a, size, now);
            if (verdict == LimitVerdict.Refused) { Refuse(RefuseCodes.TooMany); return; }
            if (flight) flightBucket.Take(1, now);
            if (verdict == LimitVerdict.Now) Dispatch(a, null, now);
        }

        static int EncodedSize(ActionRequest a)
        {
            byte[] body;
            string error;
            return Envelope.TryEncode(a, a.Req, out body, out error) ? body.Length : ActionLimits.ClientBytesBurst;
        }

        void Refuse(int code)
        {
            refusedText = CommandText.Refused(code);
            refusedAt = nowMs();
            RaiseRefusal(code);
        }

        // The player is shown every refusal of their own request in the game's dialog (the session's), not only on
        // the status line; a listener that throws must not stop the stream.
        void RaiseRefusal(int code)
        {
            Action<int> h = Refusal;
            if (h == null) return;
            try { h(code); }
            catch (Exception e) { if (Logging) Say("command: the refusal listener threw " + e.GetType().Name); }
        }

        // A refusal of this player's request, local or the host's (never Superseded): its RefuseCodes code.
        public event Action<int> Refusal;

        // A second identical do while the first is pending and still fresh: a double click must not act twice.
        bool IsRepeat(ActionRequest a, long now)
        {
            foreach (Pending p in pending.Values)
                if (p.Key == null && now - p.SentMs < ActionLimits.RepeatClickMs && Same(a, p.Request)) return true;
            return false;
        }

        static bool Same(ActionRequest a, ActionRequest b)
        {
            if (a.Kind != b.Kind || a.Agency != b.Agency || a.Body != b.Body || a.ToBody != b.ToBody
                || a.Item != b.Item || a.Count != b.Count || a.ItemId != b.ItemId || a.Position != b.Position
                || a.ToPosition != b.ToPosition || a.Value != b.Value || a.Amount != b.Amount || a.Group != b.Group
                || a.Ship != b.Ship) return false;
            return SameIds(a.Ids, b.Ids) && SameTargets(a.Targets, b.Targets);
        }

        static bool SameIds(string[] x, string[] y)
        {
            int nx = x == null ? 0 : x.Length, ny = y == null ? 0 : y.Length;
            if (nx != ny) return false;
            for (int i = 0; i < nx; i++) if (x[i] != y[i]) return false;
            return true;
        }

        static bool SameTargets(TargetRow[] x, TargetRow[] y)
        {
            int nx = x == null ? 0 : x.Length, ny = y == null ? 0 : y.Length;
            if (nx != ny) return false;
            for (int i = 0; i < nx; i++)
                if (x[i].Body != y[i].Body || x[i].Ratio != y[i].Ratio || x[i].Count != y[i].Count) return false;
            return true;
        }

        // Numbers the request for real, records it pending and sends it. A link that takes nothing frees it again.
        void Dispatch(ActionRequest a, string key, long now)
        {
            a.Req = ++lastReq;
            if (key == null) key = ActionShapes.Key(a);
            pending[a.Req] = new Pending { Request = a, Key = key, SentMs = now };
            if (sendToHost(a)) return;
            pending.Remove(a.Req);
            budget.Answered(key);
            Refuse(RefuseCodes.NotInGame);
        }

        // The window's latest set value goes out as the budget allows (at most SetPerSecond in all).
        void SendDueSets(long now)
        {
            if (pending.Count >= ActionLimits.MaxPendingPerClient) return;
            ActionRequest a = budget.TakeDue(now);
            if (a != null) Dispatch(a, null, now);
        }

        // ---- what the host sends ----

        // A message of the host's stream. A string comes back only for a message the caller must close the link on.
        public string Received(Message m)
        {
            if (m == null) return "an empty message";
            var st = m as AgencyState;
            if (st != null) return StartStream(st);
            var clock = m as Clock;
            if (clock != null)
            {
                // the gate's overdue checks are on the host's own clock, so every one is fed to it
                if (inStream) { gate.HostAt(TicksOf(clock.Game), nowMs()); NoteClock(clock); }
                return null;
            }
            if (m is ActionRequest || m is ObjectRequest || m is HealRequest || m is ResumeProgress)
                return m.GetType().Name + " is not sent to a client";
            if (m is HealData) return HealReceived((HealData)m);
            if (m is ResumeCheck) return null;   // the glue answers it (Answer)
            if (!inStream)
            {
                // A reset before this client's stream began is the host saying that stream will never start (its
                // AgencyState could not be made, or a load ended the join batch): the host sends it only to the
                // members it was about to start, so waiting on would leave this game outside the stream for ever.
                // Out of step, so the full rejoin follows (plan DD7: a StreamReset is out of step; "StreamReset
                // { Code = HostFault }, so every client rejoins fully rather than sitting on a gap").
                var early = m as StreamReset;
                if (early != null && !outOfStep)
                {
                    Fail("the host reset the stream before this client's began (code " + early.Code
                        + ")");
                    return null;
                }
                if (m is ActionResult || m is ActionRefused || m is DayClosed || m is ObjectData
                    || m is StreamReset) ignored++;
                return null;
            }
            string why = null;
            int verdict = OrderVerdict.Ok;
            var result = m as ActionResult;
            if (result != null)
            {
                long local;
                if (!DayKey.TryCurrent(world.NextDayTicks, world.OriginTicks, out local))
                {
                    Fail("the client's own day has no key");
                    return null;
                }
                verdict = order.Accept(result, local, out why);
            }
            var refused = m as ActionRefused;
            if (refused != null) { Refused(refused); return null; }
            var closed = m as DayClosed;
            if (closed != null) verdict = order.Accept(closed, out why);
            var data = m as ObjectData;
            if (data != null)
            {
                if (!Asked(data))
                {
                    Fail("an object nobody asked for: kind " + data.Kind + " day " + data.Day);
                    return null;
                }
                if (data.Code != ObjectCodes.Ok && Logging)
                    Say("command: object " + KindName(data.Kind) + " " + data.Agency + " " + data.Body + " "
                        + CodeName(data.Code));
                verdict = order.Accept(data, out why);
            }
            var reset = m as StreamReset;
            if (reset != null) verdict = order.Accept(reset, out why);
            if (verdict != OrderVerdict.Ok) Fail(why);
            else if (result != null)
            {
                NoteArrived(result);
                Accepted(result);
            }
            return null;
        }

        // The entry answering one of this player's requests has arrived: the host accepted it. It applies on its own
        // day, which a game paused by its own planner does not reach until the planner closes.
        void Accepted(ActionResult entry)
        {
            Pending mine;
            if (entry.Req == 0 || entry.Player <= 0 || entry.Player != Player) return;
            if (!pending.TryGetValue(entry.Req, out mine)) return;
            Action<ActionRequest> accepted = RequestAccepted;
            if (accepted != null) accepted(mine.Request);
        }

        // One of this player's requests was accepted (its entry arrived, not yet applied).
        public event Action<ActionRequest> RequestAccepted;

        static long TicksOf(double gameSeconds)
        {
            double t = gameSeconds * 1e7;
            if (!(t > 0)) return 0;
            return t >= long.MaxValue ? long.MaxValue : (long)t;
        }

        string StartStream(AgencyState st)
        {
            string bad = st.Validate();
            if (bad != null) return bad;
            // out of step is latched until the glue's FullRejoinStarted: a stray AgencyState must not quietly undo it
            if (outOfStep)
            {
                ignored++;
                return null;
            }
            if (ignored > 0 && Logging) Say("command: ignored " + ignored + " messages before the first AgencyState");
            ignored = 0;
            order.Reset(st.Day, st.NextSeq);
            gate.Reset();
            ForgetEvents();
            WorldReset(true);
            inStream = true;
            ClearDay();
            lastDay = long.MinValue;
            // the requests still pending were sent before the batch and are answered after it: they stay (DD6)
            Action<AgencyState> started = StreamStarted;
            if (started != null) started(st);
            return null;
        }

        void ClearDay()
        {
            fetched.Clear();
            fetching = 0;
            checkedDay = -1;
            holding = false;
        }

        void Refused(ActionRefused r)
        {
            Pending p;
            if (!pending.TryGetValue(r.Req, out p)) return;   // after a lost link: nothing waits for it
            pending.Remove(r.Req);
            budget.Answered(p.Key);
            if (r.Code == RefuseCodes.Superseded) return;   // a newer value replaced it: nothing to tell the player
            refusedText = CommandText.Refused(r.Code);
            refusedAt = nowMs();
            if (Logging) Say("command: req " + r.Req + " refused " + RefuseCodes.Name(r.Code));
            RaiseRefusal(r.Code);
            Action<ActionRequest> refusedRequest = RequestRefused;
            if (refusedRequest != null) refusedRequest(p.Request);
        }

        // One of this player's requests was refused by the host (not replaced by a newer one): the planner that waits
        // for a flight stops waiting (M6 Task 11).
        public event Action<ActionRequest> RequestRefused;

        static string KindName(int kind)
        {
            switch (kind)
            {
                case ObjectKind.Research: return "Research";
                case ObjectKind.SiteBuilds: return "SiteBuilds";
                case ObjectKind.SiteFacilities: return "SiteFacilities";
                case ObjectKind.Missions: return "Missions";
                default: return "Kind" + kind;
            }
        }

        static string CodeName(int code)
        {
            switch (code)
            {
                case ObjectCodes.Ok: return "Ok";
                case ObjectCodes.TooBig: return "TooBig";
                case ObjectCodes.TooMany: return "TooMany";
                case ObjectCodes.NoSuchObject: return "NoSuchObject";
                default: return "Code" + code;
            }
        }

        bool Asked(ObjectData d)
        {
            return fetching > 0 && fetched.Contains(FetchKey(d.Day, d.Kind, d.Agency, d.Body));
        }

        static string FetchKey(long day, int kind, string agency, int body)
        {
            return day + "|" + kind + "|" + agency + "|" + body;
        }

        void Fail(string why)
        {
            if (outOfStep) return;
            outOfStep = true;
            outWhy = why;
            inStream = false;
            running = null;
            // the requests sent will never be answered in this stream: the player is told, not left on "waiting"
            if (pending.Count > 0)
            {
                pending.Clear();
                refusedText = CommandText.OutOfStep();
                refusedAt = nowMs();
            }
            string line = "command: out of step: " + why + "; full rejoin";
            if (sink != null) sink(line); else Log.Warn(line);
        }

        // The link dropped: nothing pending will be answered, and the stream stays off until the next AgencyState. Out
        // of step is NOT cleared by a reconnect (a new link alone cannot make the game agree with the host again); the
        // object-request count is kept too, as the host keeps its own across links.
        public void LinkLost()
        {
            pending.Clear();
            budget.Clear();
            gate.Reset();
            ForgetEvents();
            WorldReset(true);
            inStream = false;
            ClearDay();
            lastDay = long.MinValue;
        }

        // Something the glue found outside the stream (a day with no key, an impact nobody announced, a day event the
        // gate never allowed): out of step, as the stream's own anomalies are.
        public void OutOfStepNow(string why) { Fail(why ?? "the game left the stream"); }

        // The glue's explicit start of a full rejoin (a new snapshot is being loaded): the only way out of OutOfStep.
        public void FullRejoinStarted()
        {
            LinkLost();
            outOfStep = false;
        }

        // ---- the fixed step ----

        // Applies what is due for localDay, sends due set requests and checks the deadlines.
        public void Update(long localDay)
        {
            // the world's own apply calling back into the step would apply the same entry twice
            if (!inStream || applying) return;
            waitingFor = StepCut.None;
            long now = nowMs();
            if (localDay != lastDay)
            {
                // a crossing: no hold, fetch or wait of the old day goes on, and none of its lateness with it. The
                // first step of a stream is no crossing: a MayCross before it may already have asked for an object
                if (lastDay != long.MinValue)
                {
                    gate.Released(now);
                    ClearDay();
                }
                lastDay = localDay;
            }
            if (order.Behind(localDay)) { Fail("the client's day " + localDay + " is outside the stream"); return; }
            // an apply that goes on over frames holds the stream until it is done
            if (running != null) PollRunning(now);
            if (!inStream) return;
            if (running == null) ApplyDue(localDay);
            if (!inStream) return;
            long next = world.NextDayTicks;
            // while catching up the host's clock is paused far ahead: only the resume's own deadline counts (M7 DD-W12)
            if (!catchingUp && gate.FetchOverdue(now)) { Fail("a fetched object did not arrive in time"); return; }
            if (!catchingUp && holding && gate.CloseOverdue(next, now))
            {
                Fail("day " + lastDay + " was never closed by the host");
                return;
            }
            WorldUpdate(localDay, now);
            if (!inStream) return;
            SendDueSets(now);
        }

        void ApplyDue(long localDay)
        {
            Message m;
            while (inStream && running == null && (m = order.NextDue(localDay)) != null)
            {
                var data = m as ObjectData;
                if (data != null) { Restore(data); continue; }
                var entry = (ActionResult)m;
                if (entry.Action == null) { Fail("an entry with no action"); return; }
                // the year's growth is applied inside the client's own year event (ApplyInEventNow), never here
                if (entry.Action.Kind == ActionKind.PopulationGrowth) { YearNotTaken(entry, localDay); return; }
                // a landing entry (an impact's 59, a pull's 72) is applied inside the client's own landing step
                // (ApplyImpactNow), and a checked launch's outcome inside its launch step (ApplyLaunchNow), never
                // here; it and every entry behind it wait
                if (FlightEvents.IsLanding(entry.Action) || FlightEvents.IsLaunch(entry.Action)) return;
                // the craft a landing built is released by this game's own landing of that mission
                if (entry.Action.Kind == ActionKind.LandingCraft && !LandingRecorded(entry)) return;
                if (WaitsForHostTick(entry)) return;
                ApplyEntry(entry);
            }
        }

        bool WaitsForHostTick(ActionResult entry)
        {
            if (!ApplyAtHostTick) return false;
            long next = world.NextDayTicks;
            if (!HostTick.Waits(world.GameTicks, entry.AtTicks, next, Standing)) return false;
            waitingFor = HostTick.Until(entry.AtTicks, next);
            return true;
        }

        // runBranch is only given for a landing or a launch (CommandClientImpact.cs, CommandClientFlights.cs): the
        // world runs it inside the apply. inStep: the apply must finish inside the call (an entry applied beside a
        // landing). True when the entry was applied, or goes on over frames; false after Fail.
        bool ApplyEntry(ActionResult entry, Func<string> runBranch = null, bool inStep = false)
        {
            // a new one each time: the world may keep what it was given, and an entry without counters or ids must
            // not see the last entry's
            string badOrigin = OriginProblem(entry);
            if (badOrigin != null) { Fail("#" + entry.Seq + ": " + badOrigin); return false; }
            ApplyNumbers numbers = NumbersOf(entry);
            // the branch is wrapped so that the driver can tell a world that never ran it, or whose branch said no (in
            // its own object: a closure here would allocate on every entry)
            BranchRun branch = null;
            if (runBranch != null)
            {
                branch = new BranchRun(runBranch);
                numbers.RunBranch = branch.Run;
            }
            ApplyOutcome outcome;
            applying = true;
            applyingKind = entry.Action.Kind;
            applyingEntry = entry.Action;
            applyingAt = entry.AtTicks;
            try { outcome = world.Apply(entry.Action, numbers); }
            catch (Exception e)
            {
                Log.Error("command: applying #" + entry.Seq + " threw: " + ExceptionText.Chain(e));
                Fail("applying #" + entry.Seq + " threw " + e.GetType().Name);
                return false;
            }
            finally
            {
                applying = false;
                applyingKind = -1;
                applyingEntry = null;
                applyingAt = long.MinValue;
                if (branch != null) branch.Close();   // a branch kept past the apply must not run the game again
            }
            if (outOfStep) return false;   // something inside the apply (a nested landing) already failed it
            if (outcome != null && outcome.Deferred && outcome.Code == 0)
            {
                // the world goes on over frames: the stream holds here until Poll gives the outcome
                if (inStep || branch != null || Flights == null)
                {
                    Fail("applying #" + entry.Seq + " went on over frames where it must finish at once");
                    return false;
                }
                BeginRunning(entry);
                return true;
            }
            return Complete(entry, outcome, branch);
        }

        // The host's numbers for the world: an entry that carries market ops, a rival's steps or a 92's chain gets M7's
        // WorldNumbers (m7-host's subclass), every other one the plain ApplyNumbers, so an ordinary entry allocates no
        // more than before (CommandClientAllocTests).
        static ApplyNumbers NumbersOf(ActionResult entry)
        {
            ApplyNumbers n;
            if (entry.Ops != null || entry.Steps != null || entry.Chain != null)
                n = new WorldNumbers { Ops = entry.Ops, Steps = entry.Steps, Chain = entry.Chain };
            else n = new ApplyNumbers();
            n.Counters = entry.Counters;
            n.NewIds = entry.NewIds;
            n.Draws = entry.Draws;
            n.After = entry.After;
            n.SystemName = entry.SystemName;
            n.Spawns = entry.Spawns;
            n.Pull = entry.Pull;
            return n;
        }

        // The tail of an apply, at once or after the frames it took: the outcome is checked, the entry counted as
        // applied, the request it answers released.
        bool Complete(ActionResult entry, ApplyOutcome outcome, BranchRun branch)
        {
            if (outcome == null || outcome.Code != 0)
            {
                Fail("applying #" + entry.Seq + " gave code " + (outcome == null ? -1 : outcome.Code));
                return false;
            }
            if (outcome.Deferred) { Fail("applying #" + entry.Seq + " was deferred again"); return false; }
            if (outcome.PaymentFailed) { Fail("the payment of #" + entry.Seq + " did not go through"); return false; }
            if (branch != null && (!branch.Ran || branch.Said != null))
            {
                Fail("the impact of #" + entry.Seq
                    + (branch.Ran ? " failed: " + branch.Said : " was never run by the world"));
                return false;
            }
            if (branch != null && branch.Misused != null)
            {
                Fail("the impact of #" + entry.Seq + ": " + branch.Misused);
                return false;
            }
            order.Applied(entry);
            Pending mine;
            // another player's entry can carry the same request number: the player tells them apart
            if (entry.Req != 0 && entry.Player > 0 && entry.Player == Player && pending.TryGetValue(entry.Req, out mine))
            {
                pending.Remove(entry.Req);
                budget.Answered(mine.Key);
                if (Logging) Say("command: req " + entry.Req + " answered by #" + entry.Seq);
            }
            if (Logging)
            {
                Say("command: applied #" + entry.Seq + " day " + entry.Day + " at " + world.GameTicks + " (host "
                    + entry.AtTicks + ")");
                LogWorldEntry(entry);
            }
            Action<ActionResult> applied = EntryApplied;
            if (applied != null) applied(entry);
            return true;
        }

        void Restore(ObjectData first)
        {
            ObjectData[] pieces = order.PiecesOf(first);
            string bad;
            // a restore is the world changing under the host's numbers, like an apply: the capture hooks let it by
            applying = true;
            try { bad = world.Restore(pieces.Length == 1 ? first : Merge(pieces)); }
            catch (Exception e)
            {
                Log.Error("command: restoring threw: " + ExceptionText.Chain(e));
                bad = "restoring threw " + e.GetType().Name;
            }
            finally { applying = false; }
            if (bad != null) { Fail("restoring " + first.Agency + " " + first.Body + ": " + bad); return; }
            order.Applied(first);
            if (fetching > 0) fetching--;
            gate.FetchEnded();
            if (Logging)
                Say("command: object " + KindName(first.Kind) + " " + first.Agency + " " + first.Body
                    + " restored after #" + first.AfterSeq);
        }

        // The host splits a large object into pieces by rows; the world restores it whole.
        static ObjectData Merge(ObjectData[] ps)
        {
            ObjectData f = ps[0];
            var m = new ObjectData { Code = ObjectCodes.Ok, Day = f.Day, AfterSeq = f.AfterSeq, Piece = 0, Pieces = 1,
                Kind = f.Kind, Agency = f.Agency, Body = f.Body, Research = f.Research };
            if (f.Builds != null)
            {
                var building = new List<BuildRow>();
                var queue = new List<QueueRow>();
                foreach (ObjectData p in ps)
                {
                    if (p.Builds.Building != null) building.AddRange(p.Builds.Building);
                    if (p.Builds.Queue != null) queue.AddRange(p.Builds.Queue);
                }
                m.Builds = new SiteBuildState { Counters = f.Builds.Counters, Building = building.ToArray(),
                    Queue = queue.ToArray() };
            }
            if (f.Facilities != null)
            {
                var built = new List<FacilityRow>();
                var mining = new List<string>();
                foreach (ObjectData p in ps)
                {
                    if (p.Facilities.Built != null) built.AddRange(p.Facilities.Built);
                    if (p.Facilities.Mining != null) mining.AddRange(p.Facilities.Mining);
                }
                m.Facilities = new SiteFacilityState { Built = built.ToArray(), Mining = mining.ToArray() };
            }
            return m;
        }

        // ---- the day gate ----

        // May the client run the day event that ends `day`? The prints are compared first, once the day is closed
        // and every entry up to its close is applied; a difference asks the host for its copy and holds.
        public bool MayCross(long day)
        {
            if (gate.Disabled) return true;
            if (!inStream) return false;
            long now = nowMs();
            DayClosed close = order.ClosedFor(day);
            if (close != null && order.LastApplied >= close.LastSeq && fetching == 0
                && checkedDay != day)
            {
                Compare(day, close, now);
                if (!inStream) return false;
            }
            if (gate.MayCross(day) && !HealHolds(day, now))
            {
                gate.Released(now);
                holding = false;
                return true;
            }
            gate.Holding(day, now);
            holding = true;
            return false;
        }

        // The day's prints against the host's. Every difference is one ObjectRequest, and the host answers more than
        // ObjectRequestsPerMinute a minute with TooMany (out of step); a burst of them would also trip its limiter on
        // this connection and close an honest client. So when more objects differ than the minute still allows,
        // nothing is sent and the client is out of step at once.
        void Compare(long day, DayClosed close, long now)
        {
            PrintEntry[] prints = close.Prints;
            int n = prints == null ? 0 : prints.Length;
            if (n > differing.Length)
            {
                Fail("day " + day + " closed with " + n + " prints, more than " + differing.Length);
                return;
            }
            int diffs = 0;
            for (int i = 0; i < n; i++)
            {
                PrintEntry p = prints[i];
                string mine;
                try { mine = world.PrintOf(p.Kind, p.Agency, p.Body); }
                catch (Exception e)
                {
                    Fail("printing " + KindName(p.Kind) + " " + p.Agency + " " + p.Body + " threw "
                        + e.GetType().Name);
                    return;
                }
                if (mine == p.Print) continue;
                // the host keeps no copy of this kind to send (the missions): a difference is a real one
                if (!ObjectKind.Fetchable(p.Kind))
                {
                    Fail("day " + day + " print " + KindName(p.Kind) + " " + p.Agency + " " + p.Body + " differs");
                    return;
                }
                differing[diffs++] = i;
            }
            if (diffs == 0)
            {
                checkedDay = day;
                gate.Compared(day);
                if (n > 0 && Logging) Say("command: day " + day + " prints ok");
                return;
            }
            for (int i = 0; i < diffs; i++)
            {
                PrintEntry p = prints[differing[i]];
                // the object came from the host for this very close and still differs: a restore cannot mend it
                if (fetched.Contains(FetchKey(day, p.Kind, p.Agency, p.Body)))
                {
                    Fail("day " + day + " print " + KindName(p.Kind) + " " + p.Agency + " " + p.Body
                        + " still differs after the restore");
                    return;
                }
            }
            int left = AsksLeft(now);
            if (diffs > left)
            {
                Fail("day " + day + ": " + diffs + " objects differ but only " + left
                    + " object requests are left this minute");
                return;
            }
            for (int i = 0; i < diffs; i++)
            {
                PrintEntry p = prints[differing[i]];
                fetched.Add(FetchKey(day, p.Kind, p.Agency, p.Body));
                if (Logging)
                    Say("command: day " + day + " print " + KindName(p.Kind) + " " + p.Agency + " " + p.Body
                        + " DIFFERS");
                var req = new ObjectRequest { Day = day, Kind = p.Kind, Agency = p.Agency, Body = p.Body };
                if (!sendToHost(req)) { Fail("the object request could not be sent"); return; }
                askedAt[askedNext] = now;
                askedNext = (askedNext + 1) % askedAt.Length;
                gate.FetchStarted(now);
                fetching++;
            }
            // compared again once every restore has landed
        }

        // How many more object requests the host will take from this client within the minute.
        int AsksLeft(long now)
        {
            int used = 0;
            for (int i = 0; i < askedAt.Length; i++)
                if (askedAt[i] != long.MinValue && now - askedAt[i] <= 60000) used++;
            return askedAt.Length - used;
        }
    }
}
