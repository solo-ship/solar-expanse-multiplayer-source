using System.Collections.Generic;
using Offer = Game.ObjectInfoDataScripts.Offer;

namespace SolarExpanseMultiplayer
{
    // M7 Task 8 (DD-W3): the rules' names for market ops as the game holds them, and their apply on a game that did not
    // run them (the maker's 90, a rival's 94, the players' market entries). The one implementation is WorldMarket's
    // (Task 11): the same keys, the same offer search and the same game calls in the same order on every game, with
    // Instant done there too. Main thread.
    public static class MarketApply
    {
        // MarketOfferManager.countOffer, the OfferCount counter (game-wide).
        public static long OfferCount() { return WorldCounters.Offers; }

        public static OfferKey KeyOf(Offer o) { return WorldMarket.KeyOf(o); }

        // The offers the game lists, as MarketRules judges them.
        public static List<LiveOffer> Live() { return WorldMarket.Live(); }

        // The one listed offer the key names, or null (none, or several).
        public static Offer Find(OfferKey key) { return WorldMarket.Find(key); }

        // Applies one op for a game that was not the one that ran it. null = applied; else why this game is out of step.
        public static string Apply(MarketOp op) { return WorldMarket.Apply(op); }
    }
}
