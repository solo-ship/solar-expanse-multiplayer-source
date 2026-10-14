using System;
using System.Collections.Generic;
using Company = Game.Company;
using MarketOfferManager = Manager.MarketOfferManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Offer = Game.ObjectInfoDataScripts.Offer;
using OfferData = Game.ObjectInfoDataScripts.OfferData;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The market as the shared world sees it (M7 DD-W3, DD-W4): the offers as facts and keys, and the one way a list of
    // market ops is applied. The host builds the ops of a player's request (WorldApply), then runs them here; a client
    // runs the host's ops here; so both do the same game calls in the same order. Every call acts for the op's agency,
    // never for the local player. Main thread.
    public static class WorldMarket
    {
        static MarketOfferManager Mgr { get { return MarketOfferManager.Instance; } }

        public static ResourceDefinition Resource(string id)
        {
            if (id == null) return null;
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance.AllResourceDefinitions;
            ResourceDefinition rd = all != null ? all.GetByID(id) : null;
            return rd != null && rd.ID == id ? rd : null;   // GetByID renames one old id: exact ids only
        }

        public static bool InMarketList(ResourceDefinition rd)
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance.AllResourceDefinitions;
            return rd != null && all != null && all.ListResourceDefinitionInMarketPlaceOffer.Contains(rd);
        }

        // The key an offer is named by. A listed offer with no resource (a modded save's) gets an empty one that no
        // request can match.
        public static OfferKey KeyOf(Offer o)
        {
            Company c = o.Company;   // also caches the company FullFill reads from its field (a loaded offer has none)
            return new OfferKey { Id = o.ID, Agency = GameAgencies.Wire(c), Resource = o.Rd != null ? o.Rd.ID : "",
                Buy = o.BuySell, Where = o.WhereOfferID, StartTicks = o.OfferStartDate.Ticks };
        }

        public static List<LiveOffer> Live()
        {
            List<Offer> offers = Mgr.Offerts;
            var list = new List<LiveOffer>(offers.Count);
            foreach (Offer o in offers) list.Add(new LiveOffer { Key = o == null ? null : KeyOf(o), Done = o != null && o.OfferDone });
            return list;
        }

        public static List<OfferFact> Facts()
        {
            List<Offer> offers = Mgr.Offerts;
            var list = new List<OfferFact>(offers.Count);
            foreach (Offer o in offers)
                list.Add(new OfferFact { Key = o == null ? null : KeyOf(o), Done = o != null && o.OfferDone,
                    Left = o != null ? o.CountLeft : 0, Price = o != null ? o.PricePerUnit : 0 });
            return list;
        }

        // The one listed offer the key names (done or not), or null when none or several match.
        public static Offer Find(OfferKey key)
        {
            int at = MarketRules.Find(Live(), key);
            return at < 0 ? null : Mgr.Offerts[at];
        }

        // A human agency's offers still open.
        public static int LiveOffersOf(string agency)
        {
            int n = 0;
            foreach (Offer o in Mgr.Offerts)
                if (o != null && !o.OfferDone && GameAgencies.Wire(o.Company) == agency) n++;
            return n;
        }

        // ---- applying ops, both sides ----

        // null, or why an op list could not be applied (a client then goes out of step; the host built it from its own
        // checks, so a problem there is a fault).
        public static string Run(MarketOp[] ops)
        {
            if (ops == null) return "no ops";
            for (int k = 0; k < ops.Length; k++)
            {
                string bad = One(ops[k]);
                if (bad != null) return "op " + k + " (" + (ops[k] == null ? "empty" : ops[k].Op.ToString()) + "): " + bad;
            }
            return null;
        }

        // One op: the rules' MarketApply.Apply (a 90's, a 94's) is this too.
        public static string Apply(MarketOp op) { return One(op); }

        static string One(MarketOp op)
        {
            if (op == null) return "empty";
            switch (op.Op)
            {
                case MarketOps.Add: return Add(op);
                case MarketOps.Cancel: return Cancel(op);
                case MarketOps.Fill: return Fill(op);
                case MarketOps.Instant: return Instant(op);
                case MarketOps.PriceDiff: return PriceDiff(op);
                case MarketOps.Lock: return Lock(op);
                default: return "unknown op";
            }
        }

        static string Add(MarketOp op)
        {
            Company c = GameAgencies.Find(op.Offer.Agency);
            ObjectInfo body = GameAgencies.Body(op.Offer.Where);
            ResourceDefinition rd = Resource(op.Offer.Resource);
            if (c == null || body == null || rd == null) return "the agency, body or resource does not resolve";
            if (WorldCounters.Offers + 1 != op.Offer.Id)
                return "the next offer id here is " + (WorldCounters.Offers + 1) + ", the host's is " + op.Offer.Id;
            // the maker's escrow, topped up within the tolerance a client's timing allows
            string short_ = op.Offer.Buy ? TopUpMoney(c, op.Price * op.Count, body)
                : TopUpStock(c, body, rd, op.Count);
            if (short_ != null) return short_;
            var offer = new Offer(new OfferData { whereOffer = body, offerStartDate = new DateTime(op.Offer.StartTicks),
                buySell = op.Offer.Buy, pricePerUnit = op.Price, rd = rd, countToBuySell = op.Count, company = c });
            Company read = offer.Company;   // AddOffer reads it too; the cached company is what FullFill needs
            if (read == null) return "the offer's company does not resolve";
            if (!Mgr.AddOffer(offer, true)) return "AddOffer refused";
            return offer.ID == op.Offer.Id ? null : "the offer took id " + offer.ID + ", the host's is " + op.Offer.Id;
        }

        static string Cancel(MarketOp op)
        {
            int at;
            string problem;
            List<LiveOffer> live = Live();
            int path = MarketRules.CancelPath(live, op, out at, out problem);
            if (path == 0) return problem;
            if (path == MarketRules.CancelListed)
            {
                Offer offer = Mgr.Offerts[at];
                Company read = offer.Company;
                if (read == null) return "the offer's company does not resolve";
                return Mgr.CancelOffer(offer, true) ? null : "CancelOffer found no such offer";
            }
            // gone here already (its removal ran first): pay the refund the host paid, by the calls CancelOffer makes
            Company c = GameAgencies.Find(op.Offer.Agency);
            ObjectInfo body = GameAgencies.Body(op.Offer.Where);
            ResourceDefinition rd = Resource(op.Offer.Resource);
            if (c == null || body == null || rd == null) return "the agency, body or resource does not resolve";
            if (op.Offer.Buy) c.MoneyController.AddMoney(op.Price * op.Count);
            else
            {
                ObjectInfoData site = GameAgencies.Site(body, c);
                if (site == null) return "the maker has no site at the offer's body";
                site.AddResources(rd, op.Count);
            }
            return null;
        }

        static string Fill(MarketOp op)
        {
            Offer offer = Find(op.Offer);
            if (offer == null) return "no one listed offer matches the key";
            if (offer.OfferDone) return "the offer is done";
            Company taker = GameAgencies.Find(op.Taker);
            if (taker == null) return "the taker does not resolve";
            if (!MarketRules.SamePrice(op.Price, offer.PricePerUnit))
                return "the price is " + offer.PricePerUnit + ", the host's " + op.Price;
            Company maker = offer.Company;   // FullFill reads the cached field, null until the property was read
            if (maker == null) return "the offer's company does not resolve";
            ObjectInfo body = offer.WhereOffer;
            string short_ = offer.BuySell ? TopUpStock(taker, body, offer.Rd, op.Count)
                : TopUpMoney(taker, offer.PricePerUnit * op.Count, body);
            if (short_ != null) return short_;
            return offer.FullFill(taker, op.Count) ? null : "FullFill refused";
        }

        static string Instant(MarketOp op)
        {
            Company c = GameAgencies.Find(op.Taker);
            ObjectInfo body = GameAgencies.Body(op.Body);
            ResourceDefinition rd = Resource(op.Resource);
            if (c == null || body == null || rd == null) return "the agency, body or resource does not resolve";
            ObjectInfoData site = GameAgencies.Site(body, c);
            if (site == null) return "no site of the agency at the body";
            double value = op.Count * op.Price;
            if (op.Value == 0)
            {
                string short_ = TopUpMoney(c, value, body);
                if (short_ != null) return short_;
                c.MoneyController.RemoveMoney(value);
                site.AddResources(rd, op.Count);
            }
            else
            {
                string short_ = TopUpStock(c, body, rd, op.Count);
                if (short_ != null) return short_;
                c.MoneyController.AddMoney(value, true);
                site.RemoveResource(rd, op.Count);
            }
            return null;
        }

        static string PriceDiff(MarketOp op)
        {
            ObjectInfo body = GameAgencies.Body(op.Body);
            ResourceDefinition rd = Resource(op.Resource);
            if (body == null || rd == null) return "the body or resource does not resolve";
            body.GetResourceMarketData(rd).maxPriceDifferencePercentage = op.Price;
            return null;
        }

        static string Lock(MarketOp op)
        {
            Offer offer = Find(op.Offer);
            if (offer == null) return "no one listed offer matches the key";
            Company by = op.Value == 1 ? GameAgencies.Find(op.Taker) : null;
            if (op.Value == 1 && by == null) return "the locking rival does not resolve";
            offer.IsBeingEvaluatedByCompanyAI = by;
            return null;
        }

        // A game a fraction short of what the host paid (its timing within a day differs) is topped up, within the
        // area's value tolerance, and logged; anything more is out of step. Money is area 0, stock area 4.
        static string TopUpMoney(Company c, double need, ObjectInfo body)
        {
            double have = c.MoneyController.CurrentMoney, clamped;
            if (!WorldTolerance.Shortfall(0, have, need, out clamped))
                return GameAgencies.Wire(c) + " is short of money: has " + have + ", needs " + need;
            if (clamped > 0)
            {
                c.MoneyController.AddMoney(clamped);
                Log.Info("shortfall: " + GameAgencies.Wire(c) + " money " + (body != null ? body.id : 0) + " clamped "
                    + clamped);
            }
            return null;
        }

        static string TopUpStock(Company c, ObjectInfo body, ResourceDefinition rd, double need)
        {
            ObjectInfoData site = GameAgencies.Site(body, c);
            if (site == null) return GameAgencies.Wire(c) + " has no site at the body";
            double have = site.CheckResources(rd), clamped;
            if (!WorldTolerance.Shortfall(4, have, need, out clamped))
                return GameAgencies.Wire(c) + " is short of " + rd.ID + ": has " + have + ", needs " + need;
            if (clamped > 0)
            {
                site.AddResources(rd, clamped);
                Log.Info("shortfall: " + GameAgencies.Wire(c) + " " + rd.ID + " " + body.id + " clamped " + clamped);
            }
            return null;
        }

        // ---- the host builds the ops of a request ----

        public static long NowTicks { get { return TimeController.Instance.CurrentTime.Ticks; } }

        // The Add the host makes of a request's Add: the next id and the host's own start time.
        public static MarketOp MadeAdd(MarketOp request)
        {
            OfferKey k = request.Offer;
            return new MarketOp { Op = MarketOps.Add, Count = request.Count, Price = request.Price,
                Offer = new OfferKey { Id = WorldCounters.Offers + 1, Agency = k.Agency, Resource = k.Resource, Buy = k.Buy,
                    Where = k.Where, StartTicks = NowTicks } };
        }

        // The Cancel the host makes: the exact key of the listed offer, what it refunds and whether it was done.
        public static MarketOp MadeCancel(Offer o)
        {
            return new MarketOp { Op = MarketOps.Cancel, Offer = KeyOf(o), Count = o.CountToBuySell, Price = o.PricePerUnit,
                Value = o.OfferDone ? 1 : 0 };
        }

        public static MarketOp MadeFill(Offer o, string taker, double count)
        {
            return new MarketOp { Op = MarketOps.Fill, Offer = KeyOf(o), Taker = taker, Count = count,
                Price = o.PricePerUnit };
        }
    }
}
