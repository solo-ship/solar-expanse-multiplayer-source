using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using GameManager = Manager.GameManager;
using ILaunchVehicleInfo = CustomUpdate.ILaunchVehicleInfo;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using PdsType = Game.PMMissionParameterDataSave;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;
using SpaceModule = Game.ObjectInfoDataScripts.SpaceModule;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // One mission being created through the game's own load path (plan DD-F3): the host's copy of an accepted flight,
    // or a client's copy of an entry. Plain data; FlightApply owns the live ones.
    public sealed class FlightCreation
    {
        public int Token;                  // the host's handle for it (CommandService.DeferredDone)
        public bool Host;
        public Company Company;
        public ActionRequest Entry;        // the applied entry (host: as it will be sent)
        public MissionData Mission;        // Entry.Mission
        public PMMissionParameter Parameter;
        public MissionInfo Result;         // the created mission; null when it was stopped
        public int Code;                   // 0, or why it was stopped (a RefuseCodes value)
        public string Why;                 // and in words, for the log
        public bool Abandoned;             // its time ran out: its CreateFly is stopped
        public bool Done;
        public bool CreateFlyReached;
        public bool Stopped;               // the CreateFly prefix refused it: nothing was made
        public long QueuedMs, StartedMs = -1;
        public int Frames;
        public long ScAtAccept = -1;       // host: Spacecraft.count when the acceptance began
        public long ScBefore, ScAfter, LvBefore, LvAfter;
        // the mission counter around the entry's id: the host's reserved it at acceptance; a client sets it to the
        // entry's "after" when the mission exists (the load path takes the id the entry gives, so nothing else moves it)
        public long MissionIdBefore, MissionIdAfter = -1;
        public int CopiesAtAccept;         // host: copies completed when this was accepted
        public RemovalJournal Journal;     // host: where the copy's cargo removal is recorded too
        public Action<FlightCreation> OnDone;
    }

    // The flight applies of both sides: the mod's queue callback for a mission created from an entry (DD-F3, Z-6), the
    // kinds that apply at once (cancel, pause, delete, scrap, preset), and the world calls both share. One instance per
    // session, host or client; the Harmony classes reach it through Session.Commands. Main thread.
    public sealed partial class FlightApply
    {
        static readonly AccessTools.FieldRef<GameManager, PlanMissionWindow> PmwCode =
            AccessTools.FieldRefAccess<GameManager, PlanMissionWindow>("pmwForCode");
        static readonly AccessTools.FieldRef<GameManager, PlanMissionWindow> PmwAi =
            AccessTools.FieldRefAccess<GameManager, PlanMissionWindow>("pmwForAI");
        static readonly AccessTools.FieldRef<GameManager, Company> PlayerRef =
            AccessTools.FieldRefAccess<GameManager, Company>("player");
        static readonly AccessTools.FieldRef<PMTabSchedule, PlanMissionWindow> TabWindow =
            AccessTools.FieldRefAccess<PMTabSchedule, PlanMissionWindow>("planMissionWindow");
        static readonly AccessTools.FieldRef<GameManager, List<(PMMissionParameter, Action)>> Queue =
            AccessTools.FieldRefAccess<GameManager, List<(PMMissionParameter, Action)>>("list");
        static readonly AccessTools.FieldRef<Manager.MissionInfoManager, int> LastIdRef =
            AccessTools.FieldRefAccess<Manager.MissionInfoManager, int>("lastMissionID");
        static readonly MethodInfo LoadMissionOne = AccessTools.Method(typeof(Company), "LoadMissionOne");

        readonly CommandSession session;
        readonly Dictionary<PMMissionParameter, FlightCreation> expecting =
            new Dictionary<PMMissionParameter, FlightCreation>();
        readonly List<FlightCreation> live = new List<FlightCreation>();
        FlightCreation clientCreation;
        // dev fault `noga`: the next copy's callback skips the slingshot flag's write once
        string fault;

        public FlightApply(CommandSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            this.session = session;
        }

        public int Live { get { return live.Count; } }
        public void ArmFault(string name) { fault = name; }

        // A client's copy is running its schedule step now: the rules treat this frame as part of the entry's apply
        // (only this frame: the plan's other frames are the game's own).
        public bool InClientStep { get; private set; }

        static GameManager GM { get { return MonoBehaviourSingleton<GameManager>.Instance; } }

        public static int LastMissionId
        {
            get { return LastIdRef(Manager.MissionInfoManager.Instance); }
            set { LastIdRef(Manager.MissionInfoManager.Instance) = value; }
        }

        // The window the game's own queue gives this parameter (GameManager.UpdateJobSystem).
        public static PlanMissionWindow WindowFor(PMMissionParameter p)
        {
            return ReferenceEquals(p.FlyCompany, GM.Player) && !p.Fast ? PmwCode(GM) : PmwAi(GM);
        }

        // true for the two hidden code planners: a visible planner is neither
        public static bool IsCodeWindow(PlanMissionWindow w)
        {
            GameManager gm = GM;
            return gm != null && w != null && (ReferenceEquals(w, PmwCode(gm)) || ReferenceEquals(w, PmwAi(gm)));
        }

        public static PMMissionParameter ParameterOf(PMTabSchedule tab) { return TabWindow(tab).PMMissionParameter; }

        // ---- the plans this apply queued ----

        // The parameters a flight apply queued, by object: the PlanFlyCode backstop lets exactly these through.
        public bool IsExpecting(PMMissionParameter p) { return p != null && expecting.ContainsKey(p); }

        public FlightCreation CreationOf(PMMissionParameter p)
        {
            FlightCreation c;
            return p != null && expecting.TryGetValue(p, out c) ? c : null;
        }

        // A load: the plans this apply queued belong to a world that is gone. Only these leave the game's queue; the
        // new world's own load plans (and a game-made plan) are not this apply's to drop.
        public void DropAll()
        {
            GameManager gm = GM;
            List<(PMMissionParameter, Action)> queue = gm != null ? Queue(gm) : null;
            if (queue != null) queue.RemoveAll(q => expecting.ContainsKey(q.Item1));
            foreach (PMMissionParameter p in expecting.Keys) FlightSources.Done(p);
            expecting.Clear();
            live.Clear();
            clientCreation = null;
        }

        // The game's plan queue has work, or a porkchop job runs (the host's plan hold waits for it).
        public static bool QueueBusy(GameManager gm)
        {
            List<(PMMissionParameter, Action)> queue = Queue(gm);
            if (queue != null && queue.Count > 0) return true;
            return !PmwCode(gm).PmTabSchedule.ComputePorkchopJobDone() || !PmwAi(gm).PmTabSchedule.ComputePorkchopJobDone();
        }

        public static void SetPlayer(GameManager gm, Company c) { PlayerRef(gm) = c; }

        static readonly AccessTools.FieldRef<ObjectInfoData, Dictionary<ObjectInfoData.FacilityIntTuple, Spacecraft>>
            FakeShipList = AccessTools.FieldRefAccess<ObjectInfoData,
                Dictionary<ObjectInfoData.FacilityIntTuple, Spacecraft>>("fakeSCFromFacilityList");

        // The site's cache of the fake ships its facilities' crafts are planned with.
        public static Dictionary<ObjectInfoData.FacilityIntTuple, Spacecraft> FakeShips(ObjectInfoData site)
        {
            return FakeShipList(site);
        }

        // ---- the game's own player, as the acting agency, while the load path builds a plan ----

        // LoadMissionOne reads "this == GameManager.Player" for the orbital container (D 103069, 103141): every game
        // reads the acting agency there, whoever its own player is, so the same plan builds the same way everywhere.
        public static PMMissionParameter Build(Company c, PdsType pds)
        {
            var lvAll = new List<LaunchVehicle>();
            foreach (ObjectInfo o in MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos)
                if (o != null && o.ListLaunchVehicle != null) lvAll.AddRange(o.ListLaunchVehicle);
            var list = new List<PMMissionParameter>();
            Company real = GM.Player;
            bool swap = !ReferenceEquals(c, real);
            if (swap) PlayerRef(GM) = c;
            try
            {
                LoadMissionOne.Invoke(c, new object[] { pds, list, lvAll, ShipManager.Instance.ListAllSpaceShip,
                    new[] { pds } });
            }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            finally { if (swap) PlayerRef(GM) = real; }
            if (list.Count != 1) throw new InvalidOperationException("LoadMissionOne made " + list.Count + " plans");
            return list[0];
        }

        // The plan an entry's mission stands for, set up as every game's copy has it: the load path's flags, the
        // entry's id, name and figures, no cost type of its own (an entry carries none).
        public static PMMissionParameter ParameterOf(Company c, MissionData m, bool loading)
        {
            PdsType pds = FlightMap.ToPds(c, m, m.MissionId >= 0 ? (int)m.MissionId : -1);
            PMMissionParameter p = Build(c, pds);
            p.LoadingFromSave = loading;
            if (m.Figures != null) FlightMap.ApplyFigures(p, m.Figures);
            p.SetCostType(MissionInfo.ECostType.Custom);
            return p;
        }

        // ---- a mission created from an entry ----

        // Queues the creation: the plan goes through the game's own queue with the mod's callback. The caller has
        // taken whatever the entry says is taken before the plan (a client its removals; the host did at acceptance).
        public void Begin(FlightCreation c)
        {
            c.QueuedMs = NetClock.NowMs;
            // the counters as every game has them before its copy: building the plan may already make a facility's
            // fake craft, so they are read before it, on the host and on a client alike (a client's are the entry's)
            c.ScBefore = Spacecraft.count;
            c.LvBefore = LaunchVehicle.count;
            c.Parameter = ParameterOf(c.Company, c.Mission, true);
            c.Parameter.SetMissionID((int)c.Mission.MissionId);
            // a rival's AI name gets its line break back (the wire form has none); a human's name is as sent
            c.Parameter.ChangeMissionName(MissionNames.ForGame(c.Mission.Name, c.Entry.Agency,
                !HumanAgencies.IsHuman(c.Company)));
            expecting[c.Parameter] = c;
            // the rules let exactly the plans an apply queued through (the PlanFlyCode backstop, the batch's queue,
            // the host's drop of a game-made plan and its FlightPlanned)
            FlightSources.Expect(c.Parameter);
            live.Add(c);
            GM.SetPMParameterForCodeJobSystem(c.Parameter, () => Run(c));
        }

        // Once a frame: marks the plans that have left the game's queue (the clock FlightApplyMs counts from) and
        // abandons a creation whose time has run out.
        public void Update()
        {
            if (live.Count == 0) return;
            long now = NetClock.NowMs;
            List<(PMMissionParameter, Action)> queue = Queue(GM);
            for (int i = 0; i < live.Count; i++)
            {
                FlightCreation c = live[i];
                c.Frames++;
                if (c.StartedMs < 0 && !InQueue(queue, c.Parameter)) c.StartedMs = now;
                if (c.StartedMs >= 0 && !c.Abandoned && now - c.StartedMs > ActionLimits.FlightApplyMs)
                {
                    c.Abandoned = true;
                    FlightSources.Abandon(c.Parameter);
                    Log.Warn("flight: creation of mission " + c.Mission.MissionId + " ran past " +
                        ActionLimits.FlightApplyMs + " ms; its CreateFly is stopped");
                }
            }
        }

        static bool InQueue(List<(PMMissionParameter, Action)> queue, PMMissionParameter p)
        {
            for (int i = 0; i < queue.Count; i++) if (ReferenceEquals(queue[i].Item1, p)) return true;
            return false;
        }

        // nowMs when the oldest live creation's plan left the queue; -1 while it is queued (or none runs)
        public long StartedMs
        {
            get { return live.Count == 0 ? -1 : live[0].StartedMs; }
        }

        // The mod's callback, on the window the job ran on (Z-6): what PlanFlyCode's closure does for a load plan,
        // the same for every agency, with the entry's figures written over the recomputed ones right before the step,
        // a null result handled here and ClearVariableForCode always run.
        void Run(FlightCreation c)
        {
            PlanMissionWindow pmw = WindowFor(c.Parameter);
            MissionInfo made = null;
            try
            {
                if (c.Abandoned) Fail(c, RefuseCodes.PlanFailed, "its time ran out before the plan was made");
                else
                {
                    PMTabSchedule tab = pmw.PmTabSchedule;
                    PMMissionParameter p = c.Parameter;
                    if (p.IsCustomTypeAcceleration)
                    {
                        tab.sliderConstantAcceleration.ChangeTimeSpanToMinValue();
                        tab.sliderConstantAcceleration.OnValuePorkchopeAlternativeSlider(0f);
                        tab.sliderConstantAcceleration.OnValuePorkchopeAlternativeSliderD(0f);
                    }
                    else if (p.ClickFastestButton) tab.ButtonFastestClickButton();
                    // the porkchop recomputed these from this game's own window; the entry's are the truth
                    FlightMap.ApplyFigures(p, c.Mission.Figures);
                    SetSlingshot(p, c.Mission.Figures.GravityAssistClick);
                    InClientStep = !c.Host;
                    // F10's own plan runs silent (no mission-planned toast for a supply flight, GameManager.PlanFlyCode)
                    bool silent = c.Entry.Kind == ActionKind.SupplyFlight;
                    Manager.NotificationManager notes = silent ? MonoBehaviourSingleton<Manager.NotificationManager>.Instance
                        : null;
                    if (notes != null) notes.SilentMode = true;
                    try { made = tab.OnClickScheduleButtonForCode(); }
                    finally { if (notes != null) notes.SilentMode = false; }
                    if (made != null && made.cargoAll != null)
                        made.cargoAll.cargoFuel.cargoMass = c.Mission.Figures.LeftOverFuel;
                    if (made == null && c.Code == 0) Fail(c, RefuseCodes.PlanFailed, "the schedule step made no mission");
                }
            }
            catch (Exception e)
            {
                Fail(c, RefuseCodes.PlanFailed, "the plan threw " + e.GetType().Name + " " + e.Message);
                made = null;
            }
            finally
            {
                InClientStep = false;
                try { pmw.ClearVariableForCode(); }
                catch (Exception e) { Log.Warn("flight: ClearVariableForCode threw " + e.GetType().Name); }
            }
            c.Result = made;
            c.ScAfter = Spacecraft.count;
            c.LvAfter = LaunchVehicle.count;
            expecting.Remove(c.Parameter);
            FlightSources.Done(c.Parameter);
            live.Remove(c);
            c.Done = true;
            Action<FlightCreation> done = c.OnDone;
            if (done != null) done(c);
        }

        // The slingshot flag the schedule step's OnCreatedFly reads, set the same on every game (item 21): the entry's
        // value, written after the porkchop's grid click and before the schedule step.
        void SetSlingshot(PMMissionParameter p, bool value)
        {
            if (fault == "noga")
            {
                fault = null;
                Log.Warn("flight: fault noga: the slingshot flag is left as the load path set it");
                return;
            }
            p.gravityAssistChangeDVOnForUI = value;
        }

        static void Fail(FlightCreation c, int code, string why)
        {
            if (c.Code != 0) return;
            c.Code = code;
            c.Why = why;
            Log.Warn("flight: mission " + c.Mission.MissionId + " was not created: " + why);
        }

        // The prefix on PMTabSchedule.CreateFly for a plan this apply queued. false stops the creation before anything
        // exists (the host decided it cannot go; nothing was taken that the journal does not hold). The cargo is
        // taken here, inside the journal, because the load path's CreateFly skips it (D 158143).
        public bool BeforeCreateFly(PMTabSchedule tab)
        {
            FlightCreation c = CreationOf(ParameterOf(tab));
            if (c == null) return true;
            c.CreateFlyReached = true;
            if (c.Abandoned) { Fail(c, RefuseCodes.PlanFailed, "abandoned"); c.Stopped = true; return false; }
            PMMissionParameter p = c.Parameter;
            if (c.Host)
            {
                int code = session.Flights.Recheck(c);
                if (code != 0)
                {
                    Fail(c, code, "the host's re-check refused it (" + RefuseCodes.Name(code) + ")");
                    c.Stopped = true;
                    return false;
                }
            }
            // the game's removal scraps the rows it could bind even when a later one fails, and the journal holds
            // no modules, so an unbound row stops the copy before anything is taken
            if (!BindModules(p) && !p.ForCyclicalMission)
            {
                Fail(c, RefuseCodes.PlanFailed, "a module in the cargo has no stack to come out of");
                c.Stopped = true;
                return false;
            }
            bool gameMade = c.Entry != null && c.Entry.Kind == ActionKind.FlightPlanned;
            var left = new List<string>();
            string short_ = c.Host || !WorldShared ? null : TopUpCargo(p, gameMade, left);
            if (short_ != null)
            {
                Fail(c, RefuseCodes.PlanFailed, "the cargo is short beyond the stock tolerance: " + short_);
                c.Stopped = true;
                return false;
            }
            bool ok;
            RemovalJournal was = session.Flights == null ? null : session.Flights.Recording;
            if (c.Host) session.Flights.Recording = c.Journal;
            try { ok = p.StartHermesCase.RemoveResources(p.CargoAll, p.FlyCompany, p.ForCyclicalMission); }
            finally { if (c.Host) session.Flights.Recording = was; }
            if (!ok && left.Count > 0 && !FuelRowOfOwnPropellant(p))
            {
                // the host's CreateFly flew on past the same failed removal, with the same rows untaken
                Log.Info("flight: mission " + c.Mission.MissionId + " flies with its "
                    + string.Join(", ", left.ToArray()) + " untaken, short on the host too (the game's CreateFly)");
                ok = true;
            }
            if (!ok)
            {
                Fail(c, RefuseCodes.PlanFailed, "the cargo could not be taken");
                c.Stopped = true;
                return false;
            }
            return true;
        }

        // The cargo list rows of modules need the site's module stack they come out of (the saved record does not keep
        // it): ModuleSource picks it, a finished stack first, else one still under construction as the planner offers.
        // The site is the plan's Start body, where the planner and a rival's AI find modules; StartHermesCase is the
        // ship's own body (an orbit ship's low orbit), which only the resource rows come out of. false: a row is left
        // without one.
        static bool BindModules(PMMissionParameter p)
        {
            ObjectInfoData site = p.Start.GetObjectInfoData(p.FlyCompany);
            var modules = new List<SpaceModule>();
            foreach (var f in site.ListFacility)
            {
                var stack = f as SpaceModule;
                if (stack != null) modules.Add(stack);
            }
            var taken = new long[modules.Count];
            var rows = new List<Game.ObjectInfoDataScripts.Cargo>();
            bool all = true;
            foreach (Game.ObjectInfoDataScripts.Cargo cargo in p.CargoAll.listCargo)
            {
                if (cargo.resourceTypeType != Game.ObjectInfoDataScripts.EResourceTypeType.modules
                    || cargo.moduleData == null) continue;
                int bound = cargo.SourceModule == null ? -1 : modules.IndexOf(cargo.SourceModule as SpaceModule);
                if (bound >= 0) taken[bound]++;
                else if (cargo.SourceModule == null) rows.Add(cargo);
            }
            foreach (Game.ObjectInfoDataScripts.Cargo cargo in rows)
            {
                var stacks = new List<ModuleStack>(modules.Count);
                foreach (SpaceModule m in modules)
                    stacks.Add(new ModuleStack { Same = m.facilityDescriptor == cargo.moduleData, Quantity = m.Quantity,
                        Building = m.BuildProgress < 1f && site.ProductionItem.Contains(m) });
                int i = ModuleSource.Take(stacks, taken);
                if (i < 0) { all = false; continue; }
                cargo.SourceModule = modules[i];
            }
            return all;
        }

        // A client's copy: each site's resource cargo topped up by what it lacks within the stock tolerance
        // (CargoTopUp), so the game's own removal takes what the host's took. Null, or what is short beyond it. A
        // game-made flight's rows short beyond it are added to `left` instead (the host's removal failed on them too).
        static string TopUpCargo(PMMissionParameter p, bool gameMade, List<string> left)
        {
            Game.ObjectInfoDataScripts.CargoAll all = p.CargoAll;
            ObjectInfo start = p.StartHermesCase;
            if (all == null || start == null) return null;
            string why = TopUpSite(start.GetObjectInfoData(p.FlyCompany), all.listCargo, gameMade, left);
            if (why == null && start.parentObjectInfo != null)
                why = TopUpSite(start.parentObjectInfo.GetObjectInfoData(p.FlyCompany), all.listCargoToOrbit, gameMade,
                    left);
            return why;
        }

        // After a failed removal CreateFly sets each cargo row of the ship's own propellant to what the site then
        // holds and takes it; the host's entry carries those rows already rewritten, so a copy cannot redo it alike:
        // such a flight stays out of step.
        static bool FuelRowOfOwnPropellant(PMMissionParameter p)
        {
            Game.ObjectInfoDataScripts.CargoAll all = p.CargoAll;
            if (all == null || all.cargoFuel == null || all.cargoFuel.resourceType == null) return false;
            foreach (Game.ObjectInfoDataScripts.Cargo row in all.listCargo)
                if (row != null && row.resourceType == all.cargoFuel.resourceType) return true;
            return false;
        }

        static string TopUpSite(ObjectInfoData site, List<Game.ObjectInfoDataScripts.Cargo> cargo, bool gameMade,
            List<string> left)
        {
            if (site == null || cargo == null) return null;
            var rows = new List<KeyValuePair<string, double>>();
            var defs = new Dictionary<string, ScriptableObjectScripts.ResourceDefinition>();
            foreach (Game.ObjectInfoDataScripts.Cargo row in cargo)
            {
                if (row == null || row.resourceTypeType != Game.ObjectInfoDataScripts.EResourceTypeType.resorces
                    || row.moduleData != null || row.resourceType == null) continue;
                defs[row.resourceType.ID] = row.resourceType;
                rows.Add(new KeyValuePair<string, double>(row.resourceType.ID, row.cargoMass));
            }
            if (rows.Count == 0) return null;
            string problem;
            List<string> shortHere;
            Dictionary<string, double> add = CargoTopUp.Plan(rows, id => site.CheckResources(defs[id]), gameMade,
                out problem, out shortHere);
            if (add == null) return problem;
            left.AddRange(shortHere);
            foreach (KeyValuePair<string, double> a in add)
            {
                site.AddResources(defs[a.Key], a.Value);
                Log.Info("shortfall: " + GameAgencies.Wire(site.company) + " cargo " + a.Key + " " + site.id
                    + " clamped " + a.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            return null;
        }

        // ---- a shortfall in a removal an entry names (plan DD-F2) ----

        // The one place a shortfall is decided. With M7's shared world wired every agency's stock is in step, so the
        // world tolerance decides for all of them (the stock area's, as the market's and a rival's replay): a gap
        // within it is a game's timing, clamped and logged; more is out of step. Without it (M6, or
        // -semp-no-world-rules) a human agency's is out of step (PaymentFailed) and a rival's takes what is on hand,
        // unless FlightRules.RivalStockStrict. Returns the amount to take, or -1 for out of step.
        public static double Shortfall(string agency, double needed, double onHand, bool human)
        {
            if (onHand >= needed) return needed;
            if (WorldShared)
            {
                double clamped;
                if (!WorldTolerance.Shortfall(4, onHand, needed, out clamped)) return -1;
                Log.Info("shortfall: " + agency + " resource " + clamped.ToString("R",
                    System.Globalization.CultureInfo.InvariantCulture) + " clamped");
                return Math.Max(0, onHand);
            }
            if (human || FlightRules.RivalStockStrict) return -1;
            Log.Warn("flight: " + agency + " is short " + (needed - onHand).ToString("R",
                System.Globalization.CultureInfo.InvariantCulture) + " of a removal; the rest is clamped");
            return Math.Max(0, onHand);
        }

        static bool WorldShared
        {
            get { return HumanAgencies.Active && HumanAgencies.State != null && HumanAgencies.State.World.Wired; }
        }
    }
}
