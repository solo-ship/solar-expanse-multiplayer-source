using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One listed offer as the host's checks need it.
    public struct OfferFact { public OfferKey Key; public bool Done; public double Left; public double Price; }

    // The host's market checks that need no game (DD-W4): the facts come from the game, the verdicts are these. The
    // order is the order the checks are made in; the first that fails is the refusal.
    public static class MarketHostRules
    {
        static IList<LiveOffer> Live(IList<OfferFact> listed)
        {
            var l = new List<LiveOffer>(listed == null ? 0 : listed.Count);
            if (listed != null)
                for (int i = 0; i < listed.Count; i++) l.Add(new LiveOffer { Key = listed[i].Key, Done = listed[i].Done });
            return l;
        }

        // 83 OfferTake: 0, or the refusal. fill is the request's op (Count, Price, Left; Taker is the actor).
        // The offer must be listed once and not done (NoSuchOffer), not the actor's own (OwnOffer), still have as many
        // units left as the dialog saw (OfferSmaller), and its price must be the one the actor saw (PriceChanged).
        public static int Take(IList<OfferFact> listed, MarketOp fill, string actor)
        {
            if (fill == null || fill.Offer == null) return RefuseCodes.NoSuchOffer;
            int at = MarketRules.Find(Live(listed), fill.Offer);
            if (at < 0 || listed[at].Done) return RefuseCodes.NoSuchOffer;
            OfferFact o = listed[at];
            if (string.Equals(o.Key.Agency, actor, StringComparison.Ordinal)) return RefuseCodes.OwnOffer;
            if (o.Left < fill.Left || o.Left < fill.Count) return RefuseCodes.OfferSmaller;
            return MarketRules.SamePrice(fill.Price, o.Price) ? 0 : RefuseCodes.PriceChanged;
        }

        // 81 OfferCancel (and the Cancel of 82): the offer is listed once, not done, and the actor's own.
        public static int Cancel(IList<OfferFact> listed, MarketOp cancel, string actor)
        {
            if (cancel == null || cancel.Offer == null) return RefuseCodes.NoSuchOffer;
            int at = MarketRules.Find(Live(listed), cancel.Offer);
            if (at < 0 || listed[at].Done) return RefuseCodes.NoSuchOffer;
            return string.Equals(listed[at].Key.Agency, actor, StringComparison.Ordinal) ? 0 : RefuseCodes.OfferNotYours;
        }

        // 80 / 82: the mod's own rules on top of the window's (the resource is in the market list; the agency's live
        // offers stay within MaxOffersPerAgency). An edit swaps one offer for another, so it adds none.
        public static int Create(bool resourceInMarketList, int liveOffersOfActor, bool edit)
        {
            if (!resourceInMarketList) return RefuseCodes.MarketClosed;
            int after = edit ? liveOffersOfActor : liveOffersOfActor + 1;
            return after > ActionLimits.MaxOffersPerAgency ? RefuseCodes.OffersFull : 0;
        }

        // 84 MarketInstant. Source 0 is the unused MarketWindow (refused); source 1 needs the actor's home body, and
        // the limit price the actor typed must still be met by the clearing price (a buy at or above it, a sell at
        // or below it): only then is the trade made, at the clearing price.
        public static int Instant(int source, bool onHome, bool sell, double limit, double clearing)
        {
            if (source == MarketShapes.SourceMarketWindow) return RefuseCodes.NotAllowedHere;
            if (!onHome) return RefuseCodes.NotOnHome;
            bool same = MarketRules.SamePrice(limit, clearing);
            bool met = sell ? limit <= clearing || same : limit >= clearing || same;
            return met ? 0 : RefuseCodes.PriceChanged;
        }
    }
}
