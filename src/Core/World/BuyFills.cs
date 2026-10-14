using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public struct BuyNeed
    {
        public readonly string Resource;
        public readonly double Amount;
        public BuyNeed(string resource, double amount) { Resource = resource; Amount = amount; }
    }

    public struct BuyOffer
    {
        public readonly string Resource;
        public readonly double CountLeft;
        public BuyOffer(string resource, double countLeft) { Resource = resource; CountLeft = countLeft; }
    }

    // Buy what is missing (B6, P5): how much each offer the market walk listed is filled with. The walk
    // (MarketOfferManager.HaveOffertToBuy) goes through the need in order and, per resource, the offers in its list
    // order: an offer that cannot cover the rest is taken whole (its CountLeft, which the walk prices and takes off
    // the need), the first that can covers the rest. The game's own buy fills each offer with HelpHowMuchBuy, which
    // the walk sets only for that last offer; an offer taken whole keeps whatever an earlier walk left there (0 on a
    // fresh offer), so a stack spread over several offers was bought short and only part of it could be paid for.
    public static class BuyFills
    {
        // One count per walked offer, in the walk's order (0 for an offer the need never reached).
        public static double[] Counts(IList<BuyNeed> need, IList<BuyOffer> walked)
        {
            var fills = new double[walked.Count];
            var used = new bool[walked.Count];
            foreach (BuyNeed n in need)
            {
                double rest = n.Amount;
                if (rest == 0.0) continue;
                for (int i = 0; i < walked.Count; i++)
                {
                    if (used[i] || walked[i].Resource != n.Resource) continue;
                    used[i] = true;
                    if (rest <= walked[i].CountLeft) { fills[i] = rest; break; }
                    fills[i] = walked[i].CountLeft;
                    rest -= walked[i].CountLeft;   // the walk's own arithmetic, so the last fill is its HelpHowMuchBuy
                }
            }
            return fills;
        }
    }
}
