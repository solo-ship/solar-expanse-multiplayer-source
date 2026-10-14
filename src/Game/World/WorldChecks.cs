using System;
using System.Collections.Generic;
using Company = Game.Company;
using CompanyContractData = Game.ContractsObjectives.CompanyContractData;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Offer = Game.ObjectInfoDataScripts.Offer;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;

namespace SolarExpanseMultiplayer
{
    // The windows' checks for the market and contract kinds (M7 DD-W3, DD-W4, DD-W7, DD-W13), repeated on the host for
    // the ACTING agency: the game's own tests read the local player, so they are written here from the same
    // conditions, and the decisions that need no game are Core's (MarketHostRules). 0, or the refusal. Main thread.
    public static class WorldChecks
    {
        public static bool Handles(int kind)
        {
            return (kind >= ActionKind.OfferCreate && kind <= ActionKind.LeaveStock) || kind == ActionKind.BuyAndBuild
                || kind == ActionKind.CraftBuyAndBuild;
        }

        public static int Check(ActionRequest a)
        {
            Company actor = GameAgencies.Find(a.Agency);
            if (actor == null || actor.MoneyController == null) return RefuseCodes.BadTarget;
            switch (a.Kind)
            {
                case ActionKind.OfferCreate: return Create(a, actor, false);
                case ActionKind.OfferCancel: return MarketHostRules.Cancel(WorldMarket.Facts(), a.Market[0], a.Agency);
                case ActionKind.OfferEdit: return Edit(a, actor);
                case ActionKind.OfferTake: return Take(a, actor);
                case ActionKind.MarketInstant: return Instant(a, actor);
                case ActionKind.NukeAsteroid: return WorldNuke.Check(a, actor);
                case ActionKind.ContractAccept: return Accept(a, actor);
                case ActionKind.ContractCancel: return CancelContract(a, actor);
                case ActionKind.ObjectiveUi: return ObjectiveUi(a, actor);
                case ActionKind.LeaveStock: return LeaveStock(a, actor);
                case ActionKind.BuyAndBuild: case ActionKind.CraftBuyAndBuild: return WorldBuy.Check(a, actor);
                default: return RefuseCodes.NotAllowedHere;
            }
        }

        // ---- the offer board ----

        // ValidCreatedOffer for the actor (D 135287-135346): a body, a resource that is not empty, the stock at the body
        // for a sell or the money for a buy, a count above 0; then the mod's own market list and offer cap. Neither the
        // market list nor the price is tested by the window (vanilla offers only the list and has no price floor).
        static int Create(ActionRequest a, Company actor, bool edit)
        {
            MarketOp add = a.Market[a.Market.Length - 1];
            ObjectInfo body = GameAgencies.Body(add.Offer.Where);
            ResourceDefinition rd = WorldMarket.Resource(add.Offer.Resource);
            if (body == null || rd == null) return RefuseCodes.BadTarget;
            ObjectInfoData site = GameAgencies.Site(body, actor);
            if (site == null) return RefuseCodes.BadTarget;
            if (!add.Offer.Buy && site.CheckResources(rd) < add.Count) return RefuseCodes.CannotAfford;
            if (add.Offer.Buy && actor.MoneyController.CurrentMoney < add.Count * add.Price) return RefuseCodes.CannotAfford;
            return MarketHostRules.Create(WorldMarket.InMarketList(rd), WorldMarket.LiveOffersOf(a.Agency), edit);
        }

        // The Cancel's own checks first, then the new data's, before the refund (vanilla checks first and cancels
        // second, so the escrow of the offer being edited does not count).
        static int Edit(ActionRequest a, Company actor)
        {
            int code = MarketHostRules.Cancel(WorldMarket.Facts(), a.Market[0], a.Agency);
            return code != 0 ? code : Create(a, actor, true);
        }

        static int Take(ActionRequest a, Company actor)
        {
            MarketOp fill = a.Market[0];
            int code = MarketHostRules.Take(WorldMarket.Facts(), fill, a.Agency);
            if (code != 0) return code;
            Offer offer = WorldMarket.Find(fill.Offer);
            if (offer == null || offer.Rd == null || offer.WhereOffer == null) return RefuseCodes.NoSuchOffer;
            ObjectInfoData site = GameAgencies.Site(offer.WhereOffer, actor);
            if (site == null) return RefuseCodes.BadTarget;
            // Offer.CanFullFill for the actor (D 117424-117448): the stock when the maker buys, the money when it sells
            if (offer.BuySell)
            {
                if (site.CheckResources(offer.Rd) < fill.Count) return RefuseCodes.CannotAfford;
                if (offer.Rd.ResourceType == ResourceDefinition.EResourceType.Energy && fill.Count > site.EnergyInBattery)
                    return RefuseCodes.CannotAfford;
            }
            else
            {
                if (actor.MoneyController.CurrentMoney < offer.PricePerUnit * fill.Count) return RefuseCodes.CannotAfford;
                if (offer.Rd.ResourceType == ResourceDefinition.EResourceType.Energy
                    && fill.Count > site.EnergyFreeSpaceInBattery)
                    return RefuseCodes.CannotAfford;
            }
            return 0;
        }

        // The make-offer window's "Immediately" (source 1): ValidCreatedOffer's tests, the limit against the clearing
        // price, and the actor's home body (D 135348-135379). Source 0 (the unused window) is refused.
        static int Instant(ActionRequest a, Company actor)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            ResourceDefinition rd = WorldMarket.Resource(a.Item);
            if (body == null || rd == null) return RefuseCodes.BadTarget;
            ObjectInfoData site = GameAgencies.Site(body, actor);
            if (site == null) return RefuseCodes.BadTarget;
            double limit = a.Market != null && a.Market.Length == 1 ? a.Market[0].Price : 0;
            int code = MarketHostRules.Instant((int)a.ItemId, ReferenceEquals(actor.mainObjectInfo, body), a.Value == 1,
                limit, body.GetPrice(rd));
            if (code != 0) return code;
            if (a.Value == 0 && actor.MoneyController.CurrentMoney < a.Amount * limit) return RefuseCodes.CannotAfford;
            if (a.Value == 1 && site.CheckResources(rd) < a.Amount) return RefuseCodes.CannotAfford;
            return 0;
        }

        // ---- contracts ----

        // ActivateContractForCompany's own tests (D 80076-80117): not blocked, Available for the agency; the
        // skipForAI test passes for a human agency.
        static int Accept(ActionRequest a, Company actor)
        {
            Contract c = WorldContracts.Find(a.Contract.Contract);
            if (c == null || c.ContractDefinition == null) return RefuseCodes.BadTarget;
            if (actor.BlockContract) return RefuseCodes.ContractState;
            return WorldContracts.StateOf(c, actor) == ContractManager.EContractState.Available
                ? 0 : RefuseCodes.ContractState;
        }

        static int CancelContract(ActionRequest a, Company actor)
        {
            Contract c = WorldContracts.Find(a.Contract.Contract);
            if (c == null) return RefuseCodes.BadTarget;
            return WorldContracts.StateOf(c, actor) == ContractManager.EContractState.Active ? 0 : RefuseCodes.ContractState;
        }

        // A UI-only objective, active for the actor and not done: trusted from the owner's screen, nothing else.
        static int ObjectiveUi(ActionRequest a, Company actor)
        {
            Contract c = WorldContracts.Find(a.Contract.Contract);
            if (c == null) return RefuseCodes.BadTarget;
            if (WorldContracts.StateOf(c, actor) != ContractManager.EContractState.Active) return RefuseCodes.ContractState;
            CompanyContractData d = WorldContracts.DataOf(c, actor);
            int i = a.Contract.Objective;
            if (d == null || i < 0 || i >= d.ObjectivesDataList.Count || d.ObjectivesDataList[i] == null)
                return RefuseCodes.BadTarget;
            if (!WorldContracts.UiOnly(d.ObjectivesDataList[i].Objective)) return RefuseCodes.NotAllowedHere;
            return d.ObjectivesDataList[i].IsCompleteUI ? RefuseCodes.ContractState : 0;
        }

        // ---- leave stock on a body (the dialog's own checks for the actor) ----

        static int LeaveStock(ActionRequest a, Company actor)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            ResourceDefinition rd = WorldMarket.Resource(a.Item);
            if (body == null || rd == null) return RefuseCodes.BadTarget;
            ObjectInfoData site = GameAgencies.Site(body, actor);
            if (site == null || !rd.CanBeLeftOnObject) return RefuseCodes.BadTarget;
            var row = site.FastGetResource(rd);
            return row == null || a.Amount <= 0 ? RefuseCodes.BadTarget : 0;
        }
    }
}
