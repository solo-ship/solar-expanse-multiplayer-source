using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Task 2's pairs table (revision 14, ruling 15): measured transfer times and leg delta-v per body pair, the seed
    // for FlightSanity's bounds. Keys are body names as the game shows them, compared without regard to case. Task 9
    // extends the table as F3 measures more pairs; a pair that is not here uses the defaults.
    public static class SanityPairs
    {
        public const long DefaultMinTicks = 1;   // unmeasured pairs: only arrive after leaving (short hops take hours)
        public const long DefaultMaxTicks = 20L * 365L * TimeSpan.TicksPerDay;   // 20 years
        // the largest leg seen anywhere was 19.2
        public const double DefaultMaxDeltaV = 40.0;

        sealed class Bounds3 { public long Min, Max; public double Dv; }

        static readonly Dictionary<string, Bounds3> Table = Build();

        // The bounds for a body pair: min = a quarter of the fastest transfer seen, max = three times the slowest,
        // delta-v ceiling = twice the largest leg seen; unmeasured pairs: any positive time, 20 years, 40 km/s.
        public static void Bounds(string startKey, string targetKey, out long minTicks, out long maxTicks,
                                  out double maxDeltaV)
        {
            Bounds3 b;
            if (startKey != null && targetKey != null && Table.TryGetValue(Key(startKey, targetKey), out b))
            {
                minTicks = b.Min; maxTicks = b.Max; maxDeltaV = b.Dv;
                return;
            }
            minTicks = DefaultMinTicks; maxTicks = DefaultMaxTicks; maxDeltaV = DefaultMaxDeltaV;
        }

        static string Key(string start, string target) { return start + ">" + target; }

        static Dictionary<string, Bounds3> Build()
        {
            var t = new Dictionary<string, Bounds3>(StringComparer.OrdinalIgnoreCase);
            // fastest and slowest transfer seen in days, largest leg delta-v seen (0: none measured, so the default)
            Add(t, "EARTH", "LUNA", 7, 7, 1.38);
            Add(t, "EARTH", "MARS", 201, 335, 19.17);       // the fast transfer's 19.2, the efficient one's 3.5
            Add(t, "EARTH", "VENUS", 163, 163, 3.43);
            Add(t, "EARTH", "MERCURY", 144, 144, 15.28);
            Add(t, "EARTH", "CERES", 227, 227, 19.04);
            Add(t, "EARTH", "BENNU", 137, 137, 15.0);
            Add(t, "EARTH", "ITOKAWA", 397, 397, 3.0);
            Add(t, "EARTH", "JUPITER", 1351, 1351, 9.21);
            Add(t, "EARTH", "TITAN", 3468, 3468, 0);
            Add(t, "EARTH", "NEPTUNE", 3825, 3825, 0);
            return t;
        }

        static void Add(Dictionary<string, Bounds3> t, string start, string target, int fastestDays, int slowestDays,
                        double largestLeg)
        {
            t[Key(start, target)] = new Bounds3
            {
                Min = fastestDays * TimeSpan.TicksPerDay / 4,
                Max = slowestDays * 3L * TimeSpan.TicksPerDay,
                Dv = largestLeg > 0 ? largestLeg * 2 : DefaultMaxDeltaV
            };
        }
    }
}
