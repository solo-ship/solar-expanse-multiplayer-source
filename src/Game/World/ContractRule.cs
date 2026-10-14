using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using CompanyObjectiveData = Game.ContractsObjectives.CompanyObjectiveData;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using GameManager = Manager.GameManager;
using LoadSaveManager = Manager.LoadSaveManager;
using ObjectInfo = Game.Info.ObjectInfo;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;
using Spacecraft = CustomUpdate.Spacecraft;
using TutorialManager = Tutorial.TutorialManager;

namespace SolarExpanseMultiplayer
{
    // DD-W7: contracts are shared. Every human agency listens for the same world events on every game, and a
    // completion or an activation is the host's decision, raised as one entry (a 92, a 95) and applied everywhere:
    //   * the "at once for any agency but the local player" objectives (StartObjective's seven player tests) treat every
    //     human agency as the player, so its progress counts alike on every game; the UI-only ones listen only for the
    //     local player and are reported (ObjectiveUi);
    //   * MarkAsComplete, FinishContractForCompany and ActivateContractForCompany are held on every game unless the
    //     entry being applied carries them (ApplyingKind reads); the host notes what it held and raises it at the day
    //     event, a client keeps its own held list (for a save written meanwhile);
    //   * rewards land at the completion for every human agency (vanilla delays the local player's behind a pop-up);
    //   * a file save first raises every note on the host, and on a client marks its held completions in the written
    //     copy only.
    // The load is the game's own (ExtractAllFromSaveData), and with no glue behind the rules the game runs as it was.
    // The end game: a client's own ChangeTrajectoryAsteroidToEarth is held (FlightSources, M6); the host's runs as
    // vanilla and the puller it builds is raised as a 96 before the flight's 70 (the plan is M6's, IsEndGame).
    // The class needs a [HarmonyPatch] of its own: HarmonyX silently skips one without.
    [HarmonyPatch]
    public static class ContractRule
    {
        // the objective types only the local player's own windows reach
        static readonly string[] UiOnly = { "SelectLayer", "CalculateFly", "AddModuleToCargo", "SelectObject", "SelectTarget" };

        static WorldRuleState State { get { return HumanAgencies.State.World; } }

        // The rules act: a session, glue behind them, and not inside the game's own load.
        static bool Holding(out WorldRuleState st)
        {
            st = null;
            if (!HumanAgencies.Active) return false;
            st = State;
            return st.Wired && !AiSteps.Loading(st);
        }

        static bool IsLocal(Company c)
        {
            GameManager m = MonoBehaviourSingleton<GameManager>.Instance;
            return m != null && c != null && c == m.Player;
        }

        static ContractRef RefOf(CompanyObjectiveData o)
        {
            return new ContractRef
            {
                Contract = o.ContractData.contractDefinition.ID, Objective = o.ContractData.ObjectivesDataList.IndexOf(o)
            };
        }

        static void NoteChain(WorldRuleState st, string agency, ContractRef done, bool finished)
        {
            if (st.Chain != null) st.Chain.Add(new ChainRow { Agency = agency, Done = done, Finished = finished });
        }

        // What a 92's apply completed, in order: the glue opens it before the apply and takes it after.
        public static void BeginChain() { State.Chain = new List<ChainRow>(); }

        public static ChainRow[] TakeChain()
        {
            WorldRuleState st = State;
            ChainRow[] rows = st.Chain == null ? new ChainRow[0] : st.Chain.ToArray();
            st.Chain = null;
            return rows;
        }

        // ---- the widened listeners ----

        // `company != Player` -> "not the player and not a human agency" at the seven places StartObjective asks it.
        [HarmonyPatch(typeof(CompanyObjectiveData), "StartObjective")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> WidenTranspiler(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "CompanyObjectiveData.StartObjective", 7,
                (c, i) => i > 0 && Il.IsGetPlayer(c[i - 1]) && Il.Calls(c[i], typeof(UnityEngine.Object), "op_Inequality", 2),
                () => new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HumanAgencies), "SkipsAgency")) });
        }

        // The UI-only handlers: the local player's own windows fired them, so only the local agency's objective hears.
        [HarmonyPatch(typeof(CompanyObjectiveData), "InstanceOnonLayersChange")]
        [HarmonyPrefix]
        static bool LayersOnlyLocal(CompanyObjectiveData __instance) { return HearsUi(__instance); }

        [HarmonyPatch(typeof(CompanyObjectiveData), "OnFindPathToTarget")]
        [HarmonyPrefix]
        static bool FindPathOnlyLocal(CompanyObjectiveData __instance) { return HearsUi(__instance); }

        [HarmonyPatch(typeof(CompanyObjectiveData), "ResourcesListOnclickAddModule")]
        [HarmonyPrefix]
        static bool AddModuleOnlyLocal(CompanyObjectiveData __instance) { return HearsUi(__instance); }

        static bool HearsUi(CompanyObjectiveData o)
        {
            WorldRuleState st;
            if (!Holding(out st)) return true;
            return IsLocal(o.ContractData.company);
        }

        // The gravity-assist handler has no company filter: only the flight's own company counts.
        [HarmonyPatch(typeof(CompanyObjectiveData), "OnCreatedFly")]
        [HarmonyPrefix]
        static bool GravityAssistOfThisCompany(CompanyObjectiveData __instance, PMMissionParameter pmmp)
        {
            WorldRuleState st;
            if (!Holding(out st)) return true;
            return pmmp != null && pmmp.FlyCompany == __instance.ContractData.company;
        }

        // The interstellar discovery names no company: the discoverer is captured around the survey call, and a
        // discovery with none (the load re-making a system) completes nothing.
        [HarmonyPatch(typeof(CompanyObjectiveData), "CheckExplorationInterstellar")]
        [HarmonyPrefix]
        static bool OnlyTheDiscoverer(CompanyObjectiveData __instance)
        {
            WorldRuleState st;
            if (!Holding(out st)) return true;
            return st.Discoverer != null && st.Discoverer == __instance.ContractData.company;
        }

        [HarmonyPatch(typeof(Company), "DecreaseExoplanetSurveyCost")]
        [HarmonyPrefix]
        static void DiscovererIn(Company __instance, ref bool __state)
        {
            __state = false;
            if (!HumanAgencies.Active) return;
            WorldRuleState st = State;
            if (st.Discoverer != null) return;
            st.Discoverer = __instance;
            __state = true;
        }

        [HarmonyPatch(typeof(Company), "DecreaseExoplanetSurveyCost")]
        [HarmonyFinalizer]
        static void DiscovererOut(bool __state)
        {
            if (__state && HumanAgencies.Active) State.Discoverer = null;
        }

        // ---- the holds ----

        [HarmonyPatch(typeof(CompanyObjectiveData), "MarkAsComplete")]
        [HarmonyPrefix]
        static bool HoldCompletion(CompanyObjectiveData __instance)
        {
            WorldRuleState st;
            if (!Holding(out st)) return true;
            try
            {
                Company c = __instance.ContractData.company;
                string agency = GameAgencies.Wire(c);
                ContractRef r = RefOf(__instance);
                if (HumanAgencies.ApplyingKind == ActionKind.ObjectiveDone)
                {
                    NoteChain(st, agency, r, false);
                    return true;
                }
                string key = HeldNotes.Key(agency, r.Contract, r.Objective);
                if (HumanAgencies.Host != null)
                {
                    st.Host.NoteCompletion(agency, r);
                    return false;
                }
                st.Held.Add(key);
                if (IsLocal(c) && Array.IndexOf(UiOnly, __instance.Objective.objectiveType.ToString()) >= 0)
                    st.Client.ObjectiveUi(r);
                return false;
            }
            catch (Exception e)
            {
                Log.Error("contract: holding a completion failed (it is held): " + e.GetType().Name);
                return false;
            }
        }

        // A finish is reached only from a completion, so outside a 92 it is held with it. Inside one, the rewards come
        // at once for every human agency, with the lines only the owner's game runs.
        [HarmonyPatch(typeof(Contract), "FinishContractForCompany")]
        [HarmonyPrefix]
        static bool FinishAtCompletion(Contract __instance, Company company)
        {
            WorldRuleState st;
            if (!Holding(out st)) return true;
            if (HumanAgencies.ApplyingKind != ActionKind.ObjectiveDone)
            {
                Log.Warn("contract: a finish outside a completion entry was held: " + __instance.ContractDefinition.ID);
                return false;
            }
            NoteChain(st, GameAgencies.Wire(company),
                new ContractRef { Contract = __instance.ContractDefinition.ID, Objective = -1 }, true);
            if (!HumanAgencies.IsHuman(company)) return true;
            if (IsLocal(company)) OwnerLines(__instance);
            Contract.GiveRewards(__instance.ContractDefinition, __instance, company, __instance.Rewards);
            return false;
        }

        // The local player's achievements and tutorial stage: only the owner's game has them.
        static void OwnerLines(Contract contract)
        {
            var def = contract.ContractDefinition;
            if (def.SteamAchievements != null)
                foreach (var achievement in def.SteamAchievements) achievement.CheckIfMetAndUnlock(contract);
            SerializedMonoBehaviourSingleton<TutorialManager>.Instance.StageTutorial = def.StageTutorialToActive + 1;
        }

        // "Player" in the skipForAI test is every human agency; rivals never get a skipForAI contract.
        public static bool CountsAsPlayer(Company company)
        {
            return HumanAgencies.Active ? HumanAgencies.IsHuman(company) : company.IsPlayer;
        }

        [HarmonyPatch(typeof(ContractManager), "ActivateContractForCompany", new[] { typeof(Company), typeof(Contract) })]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> SkipForAiTest(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "ContractManager.ActivateContractForCompany", 1,
                (c, i) => Il.Calls(c[i], typeof(Company), "get_IsPlayer", 0),
                () => new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ContractRule), "CountsAsPlayer")) });
        }

        [HarmonyPatch(typeof(ContractManager), "ActivateContractForCompany", new[] { typeof(Company), typeof(Contract) })]
        [HarmonyPrefix]
        static bool HoldActivation(Company company, Contract contract)
        {
            WorldRuleState st;
            if (!Holding(out st)) return true;
            try
            {
                string agency = GameAgencies.Wire(company), id = contract.ContractDefinition.ID;
                int kind = HumanAgencies.ApplyingKind;
                if (kind == ActionKind.ObjectiveDone) return true;
                if (kind == ActionKind.ContractAccept || kind == ActionKind.ContractActivated)
                {
                    ActionRequest e = HumanAgencies.ApplyingEntry;
                    if (e != null && e.Contract != null && e.Contract.Contract == id && AgencyWire.From(e.Agency) == agency)
                        return true;
                }
                // noted only when vanilla would activate it: no 95 is ever a logged no-op
                bool wouldActivate = !company.BlockContract
                    && !(contract.ContractDefinition.SkipForAI && !CountsAsPlayer(company))
                    && contract.ContractStateForCompany(company) == ContractManager.EContractState.Available;
                if (!wouldActivate) return false;
                if (HumanAgencies.Host != null) st.Host.NoteActivation(agency, id);
                else st.Held.Add(HeldNotes.Key(agency, id, -1));
                return false;
            }
            catch (Exception e)
            {
                Log.Error("contract: holding an activation failed (it is held): " + e.GetType().Name);
                return false;
            }
        }

        // ---- the end game (kind 96) ----

        // The host's own run: the ship counter before it. The job system plans the flight in a later frame, so the 96
        // raised at the end of this call always goes out before the flight's 70.
        [HarmonyPatch(typeof(ContractManager), "ChangeTrajectoryAsteroidToEarth")]
        [HarmonyPrefix]
        static void EndGameIn(ref long __state)
        {
            __state = -1;
            WorldRuleState st;
            if (!Holding(out st) || HumanAgencies.Host == null || st.Host == null) return;
            __state = Spacecraft.count;
        }

        // A puller was built (the counter moved): it took the counter's old value as its id (ConstructShipOnPlanet).
        // Vanilla builds it for the local player, the host's own agency.
        [HarmonyPatch(typeof(ContractManager), "ChangeTrajectoryAsteroidToEarth")]
        [HarmonyFinalizer]
        static void EndGameOut(long __state)
        {
            if (__state < 0 || !HumanAgencies.Active) return;
            try
            {
                WorldRuleState st = State;
                long after = Spacecraft.count;
                GameManager m = MonoBehaviourSingleton<GameManager>.Instance;
                if (after == __state || st.Host == null || m == null) return;
                st.Host.EndGameShip(GameAgencies.Wire(m.Player), EndGame.PullerType, __state, new[]
                {
                    new CounterRow { Which = CounterKind.SpacecraftCount, Before = __state, After = after }
                });
            }
            catch (Exception e)
            {
                Log.Error("contract: raising the end-game ship failed: " + e.GetType().Name);
                if (State.Host != null) State.Host.HostFault("contract: the end-game ship could not be raised");
            }
        }

        // ---- file saves ----

        // Every file save ends in SaveToFile. The host raises what it has noted first (the notes never outlive a save);
        // a client only marks that a file save is running.
        [HarmonyPatch(typeof(LoadSaveManager), "SaveToFile")]
        [HarmonyPrefix]
        static void SaveStarting(ref bool __state)
        {
            __state = false;
            WorldRuleState st;
            if (!Holding(out st)) return;
            st.FileSaving = true;
            __state = true;
            if (HumanAgencies.Host == null) return;
            try { st.Host.FlushNotes(); }
            catch (Exception e)
            {
                Log.Error("contract: flushing the notes before a save failed: " + e.GetType().Name);
                st.Host.HostFault("contract: the save flush failed");
            }
        }

        [HarmonyPatch(typeof(LoadSaveManager), "SaveToFile")]
        [HarmonyFinalizer]
        static void SaveEnded(bool __state)
        {
            if (__state && HumanAgencies.Active) State.FileSaving = false;
        }

        static readonly AccessTools.FieldRef<CompanyObjectiveData, bool> IsCompleteField =
            AccessTools.FieldRefAccess<CompanyObjectiveData, bool>("isComplete");

        // A client's save written while it holds completions the host has not raised yet: the written copy (never the
        // live game) marks them complete, so a later load finishes them. Never throws: SaveToFile ignores a failure
        // here and writes anyway.
        [HarmonyPatch(typeof(ContractManager), "Manager.ISaveStateDataProvider.InjectIntoSaveGameData")]
        [HarmonyPostfix]
        static void MarkHeldInCopy(Manager.SaveGameData saveGameData)
        {
            try
            {
                if (!HumanAgencies.Active || HumanAgencies.Host != null) return;
                WorldRuleState st = State;
                if (!st.FileSaving || st.Held.Count == 0 || saveGameData == null) return;
                foreach (var copy in saveGameData.companyContractDataSave)
                {
                    string agency = AgencyWire.From(copy.companyId.id);
                    for (int i = 0; i < copy.ObjectivesDataList.Count; i++)
                        if (st.Held.Has(HeldNotes.Key(agency, copy.contractId, i)))
                            IsCompleteField(copy.ObjectivesDataList[i]) = true;
                }
            }
            catch (Exception e)
            {
                Log.Error("contract: marking held completions in the saved copy failed: " + e.GetType().Name);
            }
        }
    }
}
