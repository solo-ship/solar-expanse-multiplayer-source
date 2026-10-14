using System;

namespace SolarExpanseMultiplayer
{
    // The market windows' clicks as requests (80-84), from the numbers the windows hold. CaptureMarket reads the
    // window and calls these, so a test builds a request from a real save's offer exactly as the capture does. Null:
    // the window's numbers leave nothing to send.
    public static class MarketRequests
    {
        // 80 and 82's Add: the protocol's offers are whole, so the typed count is floored and the price rounded (at
        // least 1, as the window's own floor).
        public static MarketOp NewOffer(string agency, string resource, bool buy, int where, double count, double price)
        {
            if (resource == null || where < 1) return null;
            double whole = Math.Floor(count);
            if (!(whole >= 1.0)) return null;
            return new MarketOp { Op = MarketOps.Add, Count = whole,
                Offer = new OfferKey { Agency = agency, Resource = resource, Buy = buy, Where = where },
                Price = Math.Max(1.0, Math.Round(price, MidpointRounding.AwayFromZero)) };
        }

        // 80, or 82 when the window was opened on an offer (`editing`, which the Add replaces).
        public static ActionRequest Make(string agency, MarketOp add, OfferKey editing)
        {
            if (add == null) return null;
            return new ActionRequest { Kind = editing != null ? ActionKind.OfferEdit : ActionKind.OfferCreate,
                Agency = agency, Market = editing != null ? new[] { Cancel(editing), add } : new[] { add } };
        }

        // 81
        public static ActionRequest CancelOffer(string agency, OfferKey key)
        {
            if (key == null) return null;
            return new ActionRequest { Kind = ActionKind.OfferCancel, Agency = agency, Market = new[] { Cancel(key) } };
        }

        // 83: the dialog's count, the price it showed and the units it saw left (whole, as the dialog counts them).
        public static ActionRequest Take(string agency, OfferKey key, int count, double price, double countLeft)
        {
            double left = Math.Floor(countLeft);
            if (key == null || count < 1 || left < count) return null;
            var fill = new MarketOp { Op = MarketOps.Fill, Offer = key, Taker = agency, Count = count, Price = price,
                Left = left };
            return new ActionRequest { Kind = ActionKind.OfferTake, Agency = agency, Market = new[] { fill } };
        }

        // 84 from the make-offer window (source 1): the typed price is the limit.
        public static ActionRequest Instant(string agency, int body, string resource, bool buy, double amount,
            double limit)
        {
            if (body < 1 || resource == null || !(amount > 0)) return null;
            return new ActionRequest { Kind = ActionKind.MarketInstant, Agency = agency, Body = body, Item = resource,
                Amount = amount, Value = buy ? 0 : 1, ItemId = MarketShapes.SourceMakeOfferWindow,
                Market = new[] { new MarketOp { Op = MarketOps.Instant, Price = limit } } };
        }

        static MarketOp Cancel(OfferKey key) { return new MarketOp { Op = MarketOps.Cancel, Offer = key }; }
    }
}
