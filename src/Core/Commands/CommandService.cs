using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's command driver (M5 Task 7a): players' action requests in arrival order, applied through IActionWorld
    // with the host's numbers, and the results sent to the stream's recipients under each one's output budget (plan
    // "The drivers", DD4-DD7, DD10). Pure and single-threaded: every method runs on the main thread.
    //
    // Requests wait here in arrival order. One is applied only when every recipient's OutputBudget can take its
    // worst-case entry, so a spent budget delays applying and never splits the stream. Entries the game makes itself
    // (HostDecided) are applied at once; only their sending waits in each recipient's queue. Every entry is checked
    // with Validate after the apply and before it is sent: one the host cannot send is a new stream (HostFault), never
    // a message a client would take for a hostile host.
    //
    // Update allocates nothing when nothing is waiting (plan Global Constraints, Allocation).
    public sealed partial class CommandService : ICommandStream
    {
        // An entry raised inside an apply or a record: its origin, and (a record's) the numbers it already holds
        sealed class Deferred { public ActionRequest A; public int Origin; public ApplyNumbers Recorded; }

        sealed class Waiting { public int Player; public ActionRequest A; public int Bytes; public bool Sized; }

        // A message ready to send: checked, encoded once for its size. Seq is the entry's number, 0 for the rest.
        sealed class Item { public Message M; public int Bytes; public uint Seq; }

        sealed class Player
        {
            public int Id;
            public bool Recipient;                     // in the stream: entries, DayClosed
            public int DropEpoch = -1;                 // the stream it was a recipient of when it dropped (-1: none)
            public uint ReqMark;                       // the highest request number seen on this link
            // refusals and fetch answers, apart from the output budget: a flood of them must not use up the room every
            // other player's entries need
            public readonly TokenBucket Minor = new TokenBucket(MinorPerSecond, MinorBurst);
            // answers owed: an honest client has at most MaxOwed outstanding, so more than that is a flood
            public readonly Queue<Message> Owed = new Queue<Message>();
            public bool Flooding;
            // A fetch answered with a code puts the client out of step, so it needs telling once: later refused fetches
            // on this link are counted, not answered (they must not count towards Owed), and logged once.
            public bool FetchRefused, OutsideLogged;
            public int FetchesIgnored;
            public readonly Queue<Item> Out = new Queue<Item>();       // stream messages waiting for the budget
            public readonly Queue<Item> Objects = new Queue<Item>();   // object pieces, paced apart from the stream
            public readonly TokenBucket ObjectBucket =
                new TokenBucket(ActionLimits.ObjectBytesPerMinute / 60.0, MaxFrame);
            public readonly long[] Asked = new long[ActionLimits.ObjectRequestsPerMinute];   // ring of fetch times
            public int AskedCount, AskedHead;
        }

        // The largest frame an ObjectData can make: the pacing bucket holds exactly one such piece
        static readonly int MaxFrame = Envelope.MaxPayloadOf(typeof(ObjectData)) + Envelope.HeaderSize + 4;
        const long AskWindowMs = 60000;
        const int MinorPerSecond = 10, MinorBurst = 20;   // 140 messages a second at the very most with HostOut* (150)
        // The answers an honest client can be owed at once: every request it has outstanding (pending sends and
        // coalesced set keys) and its fetches. Past it the client is flooding.
        const int MaxOwed = ActionLimits.MaxPendingPerClient + ActionLimits.MaxSetKeysPerClient
            + ActionLimits.ObjectRequestsPerMinute;
        // Stream messages waiting for one player's budget. The budget sends 100 a second at the most, so 4096 is over
        // 40 s of it; a player further behind than that cannot catch up while entries keep coming, and rejoins fully.
        // At the very most 45 player entries a second (five players at their maxima) reach it, so the budget drains
        // faster than players fill it; only host-decided entries (observatory steps, crafts) add to that, and Task 13
        // measures their rate at 200000 against the budget.
        const int MaxOutQueue = 4096;
        // object pieces waiting for one player: its fetches a minute at their most
        const int MaxObjectQueue = ActionLimits.ObjectRequestsPerMinute * ActionLimits.MaxPieces;

        readonly IActionWorld world;
        readonly IJoinLink link;
        readonly ISeats seats;
        readonly ActionLimiter limiter;
        readonly HumanSet humans;
        readonly Func<int, OutputBudget> budgetOf;
        readonly Func<long> nowMs;

        readonly List<Player> players = new List<Player>();
        readonly Dictionary<int, Player> byId = new Dictionary<int, Player>();
        readonly List<Waiting> waiting = new List<Waiting>();
        readonly List<Deferred> deferred = new List<Deferred>();   // raised inside an apply
        readonly List<Item> inside = new List<Item>();        // entries raised inside the day event
        // entries the game made during a join batch, after the snapshot: the members loading miss them otherwise
        readonly List<Item> heldEntries = new List<Item>();
        List<Item> collecting;                                 // BatchEnded's releases, sent after AgencyState
        readonly List<PrintEntry> touched = new List<PrintEntry>();
        readonly HashSet<string> touchedKeys = new HashSet<string>(StringComparer.Ordinal);
        readonly List<Kept> kept = new List<Kept>();
        long keptBytes;

        uint seq;
        bool held, closing, applying;
        int applyingKind = -1;
        ActionRequest applyingEntry;
        long closingDay, snapshotDay;
        uint snapshotSeq;
        int batchNo, epoch;
        long tooManyLogMs;
        int tooManySuppressed;

        public CommandService(IActionWorld world, IJoinLink link, ISeats seats, ActionLimiter limiter, HumanSet humans,
                              Func<int, OutputBudget> budgetOf, Func<long> nowMs)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (link == null) throw new ArgumentNullException("link");
            if (seats == null) throw new ArgumentNullException("seats");
            if (limiter == null) throw new ArgumentNullException("limiter");
            if (humans == null) throw new ArgumentNullException("humans");
            if (budgetOf == null) throw new ArgumentNullException("budgetOf");
            if (nowMs == null) throw new ArgumentNullException("nowMs");
            this.world = world;
            this.link = link;
            this.seats = seats;
            this.limiter = limiter;
            this.humans = humans;
            this.budgetOf = budgetOf;
            this.nowMs = nowMs;
        }

        public bool Held { get { return held; } }

        // Other players' requests wait for either reason: a join batch, or a player catching up after a drop. Each ends
        // on its own, and neither one's end releases the other's (DD-W12).
        public bool Holding { get { return held || resumes.Count > 0; } }
        public uint LastSeq { get { return seq; } }
        public string[] HumanAgencies { get { return humans.All(); } }

        // Answered: player, request, RefuseCodes (0 = applied). Also raised for the host's own clicks (player 0).
        public event Action<int, ActionRequest, int> Answered;

        // The per-agency and game-wide counters AgencyState carries (DD7), for the human agencies it is given; the
        // game side supplies them (the interface the plan fixes has no counter read). Required: an AgencyState cannot
        // be made without it, and that is a host fault, never a silent empty list.
        public Func<string[], CounterRow[]> StateCounters { get; set; }

        // ---- requests ----

        public string FromPlayer(int player, ActionRequest a) { return FromPlayer(player, a, nowMs()); }

        // arrivalMs is when the frame was read on the network thread: the limiter must see that, not the main
        // thread's clock, or a host hitch (an autosave) bunches honest arrivals into one instant.
        public string FromPlayer(int player, ActionRequest a, long arrivalMs)
        {
            if (a == null) return "empty request";
            if (player < 1) return "a request from player " + player + " on a client link";
            string bad = a.Validate();   // also refuses a host-only kind
            if (bad != null) return bad;
            Player p = PlayerOf(player);
            if (p.Flooding) return "has more answers owed than a client can have requests out";
            if (a.Req <= p.ReqMark) return "request number " + a.Req + " after " + p.ReqMark;
            p.ReqMark = a.Req;
            Accept(player, a, arrivalMs);
            return null;
        }

        // The host's own click: the same checks and limits as anyone's.
        public void Local(ActionRequest a)
        {
            if (a == null) return;
            if (a.Validate() != null) { RaiseAnswered(0, a, RefuseCodes.CannotSend); return; }
            PlayerOf(0);
            Accept(0, a, nowMs());
        }

        // Seats, bankruptcy, craft completion: applied at once, whatever the budget; only the sending waits.
        public void HostDecided(ActionRequest a) { HostDecided(a, EntryOrigin.Host); }

        void Accept(int player, ActionRequest a, long arrivalMs)
        {
            int code = Gate(player, a);
            if (code != 0) { Refuse(player, a, code); return; }
            ActionRequest superseded;
            int verdict = limiter.Offer(player, a, arrivalMs, out superseded);
            if (superseded != null) Refuse(player, superseded, RefuseCodes.Superseded);
            if (verdict == LimitVerdict.Refused) { Refuse(player, a, RefuseCodes.TooMany); return; }
            if (verdict != LimitVerdict.Held && !FlightBucketAllows(player, a, arrivalMs))
            {
                Refuse(player, a, RefuseCodes.TooMany);
                return;
            }
            if (verdict == LimitVerdict.Held) return;   // Update releases it when its window opens
            Enqueue(player, a);
        }

        int Gate(int player, ActionRequest a)
        {
            int code = AgencyAccess.MayAct(seats, player, a.Agency);
            if (code != 0) return code;
            try { return world.GameOver ? RefuseCodes.GameOver : 0; }
            catch (Exception e) { return WorldThrew("GameOver", e); }
        }

        // A game call that throws refuses the request that needed it (and says so in the log): one bad request must not
        // stop the queue behind it.
        static int WorldThrew(string what, Exception e)
        {
            Log.Warn("command: the game threw in " + what + ": " + ExceptionText.Chain(e));
            return RefuseCodes.NotAllowedHere;
        }

        void Enqueue(int player, ActionRequest a)
        {
            if (waiting.Count >= ActionLimits.MaxWaiting || (Holding && HeldFull(player, a)))
            {
                Refuse(player, a, RefuseCodes.Busy);
                return;
            }
            waiting.Add(new Waiting { Player = player, A = a });
        }

        // A join batch keeps each player's requests in order up to a cap, and its distinct set keys up to another.
        bool HeldFull(int player, ActionRequest a)
        {
            int count = 0, keys = 0;
            string key = ActionShapes.Key(a);
            bool newKey = key != null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Waiting w in waiting)
            {
                if (w.Player != player) continue;
                count++;
                string k = ActionShapes.Key(w.A);
                if (k == null || !seen.Add(k)) continue;
                keys++;
                if (k == key) newKey = false;
            }
            return count >= ActionLimits.MaxHeldPerPlayer || (newKey && keys >= ActionLimits.MaxHeldSetKeys);
        }

        // ---- the frame ----

        public void Update()
        {
            long now = nowMs();
            FlightFrame();
            for (int i = 0; i < players.Count; i++)
            {
                ActionRequest due;
                while ((due = limiter.TakeDue(players[i].Id, now)) != null) Enqueue(players[i].Id, due);
            }
            WorldUpdate(now);
            Pump(now);
            for (int i = 0; i < players.Count; i++) if (players[i].Owed.Count > 0) PayOwed(players[i], now);
            if (!Holding) ApplyWaiting(now);
        }

        void ApplyWaiting(long now)
        {
            if (waiting.Count == 0 || DayPending(now)) return;
            while (waiting.Count > 0)
            {
                Waiting w = waiting[0];
                int code = Gate(w.Player, w.A);
                if (code == 0)
                {
                    if (!w.Sized) { w.Bytes = WorstBytes(w.Player, w.A); w.Sized = true; }
                    // the room is asked first: a request waiting for the budget costs the game no work every frame
                    if (w.Bytes > 0 && !RoomForAll(w.Bytes, now)) return;
                    code = Precheck(w);
                }
                waiting.RemoveAt(0);
                if (code != 0) Refuse(w.Player, w.A, code);
                else ApplyEntry(w.Player, w.A, false);
            }
        }

        // The checks that read the game, in the plan's order, as the request stands when its turn comes.
        int Precheck(Waiting w)
        {
            int busy = DeferredBusy(w.Player, w.A);
            if (busy != 0) return busy;
            try
            {
                string owner = world.OwnerOf(w.A);
                if (owner == null || owner != w.A.Agency) return RefuseCodes.BadTarget;
                int code = world.Check(w.A);
                if (code != 0) return code;
            }
            catch (Exception e) { return WorldThrew(ActionKind.Name(w.A.Kind), e); }
            return w.Bytes == 0 ? RefuseCodes.TooLarge : 0;
        }

        // The size of the entry's worst case for this request (the request as the host may apply it, the kind's most
        // new ids and MaxCounters wide counter rows), encoded before anything is applied; 0 when it cannot be sent at
        // all. The observatory kinds add their own widening.
        int WorstBytes(int player, ActionRequest a)
        {
            try
            {
                int ids = a.Kind == ActionKind.MoveToOrbit ? ActionLimits.MaxMoveNewIds : ActionLimits.MaxNewIds;
                var newIds = new long[ids];
                for (int i = 0; i < ids; i++) newIds[i] = int.MaxValue;
                var worst = new ActionResult { Seq = uint.MaxValue, Day = ActionLimits.MaxDay,
                    AtTicks = world.GameTicks, Player = player, Req = a.Req, Action = Widest(a), NewIds = newIds,
                    Origin = ActionKind.HostOnly(a.Kind) ? EntryOrigin.Host : EntryOrigin.Player,
                    Counters = CounterRows.Worst(a.Agency, ActionLimits.MaxCounters) };
                WidenForObservatory(worst, a);
                WidenForWorld(worst, a);
                string why;
                Item it = Encode(worst, out why);
                return it == null ? 0 : it.Bytes;
            }
            catch (Exception e) { WorldThrew("GameTicks", e); return 0; }
        }

        // The host may expand a prerequisite add into the whole queue's worth of topics (R3).
        static ActionRequest Widest(ActionRequest a)
        {
            a = FlightWidest(a);
            if (a.Kind != ActionKind.ResearchAddWithPrereqs) return a;
            var ids = new string[ActionLimits.MaxQueue];
            for (int i = 0; i < ids.Length; i++) ids[i] = CounterRows.WideId;
            return new ActionRequest { Req = a.Req, Kind = a.Kind, Agency = a.Agency, Item = a.Item, Ids = ids };
        }
        // Every recipient has caught up and has room for a message this size.
        bool RoomForAll(int bytes, long now)
        {
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if (!p.Recipient) continue;
                if (p.Out.Count > 0) return false;
                OutputBudget b = budgetOf(p.Id);
                if (b != null && !b.CanTake(bytes, now)) return false;
            }
            return true;
        }

        // ---- applying ----

        // True while an entry is applied or a record is made, with the kind and request of the entry (-1 and null
        // for a record): a hold reads them to tell what the world is changing under it.
        public bool Applying { get { return applying; } }
        public int ApplyingKind { get { return applyingKind; } }
        public ActionRequest ApplyingEntry { get { return applyingEntry; } }

        // The day an entry made now is stamped with: the day that ends at the game's nextDay, or the next one while
        // the day event runs (an entry raised there follows the close, I5).
        bool StampDay(out long day)
        {
            if (closing) { day = closingDay + 1; return true; }
            if (!DayKey.TryCurrent(world.NextDayTicks, world.OriginTicks, out day)) return false;
            // a closed day whose event has not finished (a handler threw: the game runs the day again with nextDay
            // unchanged) takes no more entries; whatever comes now belongs to the next day
            if (day <= closedThrough) day = closedThrough + 1;
            return true;
        }

        // The game can throw half way through an apply, leaving a world that no longer matches any entry: that is a new
        // stream for everyone, like any entry the host cannot send.
        void ApplyEntry(int player, ActionRequest a, bool hostDecided, int origin = EntryOrigin.Host,
            ApplyNumbers recorded = null)
        {
            try { ApplyEntryCore(player, a, hostDecided, origin, recorded); }
            catch (Exception e)
            {
                Log.Error("command: the game threw applying " + ActionKind.Name(a.Kind) + ": "
                    + ExceptionText.Chain(e));
                Fault("the game threw applying " + ActionKind.Name(a.Kind) + ": " + e.GetType().Name + " " + e.Message);
                RaiseAnswered(player, a, RefuseCodes.Busy);
            }
        }

        void ApplyEntryCore(int player, ActionRequest a, bool hostDecided, int origin, ApplyNumbers recorded)
        {
            long day;
            if (!StampDay(out day)) { Fault("no day key for nextDay " + world.NextDayTicks); return; }
            ApplyOutcome o;
            int startEpoch = epoch;
            applying = true;
            applyingKind = a.Kind;
            applyingEntry = a;
            try
            {
                // a record already holds its numbers: the game made the changes, there is nothing to apply
                o = recorded != null ? new ApplyOutcome { Applied = a, Numbers = recorded } : world.Apply(a, null);
            }
            finally { applying = false; applyingKind = -1; applyingEntry = null; }
            // the game reported a fault inside the apply: the stream it belonged to is gone, and everyone rejoins
            if (epoch != startEpoch) return;
            if (o == null) Fault("Apply returned no outcome for " + ActionKind.Name(a.Kind));
            else if (o.Deferred && o.Code == 0 && !hostDecided) Defer(player, a, o);
            else if (o.Deferred) Fault(ActionKind.Name(a.Kind) + " was deferred without a player to answer");
            else if (o.Code != 0) Refuse(player, a, o.Code);
            else
            {
                Publish(player, a, o, day, hostDecided, origin);
                AfterPublish(a, hostDecided);
            }
            DrainDeferred();
        }

        // Host-decided entries raised while another was being applied (or a record was being made) go out after it,
        // each with the seq that follows. One place, so a partial file that records a seam drains the same way.
        void DrainDeferred()
        {
            while (deferred.Count > 0)
            {
                Deferred next = deferred[0];
                deferred.RemoveAt(0);
                ApplyEntry(0, next.A, true, next.Origin, next.Recorded);
            }
            DrainDone();
        }

        // A record made by a partial file (a draw, a spawn) is in the same position as an apply: an entry raised
        // inside it is queued, then drained by EndRecord. BeginRecord says whether it opened the scope; inside an
        // apply (or another record) it does not, and EndRecord(false) leaves the outer scope to drain.
        internal bool BeginRecord()
        {
            if (applying) return false;
            applying = true;
            return true;
        }

        internal void EndRecord(bool opened)
        {
            if (!opened) return;
            applying = false;
            DrainDeferred();
        }

        void Publish(int player, ActionRequest req, ApplyOutcome o, long day, bool hostDecided, int origin)
        {
            var r = new ActionResult { Seq = seq + 1, Day = day, AtTicks = world.GameTicks,
                Player = hostDecided ? 0 : player, Req = hostDecided ? 0 : req.Req, Action = o.Applied ?? req,
                Origin = hostDecided ? origin : EntryOrigin.Player };
            NumbersInto(r, o.Numbers);
            string why;
            Item it = Encode(r, out why);
            if (it == null) { Fault("entry " + ActionKind.Name(r.Action.Kind) + " is not sendable: " + why); return; }
            seq++;
            it.Seq = seq;
            AddTouched(o.Touched);
            Distribute(it);
            string who = hostDecided ? "host" : "player " + r.Player + " req " + r.Req;
            Log.Info("command: " + who + " " + ActionKind.Name(r.Action.Kind) + " " + r.Action.Agency + " applied #"
                + seq + " day " + day + " at " + r.AtTicks);
            RaisePublished(r);
            LogWorldEntry(r);
            RaiseAnswered(player, req, 0);
            Pump(nowMs());
        }

        // The seam for the numbers an apply made: the counters and ids here, the observatory and asteroid numbers in
        // CommandServiceObservatory.cs.
        static void NumbersInto(ActionResult r, ApplyNumbers n)
        {
            if (n == null) return;
            r.Counters = n.Counters;
            r.NewIds = n.NewIds;
            r.Pull = n.Pull;
            var w = n as WorldNumbers;
            if (w != null)
            {
                r.Ops = w.Ops;
                r.Steps = w.Steps;
                r.Chain = w.Chain;
            }
            ObservatoryNumbersInto(r, n);
        }

        // Validate, then the real encode: a message that fails either is never sent.
        static Item Encode(Message m, out string why)
        {
            why = m.Validate();
            if (why != null) return null;
            byte[] body;
            if (!Envelope.TryEncode(m, 1, out body, out why)) return null;
            return new Item { M = m, Bytes = body.Length + 4 };
        }

        void AddTouched(IList<PrintEntry> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                PrintEntry t = list[i];
                if (t == null) continue;
                if (touchedKeys.Add(TouchKey(t))) touched.Add(new PrintEntry { Kind = t.Kind, Agency = t.Agency,
                    Body = t.Body });
            }
        }

        static string TouchKey(PrintEntry t) { return t.Kind + "|" + t.Agency + "|" + t.Body; }

        // ---- sending ----

        // An entry (never a close or a state: those are delivered directly) goes where the moment sends it.
        void Distribute(Item it)
        {
            if (collecting != null) { collecting.Add(it); return; }
            if (held && KeepsForMembers) heldEntries.Add(it);
            if (!closing) Deliver(it);
            else inside.Add(it);   // goes out at DayEventDone, after the close
        }

        void Deliver(Item it)
        {
            if (HoldsAfterSnapshot(it) || HoldsBack(it)) return;
            KeepForReplay(it);
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if (!p.Recipient || Drops(p, it)) continue;
                if (QueueForResume(p, it)) continue;
                p.Out.Enqueue(it);
                if (p.Out.Count > MaxOutQueue) DropFromStream(p, "more than " + MaxOutQueue + " messages wait for it");
            }
        }

        // A player the stream cannot keep up with, or whose link is gone, would have a gap it cannot heal:
        // it leaves the stream and rejoins fully.
        void DropFromStream(Player p, string why)
        {
            Log.Warn("command: player " + p.Id + " leaves the stream: " + why);
            Remove(p);
            link.Send(p.Id, new StreamReset { Code = StreamCodes.HostFault });
        }
        void Pump(long now)
        {
            for (int i = 0; i < players.Count; i++) PumpPlayer(players[i], now);
        }

        void PumpPlayer(Player p, long now)
        {
            if (p.Out.Count == 0 && p.Objects.Count == 0) return;
            OutputBudget b = budgetOf(p.Id);
            while (p.Out.Count > 0)
            {
                Item it = p.Out.Peek();
                if (b != null && !b.CanTake(it.Bytes, now)) break;
                p.Out.Dequeue();
                if (!SendTo(p, it, b, now)) return;
            }
            while (p.Objects.Count > 0)
            {
                Item it = p.Objects.Peek();
                if (!p.ObjectBucket.Has(it.Bytes, now) || (b != null && !b.CanTake(it.Bytes, now))) break;
                p.Objects.Dequeue();
                p.ObjectBucket.Take(it.Bytes, now);
                if (!SendTo(p, it, b, now)) return;
            }
        }

        bool SendTo(Player p, Item it, OutputBudget b, long now)
        {
            if (link.Send(p.Id, it.M))
            {
                if (b != null) b.Take(it.Bytes, now);
                return true;
            }
            // every entry passed Validate and the real encode in Encode, so a send that fails is a link that has closed
            // (the player left, or the link closed itself on a full queue): the player's own leave line says why
            DropFromStream(p, "its link is gone (" + it.M.GetType().Name + " not sent)");
            return false;
        }

        // A refusal or a fetch's answer: owed to the player and paid at the small-message rate, never charged to the
        // output budget the stream's applies wait on, so one client's flood cannot hold up the rest. Every answer is
        // sent (a client's pending request is only freed by its answer); a client owed more than it can have
        // outstanding is flooding and is told off through FromPlayer.
        void SendMinor(Player p, Message m)
        {
            string why;
            if (Encode(m, out why) == null) return;
            if (p.Owed.Count >= MaxOwed) { p.Flooding = true; return; }
            p.Owed.Enqueue(m);
            PayOwed(p, nowMs());
        }

        void PayOwed(Player p, long now)
        {
            while (p.Owed.Count > 0 && p.Minor.Has(1, now))
            {
                p.Minor.Take(1, now);
                link.Send(p.Id, p.Owed.Dequeue());
            }
        }

        // A small message that matters to the stream (a reset): sent now, counted in the budget, never delayed.
        void SendSmall(int player, Message m)
        {
            string why;
            Item it = Encode(m, out why);
            if (it == null) return;
            if (!link.Send(player, m)) return;
            OutputBudget b = budgetOf(player);
            if (b != null) b.Spend(it.Bytes, nowMs());
        }

        void Refuse(int player, ActionRequest a, int code)
        {
            long now = nowMs();
            if (code != RefuseCodes.TooMany) LogRefusal(player, a, code);
            else if (now - tooManyLogMs >= 1000)
            {
                LogRefusal(player, a, code);
                if (tooManySuppressed > 0) Log.Info("command: " + tooManySuppressed + " more TooMany refusals");
                tooManyLogMs = now;
                tooManySuppressed = 0;
            }
            else tooManySuppressed++;
            if (player > 0)
            {
                bool wire = RefuseCodes.IsKnown(code) && code != RefuseCodes.CannotSend;
                SendMinor(PlayerOf(player),
                    new ActionRefused { Req = a.Req, Code = wire ? code : RefuseCodes.NotAllowedHere });
            }
            RaiseAnswered(player, a, code);
        }

        static void LogRefusal(int player, ActionRequest a, int code)
        {
            Log.Info("command: player " + player + " req " + a.Req + " " + ActionKind.Name(a.Kind) + " refused "
                + RefuseCodes.Name(code));
        }

        void RaiseAnswered(int player, ActionRequest a, int code)
        {
            Action<int, ActionRequest, int> h = Answered;
            if (h == null) return;
            try { h(player, a, code); }
            catch (Exception e) { Log.Warn("command: a listener threw on an answer: " + e.GetType().Name); }
        }

        // ---- the day ----

        // Before the day event: the prints of every object the day touched, their copies kept, DayClosed sent. The
        // game reads that fail leave no close the clients could trust: a new stream.
        public void DayClosing(long day)
        {
            if (Reopens(day)) return;
            try { CloseDay(day); }
            catch (Exception e)
            {
                Fault("the game threw closing day " + day + ": " + e.GetType().Name + " " + e.Message);
            }
        }

        void CloseDay(long day)
        {
            AddTouched(FlightTouchesFor(day));
            int n = Math.Min(touched.Count, ActionLimits.MaxPrintsPerDay);
            PrintEntry[] prints = n == 0 ? null : new PrintEntry[n];
            for (int i = 0; i < n; i++)
            {
                PrintEntry t = touched[i];
                prints[i] = new PrintEntry { Kind = t.Kind, Agency = t.Agency, Body = t.Body,
                    Print = world.PrintOf(t.Kind, t.Agency, t.Body) };
                KeepCopy(day, t);
                touchedKeys.Remove(TouchKey(t));
            }
            if (touched.Count > n) Log.Info("command: " + (touched.Count - n) + " objects wait for the next day");
            touched.RemoveRange(0, n);
            TrimKept(day);
            string why;
            Item it = Encode(WithWorld(new DayClosed { Day = day, LastSeq = seq, Prints = prints }, day), out why);
            if (it == null) { Fault("DayClosed " + day + " is not sendable: " + why); return; }
            Deliver(it);
            SendQueuedHeals(day);
            closing = true;
            closingDay = day;
            closedThrough = day;
            if (n > 0 || day % 30 == 0)
                Log.Info("command: day " + day + " closed through #" + seq + ", " + n + " prints");
            Pump(nowMs());
        }
        // After the day event: the entries raised inside it go out, on the next day.
        public void DayEventDone()
        {
            closing = false;
            for (int i = 0; i < inside.Count; i++) Deliver(inside[i]);
            inside.Clear();
            RaiseNotes();
            Pump(nowMs());
        }

        // ---- seats and the join batch (DD5, DD6) ----

        public void BatchStarting(IList<string> newAgencies)
        {
            batchNo++;
            int startEpoch = epoch;
            try
            {
                if (newAgencies != null)
                {
                    foreach (string agency in newAgencies)
                    {
                        if (humans.Contains(agency)) continue;   // a seat is claimed once per session
                        HostDecided(new ActionRequest { Kind = ActionKind.SeatClaimed, Agency = agency,
                            Amount = world.MoneyFor(agency) });
                    }
                }
                long day;
                if (epoch != startEpoch) return;   // a fault has already started a new stream: no hold to keep
                if (!StampDay(out day)) { Fault("no day key for nextDay " + world.NextDayTicks); return; }
                held = true;
                snapshotSeq = seq;
                snapshotDay = day;
            }
            catch (Exception e) { Fault("the game threw starting batch " + batchNo + ": " + e.GetType().Name); return; }
            Log.Info("command: held " + waiting.Count + " requests (join batch " + batchNo + ")");
        }

        // The host removed the player that held this agency: the seat is released as a never-loaded claim's is, so the
        // agency leaves the human set and goes back to the computer, and a new claim cannot take the set past its cap.
        public void ReleaseSeat(string agency)
        {
            if (!humans.Contains(agency)) return;
            HostDecided(new ActionRequest { Kind = ActionKind.SeatReleased, Agency = agency });
        }

        // Nothing to settle without flights; with them, every creation accepted before the batch must have gone out.
        public bool ReadyToSnapshot() { return FlightsReadyToSnapshot(); }

        // The snapshot's frame: its stream position replaces the one BatchStarting noted, and the entries applied between the
        // two (the wait for readiness) are in the snapshot, so they are not kept for the members.
        public void SnapshotTaken()
        {
            if (!held) return;   // a reset ended the batch
            try
            {
                long day;
                if (!StampDay(out day)) { Fault("no day key for nextDay " + world.NextDayTicks); return; }
                snapshotSeq = seq;
                snapshotDay = day;
                heldEntries.Clear();
                snapshotDone = true;
            }
            catch (Exception e) { Fault("the game threw taking the snapshot: " + e.GetType().Name); }
        }

        // A stream reset during a batch ends it: the members' snapshots belong to the old stream, so they are sent a
        // reset (a full rejoin) instead of AgencyState. The hold is over whatever happens in here.
        public void BatchEnded(IList<int> loaded, IList<string> releasedAgencies)
        {
            bool intact = held;   // a reset ends the batch (EndBatch), so a batch still held is the one that began
            uint start = snapshotSeq;
            long startDay = snapshotDay;
            var mine = new List<Item>(heldEntries);
            List<Item> after = TakeAfterSnapshot();
            EndBatch();
            // 1: the releases are appended; they reach everyone, the newcomers included, only after AgencyState
            int streamEpoch = epoch;
            collecting = new List<Item>();
            if (releasedAgencies != null)
                foreach (string agency in releasedAgencies)
                    HostDecided(new ActionRequest { Kind = ActionKind.SeatReleased, Agency = agency });
            List<Item> releases = collecting;
            collecting = null;
            if (!intact || epoch != streamEpoch)
            {
                Log.Info("command: batch " + batchNo + " ended in a new stream: its members rejoin fully");
                ResetMembers(loaded);
                return;
            }
            // 2: each member that loaded gets AgencyState and joins the recipients
            if (loaded != null && loaded.Count > 0)
            {
                Item it = StateItem(startDay, start + 1);
                if (it == null) { ResetMembers(loaded); return; }
                foreach (int id in loaded)
                {
                    Player p = PlayerOf(id);
                    Forget(p);
                    NoteMember(id);
                    StateLine(id, start + 1);
                    p.Recipient = true;
                    p.DropEpoch = -1;
                    p.Out.Enqueue(it);
                    foreach (Item e in mine) p.Out.Enqueue(e);
                }
            }
            // the entries made after the snapshot go to everyone, the joiners after their state
            for (int i = 0; i < after.Count; i++) Deliver(after[i]);
            // 3: the releases go to every recipient
            for (int i = 0; i < releases.Count; i++) Deliver(releases[i]);
            // 4: the hold is already over (EndBatch); the held requests apply from the next Update
            Log.Info("command: batch " + batchNo + " ended; applying " + waiting.Count + " held");
            Pump(nowMs());
        }

        void EndBatch()
        {
            held = false;
            snapshotDone = false;
            heldEntries.Clear();
            snapshotSeq = 0;
            snapshotDay = 0;
        }

        void ResetMembers(IList<int> members)
        {
            if (members == null) return;
            var reset = new StreamReset { Code = StreamCodes.HostFault };
            foreach (int id in members) SendSmall(id, reset);
        }

        // AgencyState, checked and encoded; null (after a host fault) when it cannot be made. The game's money and the
        // counters are required reads: a missing one is a fault, never a quiet default.
        Item StateItem(long day, uint nextSeq)
        {
            string why;
            try
            {
                Item it = Encode(StateFor(day, nextSeq), out why);
                if (it != null) return it;
            }
            catch (Exception e) { why = e.GetType().Name + " " + e.Message; }
            Fault("AgencyState is not sendable: " + why);
            return null;
        }

        // The join driver marks the members that loaded as in game before it ends the batch, so Away is true to life.
        AgencyState StateFor(long day, uint nextSeq)
        {
            string[] all = humans.All();
            var rows = new AgencyMoneyRow[all.Length];
            for (int i = 0; i < all.Length; i++)
                rows[i] = new AgencyMoneyRow { Agency = all[i], Money = world.MoneyFor(all[i]),
                    Away = AgencyAccess.Away(seats, all[i]) };
            Func<string[], CounterRow[]> counters = StateCounters;
            if (counters == null) throw new InvalidOperationException("StateCounters is not set");
            CounterRow[] rowsOfCounters = counters(all);
            if (rowsOfCounters == null) throw new InvalidOperationException("StateCounters gave no list");
            return new AgencyState { Day = day, NextSeq = nextSeq, Stream = epoch, Agencies = rows,
                Counters = rowsOfCounters, LifeSupport = TimersFor(all) };
        }

        // ---- links ----

        public void LinkReplaced(int player)
        {
            Player was = PlayerOf(player);
            if (was.Recipient) was.DropEpoch = epoch;   // replaced with no Left in between: this is its drop
            int dropped = Forget(was);
            OrphanFlightsOf(player);
            Log.Info("command: player " + player + " link replaced; " + dropped + " requests dropped");
        }

        public void Left(int player)
        {
            Player was = PlayerOf(player);
            was.DropEpoch = was.Recipient ? epoch : -1;   // a resume needs the same stream it was in
            int dropped = Forget(was);
            OrphanFlightsOf(player);
            Log.Info("command: player " + player + " left; " + dropped + " requests dropped");
        }

        public void QuickRejoined(int player)
        {
            Player p = PlayerOf(player);
            Forget(p);
            long day;
            Item it = null;
            try { if (StampDay(out day)) it = StateItem(day, seq + 1); else Fault("no day key for nextDay"); }
            catch (Exception e) { Fault("the game threw in a quick rejoin: " + e.GetType().Name); }
            if (it == null) { ResetMembers(new[] { player }); return; }
            StateLine(player, seq + 1);
            p.Recipient = true;
            p.DropEpoch = -1;
            p.Out.Enqueue(it);
            Pump(nowMs());
        }

        // A new link for this player: what the old one had queued is dropped unanswered (never applied), its request
        // number and limiter start again, and it is out of the stream until its next AgencyState.
        int Forget(Player p)
        {
            int dropped = waiting.RemoveAll(w => w.Player == p.Id);
            WorldForget(p.Id);
            limiter.Forget(p.Id);
            p.ReqMark = 0;
            p.Owed.Clear();
            p.Flooding = false;
            p.FetchRefused = false;
            p.OutsideLogged = false;
            p.FetchesIgnored = 0;
            Remove(p);
            return dropped;
        }

        static void Remove(Player p)
        {
            p.Recipient = false;
            p.Out.Clear();
            p.Objects.Clear();
        }

        // The seq the client must have applied for a quick rejoin to be safe: the stream's current one. A player out of
        // the stream (a dropped link) is owed every entry applied since, so only a client that is up to date matches.
        public uint LastSentTo(int player)
        {
            return byId.ContainsKey(player) ? seq : 0;
        }

        // ---- loads and faults ----

        public void HostLoaded()
        {
            DropFlights();
            StartNewStream(StreamCodes.HostLoaded);
        }

        void Fault(string why)
        {
            Log.Warn("command: host fault: " + why);
            StartNewStream(StreamCodes.HostFault);
        }

        // Every recipient is told, then nobody is in the stream: each rejoins fully and the seq starts again at 1. A
        // join batch under way ends with it (its members' snapshots belong to the old stream).
        void StartNewStream(int code)
        {
            var reset = new StreamReset { Code = code };
            for (int i = 0; i < players.Count; i++)
                if (players[i].Recipient) SendSmall(players[i].Id, reset);
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                limiter.Forget(p.Id);
                p.ReqMark = 0;
                Remove(p);
            }
            seq = 0;
            epoch++;
            EndBatch();
            waiting.Clear();
            afterSnapshot.Clear();
            deferred.Clear();
            inside.Clear();
            touched.Clear();
            touchedKeys.Clear();
            kept.Clear();
            keptBytes = 0;
            closing = false;
            ResetHostState();
            Log.Info("command: stream reset (" + code + ") stream " + epoch);
        }

        Player PlayerOf(int id)
        {
            Player p;
            if (byId.TryGetValue(id, out p)) return p;
            p = new Player { Id = id };
            byId[id] = p;
            players.Add(p);
            return p;
        }
    }
}