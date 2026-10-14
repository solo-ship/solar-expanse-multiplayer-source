using System;
using System.Collections.Generic;
using Company = Game.Company;
using FacilityIntTuple = Game.ObjectInfoDataScripts.ObjectInfoData.FacilityIntTuple;
using GameManager = Manager.GameManager;
using ILaunchVehicleInfo = CustomUpdate.ILaunchVehicleInfo;
using ISpacecraftInfo = Game.Info.ISpacecraftInfo;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The host half of a shared flight (plan DD-F2, DD-F3): the synchronous acceptance of a player's flight (checks,
    // sanity, the game's own schedule step for the acting agency stopped at CreateFly, all inside CraftIdScope and the
    // removal journal), then the host's own copy of the mission through the load path, and the entry once it is made.
    // Also the host's IFlightWorld for the driver (plans pending, the prints a day owes). One per host session.
    public sealed class FlightHost : IFlightWorld
    {
        // Each pending acceptance's fakes, for CraftIdScope's undo: key -> the site and the facility key.
        sealed class Fake { public ObjectInfoData Site; public FacilityIntTuple Key; }

        readonly CommandSession session;
        readonly CraftIdScope scope = new CraftIdScope();
        readonly Dictionary<string, Fake> fakes = new Dictionary<string, Fake>();
        readonly List<FlightCreation> pending = new List<FlightCreation>();
        int tokens, copiesDone;
        bool accepting;
        PMMissionParameter acceptParameter;
        bool acceptReached;
        string fault;

        public FlightHost(CommandSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            this.session = session;
        }

        // The acting agency while an acceptance runs (null otherwise): the schedule step's "is this the player" tests
        // read it, and the journal's prefixes record only then.
        public Company Acting { get; private set; }
        // Where the game's removals are recorded now, or null.
        public RemovalJournal Recording { get; set; }
        public bool Accepting { get { return accepting; } }
        public int Pending { get { return pending.Count; } }

        static GameManager GM { get { return MonoBehaviourSingleton<GameManager>.Instance; } }

        public void ArmFault(string name) { fault = name; }

        // ---- the driver's view (IFlightWorld, host half) ----

        // A plan is in the game's queue or its porkchop runs: the host's day cut waits for it.
        public bool PlansPending
        {
            get
            {
                GameManager gm = GM;
                if (gm == null) return false;
                return FlightApply.QueueBusy(gm);
            }
        }

        // HostLoaded: the queued plans belong to the world that was dropped.
        public void DropQueued()
        {
            pending.Clear();
            session.FlightApply.DropAll();
            Log.Info("flight: queued plans dropped for the load");
        }

        public IList<PrintEntry> FlightTouches(long day)
        {
            return session.FlightRead.Touches(day, session.Humans.All());
        }

        // The client half is the client glue's.
        public ApplyOutcome Poll() { throw new NotSupportedException("the host creates through DeferredDone"); }
        public long ApplyStarted { get { return -1; } }
        public bool Landed(long missionId) { throw new NotSupportedException("a client's question"); }

        // The host's plan hold ran out: every accepted flight whose copy has not reached CreateFly is stopped (its
        // CreateFly refuses, the journal rolls back, the player is told PlanFailed).
        public void AbandonPending()
        {
            foreach (FlightCreation c in pending)
                if (!c.CreateFlyReached) c.Abandoned = true;
            if (pending.Count > 0) Log.Warn("flight: the plan hold ran out with " + pending.Count + " creations pending");
        }

        // ---- the fakes a facility craft's plan makes (CraftIdScope) ----

        // From the prefix on ObjectInfoData.GetFakeSCFromFacility: the site, the key and what the cache held.
        public void NoteFake(ObjectInfoData site, FacilityIntTuple key, string typeId, Spacecraft cached)
        {
            if (!accepting) return;
            string k = "F" + typeId + "#" + key.i;
            fakes[k] = new Fake { Site = site, Key = key };
            scope.Touched(k, cached != null ? cached.ID : -1, cached != null);
        }

        // ---- the acceptance ----

        public ApplyOutcome Apply(ActionRequest a)
        {
            switch (a.Kind)
            {
                case ActionKind.FlightSchedule:
                case ActionKind.SupplyFlight: return Accept(a);
                default: return session.FlightApply.ApplyAtOnce(a, true);
            }
        }

        static ApplyOutcome Refused(ActionRequest a, int code, string why)
        {
            Log.Info("flight: " + ActionKind.Name(a.Kind) + " " + a.Agency + " refused " + RefuseCodes.Name(code)
                + ": " + why);
            return new ApplyOutcome { Code = code };
        }

        // Steps 1 to 5 of DD-F2: nothing is sent; the stock, fuel and money are taken; the host's copy is queued.
        ApplyOutcome Accept(ActionRequest a)
        {
            Company c = GameAgencies.Find(a.Agency);
            if (c == null) return Refused(a, RefuseCodes.BadTarget, "no such agency");
            CommandHost world = session.World;
            long nextDay = world.NextDayTicks, now = world.GameTicks;
            int code = FlightChecks.Check(a, nextDay);
            if (code != 0) return Refused(a, code, "the checks");
            MissionData m = a.Mission;
            string bad = FlightSanity.Problem(m, a.Agency, FlightChecks.Facts(a, c, now, nextDay));
            if (bad != null) return Refused(a, RefuseCodes.PlanFailed, bad);
            if (!UniqueFuelRow(m)) return Refused(a, RefuseCodes.PlanFailed, "more than one fuel row");

            var journal = new RemovalJournal();
            var containers = new ContainerState();
            containers.Take();
            long scBefore = Spacecraft.count, lvBefore = LaunchVehicle.count;
            int lastId = FlightApply.LastMissionId;
            fakes.Clear();
            scope.Begin(scBefore, lvBefore);
            PMMissionParameter pmp = null;
            MissionInfo made = null;
            Exception error = null;
            int stop = 0;
            string why = null;
            try
            {
                Acting = c;
                // the schedule step's "is this the player" tests, its container swap and pool, and the buy-fuel popups
                // read the acting agency (FlightRules), and only while this acceptance runs
                FlightRules.BeginActing(c);
                accepting = true;
                acceptReached = false;
                Recording = journal;
                pmp = FlightApply.ParameterOf(c, m, false);
                acceptParameter = pmp;
                if (m.Figures.GravityAssistClick && !pmp.GravityAssist)
                {
                    stop = RefuseCodes.PlanFailed;
                    why = "the slingshot flag is set on a plan with no gravity assist";
                }
                if (stop == 0) pmp.ChangeStage(PlanMissionWindow.EStageWindow.Schedule);
                if (stop == 0) stop = StockCheck(pmp, m, c, out why);
                if (stop == 0)
                {
                    PlanMissionWindow pmw = FlightApply.WindowFor(pmp);
                    try
                    {
                        pmw.SetPMParameterForCode(pmp);
                        made = pmw.PmTabSchedule.OnClickScheduleButtonForCode();
                    }
                    finally { pmw.ClearVariableForCode(); }
                    if (!acceptReached) { stop = RefuseCodes.PlanFailed; why = "the schedule step refused the plan"; }
                }
            }
            catch (Exception e) { error = e; }
            finally
            {
                accepting = false;
                Recording = null;
                Acting = null;
                FlightRules.EndActing();
                acceptParameter = null;
                // the craft ids and counters as the acceptance found them, whatever happened (revision 15)
                IList<CraftIdUndo> undo = scope.End(Spacecraft.count, lvBefore);
                if (fault == "fakeleft")
                {
                    fault = null;
                    Log.Warn("flight: fault fakeleft: the acceptance's undo is skipped once");
                }
                else RunUndo(undo);
            }
            containers.Undo();
            if (error != null || stop != 0)
            {
                Roll(journal, c);
                return Refused(a, stop != 0 ? stop : RefuseCodes.PlanFailed,
                    error != null ? "the step threw " + error.GetType().Name + " " + error.Message : why);
            }
            if (pmp.Transaction == null) { Roll(journal, c); return Refused(a, RefuseCodes.PlanFailed, "no transactions"); }

            // the entry as it will be sent: the client's trajectory, the host's id, name, removals and crafts
            MissionData ap = m.Copy();
            ap.MissionId = Manager.MissionInfoManager.Instance.GetNewMissionID();
            ap.Name = FinalName(m, pmp);
            ap.Removals = FlightMap.RemovalsOf(pmp.Transaction);
            bool container = false;
            ap.Ships = FlightMap.ShipRefs(pmp.SCList != null && pmp.SCList.Count > 0 ? pmp.SCList
                : new List<ISpacecraftInfo> { pmp.SC });
            foreach (CraftRef r in ap.Ships) if (r.Kind == CraftKinds.Container) container = true;
            if (pmp.LVList != null && pmp.LVList.Count > 0) ap.Rockets = FlightMap.RocketRefs(pmp.LVList);
            ap.Container = container ? 1 : 0;
            if (container) ap.Flags |= MissionFlags.OrbitalContainer;
            ap.Cargo = WithFuelRow(m.Cargo, pmp, m.Figures.LeftOverFuel);
            string invalid = ap.Validate(true);
            if (invalid != null)
            {
                Roll(journal, c);
                return Refused(a, RefuseCodes.PlanFailed, "the finished mission is not sendable: " + invalid);
            }
            ActionRequest applied = ActionCopy.Of(a);
            applied.Mission = ap;
            applied.Body = ap.Start;

            var cr = new FlightCreation { Token = ++tokens, Host = true, Company = c, Entry = applied, Mission = ap,
                Journal = journal, ScAtAccept = scBefore, MissionIdBefore = lastId, MissionIdAfter = ap.MissionId,
                CopiesAtAccept = copiesDone, OnDone = Complete };
            pending.Add(cr);
            try { session.FlightApply.Begin(cr); }
            catch (Exception e)
            {
                pending.Remove(cr);
                Roll(journal, c);
                return Refused(a, RefuseCodes.PlanFailed, "the copy could not be queued: " + e.GetType().Name);
            }
            Log.Info("flight: " + ActionKind.Name(a.Kind) + " " + a.Agency + " mission " + ap.MissionId + " "
                + ap.Start + "->" + ap.Target + " depart " + ap.DepartTicks + " arrive " + ap.ArriveTicks);
            return new ApplyOutcome { Deferred = true, Token = cr.Token, Applied = applied };
        }

        static bool UniqueFuelRow(MissionData m)
        {
            int n = 0;
            foreach (CargoRow r in m.Cargo ?? new CargoRow[0]) if (r.Kind == CargoKinds.Fuel) n++;
            return n <= 1;
        }

        // The bought-fuel price against what the player accepted and what the agency holds, before the step: a
        // MoneyController.RemoveMoney deducts even when it then fails, so the check comes first (Z-1).
        int StockCheck(PMMissionParameter pmp, MissionData m, Company c, out string why)
        {
            why = null;
            var chk = pmp.CheckCanPlanMission();
            if (chk.planMissionResult != PMMissionParameter.EPlanMissionResult.AllOk)
            {
                bool stock = (chk.planMissionResult & (PMMissionParameter.EPlanMissionResult.NoFuelCantBuy
                    | PMMissionParameter.EPlanMissionResult.WrongResourcesCargoStartHaveResource
                    | PMMissionParameter.EPlanMissionResult.WrongRemoveFuel)) != 0;
                why = "CheckCanPlanMission said " + chk.planMissionResult;
                return stock ? RefuseCodes.CannotAfford : RefuseCodes.PlanFailed;
            }
            var reserved = new List<ObjectInfoData>();
            var take = pmp.CheckFuelTakeFrom(false, reserved);
            foreach (ObjectInfoData d in reserved) if (d != null) d.ClearReservedResources();
            bool buyShip = take.Item1 == PMMissionParameter.EFuelTakeFrom.Buy;
            bool buyLv = take.Item2 == PMMissionParameter.EFuelTakeFrom.Buy;
            if (!buyShip && !buyLv) return 0;
            double price = (buyShip ? pmp.FuelCostAllInDollar : 0) + (buyLv ? pmp.FuelCostAllInDollarLV : 0);
            if (!PopupAnswered())
            {
                // without the prefix that answers the three buy-fuel popups the host's own screen would be asked
                why = "the plan buys fuel and no hook answers the buy-fuel popup";
                return RefuseCodes.PlanFailed;
            }
            if ((m.Flags & MissionFlags.BuyFuel) == 0)
            {
                why = "the plan buys fuel and the player did not accept a price";
                return RefuseCodes.CannotAfford;
            }
            if (price > m.MaxFuelPrice || price > c.MoneyController.CurrentMoney)
            {
                why = "the fuel costs " + price + ", accepted " + m.MaxFuelPrice + ", held " + c.MoneyController.CurrentMoney;
                return RefuseCodes.CannotAfford;
            }
            return 0;
        }

        // Whether some prefix of this mod stands on UIManager.ShowPopUP (Task 7's: it runs the OK delegate of the three
        // buy-fuel popups while an acceptance runs). Read once per session from Harmony's own record.
        bool? popupHooked;

        bool PopupAnswered()
        {
            if (popupHooked.HasValue) return popupHooked.Value;
            bool found = false;
            foreach (System.Reflection.MethodInfo m in typeof(Game.UI.UIManager).GetMethods())
            {
                if (m.Name != "ShowPopUP") continue;
                HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (HarmonyLib.Patch p in info.Prefixes)
                    if (p.owner == "com.solo-ship.solarexpansemultiplayer") found = true;
            }
            popupHooked = found;
            if (!found) Log.Warn("flight: no hook answers the buy-fuel popup, so a flight that buys fuel is refused");
            return found;
        }

        static string FinalName(MissionData m, PMMissionParameter pmp)
        {
            string name = MissionNames.Clean(m.Name);
            if (name.Length > 0) return name;
            ObjectInfo target = pmp.Target;
            return MissionNames.Clean(target.ObjectName + " " + target.missionCount);
        }

        // The request's cargo with exactly one fuel row, whose amount is the leftover fuel (ruling 3).
        internal static CargoRow[] WithFuelRow(CargoRow[] cargo, PMMissionParameter pmp, double leftover)
        {
            var rows = new List<CargoRow>();
            foreach (CargoRow r in cargo ?? new CargoRow[0]) if (r.Kind != CargoKinds.Fuel) rows.Add(r);
            var fuel = pmp.CargoAll.cargoFuel.resourceType;
            rows.Add(new CargoRow { Kind = CargoKinds.Fuel, Item = fuel != null ? fuel.ID : null, Amount = leftover });
            return rows.ToArray();
        }

        // ---- stopping CreateFly ----

        // The prefix on PMTabSchedule.CreateFly asks first: the acceptance's own parameter stops here, having done
        // everything up to it. false: this is not the acceptance's.
        public bool StopsAcceptance(PMMissionParameter p)
        {
            if (!accepting || !ReferenceEquals(p, acceptParameter)) return false;
            acceptReached = true;
            return true;
        }

        // The completion step's re-checks at the start of the copy's CreateFly (Z-2): the bodies and crafts still
        // exist, the launch is still ahead of the current day's end.
        public int Recheck(FlightCreation c)
        {
            MissionData m = c.Mission;
            ObjectInfo start = GameAgencies.Body(m.Start), target = GameAgencies.Body(m.Target);
            if (start == null || target == null || start.IsInGameDestroy || target.IsInGameDestroy)
                return RefuseCodes.TargetGone;
            if (LaunchTiming.TooClose(m.DepartTicks, session.World.NextDayTicks)) return RefuseCodes.TooCloseToLaunch;
            PMMissionParameter p = c.Parameter;
            if (p.SC == null) return RefuseCodes.PlanFailed;
            if (!p.StartHermesCase.CheckResources(p.CargoAll, p.FlyCompany)) return RefuseCodes.CannotAfford;
            if (fault == "targetgone")
            {
                fault = null;
                Log.Warn("flight: fault targetgone: the target is gone while the copy is made");
                return RefuseCodes.TargetGone;
            }
            return 0;
        }

        // ---- the copy is made (or stopped) ----

        void Complete(FlightCreation cr)
        {
            pending.Remove(cr);
            CommandService service = session.Service;
            if (cr.Code != 0 || cr.Result == null)
            {
                if (cr.CreateFlyReached && !cr.Stopped)
                {
                    // CreateFly ran and then failed: the world may hold a half-made mission nothing can remove
                    session.HostFault("the copy of mission " + cr.Mission.MissionId + " failed after CreateFly: " + cr.Why);
                    return;
                }
                // DeferredDone logs the refund with its answer to the player; this line is the world's side of it
                int back = Roll(cr.Journal, cr.Company);
                Log.Info("flight: token " + cr.Token + " gave back " + back + " removals");
                service.DeferredDone(cr.Token, new ApplyOutcome { Code = cr.Code == 0 ? RefuseCodes.PlanFailed : cr.Code });
                return;
            }
            string why = FlightApply.AfterCheck(cr);
            // the count the acceptance found is the copy's only when no other copy was made in between
            bool alone = cr.CopiesAtAccept == copiesDone;
            copiesDone++;
            if (why == null && alone && cr.ScBefore != cr.ScAtAccept)
                why = "Spacecraft.count was " + cr.ScBefore + " before the copy, " + cr.ScAtAccept + " before the acceptance";
            if (why != null)
            {
                session.HostFault("the host's copy of mission " + cr.Mission.MissionId + ": " + why);
                return;
            }
            session.FlightApply.Finished(cr);
            var rows = new List<CounterRow>
            {
                new CounterRow { Which = CounterKind.MissionId, Before = cr.MissionIdBefore, After = cr.MissionIdAfter },
                new CounterRow { Which = CounterKind.SpacecraftCount, Before = cr.ScBefore, After = cr.ScAfter },
                new CounterRow { Which = CounterKind.LaunchVehicleCount, Before = cr.LvBefore, After = cr.LvAfter }
            };
            var outcome = new ApplyOutcome { Applied = cr.Entry, Numbers = new ApplyNumbers { Counters = rows.ToArray() },
                Touched = FlightApply.Touched(cr) };
            service.DeferredDone(cr.Token, outcome);
        }

        // ---- giving back ----

        // Every made removal added back, newest first, exactly.
        int Roll(RemovalJournal journal, Company c)
        {
            IList<RemovalRow> rows = journal.Rollback();
            foreach (RemovalRow r in rows)
            {
                Company who = GameAgencies.Find(r.Agency) ?? c;
                if (r.What == RemovalKinds.Money) who.MoneyController.AddMoney(r.Amount);
                else
                {
                    ObjectInfoData site = FlightMap.Body(r.Body).GetObjectInfoData(who);
                    site.AddResources(FlightMap.ResourceOf(r.Resource), r.Amount);
                    site.ClearReservedResources();
                }
            }
            return rows.Count;
        }

        // CraftIdScope's undo: the fake ships the acceptance made are removed, a cached fake gets its old id, the
        // counters their old values.
        void RunUndo(IList<CraftIdUndo> undo)
        {
            foreach (CraftIdUndo u in undo)
            {
                switch (u.What)
                {
                    case CraftIdUndoKinds.RemoveFake:
                    {
                        Fake f;
                        if (!fakes.TryGetValue(u.Key, out f)) break;
                        Spacecraft sc = FlightApply.FakeShips(f.Site)[f.Key];
                        FlightApply.FakeShips(f.Site).Remove(f.Key);
                        ShipManager.Instance.RemoveSpacecraft(sc);
                        break;
                    }
                    case CraftIdUndoKinds.RestoreId:
                    {
                        Fake f;
                        if (!fakes.TryGetValue(u.Key, out f)) break;
                        FlightApply.FakeShips(f.Site)[f.Key].SetID((int)u.Value);
                        break;
                    }
                    case CraftIdUndoKinds.RestoreCount:
                        if (u.Key == "SC") Spacecraft.count = (int)u.Value;
                        else LaunchVehicle.count = (int)u.Value;
                        break;
                }
            }
            fakes.Clear();
        }
    }

    // The orbital supply containers as the acceptance found them: the schedule step swaps a capsule for a free
    // container of the pool (or makes one), which the load-path copy does not use.
    sealed class ContainerState
    {
        readonly List<Spacecraft> before = new List<Spacecraft>();
        readonly List<Spacecraft> links = new List<Spacecraft>();

        public void Take()
        {
            foreach (Spacecraft s in ShipManager.Instance.ListOrbitContainerForNormalMission)
            {
                before.Add(s);
                links.Add(s.cyclicalMissionSCOrbitalContainerShipManagerSCForCyclicalMission);
            }
        }

        public void Undo()
        {
            List<Spacecraft> now = ShipManager.Instance.ListOrbitContainerForNormalMission;
            for (int i = now.Count - 1; i >= 0; i--)
            {
                int was = before.IndexOf(now[i]);
                if (was >= 0) { now[i].cyclicalMissionSCOrbitalContainerShipManagerSCForCyclicalMission = links[was]; continue; }
                Spacecraft made = now[i];
                now.RemoveAt(i);
                ShipManager.Instance.RemoveSpacecraft(made);
            }
        }
    }
}
