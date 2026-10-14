using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Company = Game.Company;

namespace SolarExpanseMultiplayer
{
    // The root of the commands (plan "Game seams"): owned by Session for the length of a session, it owns the host's
    // command driver (CommandService), its game side (CommandHost), the output budgets, the numbers the rules recorded
    // for the entries they raise, the action script and the test profile's triggers and faults. No loose static
    // state: the Harmony classes reach it through Session.Commands. The HumanSet is not here: it is the session's and
    // survives every load (N1). Main thread only.
    //
    // It is also the host's IHostDecisions: the rules raise their entries through it into the stream.
    //
    // A client's session (ForClient) has the same root with the client's driver in place of the host's: its glue
    // (CommandClientGlue) applies the host's stream, its human set is the last snapshot's, and IsHost is false.
    public sealed partial class CommandSession : IHostDecisions, ICaptureOwner
    {
        readonly bool host;
        readonly HostTime time;
        readonly HumanSet humans;
        readonly CommandHost world;
        readonly WorldHost worldHost;
        readonly CommandClientGlue client;
        readonly Dictionary<int, OutputBudget> budgets = new Dictionary<int, OutputBudget>();
        // the numbers a rule recorded for the entry it raised, until the entry's apply takes them (by the request
        // object itself: two raised entries can be equal field by field)
        readonly Dictionary<ActionRequest, ApplyNumbers> decided =
            new Dictionary<ActionRequest, ApplyNumbers>(new Same());
        const int MaxDecided = 4096;
        AgencyLedger ledger;
        ActionRequest[] script;
        string fault;          // the test profile's planted host fault, if any
        float nextDevPoll;
        string notice;
        long noticeUntil;
        readonly RefusalPopup refusals = new RefusalPopup();

        public CommandSession(HostTime time, HumanSet humans, Func<bool> gameOver, string actionsFile,
            string commandFault)
        {
            if (time == null) throw new ArgumentNullException("time");
            if (humans == null) throw new ArgumentNullException("humans");
            host = true;
            this.time = time;
            this.humans = humans;
            world = new CommandHost(this, time, gameOver);
            worldHost = new WorldHost(time);
            fault = commandFault;
            if (actionsFile != null) script = LoadScript(actionsFile);
            Steps = new StepGate(this);
            InitFlights();
            Flights = new FlightHost(this);
            FlightRead.OriginTicks = () => time.OriginTicks;
            FlightRead.Today = () =>
            {
                long day;
                return DayKey.TryCurrent(time.NextDay, time.OriginTicks, out day) ? day : 0;
            };
            // a seat entry changes which agencies' impacts the host cuts its steps at
            humans.Changed += () => Steps.Watch.Dirty = true;
            Capture = NewCapture();
        }

        CommandSession(Func<Message, bool> sendToHost, Action<string> leave, string actionsFile, string commandFault,
            bool noDayGate, bool noRules)
        {
            host = false;
            fault = commandFault;
            Steps = new StepGate(this);
            InitFlights();
            client = new CommandClientGlue(this, sendToHost, leave, commandFault, noDayGate, noRules);
            // a client's day and its origin are its own world's (the snapshot header's origin)
            CommandClientGlue glue = client;
            FlightRead.OriginTicks = () => glue.Origin;
            FlightRead.Today = () =>
            {
                long day;
                return glue.Loaded && glue.LocalDay(out day) ? day : 0;
            };
            if (actionsFile != null) script = LoadScript(actionsFile);
            Capture = NewCapture();
        }

        // A client's commands: sendToHost is its link's Send; leave takes the full-rejoin path, given why.
        public static CommandSession ForClient(Func<Message, bool> sendToHost, Action<string> leave, string actionsFile,
            string commandFault, bool noDayGate, bool noRules)
        {
            return new CommandSession(sendToHost, leave, actionsFile, commandFault, noDayGate, noRules);
        }

        // The host's driver, once Attach has given it the join's seats and link; null before (and on a client).
        public CommandService Service { get; private set; }
        public ICommandStream Stream { get { return Service; } }
        public CommandHost World { get { return world; } }
        // The client's glue; null on the host.
        public CommandClientGlue ClientGlue { get { return client; } }
        // The fixed step's day gate and impact holds (DayGateHook), on both sides.
        public StepGate Steps { get; private set; }
        public HumanSet Humans { get { return host ? humans : client.Humans; } }
        public bool IsHost { get { return host; } }

        // M6: the flights. The host's game half (acceptance, the copy, the plan hold's world); the apply, the reads and
        // the watches, which a client has too.
        public FlightHost Flights { get; private set; }
        public FlightApply FlightApply { get; private set; }
        public FlightRead FlightRead { get; private set; }
        public FlightWatches FlightWatches { get; private set; }
        public FlightDev FlightDev { get; private set; }
        public readonly PlanHold PlanHold = new PlanHold();

        void InitFlights()
        {
            FlightDev = new FlightDev(this);
            FlightApply = new FlightApply(this);
            FlightRead = new FlightRead();
            FlightWatches = new FlightWatches();
        }

        // The flight sources' state is the rules' and is made again by every HumanAgencies.Start, so each Start is
        // followed by this: the host's reader of game-made plans (none on a client: it never raises one) and the watch
        // lists told of every cancel the rules make. Without it every game-made plan on the host is a host fault.
        public void RegisterFlights()
        {
            FlightSources.Register(host ? new FlightPlanReader() : null, FlightCancelled);
        }

        void FlightCancelled(long missionId)
        {
            FlightWatches.Dirty();
            Steps.Watch.Dirty = true;
        }

        // What the window captures (Task 9) ask and send through: this session's guard, click, pending values and
        // notes. A host has no pending value: its own click is applied at once.
        public ICaptureSeam Capture { get; private set; }

        ICaptureSeam NewCapture()
        {
            ClickCapture = CaptureEntry.Click;   // the dev trigger "click <n>" drives the window
            return new CaptureSeam(() => Active, () => Applying, Click,
                (string key, out int value) =>
                {
                    value = 0;
                    return !host && client.Client.TryPendingValue(key, out value);
                },
                Notify, ShowRefusal);
        }

        // A session is running with commands (the capture's and the rules' first question).
        public bool Active { get { return host ? Service != null : client != null; } }

        // An entry is being applied (or a rule's record is open): the captures let the game's own code run, and the
        // rules leave the entry's own changes alone.
        public bool Applying
        {
            get
            {
                // a client's copy of a flight is part of its entry's apply only in the frame of its schedule step
                if (!host) return client.Applying || FlightApply.InClientStep;
                CommandService s = Service;
                return s != null && s.Applying;
            }
        }

        public int ApplyingKind
        {
            get
            {
                if (!host) return client.Client.ApplyingKind;
                CommandService s = Service;
                return s != null ? s.ApplyingKind : -1;
            }
        }

        public ActionRequest ApplyingEntry
        {
            get
            {
                if (!host) return client.Client.ApplyingEntry;
                CommandService s = Service;
                return s != null ? s.ApplyingEntry : null;
            }
        }

        // gameover: the host's world reports the game over, so every request is refused GameOver.
        public bool PlantedGameOver { get { return fault == "gameover"; } }

        // The driver, built once the join has made its seat ledger: the seats are the ledger's (AgencyAccess's one
        // ownership check), the link the join's, each player's output budget its own.
        public void Attach(AgencyLedger ledger, Func<int, bool> inGame, IJoinLink link)
        {
            if (Service != null) throw new InvalidOperationException("the command driver is attached already");
            var limiter = new ActionLimiter(ActionLimits.SetPerSecond, ActionLimits.DoPerSecond, ActionLimits.DoBurst,
                ActionLimits.MaxHeldSetKeys);
            var service = new CommandService(world, link, new LedgerSeats(ledger, inGame), limiter, humans, BudgetOf,
                () => NetClock.NowMs);
            service.StateCounters = world.StateCounters;
            service.StateLifeSupport = world.StateLifeSupport;
            service.World = worldHost;
            service.WorldSide = worldHost;
            service.Published += OnPublished;
            service.Answered += OnAnswered;
            service.ResumeEnabled = Boot.Dev == null || !Boot.Dev.NoResume;
            service.ResumeStarted += p => SetCatchUpPause(p, true);
            service.ResumeFinished += (p, released) => SetCatchUpPause(p, false);
            service.Flights = Flights;
            Service = service;
            this.ledger = ledger;
            ArmFault("session start");
        }

        OutputBudget BudgetOf(int player)
        {
            OutputBudget b;
            if (!budgets.TryGetValue(player, out b))
                budgets[player] = b = new OutputBudget(ActionLimits.HostOutMsgsPerSecond, ActionLimits.HostOutMsgBurst,
                    ActionLimits.HostOutBytesPerSecond, ActionLimits.HostOutByteBurst);
            return b;
        }

        // ---- requests ----

        // A player's request, with the time the network thread read it. A non-null answer is a protocol violation:
        // the caller closes the link and revokes the player's keys.
        public string FromPlayer(int player, ActionRequest a, long arrivalMs)
        {
            CommandService s = Service;
            return s == null ? null : s.FromPlayer(player, a, arrivalMs);
        }

        public void ObjectWanted(int player, ObjectRequest r)
        {
            CommandService s = Service;
            if (s != null) s.ObjectWanted(player, r);
        }

        public void HealWanted(int player, HealRequest r)
        {
            CommandService s = Service;
            if (s != null) s.HealWanted(player, r);
        }

        public void ResumeProgressFrom(int player, ResumeProgress m)
        {
            CommandService s = Service;
            if (s != null) s.ResumeProgressFrom(player, m);
        }

        // The clock's CatchingUp pause for a player (set by the join host, which owns the clock): on while the host
        // replays what it missed, off when it is released or the resume fails.
        public Action<int, bool> CatchUpPause { get; set; }

        void SetCatchUpPause(int player, bool on)
        {
            Action<int, bool> a = CatchUpPause;
            if (a == null) return;
            try { a(player, on); }
            catch (Exception e) { Log.Warn("resume: the catch-up pause threw: " + e.GetType().Name); }
        }

        // The host player's own click (player 0): the same checks and limits as anyone's. A client's goes to the host.
        public void Click(ActionRequest a)
        {
            if (a == null) return;
            if (!host)
            {
                client.Click(a);
                return;
            }
            CommandService s = Service;
            if (s != null) s.Local(a);
        }

        // The window-capture hook the dev trigger "click" drives (Task 9 registers it); until then "click" is refused.
        public Func<ActionRequest, bool> ClickCapture { get; set; }

        // Once a frame (JoinHost.Update, before the join driver: a batch that ends there applies its held requests
        // from the next frame on).
        // A client's from Session.Update, after its link's messages of the frame.
        public void Update()
        {
            refusals.Update();
            if (host)
            {
                CommandService s = Service;
                if (s == null) return;
                s.Update();
                FlightApply.Update();
            }
            else
            {
                // before the stream: a creation that has left the game's queue is timed from then (FlightClientGlue)
                FlightApply.Update();
                client.Update();
                ClientGlue.Flights.UpdateLate();   // DD-F6: a held late launch whose outcome heads the stream
            }
            PlannerWait.Update();
            if (!DevTriggers.Every(ref nextDevPoll, UnityEngine.Time.realtimeSinceStartup)) return;
            // a dev trigger (click, reload, ...) that throws must not stop the frame
            try { DevPoll(); }
            catch (Exception e) { Log.Error("command: a dev trigger threw: " + ExceptionText.Chain(e)); }
        }

        // The host loaded a save in the session: nothing the rules held for the old world survives (the session's
        // human set does), and the stream starts again (the join driver's HostLoaded, called next).
        public void OnHostLoaded()
        {
            HumanAgencies.ResetForLoad();
            if (worldHost != null) worldHost.OnHostLoaded();
            Steps.Loaded();
            FlightWatches.Loaded();
            FlightRead.Reset();
            PlanHold.Reset();
            world.ForgetPoint();
            Capture.Memory.Clear();
            decided.Clear();
            ReconcileHumans();
            foreach (string agency in humans.All())
            {
                Company c = GameAgencies.Find(agency);
                Reseat(agency, c);
                double money = c != null && c.MoneyController != null ? c.MoneyController.CurrentMoney : double.NaN;
                if (HumanMoney.IsReal(money))
                    Log.Info("money: " + agency + " " + StreamLines.Money(money) + " (kept finite at load)");
                else Log.Error("money: " + agency + " is " + StreamLines.Money(money) + " after the load");
            }
            ArmFault("host load");
        }

        // The load may have moved the host to another agency, and the save may not have an agency a joiner holds: the
        // set follows the ledger's seats. An agency no player holds goes back to the computer; one a joiner holds but
        // the save lacks frees that joiner's seat (it rejoins and claims again). Before the new stream, so no
        // AgencyState is made from a set that disagrees with the seats.
        void ReconcileHumans()
        {
            if (ledger == null) return;
            try
            {
                HumanReconcile plan = HumanReconcile.Plan(humans.All(), ledger,
                    id => GameAgencies.Find(id) != null);
                foreach (int player in plan.Unseated)
                {
                    Log.Warn("command: player " + player + "'s agency is not in the loaded save; its seat is freed");
                    ledger.Forget(player);
                }
                ActionApply.FollowSeats(humans, plan);
            }
            catch (Exception e) { Log.Error("command: the human set could not follow the seats: " + e.GetType().Name); }
        }

        // The human set outlives the load, but the save may not know it: a save from before an agency was claimed
        // keeps it a computer rival with +Infinity money. Each human agency is seated again as its claim seated it
        // (SeatClaimed: its money the host's, by the same rule MoneyFor gives a claim; its crew-death notices unset;
        // its AI is switched off by JoinHost.OnSaveLoaded just before), before any AgencyState is built. A money the
        // save already holds as a real amount is kept. A start that cannot give an opening is a new stream.
        void Reseat(string agency, Company c)
        {
            if (c == null || c.MoneyController == null) return;
            try
            {
                bool opened;
                double money = HumanMoney.For(c.MoneyController.CurrentMoney, () => CommandHost.OpeningMoney(c),
                    out opened);
                ActionApply.Seat(c, money, opened);
                if (opened) Log.Info("money: " + agency + " " + StreamLines.Money(money) + " (opening, seated again)");
            }
            catch (Exception e)
            {
                Log.Error("money: " + agency + " could not be seated again after the load: " + e.GetType().Name + " "
                    + TextSanitiser.OneLine(e.Message));
            }
        }

        // ---- the day (DayCloser) ----

        // Before the game's day event: true when it fires in this step and the driver closed (or re-entered) its day.
        // A day key that cannot be made is a new stream, never a throw into the fixed step.
        public bool BeforeDayEvent(long nowTicks, long nextDayTicks)
        {
            CommandService s = Service;
            if (s == null || nowTicks < nextDayTicks) return false;
            try
            {
                long day;
                if (!DayKey.TryCurrent(nextDayTicks, time.OriginTicks, out day))
                {
                    s.HostFault("no day key for nextDay " + nextDayTicks + " (origin " + time.OriginTicks + ")");
                    return false;
                }
                s.DayClosing(day);
                return true;
            }
            catch (Exception e) { Log.Error("command: closing the day failed: " + e.GetType().Name); return false; }
        }

        public void AfterDayEvent()
        {
            try { CommandService s = Service; if (s != null) s.DayEventDone(); }
            catch (Exception e) { Log.Error("command: ending the day event failed: " + e.GetType().Name); }
        }

        // ---- IHostDecisions: the rules' entries ----

        public void AgencyBankrupt(string agency)
        {
            CommandService s = Service;
            Company c = Game(agency);
            // the day event repeats a failed payment until the stop is applied; one stop per agency
            if (s == null || c == null || !c.enabled) return;
            s.HostDecided(new ActionRequest { Kind = ActionKind.AgencyBankrupt, Agency = GameAgencies.Wire(c) });
        }

        public void CraftFinished(CraftTaken t)
        {
            if (t == null) return;
            string agency = WireOf(t.Agency);
            var a = new ActionRequest { Kind = ActionKind.CraftFinished, Agency = agency, Body = t.Body, Item = t.Item,
                ItemId = t.ItemId };
            var n = new ApplyNumbers
            {
                Counters = new[]
                {
                    new CounterRow { Which = CounterKind.SpacecraftCount, Before = t.SpacecraftBefore,
                        After = t.SpacecraftAfter },
                    new CounterRow { Which = CounterKind.LaunchVehicleCount, Before = t.LaunchVehicleBefore,
                        After = t.LaunchVehicleAfter },
                    new CounterRow { Which = CounterKind.ProductionCountId, Agency = agency,
                        Before = t.ProductionBefore, After = t.ProductionAfter }
                },
                Draws = t.Draws != null && t.Draws.Length > 0 ? t.Draws : null
            };
            Decided(a, n);
        }

        // The request names the agency as the game does (its Company.ID); the stream carries the wire id.
        public void Decided(ActionRequest request, ApplyNumbers numbers)
        {
            CommandService s = Service;
            if (s == null || request == null) return;
            request.Agency = WireOf(request.Agency);
            // numbers left by entries a stream reset dropped are never taken; they must not pile up
            if (decided.Count >= MaxDecided)
            {
                Log.Warn("command: " + decided.Count + " recorded entries were never applied; dropped");
                decided.Clear();
            }
            decided[request] = numbers ?? new ApplyNumbers();
            s.HostDecided(request);
            // applied at once unless it was raised inside another apply (it is then queued behind it and takes its
            // numbers when its turn comes)
            if (!s.Applying) decided.Remove(request);
        }

        public void HostFault(string why)
        {
            CommandService s = Service;
            if (s != null) s.HostFault(why);
        }

        // The entry's apply on the host takes the numbers the rule recorded (ActionApply).
        public ApplyNumbers TakeDecided(ActionRequest a)
        {
            ApplyNumbers n;
            if (a == null || !decided.TryGetValue(a, out n)) return null;
            decided.Remove(a);
            return n;
        }

        static Company Game(string id)
        {
            Company c = GameAgencies.Find(WireOf(id));
            return c;
        }

        static string WireOf(string id) { return AgencyWire.From(id); }

        // ---- what the host player reads (Session.Status) ----

        public string Status
        {
            get
            {
                string said = notice != null && NetClock.NowMs < noticeUntil ? notice : null;
                return host ? said : client.Status ?? said;
            }
        }

        void Notify(string text)
        {
            notice = text;
            noticeUntil = NetClock.NowMs + ActionLimits.NoticeShowMs;
        }

        // A refusal of this player's own request: on the status line and in the game's dialog.
        internal void ShowRefusal(string text)
        {
            Notify(text);
            refusals.Post(text);
        }

        void OnAnswered(int player, ActionRequest a, int code)
        {
            if (player == 0 && RefusalPopupGate.Shows(code)) ShowRefusal(CommandText.Refused(code));
            if (player == 0 && code != 0) PlannerWait.Refused(a);
        }

        void OnPublished(ActionResult r)
        {
            string line = StreamLines.Of(r);
            if (line != null) Log.Info(line);
            Steps.Watch.Dirty = true;   // an entry may have changed which impacts are in flight
            FlightLog.Ids(r);
            WindowRefresh.After(r.Action);
            if (ResearchCommand.IsResearch(r.Action.Kind)) world.ResearchChanged(r.Action.Agency);
            if (r.Action.Kind != ActionKind.AgencyBankrupt) return;
            Company c = GameAgencies.Find(r.Action.Agency);
            Company player = MonoBehaviourSingleton<Manager.GameManager>.Instance.Player;
            Notify(CommandText.Bankrupt(c != null ? c.ID : r.Action.Agency, ReferenceEquals(c, player)));
        }

        // ---- the test profile: the action script, the triggers, the planted faults ----

        static ActionRequest[] LoadScript(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > ActionLimits.MaxScriptBytes)
                {
                    Log.Error("command: the action script is missing or over " + ActionLimits.MaxScriptBytes
                        + " bytes; act is refused");
                    return null;
                }
                string error;
                ActionRequest[] lines = ActionScript.Parse(File.ReadAllText(file), out error);
                if (lines == null) Log.Error("command: the action script is refused: " + TextSanitiser.OneLine(error));
                else Log.Info("command: action script of " + lines.Length + " requests");
                return lines;
            }
            catch (Exception e)
            {
                Log.Error("command: the action script could not be read: " + TextSanitiser.OneLine(e.Message));
                return null;
            }
        }

        void DevPoll()
        {
            string act = DevTriggers.Take("act");
            if (act != null) Act(act);
            string click = DevTriggers.Take("click");
            if (click != null) DevClick(click);
            string hold = DevTriggers.Take("dayhold");
            if (hold != null) DayHold(hold);
            string trip = DevTriggers.Take("trip");
            if (trip != null) CaptureEntry.Trip(trip);
            string reload = DevTriggers.Take("reload");
            if (reload != null) Reload(reload);
            DevWorldPoll();
            string fly = DevTriggers.Take("fly");
            if (fly != null) FlightDev.Fly(fly);
            string abort = DevTriggers.Take("abort");
            if (abort != null) FlightDev.Abort(abort);
            string scrap = DevTriggers.Take("scrap");
            if (scrap != null) FlightDev.Scrap(scrap);
            string pause = DevTriggers.Take("route-pause");
            if (pause != null) FlightDev.RoutePause(pause);
            string delete = DevTriggers.Take("route-delete");
            if (delete != null) FlightDev.RouteDelete(delete);
            if (DevTriggers.Take("fault") == null) return;
            if (host) ArmFault("dev trigger");
            else client.ArmFault("dev trigger");
        }

        // `reload [save name]`: the host loads that save (the newest with no name) through the game's own load, as
        // its player would from the load window mid-session: the host-load path then runs as in play.
        void Reload(string text)
        {
            if (!host)
            {
                Log.Warn("command: reload refused: only the host loads a save mid-session");
                return;
            }
            string why;
            string name = HostReload.SaveName(text, out why);
            if (name == null)
            {
                Log.Warn("command: reload refused: " + why);
                return;
            }
            Manager.LoadSaveManager saves = SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance;
            if (saves == null)
            {
                Log.Warn("command: reload refused: the game's save manager is not there");
                return;
            }
            Log.Info("command: reload: the host loads " + (name.Length == 0 ? "its newest save" : "save " + name));
            if (name.Length == 0) saves.LoadLastSave();
            else if (!saves.Load(name)) Log.Warn("command: reload: no save " + name + " (nothing loaded)");
        }

        // `act <n>`: line n of the script (1 = the first request), as the host player's own click.
        void Act(string text)
        {
            ActionRequest a = ScriptLine(text, "act");
            if (a != null) Click(a);
        }

        // `click <n>`: line n through the window's own handler, so the capture runs. Refused until a capture exists:
        // the handler alone would change this game only.
        void DevClick(string text)
        {
            ActionRequest a = ScriptLine(text, "click");
            if (a == null) return;
            Func<ActionRequest, bool> capture = ClickCapture;
            if (capture == null)
            {
                Log.Warn("command: click " + text + " refused: no window capture is registered");
                return;
            }
            if (!capture(a)) Log.Warn("command: click " + text + ": no window handler for " + ActionKind.Name(a.Kind));
        }

        ActionRequest ScriptLine(string text, string trigger)
        {
            int n;
            if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out n))
            {
                Log.Warn("command: " + trigger + " needs a line number");
                return null;
            }
            if (script == null) { Log.Warn("command: " + trigger + " " + n + ": no action script"); return null; }
            if (n < 1 || n > script.Length)
            {
                Log.Warn("command: " + trigger + " " + n + ": the script has " + script.Length + " requests");
                return null;
            }
            return ActionCopy.Of(script[n - 1]);   // a fresh request each time: the driver keeps what it is given
        }

        void DayHold(string text)
        {
            CommandService s = Service;
            if (s == null) { Log.Warn("command: dayhold is the host's"); return; }
            if (text != "on" && text != "off") { Log.Warn("command: dayhold needs on or off"); return; }
            s.HoldCloses(text == "on");
            Log.Info("command: dayhold " + text + (text == "on" ? ": DayClosed is held" : ": DayClosed goes out"));
        }

        // The planted host faults fire once per arming: at the session's start, after a host load and on the dev
        // trigger "fault". The clients' faults are the client's own.
        void ArmFault(string why)
        {
            CommandService s = Service;
            if (s == null || fault == null) return;
            switch (fault)
            {
                case "seqgap": case "dropentry": s.DropNextEntry(fault); break;
                case "fetchlost": s.LoseNextFetch(fault); break;
                case "gameover": break;
                default: return;
            }
            Log.Info("command: fault " + fault + " armed (" + why + ")");
        }

        // Reference equality for the recorded numbers.
        sealed class Same : IEqualityComparer<ActionRequest>
        {
            public bool Equals(ActionRequest x, ActionRequest y) { return ReferenceEquals(x, y); }
            public int GetHashCode(ActionRequest a) { return RuntimeHelpers.GetHashCode(a); }
        }
    }
}
