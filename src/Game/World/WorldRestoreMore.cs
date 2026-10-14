using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using CompanyContractData = Game.ContractsObjectives.CompanyContractData;
using CompanyObjectiveData = Game.ContractsObjectives.CompanyObjectiveData;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using MarketOfferManager = Manager.MarketOfferManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Offer = Game.ObjectInfoDataScripts.Offer;
using OfferData = Game.ObjectInfoDataScripts.OfferData;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;

namespace SolarExpanseMultiplayer
{
    // The exploration, contract and market restores (M7 DD-W10, W-S7): the host's rows of one healed group set into this
    // game through plain fields and setters, which fire nothing that matters (an objective restored to complete keeps
    // its listeners until the game cleans them up; a rewards list is never re-run by a state set). Every row is found
    // before anything is written, so a row this game lacks leaves the group as it was and the answer is the reason (the
    // full rejoin), never a half restore. The caller holds Applying. Main thread.
    public static partial class WorldRestore
    {
        static readonly AccessTools.FieldRef<ObjectInfoData, bool> DiscoveredField =
            AccessTools.FieldRefAccess<ObjectInfoData, bool>("isDiscoveredAsteroid");
        static readonly AccessTools.FieldRef<CompanyObjectiveData, bool> CompleteField =
            AccessTools.FieldRefAccess<CompanyObjectiveData, bool>("isComplete");
        static readonly AccessTools.FieldRef<Offer, double> CompletedField =
            AccessTools.FieldRefAccess<Offer, double>("countCompleted");
        static readonly AccessTools.FieldRef<MarketOfferManager, int> CountOffer =
            AccessTools.FieldRefAccess<MarketOfferManager, int>("countOffer");
        static readonly AccessTools.FieldRef<MarketOfferManager, MarketOfferManager.ResourceMarketPriceData> PriceData =
            AccessTools.FieldRefAccess<MarketOfferManager, MarketOfferManager.ResourceMarketPriceData>(
                "resourceMarketPriceData");
        static readonly FieldInfo UiChange = AccessTools.Field(typeof(MarketOfferManager), "offerChangeForUI");

        // ---- exploration: the site's flags and its explored rows (a site is its body's id) ----

        static string Exploration(string agency, string group, List<HealRow> rows)
        {
            Company c = GameAgencies.Find(agency);
            int body;
            if (c == null) return "no agency " + agency;
            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out body))
                return "exploration group " + group;
            ObjectInfoData d = GameAgencies.Site(GameAgencies.Body(body), c);
            if (d == null || d.listExploredResourcesRows == null) return "no site of " + agency + " at body " + body;
            ExploreGroup g;
            string bad = HealRows.Exploration(rows, group, out g);
            if (bad != null) return bad;
            var found = new List<RowExploredResourcesData>();
            foreach (ExploreRow r in g.Rows)
            {
                RowExploredResourcesData row = null;
                foreach (RowExploredResourcesData x in d.listExploredResourcesRows)
                    if (x != null && x.ResourceType != null && x.ResourceType.ID == r.Resource && !found.Contains(x))
                    { row = x; break; }
                if (row == null) return "no explored row " + r.Resource + " at body " + body;
                found.Add(row);
            }
            DiscoveredField(d) = g.Discovered;
            d.IsBeingCurrentlyDiscovered = g.Being;
            for (int i = 0; i < found.Count; i++)
            {
                RowExploredResourcesData row = found[i];
                ExploreRow r = g.Rows[i];
                if (row.Value != r.Value) row.Value = r.Value;
                if (row.PreliminaryExplored != r.Preliminary) row.PreliminaryExplored = r.Preliminary;
                if (row.SelectedToMine != r.Selected) row.SelectedToMine = r.Selected;
            }
            return null;
        }

        // ---- contracts: the agency's state, start and objectives of one contract ----

        static string Contracts(string agency, string group, List<HealRow> rows)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null) return "no agency " + agency;
            ContractGroup g;
            string bad = HealRows.Contract(rows, group, out g);
            if (bad != null) return bad;
            CompanyContractData d = WorldContracts.DataOf(WorldContracts.Find(group), c);
            if (d == null) return "no contract " + group + " of " + agency + " here";
            if (g.StartTicks < 0 || g.StartTicks > DateTime.MaxValue.Ticks) return "the start of contract " + group;
            if (!Enum.IsDefined(typeof(ContractManager.EContractState), g.State)) return "a contract state " + g.State;
            foreach (ObjectiveRow o in g.Objectives)
                if (o.Index >= d.ObjectivesDataList.Count || d.ObjectivesDataList[o.Index] == null)
                    return "no objective " + o.Index + " of " + group;
            d.currentState = (ContractManager.EContractState)g.State;
            d.startContract = new DateTime(g.StartTicks);
            foreach (ObjectiveRow o in g.Objectives)
            {
                CompanyObjectiveData od = d.ObjectivesDataList[o.Index];
                CompleteField(od) = o.Complete;
                od.howMuchCurrent = o.Current;
            }
            return null;
        }

        // ---- market: the offers of one id, or every price entry ----

        static string Market(string group, List<HealRow> rows)
        {
            if (group == HealRows.PriceGroup) return Prices(rows);
            var offers = new List<OfferRow>();
            string bad = HealRows.Offers(rows, group, offers);
            if (bad != null) return bad;
            MarketOfferManager mgr = MarketOfferManager.Instance;
            if (mgr == null) return "no market";
            var made = new List<Offer>();
            foreach (OfferRow r in offers)
            {
                Company company = GameAgencies.Find(r.Company);
                ObjectInfo where = GameAgencies.Body(r.Where);
                ResourceDefinition rd = WorldMarket.Resource(r.Resource);
                if (company == null || where == null || rd == null) return "an offer of " + group + " does not resolve";
                var offer = new Offer(new OfferData { whereOffer = where, offerStartDate = new DateTime(r.StartTicks),
                    buySell = r.Buy, pricePerUnit = r.Price, rd = rd, countToBuySell = r.Amount, company = company });
                offer.SetID(r.Id);
                CompletedField(offer) = r.Completed;
                if (offer.Company == null) return "an offer's company does not resolve";   // also caches it for FullFill
                made.Add(offer);
            }
            // in place where this game has the offer too, so the list keeps the order every game walks it in
            List<Offer> list = mgr.Offerts;
            var at = new List<int>();
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].ID == int.Parse(group, CultureInfo.InvariantCulture) && !list[i].OfferDone)
                    at.Add(i);
            MethodInfo onPart = AccessTools.Method(typeof(MarketOfferManager), "OfferOnOnPartComplete");
            for (int k = 0; k < made.Count; k++)
            {
                if (onPart != null)
                    made[k].OnPartComplete += (Action<Offer, double>)Delegate.CreateDelegate(
                        typeof(Action<Offer, double>), mgr, onPart);
                if (k < at.Count) { made[k].offerCountWhenAdd = list[at[k]].offerCountWhenAdd; list[at[k]] = made[k]; }
                else { made[k].offerCountWhenAdd = CountOffer(mgr); list.Add(made[k]); }
            }
            for (int k = at.Count - 1; k >= made.Count; k--) list.RemoveAt(at[k]);
            int id = int.Parse(group, CultureInfo.InvariantCulture);
            if (made.Count > 0 && CountOffer(mgr) < id) CountOffer(mgr) = id;
            var changed = UiChange != null ? UiChange.GetValue(mgr) as Action : null;
            if (changed != null) changed();
            return null;
        }

        static string Prices(List<HealRow> rows)
        {
            var prices = new List<PriceRow>();
            string bad = HealRows.Prices(rows, prices);
            if (bad != null) return bad;
            MarketOfferManager mgr = MarketOfferManager.Instance;
            MarketOfferManager.ResourceMarketPriceData data = mgr != null ? PriceData(mgr) : null;
            if (data == null || data.resourceMarketPriceDataList == null) return "no price list";
            var targets = new List<MarketOfferManager.ResourceMarketPriceDataPerObjectPerRD>();
            foreach (PriceRow p in prices)
            {
                ResourceDefinition rd = WorldMarket.Resource(p.Resource);
                ObjectInfo body = GameAgencies.Body(p.Body);
                if (rd == null || body == null) return "a price entry does not resolve";
                MarketOfferManager.ResourceMarketPriceDataPerObjectPerRD entry = null;
                foreach (var e in data.resourceMarketPriceDataList)
                    if (e != null && e.oiID == p.Body && e.rd == rd) { entry = e; break; }
                if (entry == null)
                {
                    // the game adds the entry when a price nobody traded at is asked for
                    body.GetPrice(rd);
                    foreach (var e in data.resourceMarketPriceDataList)
                        if (e != null && e.oiID == p.Body && e.rd == rd) { entry = e; break; }
                }
                if (entry == null) return "no price entry for " + p.Resource + " at " + p.Body;
                targets.Add(entry);
            }
            for (int i = 0; i < targets.Count; i++) targets[i].marketClearingPrice = prices[i].Price;
            return null;
        }
    }
}
