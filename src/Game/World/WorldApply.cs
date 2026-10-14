using System;
using System.Collections.Generic;
using Company = Game.Company;
using CompanyContractData = Game.ContractsObjectives.CompanyContractData;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Offer = Game.ObjectInfoDataScripts.Offer;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using CargoAll = Game.ObjectInfoDataScripts.CargoAll;
using Spacecraft = CustomUpdate.Spacecraft;
using SpacecraftType = Data.ScriptableObject.SpacecraftType;

namespace SolarExpanseMultiplayer
{
    // The host's apply of the shared-world kinds that act now (M7 DD-W3 to DD-W7, DD-W13): a player's market, contract,
    // buy-what-is-missing and leave-stock kinds, and the 92 and 95 the host raises at the day event. Each is the host's
    // own copy of the window's steps for the acting agency (the windows' methods act for the local player), in the
    // window's order. The host decides the numbers, runs the game calls and returns them as the entry's numbers. The
    // entries the host's game already made (90, 91, 94, 96) are recorded by their rules and need no apply here.
    // A client applies the host's numbers instead (ApplyGiven): the ops, the rival's steps, the chain to compare.
    static class WorldApply
    {
        static readonly HarmonyLib.AccessTools.FieldRef<ContractManager, bool> EndGameWas =
            HarmonyLib.AccessTools.FieldRefAccess<ContractManager, bool>("changeTrajectoryAsteroidToEarthWas");

        public static bool Handles(int kind)
        {
            return (kind >= ActionKind.OfferCreate && kind <= ActionKind.LeaveStock && kind != ActionKind.NukeAsteroid)
                || kind == ActionKind.BuyAndBuild || kind == ActionKind.CraftBuyAndBuild
                || kind == ActionKind.ObjectiveDone || kind == ActionKind.ContractActivated;
        }

        public static ApplyOutcome Apply(ActionRequest a)
        {
            Company actor = GameAgencies.Find(a.Agency);
            if (actor == null) return Refused(RefuseCodes.BadTarget);
            WorldCounters before = WorldCounters.Take(actor);
            var numbers = new WorldNumbers();
            IList<PrintEntry> touched = null;
            switch (a.Kind)
            {
                case ActionKind.OfferCreate: case ActionKind.OfferCancel: case ActionKind.OfferEdit:
                case ActionKind.OfferTake: case ActionKind.MarketInstant:
                {
                    MarketOp[] ops = Ops(a, actor);
                    string bad = ops == null ? "the offer is gone" : WorldMarket.Run(ops);
                    if (bad != null) throw new InvalidOperationException(ActionKind.Name(a.Kind) + ": " + bad);
                    numbers.Ops = ops;
                    break;
                }
                case ActionKind.BuyAndBuild: case ActionKind.CraftBuyAndBuild:
                {
                    string problem;
                    MarketOp[] ops = WorldBuy.Ops(a, actor, out problem);
                    string bad = problem ?? WorldMarket.Run(ops);
                    if (bad != null) throw new InvalidOperationException(ActionKind.Name(a.Kind) + ": " + bad);
                    WorldBuy.MoveAndPay(a, actor);
                    numbers.Ops = ops;
                    touched = new List<PrintEntry> { new PrintEntry { Kind = ObjectKind.SiteBuilds, Agency = a.Agency,
                        Body = a.Body } };
                    break;
                }
                case ActionKind.ContractAccept:
                    ContractManager.Instance.ActivateContractForCompany(actor, WorldContracts.Find(a.Contract.Contract));
                    break;
                case ActionKind.ContractCancel:
                    ContractManager.Instance.CancelContractForCompany(actor, WorldContracts.Find(a.Contract.Contract));
                    break;
                case ActionKind.ObjectiveUi:
                    break;   // the 92 that follows completes it; this entry only reports the tick
                case ActionKind.LeaveStock:
                    LeaveStock(a, actor);
                    break;
                case ActionKind.ObjectiveDone:
                {
                    ChainRow[] chain = Complete(a, actor);
                    if (chain == null) return Refused(RefuseCodes.AlreadyDone);
                    numbers.Chain = chain;
                    break;
                }
                case ActionKind.ContractActivated:
                    ContractManager.Instance.UpdateUnlock(actor, a.Contract.Contract);
                    break;
                default: return Refused(RefuseCodes.NotAllowedHere);
            }
            WorldCounters after = WorldCounters.Take(actor);
            if (numbers.Counters == null) numbers.Counters = before.RowsSince(after, a.Agency);
            if (numbers.NewIds == null) numbers.NewIds = before.NewIdsSince(after);
            return new ApplyOutcome { Code = 0, Applied = a, Numbers = numbers, Touched = touched };
        }

        static ApplyOutcome Refused(int code) { return new ApplyOutcome { Code = code }; }

        // ---- the offer board ----

        // The ops the host makes of a request, from the offers as they are now (the checks passed a moment ago).
        static MarketOp[] Ops(ActionRequest a, Company actor)
        {
            switch (a.Kind)
            {
                case ActionKind.OfferCreate: return new[] { WorldMarket.MadeAdd(a.Market[0]) };
                case ActionKind.OfferCancel:
                {
                    Offer o = WorldMarket.Find(a.Market[0].Offer);
                    return o == null ? null : new[] { WorldMarket.MadeCancel(o) };
                }
                case ActionKind.OfferEdit:
                {
                    Offer o = WorldMarket.Find(a.Market[0].Offer);
                    return o == null ? null : new[] { WorldMarket.MadeCancel(o), WorldMarket.MadeAdd(a.Market[1]) };
                }
                case ActionKind.OfferTake:
                {
                    Offer o = WorldMarket.Find(a.Market[0].Offer);
                    return o == null ? null : new[] { WorldMarket.MadeFill(o, a.Agency, a.Market[0].Count) };
                }
                default:   // MarketInstant: charged the clearing price now, as the window does
                {
                    ObjectInfo body = GameAgencies.Body(a.Body);
                    ResourceDefinition rd = WorldMarket.Resource(a.Item);
                    if (body == null || rd == null) return null;
                    return new[] { new MarketOp { Op = MarketOps.Instant, Taker = a.Agency, Resource = a.Item, Body = a.Body,
                        Count = a.Amount, Price = body.GetPrice(rd), Value = a.Value } };
                }
            }
        }

        // ---- contracts ----

        // The root MarkAsComplete of a 92, under its keyed apply (the rule's hold lets it through), with everything it
        // sets off: the chain as the contract rule saw it happen (BeginChain/TakeChain, the same on every game; the
        // counters and ids of the rewards come from WorldCounters). null when the objective is complete already.
        static ChainRow[] Complete(ActionRequest a, Company actor)
        {
            Contract c = WorldContracts.Find(a.Contract.Contract);
            CompanyContractData d = WorldContracts.DataOf(c, actor);
            int i = a.Contract.Objective;
            if (d == null || i < 0 || i >= d.ObjectivesDataList.Count || d.ObjectivesDataList[i] == null) return null;
            if (d.ObjectivesDataList[i].IsCompleteUI) return null;
            ChainRow[] chain;
            ContractRule.BeginChain();
            try { d.ObjectivesDataList[i].MarkAsComplete(); }
            finally { chain = ContractRule.TakeChain(); }
            return chain;
        }

        // ---- a client: the host's numbers applied (M7 Task 12) ----

        public static bool HandlesGiven(int kind)
        {
            return Handles(kind) || kind == ActionKind.MarketMaker || kind == ActionKind.AiAction
                || kind == ActionKind.EndGameShip;
        }

        // Code 0 when applied; else the reason is logged and the client is out of step. The counters were set before
        // by the client glue (CounterSync) and are checked after it.
        public static ApplyOutcome ApplyGiven(ActionRequest a, ApplyNumbers given)
        {
            string why;
            try { why = Given(a, given as WorldNumbers); }
            catch (Exception e)
            {
                Log.Error("command: applying " + ActionKind.Name(a.Kind) + " threw: " + ExceptionText.Chain(e));
                why = "the game threw " + e.GetType().Name;
            }
            if (why == null) return new ApplyOutcome { Code = 0, Applied = a };
            Log.Warn("command: " + ActionKind.Name(a.Kind) + " " + a.Agency + ": " + why);
            return Refused(RefuseCodes.NotAllowedHere);
        }

        static string Given(ActionRequest a, WorldNumbers n)
        {
            MarketOp[] ops = n != null ? n.Ops : null;
            if (a.Kind == ActionKind.AiAction) return AiSteps.Replay(a.Agency, n != null ? n.Steps : null, ops);
            if (a.Kind == ActionKind.MarketMaker) return Ops(ops);
            Company actor = GameAgencies.Find(a.Agency);
            if (actor == null) return "no agency " + a.Agency + " here";
            switch (a.Kind)
            {
                case ActionKind.OfferCreate: case ActionKind.OfferCancel: case ActionKind.OfferEdit:
                case ActionKind.OfferTake: case ActionKind.MarketInstant:
                    return ops == null ? "a market entry with no ops" : Ops(ops);
                case ActionKind.BuyAndBuild: case ActionKind.CraftBuyAndBuild:
                {
                    string bad = Ops(ops);
                    if (bad != null) return bad;
                    WorldBuy.MoveAndPay(a, actor);
                    return null;
                }
                case ActionKind.ContractAccept:
                    ContractManager.Instance.ActivateContractForCompany(actor, WorldContracts.Find(a.Contract.Contract));
                    return null;
                case ActionKind.ContractCancel:
                    ContractManager.Instance.CancelContractForCompany(actor, WorldContracts.Find(a.Contract.Contract));
                    return null;
                case ActionKind.ObjectiveUi: return null;
                case ActionKind.LeaveStock: LeaveStock(a, actor); return null;
                case ActionKind.ObjectiveDone:
                {
                    ChainRow[] mine = Complete(a, actor);
                    if (mine == null)
                        return "objective " + a.Contract.Objective + " of " + a.Contract.Contract + " is complete here";
                    return ChainCheck.Differs(n != null ? n.Chain : null, mine);
                }
                case ActionKind.ContractActivated:
                    ContractManager.Instance.UpdateUnlock(actor, a.Contract.Contract);
                    return null;
                case ActionKind.EndGameShip: return EndGameShip(a, actor);
                default: return "a world kind no client applies";
            }
        }

        static string Ops(MarketOp[] ops)
        {
            if (ops == null) return null;
            for (int k = 0; k < ops.Length; k++)
            {
                string bad = MarketApply.Apply(ops[k]);
                if (bad != null) return "op " + k + ": " + bad;
            }
            return null;
        }

        // ---- the end-game ship (kind 96): what the host's ChangeTrajectoryAsteroidToEarth built, made here ----

        // The puller on the asteroid (the host's counters are set first), its whole-asteroid cargo, and the contract
        // manager marked as having run the end game, so this game's own copy (held by FlightSources) stops asking.
        // The flight itself is the 70 that follows.
        static string EndGameShip(ActionRequest a, Company actor)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            SpacecraftType type = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllSpacecraftType.GetByID(a.Item);
            if (body == null || body.IsInGameDestroy) return "the end-game asteroid " + a.Body + " is not here";
            if (type == null || type.ID != a.Item) return "no ship type " + a.Item + " here";
            var ship = MonoBehaviourSingleton<ShipManager>.Instance.ConstructShipOnPlanet(body, type, null, actor)
                as Spacecraft;
            if (ship == null) return "the end-game ship was not made";
            if (ship.ID != a.ItemId) return "the end-game ship took id " + ship.ID + ", the host's " + a.ItemId;
            CargoAll cargo = CargoAll.CreateCargoEmpty();
            cargo.entireAsteroid = true;
            ship.SetTabCargo(cargo);
            body.stopRefreshDisplayInfo = true;
            EndGameWas(ContractManager.Instance) = true;
            return null;
        }

        // ---- leave stock on a body (UIResorcesList.ShowLeaveResourceDialog's OK, D 164014-164076) ----

        static void LeaveStock(ActionRequest a, Company actor)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            ResourceDefinition rd = WorldMarket.Resource(a.Item);
            ObjectInfoData site = GameAgencies.Site(body, actor);
            RowResourcesData rrd = site.FastGetResource(rd);
            double result = Math.Min(a.Amount, rrd.Value);
            rrd.Value -= result;
            if (Extensions.MyExtensions.IsNearZero(rrd.Value)) rrd.Value = 0.0;
            site.InvokeResourcesChange2();
            float start = Manager.GameManager.Instance.Economic.ResourceDepositStartingMiningFactor;
            RowResourcesData deposit = null;
            foreach (RowExploredResourcesData data in site.listExploredResourcesRows)
            {
                if (data.ObservedData.ResourcesType == rd && data.ExploredInAnyCapacity
                    && data.ObservedData.MiningFactor.HasValue
                    && Math.Abs(data.ObservedData.MiningFactor.Value - start) <= 1e-6 * Math.Max(Math.Abs(start), 1e-3))
                { deposit = data.ObservedData; break; }
            }
            if (deposit == null)
            {
                deposit = new RowResourcesData { ResourcesType = rd, Value = result, MiningFactor = start,
                    ResourceState = RowResourcesData.EResourceState.Solid, ObjectInfoData = site };
                body.ListRowResourcesData.Add(deposit);
                foreach (ObjectInfoData data in body.ObjectsInfoData)
                    data.listExploredResourcesRows.Add(new RowExploredResourcesData { ObservedData = deposit, Value = 1.0,
                        PreliminaryExplored = true, ResourceType = rd });
            }
            else deposit.Value += result;
            body.InvokeOnAddOrChangeDeposit(deposit, result, actor);
            body.UpdateHabitabilityAndOrVisualization(false);
        }
    }
}
