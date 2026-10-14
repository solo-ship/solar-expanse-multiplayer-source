using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One entry of `offerts` as this game holds it.
    public struct LiveOffer { public OfferKey Key; public bool Done; }

    // M7 Task 4 (DD-W3): how a list of market ops applies to a game's offer list. Pure.
    public static class MarketRules
    {
        public const int CancelListed = 1, CancelRefundOnly = 2;

        static bool Same(OfferKey a, OfferKey b)
        {
            return a != null && b != null && a.Id == b.Id && a.Buy == b.Buy && a.Where == b.Where && a.StartTicks == b.StartTicks
                && string.Equals(a.Agency, b.Agency, StringComparison.Ordinal) && string.Equals(a.Resource, b.Resource, StringComparison.Ordinal);
        }

        static int Matches(IList<LiveOffer> listed, OfferKey key, out int first)
        {
            first = -1; int n = 0;
            if (listed == null || key == null) return 0;
            for (int i = 0; i < listed.Count; i++)
                if (Same(listed[i].Key, key)) { if (n == 0) first = i; n++; }
            return n;
        }

        // The list index of the one listed offer (done or not) the key names, or -1 when none or several match.
        public static int Find(IList<LiveOffer> listed, OfferKey key)
        {
            int first; return Matches(listed, key, out first) == 1 ? first : -1;
        }

        // 0 with a problem when out of step; else CancelListed (index set) or CancelRefundOnly.
        public static int CancelPath(IList<LiveOffer> listed, MarketOp cancel, out int index, out string problem)
        {
            index = -1; problem = null;
            if (cancel == null || cancel.Op != MarketOps.Cancel || cancel.Offer == null) { problem = "cancel: not a cancel with an offer key"; return 0; }
            int first; int n = Matches(listed, cancel.Offer, out first);
            if (n > 1) { problem = "cancel: " + n + " listed offers match the key"; return 0; }
            if (cancel.Value == 0)
            {
                if (n == 1 && !listed[first].Done) { index = first; return CancelListed; }
                problem = n == 0 ? "cancel: no listed offer matches the key" : "cancel: the offer is done here but was not done on the host";
                return 0;
            }
            if (cancel.Value == 1)
            {
                if (n == 0) return CancelRefundOnly;
                if (listed[first].Done) { index = first; return CancelListed; }
                problem = "cancel: the offer is not done here but was done on the host";
                return 0;
            }
            problem = "cancel: bad outcome value " + cancel.Value;
            return 0;
        }

        // null, or why a host op list cannot be applied in order to this list. An Add must take the next id
        // (offerCountBefore + 1, then +1 per Add), a Fill needs exactly one listed not-done offer (and finishes it),
        // a Cancel follows CancelPath.
        public static string ApplyProblem(IList<LiveOffer> listed, MarketOp[] ops, long offerCountBefore)
        {
            if (ops == null) return "ops: none";
            var work = new List<LiveOffer>();
            if (listed != null) for (int i = 0; i < listed.Count; i++) work.Add(listed[i]);
            long nextId = offerCountBefore + 1;
            for (int k = 0; k < ops.Length; k++)
            {
                MarketOp op = ops[k];
                string at = "op " + k + ": ";
                if (op == null) return at + "null";
                int first, index; string problem;
                switch (op.Op)
                {
                    case MarketOps.Add:
                        if (op.Offer == null) return at + "add without a key";
                        if (op.Offer.Id != nextId) return at + "add takes id " + op.Offer.Id + " but the next is " + nextId;
                        if (Matches(work, op.Offer, out first) > 0) return at + "add duplicates a listed offer";
                        work.Add(new LiveOffer { Key = op.Offer, Done = false });
                        nextId++;
                        break;
                    case MarketOps.Cancel:
                        if (CancelPath(work, op, out index, out problem) == 0) return at + problem;
                        if (index >= 0) work.RemoveAt(index);
                        break;
                    case MarketOps.Fill:
                        if (op.Offer == null) return at + "fill without a key";
                        int n = Matches(work, op.Offer, out first);
                        if (n != 1) return at + "fill: " + n + " listed offers match the key";
                        if (work[first].Done) return at + "fill: the offer is already done";
                        work[first] = new LiveOffer { Key = work[first].Key, Done = true };
                        break;
                    case MarketOps.Lock:
                        if (op.Offer == null) return at + "lock without a key";
                        if (Matches(work, op.Offer, out first) != 1) return at + "lock: not exactly one listed offer matches";
                        break;
                    case MarketOps.Instant:
                    case MarketOps.PriceDiff:
                        break;
                    default:
                        return at + "unknown op " + op.Op;
                }
            }
            return null;
        }

        // Within 1e-9 relative (to the larger magnitude, at least 1); else PriceChanged.
        public static bool SamePrice(double seen, double now)
        {
            if (double.IsNaN(seen) || double.IsNaN(now) || double.IsInfinity(seen) || double.IsInfinity(now)) return false;
            return Math.Abs(seen - now) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(seen), Math.Abs(now)));
        }
    }
}
