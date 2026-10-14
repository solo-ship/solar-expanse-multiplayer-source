using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // M7 Task 5 (DD-W9, DD-W11; drift.md section 10): the value tolerances by area index, and the checks built on them.
    public static class WorldTolerance
    {
        public const double Market = 1e-6;     // clearing prices and maxPriceDifferencePercentage (minor 12)
        public const double NotCompared = -1;  // habitability: never compared
        public const double BalanceTolerance = 1e-2;
        // A build's progress once every game starts its units at the same time (BuildStarts): each game still adds
        // its own frames' float steps, which part the sums by about 1e-6 at most (soak 3); a different build speed
        // parts them by far more within a value day.
        public const double BuildProgressTolerance = 1e-5;

        // Residual tolerance per DriftAreas index. 0 = exact; NotCompared = skipped.
        public static double For(int area)
        {
            switch (area)
            {
                case 0: return 1e-12;      // money
                case 1: return 1e-2;       // research (two days in a row)
                case 2: return 1e-9;       // facilities
                case 3: return 1e-9;       // production
                case 4: return 1e-6;       // stock Value
                case 9: return 1e-9;       // contracts
                case 10: return Market;    // market
                case 12: return 1e-7;      // deposits
                case 13: return NotCompared;
                default: return 0;         // population, exploration, fleet, missions, bodies: exact
            }
        }

        // Value days: every 30th game day, the day after a year event, the day after a heal. A last day <= 0 means none yet.
        public static bool IsValueDay(long day, long lastYearEventDay, long lastHealDay)
        {
            if (day <= 0) return false;
            if (day % ActionLimits.ValueDayEvery == 0) return true;
            if (lastYearEventDay > 0 && day == lastYearEventDay + 1) return true;
            if (lastHealDay > 0 && day == lastHealDay + 1) return true;
            return false;
        }


        // null, or "<area> <agency> <field> residual <r> over <tol>". Research is judged through research.Over (two checks in
        // a row), at research.Today.
        public static string Problem(AreaValue host, AreaValue mine, TwoDayRule research)
        {
            return Problem(host, mine, research, research != null ? research.Today : 0);
        }

        public static string Problem(AreaValue host, AreaValue mine, TwoDayRule research, long day)
        {
            if (host == null || mine == null) return "value block missing";
            string who = Name(host.Area) + " " + host.Agency + " " + FieldName(host.Field);
            if (host.Area != mine.Area || host.Field != mine.Field || !string.Equals(host.Agency, mine.Agency, StringComparison.Ordinal))
                return who + " not the same block on both sides";
            double tol = For(host.Area);
            if (tol == NotCompared) return null;
            if (host.Area == 4 && host.Field == DriftFields.Balance) tol = BalanceTolerance;   // a per-day rate: loose
            if ((host.Area == 2 || host.Area == 3) && host.Field == DriftFields.BuildProgress)
                tol = BuildProgressTolerance;
            double r = Residual(host.Sum, mine.Sum);
            string key = host.Area + "|" + host.Agency + "|" + host.Field;
            bool over = double.IsNaN(r) || r > tol;
            if (host.Area == 1 && research != null)
            {
                if (!over) { research.Under(key); return null; }
                if (!research.Over(key, day)) return null;
            }
            else if (!over) return null;
            return who + " residual " + r.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " over " + tol.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string Name(int area) { return area >= 0 && area < DriftAreas.All.Length ? DriftAreas.All[area] : "area" + area; }

        static string FieldName(int field)
        {
            return field >= 0 && field < DriftFields.Names.Length ? DriftFields.Names[field] : "field" + field;
        }

        // Progress sums (I2): research, production, a facility's build progress and an observatory's working days left.
        // A client applies the host's observatory step later in the same day than the host ran it, so two games' sums
        // can part for a day; they are logged and never put a client out of step.
        public static bool ProgressOnly(int area, int field)
        {
            return area == 1 || area == 3
                || (area == 2 && (field == DriftFields.BuildProgress || field == DriftFields.DaysToFinish));
        }

        static double Residual(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) return double.NaN;
            return Math.Abs(a - b) / Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        }

        // True when need - have is within the area's value tolerance of need; clamped is then the shortfall to top up (0 when
        // have covers need). False (clamped 0): out of step.
        public static bool Shortfall(int area, double have, double need, out double clamped)
        {
            clamped = 0;
            if (double.IsNaN(have) || double.IsNaN(need) || double.IsInfinity(need)) return false;
            // a rival's purse is +Infinity (CompanyBehaviour.EnableAI), on the host and, through a heal, here
            if (double.IsPositiveInfinity(have)) return true;
            if (double.IsInfinity(have)) return false;
            double gap = need - have;
            if (gap <= 0) return true;
            double tol = For(area);
            if (tol <= 0) return false;
            if (gap > tol * Math.Max(1.0, Math.Abs(need))) return false;
            clamped = gap;
            return true;
        }
    }

    // Research needs two value checks in a row over the tolerance (a single-day spike at a completion is ignored).
    public sealed class TwoDayRule
    {
        sealed class Run { public long First, Last; }
        readonly Dictionary<string, Run> runs = new Dictionary<string, Run>();

        public long Today;   // the day WorldTolerance.Problem's three-argument form judges at

        // Records that the key is over on this day; true when it was already over at an earlier check with no check under between.
        public bool Over(string key, long day)
        {
            Run r;
            if (!runs.TryGetValue(key, out r)) { runs[key] = new Run { First = day, Last = day }; return false; }
            if (day < r.First) r.First = day;
            if (day > r.Last) r.Last = day;
            return r.Last > r.First;
        }

        // The key was within the tolerance at this check: its run is broken.
        public void Under(string key) { runs.Remove(key); }

        public void Clear() { runs.Clear(); }
    }
}
