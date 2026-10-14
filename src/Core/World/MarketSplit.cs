using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The market maker's run as one or more entries. One entry holds at most MaxMarketOps ops; a run of a modded
    // 40-resource market is longer, so it is cut at op boundaries into parts that apply in order (Value and the date
    // are the same in every part). Ops apply one after another, so a cut anywhere is as good as none.
    public static class MarketSplit
    {
        public static List<MarketOp[]> Parts(MarketOp[] ops, int max)
        {
            if (max < 1) throw new ArgumentOutOfRangeException("max");
            var parts = new List<MarketOp[]>();
            if (ops == null) return parts;
            for (int at = 0; at < ops.Length; at += max)
            {
                var part = new MarketOp[Math.Min(max, ops.Length - at)];
                Array.Copy(ops, at, part, 0, part.Length);
                parts.Add(part);
            }
            return parts;
        }

        public static int Adds(MarketOp[] ops)
        {
            int n = 0;
            if (ops != null) foreach (MarketOp o in ops) if (o != null && o.Op == MarketOps.Add) n++;
            return n;
        }
    }
}
