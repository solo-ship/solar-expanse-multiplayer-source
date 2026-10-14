using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using CompanyBehaviour = AI.CompanyBehaviour;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using LEManager = Language.LEManager;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using PMMissionParameterDataSave = Game.PMMissionParameterDataSave;
using PMTab = Game.UI.Windows.Elements.PlanMissionElements.PMTab;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;
using Spacecraft = CustomUpdate.Spacecraft;
using TimeController = Manager.TimeController;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // M6 Task 7 (plan DD-F2, DD-F5, DD-F7, DD-F8, I-C): the flight rules that make the game treat every human agency as
    // it treats the local player. Each patch asks HumanAgencies.Active first, so with no session the game runs as it
    // was. The reusable-vehicle build speeds (FS9, S15) are M5's BuildTimeRule; the load's leftover fuel is
    // FlightLoadRule.
    //   * FS4/S12: a landing hands the cargo carried on for A to B to C to every human agency, not the local player.
    //   * FS5/FS6/S13: the orbital supply container is the acting agency's own, from one per-agency pool, at the
    //     schedule step, for "send supplies to orbit" (F10) and at the load.
    //   * FS7: the schedule step's three `FlyCompany == Player` tests, the container swap's, and the buy-fuel popups
    //     read "the acting agency" while the host's FlightHost runs the step for it (Acting), and only then.
    //   * The fake facility launch vehicle (the space elevator's) belongs to its facility's agency.
    //   * The departure pad: no planner but the game's own load offers a departure earlier than a game day and an
    //     hour past the planning game's boundary.
    //   * I-C: the AI's market price change, a host-only scheduled action, is moved out of the last minute before
    //     the day boundary, where it would end a step on the host that no client ends.
    // The container the planner swaps in for a capsule is named in no request: it is picked here, in the host's
    // acceptance and in each game's keyed apply, from the acting agency's pool, because every container has the
    // same id (-3) and the clicking game's pick is hard-wired to its own player.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class FlightRules
    {
        static readonly AccessTools.FieldRef<PMTab, PlanMissionWindow> WindowOf =
            AccessTools.FieldRefAccess<PMTab, PlanMissionWindow>("planMissionWindow");
        static readonly AccessTools.FieldRef<TimeController, DateTime> NextDayOf =
            AccessTools.FieldRefAccess<TimeController, DateTime>("nextDay");
        static readonly AccessTools.FieldRef<ShipManager, Spacecraft> ContainerPrefab =
            AccessTools.FieldRefAccess<ShipManager, Spacecraft>("lowOrbitContainerPrefab");
        static readonly AccessTools.FieldRef<ShipManager, UnityEngine.Transform> ContainerParent =
            AccessTools.FieldRefAccess<ShipManager, UnityEngine.Transform>("lowOrbitContainerParent");

        // Set to true by a test profile's dev flag later; false in M6: rivals keep their clamped stock removals.
        // (FlightApply.Shortfall reads it; M7's Task 9 sets it.)
        public static bool RivalStockStrict;

        // ---- the acting agency (FlightHost's acceptance runs the game's schedule step for it) ----

        public static void BeginActing(Company agency)
        {
            RuleState st = HumanAgencies.State;
            if (st != null) st.Flights.Acting = agency;
        }

        public static void EndActing()
        {
            RuleState st = HumanAgencies.State;
            if (st != null) st.Flights.Acting = null;
        }

        // The game's `GameManager.Player` where it asks "is this flight the local player's?": inside an acceptance it
        // is the acting agency, anywhere else the player.
        public static Company ActingOrPlayer(GameManager manager)
        {
            if (!HumanAgencies.Active) return manager.Player;
            Company acting = HumanAgencies.State.Flights.Acting;
            return acting != null ? acting : manager.Player;
        }

        // FS4/S12: `company.IsPlayer` in a landing.
        public static bool IsPlayerOrHuman(Company company)
        {
            if (!HumanAgencies.Active) return company.IsPlayer;
            return company.IsPlayer || HumanAgencies.IsHuman(company);
        }

        // FS6: the load's `this == Player` before it makes an orbital container.
        public static bool EqualsOrHuman(UnityEngine.Object company, UnityEngine.Object player)
        {
            if (company == player) return true;
            return HumanAgencies.Active && HumanAgencies.IsHuman(company as Company);
        }

        // The fake launch vehicle of a facility, registered under the facility's own agency.
        public static Company FacilityVehicleOwner(GameManager manager, ObjectInfoData site)
        {
            if (!HumanAgencies.Active) return manager.Player;
            return site != null && site.company != null ? site.company : manager.Player;
        }

        // ---- FS4/S12 ----

        [HarmonyPatch(typeof(Spacecraft), "FixedUpdate")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> LandingForHumans(IEnumerable<CodeInstruction> code)
        {
            MethodInfo to = AccessTools.Method(typeof(FlightRules), nameof(IsPlayerOrHuman));
            return Il.Replace(code, "Spacecraft.FixedUpdate", 1,
                (c, i) => Il.Calls(c[i], typeof(Company), "get_IsPlayer", 0),
                () => new[] { new CodeInstruction(OpCodes.Call, to) });
        }

        // ---- the load: a container for every human agency, and the figures the save recorded ----

        [HarmonyPatch(typeof(Company), "LoadMissionOne")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> LoadContainerForHumans(IEnumerable<CodeInstruction> code)
        {
            MethodInfo to = AccessTools.Method(typeof(FlightRules), nameof(EqualsOrHuman));
            return Il.Replace(code, "Company.LoadMissionOne", 1,
                (c, i) => i > 0 && Il.IsGetPlayer(c[i - 1])
                    && Il.Calls(c[i], typeof(UnityEngine.Object), "op_Equality", 2),
                () => new[] { new CodeInstruction(OpCodes.Call, to) });
        }

        // The game's own mission load queues one parameter per saved mission through PlanFlyCode, which makes the
        // mission over the next frames. These are the parameters (FlightSources asks IsLoadQueued: they run the
        // schedule step and CreateFly on every game and raise no entry), kept with the figures their record saved.
        [HarmonyPatch(typeof(Company), "LoadMissionOne")]
        [HarmonyPrefix]
        static void NoteQueueBefore(List<PMMissionParameter> listParameters, ref int __state)
        {
            if (!HumanAgencies.Active) return;
            __state = listParameters != null ? listParameters.Count : 0;
        }

        [HarmonyPatch(typeof(Company), "LoadMissionOne")]
        [HarmonyPostfix]
        static void NoteQueueAfter(PMMissionParameterDataSave pds, List<PMMissionParameter> listParameters,
            int __state)
        {
            if (!HumanAgencies.Active || listParameters == null || pds == null) return;
            try
            {
                for (int i = __state; i < listParameters.Count; i++)
                    HumanAgencies.State.Flights.NoteLoadQueued(listParameters[i], new SavedFigures
                    {
                        CostFuel = pds.allFuelNeed, OptimalCostFuel = pds.optimalFuelNeed, CostType = (int)pds.costType
                    });
            }
            catch (Exception e) { Log.Error("flights: could not note the loaded missions: " + e.GetType().Name); }
        }

        // The load recalculates a mission's fuel figures from its own porkchop, so two games differ; the save's own
        // record is the host's. Written after CreateFly made the mission (FlightSources calls this).
        public static void KeepSavedFigures(Game.Info.MissionInfo mission, SavedFigures saved)
        {
            if (mission == null || saved == null || !saved.Recorded) return;
            mission.costFuel = saved.CostFuel;
            mission.optimalCostFuel = saved.OptimalCostFuel;
            mission.costType = (Game.Info.MissionInfo.ECostType)saved.CostType;
        }

        // ---- FS5/FS6/S13: the per-agency container pool ----

        [HarmonyPatch(typeof(ShipManager), nameof(ShipManager.GetFreeToUseLowOrbitContainerForPlayer))]
        [HarmonyPrefix]
        static bool PoolPrefix(ShipManager __instance, ref Spacecraft __result)
        {
            if (!HumanAgencies.Active) return true;
            try
            {
                GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
                Company agency = gm != null ? ActingOrPlayer(gm) : null;
                if (agency == null) return true;
                __result = PoolContainer(__instance, agency);
                return false;
            }
            catch (Exception e)
            {
                Log.Error("flights: the container pool failed (the game's own ran): " + e.GetType().Name);
                return true;
            }
        }

        // The first free container of the agency, or a new one made for it, like the game's own for the player.
        public static Spacecraft PoolContainer(Company agency)
        {
            return PoolContainer(MonoBehaviourSingleton<ShipManager>.Instance, agency);
        }

        static Spacecraft PoolContainer(ShipManager ships, Company agency)
        {
            List<Spacecraft> list = ships.ListOrbitContainerForNormalMission;
            int at = ContainerPool.Pick(list.Count, i => list[i].CurrentPhase == Spacecraft.EPhase.None,
                i => ReferenceEquals(list[i].GetCompany(), agency));
            if (at >= 0) return list[at];
            Spacecraft made = UnityEngine.Object.Instantiate(ContainerPrefab(ships), ContainerParent(ships));
            made.MyInit(agency, null);
            made.SetID(-3);
            list.Add(made);
            return made;
        }

        // ---- FS7 and the container swap: "the acting agency" inside the host's acceptance ----

        [HarmonyPatch(typeof(PMTabSchedule), "ChangeSCWhenOrbitalContainer")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SwapForTheActor(IEnumerable<CodeInstruction> code)
        {
            return ActingForPlayer(code, "PMTabSchedule.ChangeSCWhenOrbitalContainer", 1);
        }

        [HarmonyPatch(typeof(PMTabSchedule), "OnClickScheduleButton",
            new[] { typeof(bool), typeof(bool) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ScheduleForTheActor(IEnumerable<CodeInstruction> code)
        {
            return ActingForPlayer(code, "PMTabSchedule.OnClickScheduleButton", 3);
        }

        static IEnumerable<CodeInstruction> ActingForPlayer(IEnumerable<CodeInstruction> code, string where,
            int expected)
        {
            MethodInfo to = AccessTools.Method(typeof(FlightRules), nameof(ActingOrPlayer));
            return Il.Replace(code, where, expected, (c, i) => Il.IsGetPlayer(c[i]),
                () => new[] { new CodeInstruction(OpCodes.Call, to) });
        }

        // The three buy-fuel popups ask the player to confirm a purchase; inside an acceptance the OK is the
        // agency's request already, so it is answered at once. Nothing else is: a popup that is not one of the three
        // shows as the game has it.
        [HarmonyPatch(typeof(UIManager), nameof(UIManager.ShowPopUP),
            new[]
            {
                typeof(string), typeof(Action), typeof(Action), typeof(bool), typeof(bool), typeof(bool),
                typeof(bool), typeof(bool), typeof(string), typeof(Action), typeof(bool), typeof(string),
                typeof(string), typeof(string), typeof(string)
            })]
        [HarmonyPrefix]
        static bool AnswerBuyFuel(string text, Action onOK)
        {
            if (!HumanAgencies.Active) return true;
            if (HumanAgencies.State.Flights.Acting == null) return true;
            try
            {
                if (onOK == null || !PopupMatch.Matches(LEManager.Get("PopUp.NoMony"), text))
                {
                    Log.Warn("flights: a popup inside a flight acceptance was not the buy-fuel one");
                    return true;
                }
                onOK();
                return false;
            }
            catch (Exception e)
            {
                Log.Error("flights: the buy-fuel answer failed: " + e.GetType().Name);
                return false;
            }
        }

        // ---- the fake facility launch vehicle ----

        [HarmonyPatch(typeof(ObjectInfoData), nameof(ObjectInfoData.GetFakeLVFromFacility))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> VehicleForTheFacility(IEnumerable<CodeInstruction> code)
        {
            return Il.PlayerToOwner(code, "ObjectInfoData.GetFakeLVFromFacility", 1, (c, i) => true, OpCodes.Ldarg_0,
                AccessTools.Method(typeof(FlightRules), nameof(FacilityVehicleOwner)));
        }

        // ---- the departure pad (DD-F5, S6) ----

        [HarmonyPatch(typeof(PMTabSchedule), "BeforeGravityEngineCalculate")]
        [HarmonyPostfix]
        static void PadPostfix(PMTabSchedule __instance)
        {
            if (!HumanAgencies.Active) return;
            try
            {
                PlanMissionWindow window = WindowOf(__instance);
                PMMissionParameter p = window != null ? window.PMMissionParameter : null;
                // the game's own load, and each game's keyed apply, keep the dates the entry fixed
                if (p == null || p.LoadingFromSave) return;
                TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
                LambertPorkchop porkchop = __instance.LambertPorkchop;
                if (tc == null || porkchop == null) return;
                double padded = DeparturePad.AddToStart(porkchop.addToStart, GravityScaler.game_sec_per_phys_sec,
                    tc.CurrentTime.Ticks, NextDayOf(tc).Ticks, window.ForCode, tc.CurrentTimeScale);
                if (padded == porkchop.addToStart) return;
                porkchop.addToStart = padded;
                porkchop.modyficatTimeStart = true;
            }
            catch (Exception e) { Log.Error("flights: the departure pad failed: " + e.GetType().Name); }
        }

        // ---- I-C: the AI's price change is a host-only step boundary ----

        [HarmonyPatch(typeof(CompanyBehaviour), "ScheduleNextPriceChange")]
        [HarmonyPrefix]
        static void PriceChangeStart()
        {
            if (!HumanAgencies.Active) return;
            HumanAgencies.State.Flights.PriceChange = true;
        }

        // A finalizer, so a throwing schedule still puts the flag down.
        [HarmonyPatch(typeof(CompanyBehaviour), "ScheduleNextPriceChange")]
        [HarmonyFinalizer]
        static void PriceChangeEnd()
        {
            if (!HumanAgencies.Active) return;
            HumanAgencies.State.Flights.PriceChange = false;
        }

        [HarmonyPatch(typeof(TimeController), nameof(TimeController.DateTimeToScheduledAction))]
        [HarmonyPrefix]
        static void MoveOutOfTheLastMinute(ref DateTime time)
        {
            if (!HumanAgencies.Active) return;
            if (!HumanAgencies.State.Flights.PriceChange) return;
            try
            {
                TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
                if (tc == null) return;
                long moved = AlignedBoundary.MoveHostOnly(time.Ticks, NextDayOf(tc).Ticks);
                if (moved != time.Ticks) time = new DateTime(moved);
            }
            catch (Exception e) { Log.Error("flights: the price change could not be moved: " + e.GetType().Name); }
        }
    }
}
