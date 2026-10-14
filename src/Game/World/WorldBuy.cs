using System;
using System.Collections.Generic;
using Company = Game.Company;
using EBonus = ScriptableObjectScripts.EBonus;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Offer = Game.ObjectInfoDataScripts.Offer;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using ResourcePriceOne = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePriceOne;
using SpacecraftConstructData = Data.SpacecraftConstructData;

namespace SolarExpanseMultiplayer
{
    // "Buy what is missing and build now" (B6, P5; kinds 15 and 34; M7 DD-W4). The row's own function buys as the local
    // player: this is its two lines for the acting agency. The need is the queued entry's price less the stock at the
    // body; the offers are walked in list order (MarketOfferManager.HaveOffertToBuy); each fill it lists is recorded as
    // a Fill op and applied through WorldMarket, so a client replays the counts, never the walk. Then the entry moves
    // up the queue and is paid (MoveUpInQuery, AddToProductionFromQuery).
    // Only an entry whose body is also where it buys is handled (the window can also buy for an orbit entry at its
    // parent planet; that row is refused here).
    static class WorldBuy
    {
        sealed class Target
        {
            public ObjectInfoData Site;
            public object Entry;         // the queue entry
            public object Placeholder;   // what the game moves up the queue
            public ResourcePrice Need;
        }

        static Target Find(ActionRequest a, Company actor)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            ObjectInfoData site = body != null ? GameAgencies.Site(body, actor) : null;
            if (site == null) return null;
            var things = new List<object>();
            List<SiteRow> rows = ProductionWorld.Rows(site, things);
            bool craftKind = a.Kind == ActionKind.CraftBuyAndBuild;
            for (int i = 0; i < rows.Count; i++)
            {
                SiteRow r = rows[i];
                if (!r.Queued || r.Craft != craftKind || r.Item != a.Item || r.Remaining != a.Count) continue;
                var e = (ProductionQuery)things[i];
                var t = new Target { Site = site, Entry = e };
                if (!craftKind)
                {
                    Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay f;
                    if (!site.DictionaryFacilityNotYetPayCash.TryGetValue(e, out f) || f == null) return null;
                    t.Placeholder = f;
                    int left = f.CountToBuild - f.CountBuild;
                    t.Need = Reduced(f.facilityDescriptor.Price * left * actor.BonusController.GetBonus(EBonus.BuildCost,
                        f.facilityDescriptor), site);
                    return t;
                }
                SpacecraftConstructData p = ProductionWorld.Placeholder(site, e);
                if (p == null) return null;
                t.Placeholder = p;
                int missing = p.CountToBuild - p.CountBuild;
                ResourcePrice one = p.SpacecraftType != null
                    ? p.SpacecraftType.Price * missing * actor.BonusController.GetBonus(EBonus.BuildCost, p.SpacecraftType)
                    : p.LaunchVehicleType.PriceBase * missing
                        * actor.BonusController.GetBonus(EBonus.BuildCost, p.LaunchVehicleType);
                t.Need = Reduced(one, site);
                return t;
            }
            return null;
        }

        // What the stock at the body already covers is taken off, then the rest rounded up (D 165240-165250).
        static ResourcePrice Reduced(ResourcePrice price, ObjectInfoData site)
        {
            foreach (ResourcePriceOne r in price.ListResources)
                r.Price -= Math.Min(r.Price, site.CheckResourcesInterface(r.ResourceDefinition));
            price.CeilToInt();
            return price;
        }

        static bool Walk(Company actor, Target t, out List<Offer> toBuy)
        {
            double price;
            string lack;
            return Manager.MarketOfferManager.Instance.HaveOffertToBuy(actor, t.Need, t.Site.ObjectInfo, out price, out toBuy,
                out lack);
        }

        public static int Check(ActionRequest a, Company actor)
        {
            Target t = Find(a, actor);
            if (t == null) return RefuseCodes.BadTarget;
            List<Offer> toBuy;
            if (!Walk(actor, t, out toBuy)) return RefuseCodes.CannotAfford;
            int fills = 0;
            foreach (double n in Counts(t, toBuy)) if (n > 0) fills++;
            return fills > ActionLimits.MaxBuyFills ? RefuseCodes.TooLarge : 0;
        }

        // The fills the walk lists, as ops, for the entry: each offer filled with what the walk counted for it, so the
        // whole stack's need is bought (an offer taken whole holds a stale HelpHowMuchBuy, see BuyFills).
        public static MarketOp[] Ops(ActionRequest a, Company actor, out string problem)
        {
            problem = null;
            Target t = Find(a, actor);
            List<Offer> toBuy;
            if (t == null || !Walk(actor, t, out toBuy)) { problem = "nothing to buy any more"; return null; }
            double[] counts = Counts(t, toBuy);
            var ops = new List<MarketOp>();
            for (int i = 0; i < toBuy.Count; i++)
                if (counts[i] > 0) ops.Add(WorldMarket.MadeFill(toBuy[i], a.Agency, counts[i]));
            return ops.ToArray();
        }

        static double[] Counts(Target t, List<Offer> toBuy)
        {
            var need = new List<BuyNeed>();
            foreach (ResourcePriceOne r in t.Need.ListResources)
                need.Add(new BuyNeed(r.ResourceDefinition != null ? r.ResourceDefinition.ID : null, r.Price));
            var walked = new List<BuyOffer>();
            foreach (Offer o in toBuy) walked.Add(new BuyOffer(o.Rd != null ? o.Rd.ID : null, o.CountLeft));
            return BuyFills.Counts(need, walked);
        }

        public static void MoveAndPay(ActionRequest a, Company actor)
        {
            Target t = Find(a, actor);
            if (t == null) throw new InvalidOperationException("the queued entry is gone after the fills");
            var f = t.Placeholder as Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
            if (f != null) t.Site.MoveUpInQuery(f);
            else t.Site.MoveUpInQuery((SpacecraftConstructData)t.Placeholder);
            t.Site.AddToProductionFromQuery();
        }
    }
}
