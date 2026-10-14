using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoManager = Manager.ObjectInfoManager;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // Every client's commands in the game (plan "Where clients apply", Task 10b): CommandClient fed with the host's
    // stream from the link, in arrival order, the Clock messages included; the entries applied through the same
    // ActionApply as the host's, with CounterSync around each; ObjectData restored through ObjectRestore; the
    // AgencyState that starts a stream applied (money, counters, crew-death timers). Anything that cannot be followed
    // puts the client out of step, and the session then takes the full-rejoin path (DD7). Owned by the client's
    // CommandSession for the length of the session; each snapshot load starts its stream position again
    // (LoadStarting). Main thread only.
    public sealed class CommandClientGlue
    {
        static readonly AccessTools.FieldRef<ObjectInfoData, DateTime?> LifeSupportNotice =
            AccessTools.FieldRefAccess<ObjectInfoData, DateTime?>("lastNotificationLifeSupportFinish");
        static readonly AccessTools.FieldRef<TimeController, DateTime> NextDayOf =
            AccessTools.FieldRefAccess<TimeController, DateTime>("nextDay");

        // what a planted fault holds back behind a late entry, at most
        const int MaxHeldBack = 1024;

        readonly CommandSession session;
        readonly Action<string> leave;
        readonly ResultOrder order;
        readonly DayGate gate;
        readonly CommandClient client;
        readonly Func<Message, bool> toHost;
        readonly CatchUp catchUp;
        readonly FlightClientGlue flights;
        readonly CounterSync counters = new CounterSync();
        readonly bool noRules;
        readonly string fault;
        HumanSet humans = new HumanSet(new string[0]);
        Action humansChanged;
        long origin;
        bool loaded, leaving;
        // the planted client faults, each armed at every load and on the dev trigger "fault", fired once
        bool printArmed, payArmed, lateArmed, structArmed;
        ActionResult lateEntry;
        WorldClientGlue worldSide;
        ResumeCheck checkWanted;
        readonly List<Message> heldBack = new List<Message>();

        // leave: the full-rejoin path, given why (the session client drops its link and its world leaves the session).
        public CommandClientGlue(CommandSession session, Func<Message, bool> sendToHost, Action<string> leave,
            string fault, bool noDayGate, bool noRules)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (sendToHost == null) throw new ArgumentNullException("sendToHost");
            if (leave == null) throw new ArgumentNullException("leave");
            this.session = session;
            this.leave = leave;
            this.fault = fault;
            this.noRules = noRules;
            toHost = sendToHost;
            order = new ResultOrder(ActionLimits.MaxQueuedEntries);
            gate = new DayGate(order, noDayGate);
            client = new CommandClient(new ClientWorld(this), sendToHost, order, gate,
                new ClientSendBudget(ActionLimits.SetPerSecond, ActionLimits.DoPerSecond, ActionLimits.DoBurst,
                    ActionLimits.ClientBytesPerSecond, ActionLimits.ClientBytesBurst,
                    ActionLimits.MaxSetKeysPerClient), () => NetClock.NowMs);
            // an entry applies at the host's tick, never earlier (HostTick); the wait relies on the day gate's stand at
            // the day point, so without the gate an entry applies as its day comes
            client.ApplyAtHostTick = !noDayGate;
            flights = new FlightClientGlue(session);
            client.Flights = flights;
            client.StreamStarted += OnStreamStarted;
            client.EntryApplied += OnEntryApplied;
            catchUp = new CatchUp(client);
            client.RequestRefused += PlannerWait.Refused;
            // the planner closes once the host has accepted, as the game's own Schedule closes it: waiting for the
            // copy would hold it open while its own pause keeps this game from the entry's day
            client.RequestAccepted += PlannerWait.Applied;
            client.Refusal += code => session.ShowRefusal(CommandText.Refused(code));
            if (noDayGate) Log.Warn("command: the day gate is off (-semp-no-day-gate)");
        }

        public CommandClient Client { get { return client; } }
        // The client half of the flight world: the apply over frames, the landing record, the launch step (M6).
        public FlightClientGlue Flights { get { return flights; } }
        public DayGate Gate { get { return gate; } }
        public CounterSync Counters { get { return counters; } }
        // The human agencies as the last snapshot's header named them; the seat entries change them from there.
        public HumanSet Humans { get { return humans; } }
        // The host's origin for this world's day keys (the header's), or 0 before the first load.
        public long Origin { get { return origin; } }
        // The snapshot has loaded and the world runs under the stream (the gate holds from here on).
        public bool Loaded { get { return loaded; } }
        // The day gate or an impact holds the fixed step now (DayGateHook): never a speed vote (DESIGN "Time").
        public bool Holding { get; set; }

        public bool Applying { get { return client.Applying; } }
        // The catch-up after a resumed drop (DD-W12): the clock asks it for its speed.
        public CatchUp CatchUp { get { return catchUp; } }

        public string Status
        {
            get { return client.OutOfStep ? CommandText.OutOfStep() : client.Status; }
        }

        public void Click(ActionRequest a) { client.Click(a); }

        // ---- the link ----

        public void Joined(int player) { client.Player = player; }

        public void LinkLost()
        {
            client.LinkLost();
            checkWanted = null;
            flights.Dropped();
            Holding = false;
        }

        // The link dropped and may resume (DD-W12): the stream, the gate and the heal history stay. The rejoin's answer
        // decides (Session: Resume calls Resumed, anything else LinkLost).
        public void LinkSuspended()
        {
            client.Suspend();
            checkWanted = null;
            flights.Dropped();   // a flight asked for before the drop is not answered after it (the host orphaned it)
            Holding = false;
        }

        // Resume was answered: the host replays from nextSeq and everyone waits at its paused date.
        public void Resumed(uint nextSeq, long hostPausedTicks)
        {
            client.ResumeFrom(nextSeq, hostPausedTicks);
        }

        // The shared world's client half (not with -semp-no-world-rules): the world print at the day-event point, the
        // year, heals and the resume's check. print: this game's M4 fingerprint now (null while it cannot take one).
        public void StartWorld(Func<Fingerprint> print)
        {
            worldSide = new WorldClientGlue(() => origin);
            client.World = worldSide;
            client.IsHuman = agency => humans.Contains(agency);
            client.FingerprintNow = print;
        }

        // One message from the host, in the order the link delivered it.
        public void Received(Message m)
        {
            if (m == null) return;
            var check = m as ResumeCheck;
            if (check != null) { checkWanted = check; return; }
            if (lateEntry != null || (lateArmed && m is ActionResult))
            {
                HoldBack(m);
                return;
            }
            Feed(m);
        }

        void Feed(Message m)
        {
            string why = client.Received(m);
            // a message no well-formed stream holds (the link's own checks keep them out): not a game to follow
            if (why != null) client.OutOfStepNow("the host sent " + m.GetType().Name + ": " + why);
        }

        // -semp-command-fault lateday (with -semp-no-day-gate): the first entry, and everything after it, reaches the
        // driver only once this game's own day has moved past the entry's day, so it arrives a day late.
        void HoldBack(Message m)
        {
            var r = m as ActionResult;
            if (lateEntry == null && r != null)
            {
                lateArmed = false;
                lateEntry = r;
                Log.Warn("command: fault lateday: #" + r.Seq + " of day " + r.Day + " held back a day");
                return;
            }
            if (m is Clock) { Feed(m); return; }
            if (heldBack.Count < MaxHeldBack) heldBack.Add(m);
            else client.OutOfStepNow("the late entry's fault held back more than " + MaxHeldBack + " messages");
        }

        void ReleaseLate(long localDay)
        {
            if (lateEntry == null || localDay <= lateEntry.Day) return;
            ActionResult r = lateEntry;
            lateEntry = null;
            Feed(r);
            foreach (Message m in heldBack) Feed(m);
            heldBack.Clear();
        }

        // ---- the load ----

        // A snapshot is about to load (a join or a full rejoin): the stream starts again from the AgencyState that
        // follows, the human agencies are the header's, the rules start on them before the extract (the load's own
        // AfterLoadState must not give a human agency +Infinity), and nothing a rule held for the old world survives.
        public void LoadStarting(JoinHeader h)
        {
            client.FullRejoinStarted();
            checkWanted = null;
            if (worldSide != null) worldSide.OnLoadStarting();
            loaded = false;
            leaving = false;
            Holding = false;
            origin = h.OriginTicks;
            if (humansChanged != null) humans.Changed -= humansChanged;
            humans = new HumanSet(h.HumanAgencies ?? new string[0]);
            humansChanged = () => session.Steps.Watch.Dirty = true;
            humans.Changed += humansChanged;
            session.Steps.Loaded();
            flights.Loading();
            lateEntry = null;
            heldBack.Clear();
            if (!noRules)
            {
                CommandSession s = session;
                HumanAgencies.Start(humans, null, () => s.Applying);
                HumanAgencies.SetKindProbe(() => s.ApplyingKind, () => s.ApplyingEntry);
                s.RegisterFlights();
                // the world rules' client half, only while the shared world runs (not -semp-no-world-rules)
                s.RegisterWorld(worldSide != null ? new WorldClientSeam(this) : null);
                HumanAgencies.ResetForLoad();
            }
            else Log.Info("rules: off (-semp-no-human-rules)");
            ArmFault("load");
        }

        // The snapshot's world is running (JoinerFlow's Loaded).
        public void LoadDone()
        {
            loaded = true;
            session.Steps.Watch.Dirty = true;
            flights.LoadDone();
        }

        // Back at the menu: no world to step, and no rules until the next snapshot's load starts them on its header.
        public void AtMenu()
        {
            loaded = false;
            checkWanted = null;
            Holding = false;
            flights.AtMenu();
            HumanAgencies.Stop();
        }

        // ---- every frame ----

        public void Update()
        {
            if (client.OutOfStep)
            {
                Leave();
                return;
            }
            if (!loaded) return;
            Strays();
            long day;
            if (!LocalDay(out day)) return;
            ReleaseLate(day);
            // entries also apply while the game is paused (no fixed step moves the day then)
            client.Update(day);
            AnswerCheck();
            if (client.OutOfStep) Leave();
        }

        // A draw or a spawn the client made outside the host's entry: this world has made its own decision.
        void Strays()
        {
            if (!HumanAgencies.Active) return;
            ObservatoryDrawState dice = HumanAgencies.Dice;
            AsteroidSpawnState spawns = HumanAgencies.Spawns;
            string why = (dice != null ? dice.TakeStray() : null) ?? (spawns != null ? spawns.TakeStray() : null);
            if (why != null) client.OutOfStepNow(why);
        }

        // The client's own day (DayKey) from its nextDay and the host's origin; false (and out of step) without one.
        public bool LocalDay(out long day)
        {
            day = 0;
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            if (tc == null) return false;
            if (DayKey.TryCurrent(NextDayOf(tc).Ticks, origin, out day)) return true;
            client.OutOfStepNow("the client's own day has no key (nextDay " + NextDayOf(tc).Ticks + ", origin "
                + origin + ")");
            return false;
        }

        // Out of step: once per fall, the session drops its link and rejoins fully from a fresh snapshot.
        void Leave()
        {
            if (leaving) return;
            leaving = true;
            Holding = false;
            string why = client.OutOfStepWhy ?? "out of step";
            WorldRowsLog.Write("joiner", why);   // the rejoin replaces this world: its side of the difference goes first
            try { leave(why); }
            catch (Exception e) { Log.Error("command: could not start the full rejoin: " + e.GetType().Name); }
        }

        // The host's ResumeCheck is answered once this game has applied through it and stands at the paused date.
        void AnswerCheck()
        {
            if (checkWanted == null) return;
            ResumeProgress p = client.Answer(checkWanted);
            if (p == null) return;
            checkWanted = null;
            if (!toHost(p)) client.OutOfStepNow("the resume check could not be sent");
        }

        // ---- the stream's start ----

        // AgencyState (a batch's end, a quick rejoin): the host's money, counters and crew-death timers for every human
        // agency, set as they stand on the host. The stream position is the driver's.
        void OnStreamStarted(AgencyState st)
        {
            Log.Info("command: stream from #" + st.NextSeq + " day " + st.Day + " stream " + st.Stream);
            string bad = null;
            try
            {
                foreach (AgencyMoneyRow row in st.Agencies)
                {
                    Company c = GameAgencies.Find(row.Agency);
                    if (c == null || c.MoneyController == null) { bad = "no agency " + row.Agency; break; }
                    c.MoneyController.ForCheatSetCurrentMony(row.Money);
                    Log.Info("money: " + row.Agency + " " + StreamLines.Money(row.Money) + " (from host)");
                }
                bad = bad ?? counters.Set(st.Counters) ?? LifeSupport(st);
            }
            catch (Exception e)
            {
                Log.Error("command: applying the state threw: " + ExceptionText.Chain(e));
                bad = "applying the state threw " + e.GetType().Name;
            }
            session.Steps.Watch.Dirty = true;
            if (bad != null) client.OutOfStepNow("the host's state: " + bad);
        }

        // Each listed agency's crew-death timers (the game does not save them) as the host's run: the exact time for a
        // site the state lists, none for any other site of that agency.
        static string LifeSupport(AgencyState st)
        {
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (m == null) return "no ObjectInfoManager";
            foreach (AgencyMoneyRow row in st.Agencies)
            {
                Company c = GameAgencies.Find(row.Agency);
                if (c == null) return "no agency " + row.Agency;
                foreach (ObjectInfo o in m.allObjectInfos)
                {
                    ObjectInfoData d = GameAgencies.Site(o, c);
                    if (d == null) continue;
                    long ticks = RestoreRules.LifeSupportTicks(st.LifeSupport, row.Agency, o.id);
                    LifeSupportNotice(d) = ticks >= 0 ? new DateTime(ticks) : (DateTime?)null;
                }
            }
            return null;
        }

        void OnEntryApplied(ActionResult r)
        {
            string line = StreamLines.Of(r);
            if (line != null) Log.Info(line);
            session.Steps.Watch.Dirty = true;
            FlightLog.Ids(r);
            WindowRefresh.After(r.Action);
            // a cancel or a launch outcome ends that mission's pending events, inside the call that applied it
            StepGate steps = session.Steps;
            FlightWatchSync.Applied(r.Action, steps.Watch, steps.Launches, steps.PlainCuts);
        }

        // ---- the client's own applies (IActionWorld) ----

        // The host's numbers around the entry's own world calls: every counter to the host's "before", the same
        // ActionApply as the host's, and every counter at the host's "after".
        ApplyOutcome Apply(ActionRequest a, ApplyNumbers given)
        {
            string why = counters.Before(given.Counters, client.ApplyingAtTicks);
            if (why != null) return OutOfStep(a, why);
            // a build this entry starts counts from the host's tick for it, as on the host (BuildStarts)
            BuildStartGlue.Note(a.Agency, client.ApplyingAtTicks);
            ApplyOutcome o = ActionApply.Apply(a, given, session);
            if (o == null || o.Code != 0) return o;
            // a flight's copy goes on over frames (the load path): its counters are the host's only once it is made,
            // so the glue checks them then (FlightClientGlue.Poll)
            if (o.Deferred)
            {
                flights.Begin(given.Counters);
                return o;
            }
            why = counters.After(given.Counters);
            if (why != null) return OutOfStep(a, why);
            if (payArmed)
            {
                payArmed = false;
                Log.Warn("command: fault payfail: " + ActionKind.Name(a.Kind) + " reported as not paid");
                return ActionApply.PaymentShortfall(false);
            }
            return o;
        }

        static ApplyOutcome OutOfStep(ActionRequest a, string why)
        {
            Log.Warn("command: " + ActionKind.Name(a.Kind) + " " + a.Agency + ": " + why);
            return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
        }

        string PrintOf(int kind, string agency, int body)
        {
            // the day's Missions print is compared standing in that day (the gate holds before its crossing): the
            // rows of that day's landings and cancels, as the host's close printed them
            if (kind == ObjectKind.Missions)
            {
                long day;
                if (!LocalDay(out day)) return null;
                session.FlightRead.PrintDay = day;
                return session.FlightRead.Print(agency);
            }
            if (kind == ObjectKind.SiteBuilds)
            {
                string spoilt = counters.BeforeBuildsPrint(agency);
                if (spoilt != null) Log.Warn("command: fault counter: " + spoilt);
            }
            string p = ObjectRead.Print(kind, agency, body);
            if (!printArmed || p == null || p.Length == 0) return p;
            printArmed = false;
            Log.Warn("command: fault print: one print of " + agency + " " + body + " perturbed");
            return (p[0] == '0' ? '1' : '0') + p.Substring(1);
        }

        string Restore(ObjectData d)
        {
            if (structArmed && d != null)
            {
                structArmed = false;
                Log.Warn("command: fault restorestruct: the copy of " + d.Agency + " " + d.Body + " gains a row");
                d = Spoiled(d);
            }
            return ObjectRestore.Restore(d, counters);
        }

        // The host's copy with one row this game cannot have: an extra queue entry, an extra built facility,
        // or a paused topic named twice (each refused by RestoreRules before anything is set).
        static ObjectData Spoiled(ObjectData d)
        {
            var s = new ObjectData { Code = d.Code, Day = d.Day, AfterSeq = d.AfterSeq, Piece = d.Piece,
                Pieces = d.Pieces, Kind = d.Kind, Agency = d.Agency, Body = d.Body };
            if (d.Research != null)
                s.Research = new ResearchState { Running = d.Research.Running, Progress = d.Research.Progress,
                    Queue = d.Research.Queue, Paused = With(With(d.Research.Paused, "semp_fault_topic"),
                    "semp_fault_topic"), PausedProgress = With(With(d.Research.PausedProgress, 0.0), 0.0) };
            if (d.Builds != null)
                s.Builds = new SiteBuildState { Counters = d.Builds.Counters, Building = d.Builds.Building,
                    Queue = With(d.Builds.Queue, new QueueRow { Item = "semp_fault_item", Count = 1 }) };
            if (d.Facilities != null)
                s.Facilities = new SiteFacilityState { Mining = d.Facilities.Mining, Built = With(d.Facilities.Built,
                    new FacilityRow { Item = "semp_fault_item", ItemId = int.MaxValue, Count = 1 }) };
            return s;
        }

        static T[] With<T>(T[] rows, T extra)
        {
            var list = new List<T>(rows ?? new T[0]);
            list.Add(extra);
            return list.ToArray();
        }

        // ---- the planted faults (test profiles) ----

        public void ArmFault(string why)
        {
            switch (fault)
            {
                case "print": printArmed = true; break;
                case "counter": counters.SkipNext(); break;
                case "payfail": payArmed = true; break;
                case "lateday": lateArmed = true; break;
                case "counterafter": counters.SpoilNext(); break;
                // a print is perturbed so a fetch follows, and that restore meets a structure it cannot set
                case "restorestruct": printArmed = true; structArmed = true; break;
                default: return;
            }
            Log.Info("command: fault " + fault + " armed (" + why + ")");
        }

        // The client's world as its driver sees it. The host-only questions are never asked of a client.
        sealed class ClientWorld : IActionWorld
        {
            readonly CommandClientGlue glue;

            public ClientWorld(CommandClientGlue glue) { this.glue = glue; }

            public long OriginTicks { get { return glue.origin; } }

            public long NextDayTicks
            {
                get
                {
                    TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
                    return tc != null ? NextDayOf(tc).Ticks : 0;
                }
            }

            public long GameTicks
            {
                get
                {
                    TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
                    return tc != null && GravityEngine.instance != null ? tc.CurrentTime.Ticks : 0;
                }
            }

            public bool GameOver { get { return false; } }

            public string OwnerOf(ActionRequest a) { throw new NotSupportedException("the host's question"); }
            public int Check(ActionRequest a) { throw new NotSupportedException("the host's question"); }
            public double MoneyFor(string agency) { throw new NotSupportedException("the host's question"); }
            public ObjectData Read(int kind, string agency, int body)
            {
                throw new NotSupportedException("the host's question");
            }

            public ApplyOutcome Apply(ActionRequest a, ApplyNumbers given) { return glue.Apply(a, given); }

            public string PrintOf(int kind, string agency, int body) { return glue.PrintOf(kind, agency, body); }

            public string Restore(ObjectData d) { return glue.Restore(d); }
        }
    }
}
