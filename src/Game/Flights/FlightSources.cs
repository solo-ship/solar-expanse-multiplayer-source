using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using AllScriptableObjectManager = Manager.AllScriptableObjectManager;
using Company = Game.Company;
using ContractManager = Manager.ContractManager;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using GameManager = Manager.GameManager;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoManager = Manager.ObjectInfoManager;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using PMTab = Game.UI.Windows.Elements.PlanMissionElements.PMTab;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;
using Spacecraft = CustomUpdate.Spacecraft;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using SpacecraftType = Data.ScriptableObject.SpacecraftType;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The state one landing-built ship carries from its prefix to its postfix.
    public sealed class LandingNote
    {
        public long Mission;
        public long SpacecraftBefore, VehicleBefore;
    }

    // M6 Task 8 (plan DD-F4, DD-F6, DD-F7, DD-F12): who plans, cancels, launches and lands a flight. In a session only
    // the host's game makes a flight nobody asked for; every other game applies what the host sends, so a flight
    // exists once and is the same everywhere.
    //   * A client never plans (PlanFlyCode refuses what is not an applied entry's or the load's, the route
    //     controller and the end-game flight are held at their entries) and never cancels a stalled build.
    //   * The host drops a plan it cannot send in time (too close to launch, or to a full target), then raises each
    //     one it made as FlightPlanned from CreateFly, and each launch it checked and each cancel it made as
    //     MissionLaunched or MissionCancelled.
    //   * The code-planner queue waits out a join batch, except the host's own creations of accepted flights.
    // Every patch is a no-op outside a session. The load's own parameters are never held or raised: they are told
    // apart by object (FlightState.IsLoadQueued), since a client's apply sets LoadingFromSave too.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class FlightSources
    {
        static readonly AccessTools.FieldRef<PMTab, PlanMissionWindow> WindowOf =
            AccessTools.FieldRefAccess<PMTab, PlanMissionWindow>("planMissionWindow");
        static readonly AccessTools.FieldRef<PMTabSchedule, MissionInfo> CreatedByCode =
            AccessTools.FieldRefAccess<PMTabSchedule, MissionInfo>("fromCodeMissionInfo");
        static readonly AccessTools.FieldRef<TimeController, DateTime> NextDayOf =
            AccessTools.FieldRefAccess<TimeController, DateTime>("nextDay");
        static readonly AccessTools.FieldRef<PlanCyclicalMissionWindow, PlanCyclicalMissionWindow.Mode> EditMode =
            AccessTools.FieldRefAccess<PlanCyclicalMissionWindow, PlanCyclicalMissionWindow.Mode>("mode");
        static readonly AccessTools.FieldRef<PlanCyclicalMissionWindow, CycleMissionsData> EditTarget =
            AccessTools.FieldRefAccess<PlanCyclicalMissionWindow, CycleMissionsData>("toEdit");
        static readonly FieldInfo JobQueue = AccessTools.Field(typeof(GameManager), "list");
        static readonly AccessTools.FieldRef<SpaceCraftCyclicalMissionController, DateTime?> LastCyclicalTry =
            AccessTools.FieldRefAccess<SpaceCraftCyclicalMissionController, DateTime?>("lastTryCycleMission");
        static readonly AccessTools.FieldRef<CycleMissionManager, int> NumberFly =
            AccessTools.FieldRefAccess<CycleMissionManager, int>("numberFly");
        static readonly AccessTools.FieldRef<CycleMissionsData, int> RouteCount =
            AccessTools.FieldRefAccess<CycleMissionsData, int>("countMission");

        // ---- what Task 9 and Task 10 call ----

        static FlightState Flights
        {
            get { RuleState st = HumanAgencies.State; return st != null ? st.Flights : null; }
        }

        // Task 9 gives the host its reader of game-made plans, and its watch lists hear of every cancel made here. The
        // state is the session's: call this again after each HumanAgencies.Start.
        public static void Register(IFlightPlanReader reader, Action<long> cancelled)
        {
            FlightState f = Flights;
            if (f == null) return;
            f.Reader = reader;
            f.Cancelled = cancelled;
        }

        // The parameters an applied entry queued (by object): the host's own copy of an accepted flight, and each
        // game's keyed apply. Done when the creation has ended; Abandon when the host gave up waiting for it.
        public static void Expect(PMMissionParameter plan) { FlightState f = Flights; if (f != null) f.Expect(plan); }
        public static void Done(PMMissionParameter plan) { FlightState f = Flights; if (f != null) f.Done(plan); }
        public static void Abandon(PMMissionParameter plan) { FlightState f = Flights; if (f != null) f.Abandon(plan); }
        public static bool IsExpecting(PMMissionParameter plan)
        {
            FlightState f = Flights;
            return f != null && f.IsExpecting(plan);
        }

        public static bool IsAbandoned(PMMissionParameter plan)
        {
            FlightState f = Flights;
            return f != null && f.IsAbandoned(plan);
        }

        public static bool IsLoadQueued(PMMissionParameter plan)
        {
            FlightState f = Flights;
            return f != null && f.IsLoadQueued(plan);
        }

        // The host's first load ran before the session (and its LoadMissionOne notes) existed, so the plans that load
        // queued are still waiting when the session starts, and each would be raised as a new game-made flight at
        // its CreateFly. They are the load's (LoadingFromSave, set by LoadMissionOne): noted without a saved record,
        // since the host's own figures are the ones its snapshots carry. Returns how many were noted.
        public static int NoteLoadPlansWaiting()
        {
            FlightState f = Flights;
            if (f == null) return 0;
            IList queue = JobQueue.GetValue(MonoBehaviourSingleton<GameManager>.Instance) as IList;
            if (queue == null) return 0;
            int n = 0;
            foreach (object item in queue)
            {
                var plan = item.GetType().GetField("Item1").GetValue(item) as PMMissionParameter;
                if (plan == null || !plan.LoadingFromSave || f.IsExpecting(plan) || f.IsLoadQueued(plan)) continue;
                f.NoteLoadQueued(plan, null);
                n++;
            }
            return n;
        }

        // A4 (revision 15c): M7's end-game copy registers its plan before queueing it; IsEndGame is the one predicate
        // every rule about the end-game flight reads (the registration, or vanilla's own shape).
        public static void MarkEndGame(PMMissionParameter plan)
        {
            FlightState f = Flights;
            if (f != null) f.MarkEndGame(plan);
        }

        public static bool IsEndGame(PMMissionParameter plan)
        {
            FlightState f = Flights;
            if (f == null || plan == null) return false;
            return EndGame.Is(f.IsMarkedEndGame(plan), (int)plan.MissionCreator,
                plan.CargoAll != null && plan.CargoAll.entireAsteroid, ShipTypeId(plan));
        }

        static string ShipTypeId(PMMissionParameter plan)
        {
            SpacecraftType type = plan.SC != null ? plan.SC.GetTypeSpaceCraft() : null;
            return type != null ? type.ID : null;
        }

        // A client applying a 72: the landing handler takes its two draws and the new orbit's id from the entry.
        public static void BeginPull(AsteroidPull given, long missionId)
        {
            FlightState f = Flights;
            if (f == null) return;
            f.PullGiven = given;
            f.PullMission = missionId;
        }

        public static void EndPull()
        {
            FlightState f = Flights;
            if (f == null) return;
            f.PullGiven = null;
            f.PullMission = -1;
        }

        // DD-F12: the missions an impact or a nuke of the asteroid cancels, in order. Raises nothing.
        public static List<TargetCancel> TargetGoneCancels(int asteroid, long atTicks)
        {
            return FlightCancels.Cancels(asteroid, atTicks);
        }

        // The game-side cancel of one of them, as the host does it inside an impact and a client does it inside the
        // apply of the host's reason-5 71. Null = done, else why not.
        public static string CancelTargetGone(TargetCancel cancel) { return FlightCancels.Cancel(cancel); }

        // A client's apply of a LandingCraft (73): the ship its landing held is made now, with the counters the entry
        // set. False when this game's landing did not hold one for that mission.
        public static bool ReleaseLandingCraft(string agency, int body, string shipType, long missionId)
        {
            FlightState f = Flights;
            if (f == null) return false;
            int at = -1;
            for (int i = 0; i < f.HeldLandings.Count; i++)
            {
                HeldLanding h = f.HeldLandings[i];
                if (h.Mission != missionId || h.Body != body || h.Type != shipType || h.Agency != agency) continue;
                at = i;
                break;
            }
            if (at < 0) return false;
            f.HeldLandings.RemoveAt(at);
            Company company = GameAgencies.Find(agency);
            ObjectInfo place = GameAgencies.Body(body);
            SpacecraftType type = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                .AllSpacecraftType.GetByID(shipType);
            if (company == null || place == null || type == null) return false;
            f.ReleasingLanding = true;
            try { MonoBehaviourSingleton<ShipManager>.Instance.ConstructShipOnPlanet(place, type, null, company); }
            finally { f.ReleasingLanding = false; }
            return true;
        }

        // ---- the asteroid pull's draws and orbit id (FS3's orbit branch; AsteroidSpawnRule transpiles to these) ----

        public static float PullDistance(float min, float max)
        {
            RuleState st = HumanAgencies.State;
            if (st == null) return UnityEngine.Random.Range(min, max);
            if (st.Flights.PullGiven != null) return st.Flights.PullGiven.DistanceFake;
            if (st.Flights.PullMade == null) return UnityEngine.Random.Range(min, max);
            float v = st.SpawnRng.NextFloat(min, max);
            st.Flights.PullMade.DistanceFake = v;
            return v;
        }

        public static int PullRotation(int min, int max)
        {
            RuleState st = HumanAgencies.State;
            if (st == null) return UnityEngine.Random.Range(min, max);
            if (st.Flights.PullGiven != null) return st.Flights.PullGiven.Rotation;
            if (st.Flights.PullMade == null) return UnityEngine.Random.Range(min, max);
            int v = st.SpawnRng.Next(min, max);
            st.Flights.PullMade.Rotation = v;
            return v;
        }

        public static UnityEngine.GameObject CreateOrbitFor(ObjectInfoManager manager, bool low, ObjectInfo parent,
            out ObjectInfo orbit, int idToOrbit)
        {
            RuleState st = HumanAgencies.State;
            FlightState f = st != null ? st.Flights : null;
            int id = f != null && f.PullGiven != null && f.PullGiven.OrbitId > 0 ? f.PullGiven.OrbitId : idToOrbit;
            UnityEngine.GameObject made = manager.CreateOrbit(low, parent, out orbit, id);
            if (f != null && f.PullMade != null && orbit != null) f.PullMade.OrbitId = orbit.id;
            return made;
        }

        // ---- clients never plan: PlanFlyCode, the route controller, the end-game flight, the stalled build ----

        static bool IsClient { get { return HumanAgencies.Active && HumanAgencies.Host == null; } }

        static void LogHold(string method, string agency)
        {
            FlightState f = Flights;
            if (f != null && f.ShouldLogHold(method)) Log.Info("flight: held " + method + " for " + agency);
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlanFlyCode))]
        [HarmonyPrefix]
        static bool PlanFlyCodeBackstop(PMMissionParameter missionParameter, ref GameManager.PlanFlyCodeResult __result)
        {
            if (!IsClient) return true;
            FlightState f = Flights;
            if (missionParameter == null || f.IsExpecting(missionParameter) || f.IsLoadQueued(missionParameter))
                return true;
            // every known creator is held upstream: a plan that gets here is one nobody knew of
            LogHold("PlanFlyCode", GameAgencies.Wire(missionParameter.FlyCompany));
            Log.Warn("flight: a client's plan was refused at PlanFlyCode ("
                + GameAgencies.Wire(missionParameter.FlyCompany) + ")");
            __result = new GameManager.PlanFlyCodeResult { endCalculation = true };
            return false;
        }

        // A route's leg is planned by the host only; a client's controller does nothing until the host's entry. The
        // host marks a leg it cancels itself (the controller's own re-plan) so the cancel is raised with its reason.
        [HarmonyPatch(typeof(SpaceCraftCyclicalMissionController),
            nameof(SpaceCraftCyclicalMissionController.TryPlanCycleMission))]
        [HarmonyPrefix]
        static bool RouteHold(SpaceCraftCyclicalMissionController __instance, ref int __state)
        {
            if (!HumanAgencies.Active) return true;
            if (HumanAgencies.Host == null)
            {
                LogHold("TryPlanCycleMission", "a route");
                return false;
            }
            FlightState f = Flights;
            __state = f.CancelReason + 1;
            f.CancelReason = CancelReasons.Cyclical;
            return true;
        }

        [HarmonyPatch(typeof(SpaceCraftCyclicalMissionController),
            nameof(SpaceCraftCyclicalMissionController.TryPlanCycleMission))]
        [HarmonyFinalizer]
        static void RouteHoldEnd(int __state)
        {
            if (!HumanAgencies.Active || __state == 0) return;
            Flights.CancelReason = __state - 1;
        }

        // A4: the end-game flight is planned by the host only. M7 holds the game's own copy on every game too; both
        // prefixes return false at the same entry.
        [HarmonyPatch(typeof(ContractManager), "ChangeTrajectoryAsteroidToEarth")]
        [HarmonyPrefix]
        static bool EndGameHold()
        {
            if (!IsClient) return true;
            LogHold("ChangeTrajectoryAsteroidToEarth", "the end game");
            return false;
        }

        // The stalled-build cancel: a client builds a craft only when the host's entry arrives, so it never sees one
        // stall; the host's cancel is raised with reason 3 once the game has made it.
        [HarmonyPatch(typeof(RocketLauncher), "LaunchSCConstructionScShouldBuildMain")]
        [HarmonyPrefix]
        static bool StalledHold(ref bool __state)
        {
            if (!HumanAgencies.Active) return true;
            if (HumanAgencies.Host == null)
            {
                LogHold("LaunchSCConstructionScShouldBuildMain", "a stalled build");
                return false;
            }
            if (HumanAgencies.Applying) return true;
            Flights.BeginQuiet();
            __state = true;
            return true;
        }

        [HarmonyPatch(typeof(RocketLauncher), "LaunchSCConstructionScShouldBuildMain")]
        [HarmonyFinalizer]
        static void StalledRaise(bool __state)
        {
            if (!HumanAgencies.Active || !__state) return;
            FlightDecisions.CancelledAll(HumanAgencies.Host, Flights.EndQuiet(), CancelReasons.Stalled);
        }

        // The edit window opens on a route by cancelling its planned leg; that is a click that changes the world, so
        // outside an applied entry the window only opens. The edit's own entry cancels the leg.
        [HarmonyPatch(typeof(PlanCyclicalMissionWindow), nameof(PlanCyclicalMissionWindow.SetMode))]
        [HarmonyPrefix]
        static bool EditOpensWithoutCancel(PlanCyclicalMissionWindow __instance, PlanCyclicalMissionWindow.Mode _mode,
            CycleMissionsData cmd)
        {
            if (!HumanAgencies.Active) return true;
            if (_mode != PlanCyclicalMissionWindow.Mode.Edit || HumanAgencies.Applying) return true;
            EditMode(__instance) = _mode;
            EditTarget(__instance) = cmd;
            if (__instance.editModeGameObjectInfo != null) __instance.editModeGameObjectInfo.gameObject.SetActive(true);
            return false;
        }

        static readonly AccessTools.FieldRef<PlanCyclicalMissionWindow, bool> ClickingSchedule =
            AccessTools.FieldRefAccess<PlanCyclicalMissionWindow, bool>("clickingSchedule");

        // Closing the editor re-plans the leg its opening cancelled and sets its ship's phase back, in the closing
        // game only. In a session nothing is cancelled on open (the edit's own entry does it, everywhere), so closing
        // only closes: the game's "I am scheduling" flag keeps that block off, and the edit target is let go, as it is
        // on every close.
        [HarmonyPatch(typeof(PlanCyclicalMissionWindow), "Hide", new[] { typeof(bool) })]
        [HarmonyPrefix]
        static void RouteWindowCloses(PlanCyclicalMissionWindow __instance, ref int __state)
        {
            if (!HumanAgencies.Active) return;
            __state = ClickingSchedule(__instance) ? 2 : 1;
            ClickingSchedule(__instance) = true;
            EditTarget(__instance) = null;
        }

        [HarmonyPatch(typeof(PlanCyclicalMissionWindow), "Hide", new[] { typeof(bool) })]
        [HarmonyFinalizer]
        static void RouteWindowClosed(PlanCyclicalMissionWindow __instance, int __state)
        {
            if (__state == 1) ClickingSchedule(__instance) = false;
        }

        // ---- the join batch: the code-planner queue waits, except the host's own creations of accepted flights ----

        [HarmonyPatch(typeof(GameManager), "UpdateJobSystem")]
        [HarmonyPrefix]
        static bool QueueWaitsOutABatch()
        {
            if (!HumanAgencies.Active || HumanAgencies.Host == null) return true;
            CommandSession commands = Session.Commands;
            CommandService service = commands != null ? commands.Service : null;
            if (service == null || !service.Held) return true;
            try
            {
                IList queue = JobQueue.GetValue(MonoBehaviourSingleton<GameManager>.Instance) as IList;
                if (queue == null || queue.Count == 0) return true;
                object head = queue[0];
                object plan = head.GetType().GetField("Item1").GetValue(head);
                return Flights.IsExpecting(plan);
            }
            catch (Exception e)
            {
                Log.Error("flight: could not read the planner queue (it ran): " + e.GetType().Name);
                return true;
            }
        }

        // ---- the host decides what to do with a plan its own game made (DD-F4), before its schedule step ----

        [HarmonyPatch(typeof(PMTabSchedule), nameof(PMTabSchedule.OnClickScheduleButtonForCode))]
        [HarmonyPrefix]
        static bool DropAGamePlan(PMTabSchedule __instance, ref MissionInfo __result)
        {
            if (!HumanAgencies.Active || HumanAgencies.Host == null) return true;
            FlightState f = Flights;
            try
            {
                PlanMissionWindow window = WindowOf(__instance);
                PMMissionParameter p = window != null ? window.PMMissionParameter : null;
                // the visible planner is a player's (a capture's), the load and an applied entry's plans are expected,
                // and an acceptance runs this step itself
                if (p == null || !window.ForCode || p.LoadingFromSave || f.Acting != null) return true;
                if (f.IsExpecting(p) || f.IsLoadQueued(p)) return true;
                string agency = GameAgencies.Wire(p.FlyCompany);
                int creator = (int)p.MissionCreator;
                TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
                bool tooClose = tc != null && LaunchTiming.TooClose(p.DepartureTimeDate.Ticks, NextDayOf(tc).Ticks);
                ObjectInfo end533 = GameAgencies.Body(EndGame.Asteroid);
                bool full = IsTargetFull(p, tc != null ? tc.CurrentTime.Ticks : 0);
                PlanFate fate = GamePlanRules.Decide(creator, !HumanAgencies.IsHuman(p.FlyCompany), IsEndGame(p),
                    tooClose, end533 != null && end533.IsInGameDestroy, full);
                if (fate == PlanFate.Raise) return true;
                Drop(fate, p, agency, tooClose);
                __result = null;
                return false;
            }
            catch (Exception e)
            {
                Log.Error("flight: could not judge a game-made plan (it ran): " + e.GetType().Name);
                return true;
            }
        }

        static void Drop(PlanFate fate, PMMissionParameter p, string agency, bool tooClose)
        {
            FlightState f = Flights;
            f.ClearEndGame(p);
            string why = tooClose ? "too close" : "target full";
            switch (fate)
            {
                case PlanFate.DropEndGameGone:
                    Log.Info("flight: plan dropped " + agency + " end game, asteroid gone");
                    break;
                case PlanFate.HostFault:
                    Log.Error("flight: plan dropped " + agency + " end game, " + why);
                    HumanAgencies.Host.HostFault("the end-game flight of " + agency + " could not be made ("
                        + why + ")");
                    break;
                case PlanFate.DropCyclical:
                    Log.Info("flight: plan dropped " + agency + " " + why);
                    ResetRoute(p);
                    break;
                case PlanFate.DropAi:
                    Log.Info("flight: plan dropped " + agency + " " + why);
                    break;
                default:
                    Log.Info("flight: plan dropped " + agency + " no creator");
                    break;
            }
        }

        // A route's leg that was dropped must leave its controller as it was before the try, or the route would wait
        // for a leg that never comes: the controller's flags, the try's date, the plan counter and the route's count.
        static void ResetRoute(PMMissionParameter p)
        {
            try
            {
                var ship = p.SC as Spacecraft;
                SpaceCraftCyclicalMissionController controller =
                    ship != null ? ship.CraftCyclicalMissionController : null;
                if (controller == null) return;
                controller.CycleMissionPlanFlyWas = false;
                LastCyclicalTry(controller) = null;
                CycleMissionsData route = controller.CycleMissionsData;
                if (route == null) return;
                route.wasSetPMParameterForCodeJobSystem = false;
                CycleMissionManager manager = MonoBehaviourSingleton<CycleMissionManager>.Instance;
                if (manager != null && NumberFly(manager) > 1) NumberFly(manager)--;
                if (RouteCount(route) > 0) RouteCount(route)--;
            }
            catch (Exception e)
            {
                Log.Error("flight: could not reset the route after a dropped leg: " + e.GetType().Name);
            }
        }

        // W1: a flight is limited only where an impact can happen. The plan carries asteroid X to a body, or goes to
        // or from X while an X impactor is in flight; a cyclical leg to a busy asteroid nobody is crashing is never
        // full.
        static bool IsTargetFull(PMMissionParameter p, long nowTicks)
        {
            if (p.Start == null || p.Target == null) return false;
            int start = p.Start.id, target = p.Target.id;
            bool carries = p.CargoAll != null && p.CargoAll.entireAsteroid;
            bool toBody = p.Target.objectTypes != Data.EObjectTypes.Orbit;
            if (carries && TargetFull.Refuses(toBody, false, Live(start, nowTicks), ActionLimits.MaxFlightsPerTarget))
                return true;
            foreach (int x in new[] { start, target })
                if (ImpactPending(x, nowTicks)
                    && TargetFull.Refuses(false, true, Live(x, nowTicks), ActionLimits.MaxFlightsPerTarget))
                    return true;
            return false;
        }

        static int Live(int body, long nowTicks)
        {
            int n = 0;
            foreach (GoneRow r in FlightCancels.Rows())
                if (r.ArriveTicks > nowTicks && (r.Start == body || r.Target == body)) n++;
            return n;
        }

        static bool ImpactPending(int body, long nowTicks)
        {
            foreach (GoneRow r in FlightCancels.Rows())
                if (r.Carried == body && r.Start == body && r.ArriveTicks > nowTicks) return true;
            return false;
        }

        // ---- every mission a game-made plan creates is raised by the host once CreateFly has made it ----

        [HarmonyPatch(typeof(PMTabSchedule), "CreateFly")]
        [HarmonyPostfix]
        static void CreatedFly(PMTabSchedule __instance)
        {
            if (!HumanAgencies.Active) return;
            FlightState f = Flights;
            try
            {
                PlanMissionWindow window = WindowOf(__instance);
                PMMissionParameter p = window != null ? window.PMMissionParameter : null;
                if (p == null) return;
                MissionInfo created = CreatedByCode(__instance);
                if (created != null) Log.Info("flight: created " + created.id + " ga " + SlingshotFlag(p));
                f.ClearEndGame(p);
                SavedFigures saved = f.TakeLoadQueued(p);
                if (saved != null)
                {
                    FlightRules.KeepSavedFigures(created, saved);
                    return;
                }
                if (f.IsExpecting(p) || HumanAgencies.Host == null || HumanAgencies.Applying) return;
                if (!window.ForCode || created == null) return;
                RaisePlanned(f, p, created);
            }
            catch (Exception e)
            {
                Log.Error("flight: could not follow a created flight: " + e.GetType().Name);
                if (HumanAgencies.Host != null)
                    HumanAgencies.Host.HostFault("a game-made flight could not be followed");
            }
        }

        // The slingshot flag the objective reads at OnCreatedFly (revision 15b); the log line lets the games compare.
        static int SlingshotFlag(PMMissionParameter p)
        {
            try { return p.GravityAssist && p.gravityAssistChangeDVOnForUI ? 1 : 0; }
            catch (Exception) { return 0; }
        }

        static void RaisePlanned(FlightState f, PMMissionParameter p, MissionInfo created)
        {
            IHostDecisions host = HumanAgencies.Host;
            ApplyNumbers numbers = null;
            ActionRequest planned = null;
            if (f.Reader != null) planned = f.Reader.Planned(p, created, out numbers);
            if (planned == null)
            {
                host.HostFault("a game-made flight of " + GameAgencies.Wire(p.FlyCompany) + " cannot be sent"
                    + (f.Reader == null ? " (nothing reads it)" : ""));
                return;
            }
            FlightDecisions.Planned(host, planned, numbers);
        }

        // ---- every cancel the host's rules make is raised with its reason; the watch lists hear of every cancel ----

        [HarmonyPatch(typeof(MissionInfo), nameof(MissionInfo.Cancel))]
        [HarmonyPrefix]
        static void CancelStart(MissionInfo __instance, ref bool __state)
        {
            if (!HumanAgencies.Active) return;
            __state = __instance.cancel;
        }

        [HarmonyPatch(typeof(MissionInfo), nameof(MissionInfo.Cancel))]
        [HarmonyPostfix]
        static void CancelDone(MissionInfo __instance, bool __state)
        {
            if (!HumanAgencies.Active || __state || !__instance.cancel) return;
            FlightState f = Flights;
            try
            {
                if (f.Cancelled != null) f.Cancelled(__instance.id);
                // a client's cancel is part of an applied entry; the host's inside an entry is that entry's
                if (HumanAgencies.Host == null || HumanAgencies.Applying) return;
                string agency = GameAgencies.Wire(__instance.company);
                if (f.CancelsAreQuiet) f.LogCancel(agency, __instance.id);
                else if (f.CancelReason != 0)
                    FlightDecisions.Cancelled(HumanAgencies.Host, agency, __instance.id, f.CancelReason);
                else Log.Warn("flight: mission " + __instance.id + " was cancelled outside an entry or a rule");
            }
            catch (Exception e) { Log.Error("flight: could not follow a cancel: " + e.GetType().Name); }
        }

        // ---- ships built on landing (S4, DD-F7): the host decides their ids, a client makes them from the entry ----

        [HarmonyPatch(typeof(Spacecraft), "GiveResourcesWhenLand")]
        [HarmonyPrefix]
        static void LandingStart(Spacecraft __instance, ref long __state)
        {
            if (!HumanAgencies.Active) return;
            FlightState f = Flights;
            __state = f.LandingMission + 2;
            MissionInfo mi = __instance.GetMissionInfo();
            f.LandingMission = mi != null ? mi.id : -1;
        }

        [HarmonyPatch(typeof(Spacecraft), "GiveResourcesWhenLand")]
        [HarmonyFinalizer]
        static void LandingEnd(long __state)
        {
            if (!HumanAgencies.Active || __state == 0) return;
            Flights.LandingMission = __state - 2;
        }

        [HarmonyPatch(typeof(ShipManager), nameof(ShipManager.ConstructShipOnPlanet),
            new[] { typeof(ObjectInfo), typeof(SpacecraftType), typeof(SpacecraftConstructData), typeof(Company) })]
        [HarmonyPrefix]
        static bool LandingCraftStart(ObjectInfo obj, SpacecraftType spacecraftType, Company company,
            ref object __result, ref LandingNote __state)
        {
            if (!HumanAgencies.Active) return true;
            FlightState f = Flights;
            if (f.LandingMission < 0 || f.ReleasingLanding) return true;
            if (HumanAgencies.Applying && HumanAgencies.Host != null) return true;
            if (HumanAgencies.Host == null)
            {
                f.HeldLandings.Add(new HeldLanding { Agency = GameAgencies.Wire(company), Body = obj.id,
                    Type = spacecraftType.ID, Mission = f.LandingMission });
                if (f.ShouldLogHold("LandingCraft"))
                    Log.Info("flight: landing craft " + spacecraftType.ID + " of " + GameAgencies.Wire(company)
                        + " held for mission " + f.LandingMission);
                __result = null;
                return false;
            }
            __state = new LandingNote { Mission = f.LandingMission, SpacecraftBefore = Spacecraft.count,
                VehicleBefore = LaunchVehicle.count };
            return true;
        }

        [HarmonyPatch(typeof(ShipManager), nameof(ShipManager.ConstructShipOnPlanet),
            new[] { typeof(ObjectInfo), typeof(SpacecraftType), typeof(SpacecraftConstructData), typeof(Company) })]
        [HarmonyPostfix]
        static void LandingCraftMade(ObjectInfo obj, SpacecraftType spacecraftType, Company company,
            LandingNote __state)
        {
            if (!HumanAgencies.Active || __state == null) return;
            try
            {
                FlightDecisions.LandingCraft(HumanAgencies.Host, GameAgencies.Wire(company), obj.id, spacecraftType.ID,
                    __state.Mission, __state.SpacecraftBefore, Spacecraft.count, __state.VehicleBefore,
                    LaunchVehicle.count);
            }
            catch (Exception e) { Log.Error("flight: could not report a landing craft: " + e.GetType().Name); }
        }
    }
}
