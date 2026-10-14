using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // ---- OFFLINE ONLY: the comparer of two drift records (SempDriftCompare.exe), its report and its tables ----
    // Pure: no UnityEngine, no Harmony, no file IO. Allocation is fine here.

    // Per (area, agency): where the two games' hashes parted and whether that was timing or real drift. One AreaResult per block.
    public sealed class AreaResult
    {
        public string Area, Agency;
        public int FirstDiscrete = -1, FirstRounded = -1, FirstExact = -1; public long FirstDiscreteKey;
        public int DaysCompared, DaysDiscreteDiffer, DaysRoundedDiffer, DaysExactDiffer, LastDiscreteDiffer = -1;
        public long MaxCountDiff;       // a long: two int counts can be 2^32 apart
        public int TransientEpisodes;   // discrete episodes back in sync within TransientDays
        public bool Persistent;         // a discrete episode longer than TransientDays, or still differing at the end
        public bool BackInSync, Capped;
        public bool Recurring;          // [add] more than 3 transient episodes, or Discrete differing on over 5 % of the compared days (not timing)
        public int MissingDays;         // [add] common days on which the block was missing on one side (not compared)
        public List<string> OneSidedFields = new List<string>();   // [add] field magnitudes and scalars present in one record only (structural)
        public List<ScalarResult> Fields = new List<ScalarResult>();   // one per field (the day records' field sums; `M` in the text form), Name = the field name
    }

    // The phase-corrected comparison of one series (a field magnitude of a block, or a named scalar). Every figure is RELATIVE:
    // |difference| / max(|x_A|, |x_B|, 1), so a 0..1 field and a 1e9 field are on one scale.
    public sealed class Residual
    {
        public double MaxRaw, MaxResidual, SlopePer100Days;
        public int DayOfMaxRaw = -1, DayOfMaxResidual = -1, FirstOver1e6 = -1, FirstOver1e3 = -1;
        public int DaysMissing;               // [add] comparable days on which the series was on one side only (not compared)
        public int DaysOver1e6;                // [add] how many days the residual was over 1e-6 (a step shows as one, drift as many)
        public double[] At;                    // median relative residual in windows around days 30, 90, 180, 365, 400 (NaN when past the run)
        public bool Grows;                     // median over the last 30 days >= 4 x median over days 20-40, and > 1e-6
        public double TailMedian = double.NaN; // [add] median relative residual over the last 30 days (the whole run when it is under 70 days)
        public bool Infinite;                  // [add] one side finite and the other not on some day (the scalar is listed as "infinite")
    }

    public sealed class ScalarResult { public string Agency, Name; public Residual R; }

    // What the run was launched with (--expect <flags>): "ai-off,no-autosave,seed=12345"; "" expects none of them.
    public sealed class DriftExpect
    {
        public bool AiOff, NoAutosave; public int Seed = -1;
        // --until-day: only days below it are compared (0 = all); a joiner's game that reloaded ends its record
        public int LastDay;

        public static bool TryParse(string flags, out DriftExpect e, out string error)
        {
            e = null; error = null;
            if (flags == null) { error = "no flags given (use an empty string to expect none)"; return false; }
            var x = new DriftExpect();
            bool seenAi = false, seenNa = false, seenSeed = false;
            foreach (string raw in flags.Split(','))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (t == "ai-off") { if (seenAi) { error = "'ai-off' is repeated"; return false; } seenAi = true; x.AiOff = true; }
                else if (t == "no-autosave") { if (seenNa) { error = "'no-autosave' is repeated"; return false; } seenNa = true; x.NoAutosave = true; }
                else if (t.StartsWith("seed=", StringComparison.Ordinal))
                {
                    if (seenSeed) { error = "'seed' is repeated"; return false; }
                    seenSeed = true;
                    int v;
                    if (!int.TryParse(t.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out v) || v > 999999) { error = "'" + t + "': the seed is a number from 0 to 999999"; return false; }
                    x.Seed = v;
                }
                else { error = "unknown expectation '" + t + "' (ai-off, no-autosave, seed=<0..999999>)"; return false; }
            }
            e = x;
            return true;
        }

        // What a header's own flags say (used where no --expect is given and the headers are taken as they are).
        public static DriftExpect FromHeaderFlags(string headerFlags)
        {
            var f = DriftAnalysis.ParseFlags(headerFlags);
            var e = new DriftExpect { AiOff = f.ContainsKey("ai-off"), NoAutosave = f.ContainsKey("no-autosave") };
            string s; int v;
            if (f.TryGetValue("seed", out s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v)) e.Seed = v;
            return e;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (AiOff) parts.Add("ai-off");
            if (NoAutosave) parts.Add("no-autosave");
            if (Seed >= 0) parts.Add("seed=" + Seed.ToString(CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }
    }

    public sealed class PhaseResult
    {
        public double GapMaxS, GapMeanS, GapP95S, ElapsedMaxA, ElapsedMaxB, RateA, RateB, CostMeanMsA, CostMaxMsA, CostMeanMsB, CostMaxMsB;
        public double GapSignedMeanS;          // [add] mean of (A - B), game seconds: the follow bias (carry-into-M3)
        public int FrameMaxMsA, FrameMaxMsB;
    }

    public sealed class DriftReport
    {
        public DriftHeader A, B; public string Label; public int CommonDays, OnlyA, OnlyB; public PhaseResult Phase;
        public int FirstCommonDay = -1, LastCommonDay = -1;     // [add] the span of the common days
        public List<AreaResult> Areas = new List<AreaResult>(); public List<ScalarResult> Scalars = new List<ScalarResult>();
        public List<string> Warnings = new List<string>(), Failures = new List<string>();
        public List<CostCheck> Checks = new List<CostCheck>();  // [add] every check in order, passed or not (the "check:" lines); Failures holds the failed ones
    }

    public static class DriftAnalysis
    {
        public const int TransientDays = 3;
        public const double PhaseNoiseMaxResidual = 1e-4;      // "phase noise" needs every field's residual at or under this, and a tail at or under 1e-6
        public const int RecurringEpisodes = 3;     // more transient episodes than this in one area (or Discrete differing on over 5 % of its days) is "recurring structural"
        const double TicksPerSecond = 10000000.0;
        const int WindowHalf = 10;
        static readonly int[] WindowDays = { 30, 90, 180, 365, 400 };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The event kinds' A, B and C, as the comparer reads them (the recorder's briefs define them the same way):
        //   ai, aicheck: A = trees running, B = trees paused, C = loading threads (any above 0 fails the run)
        // Every other kind is only listed. The text is "a=<A> b=<B> c=<C>[ text=<name>]" (DriftFile renders it).

        // ================= the comparison =================

        // From two read record files: Compare on their parts, then the file-level warnings (cut off, no End record, dropped).
        public static DriftReport Compare(string label, DriftExpect expect, DriftRecordFile a, DriftRecordFile b)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            DriftReport r = Compare(label, expect, a.Header, a.Days, a.Events, b.Header, b.Days, b.Events);
            FileWarnings(r, "A", a);
            FileWarnings(r, "B", b);
            return r;
        }

        static int LastDayOf(Dictionary<int, DayRecord> days)
        {
            int last = -1;
            foreach (int d in days.Keys) if (d > last) last = d;
            return last;
        }

        // The most distinct (area, agency) blocks a record can hold: its header's agencies, each with the per-agency areas, plus the
        // world areas. A record with more is crafted or broken, and a comparison of it would be slow; null when it is fine.
        public static string BlockCountProblem(DriftRecordFile f)
        {
            var blocks = new HashSet<string>(StringComparer.Ordinal);
            foreach (DayRecord d in f.Days)
                foreach (AreaSum a in d.Areas) blocks.Add(BlockKey(a.Area, a.Agency));
            int agencies = f.Header.Agencies == null ? 0 : f.Header.Agencies.Length, allowed = DriftAreas.Blocks(agencies);
            return blocks.Count > allowed ? "the record holds " + blocks.Count + " distinct blocks, but its header (" + agencies + " agencies) allows " + allowed : null;
        }

        // --min-days N: a run with fewer common days than the run was meant to have fails (a side that stopped early).
        public static void RequireDays(DriftReport r, int minDays)
        {
            Check(r, "at least " + minDays + " common days", r.CommonDays >= minDays, r.CommonDays + " common day(s), " + minDays + " expected (a side stopped early or lost days)");
        }

        static void FileWarnings(DriftReport r, string side, DriftRecordFile f)
        {
            if (f.Truncated) r.Warnings.Add(side + "'s record is cut off inside a record (copied while the game was writing, or a crash): its " + f.Days.Count + " complete days were compared");
            else if (!f.Ended) r.Warnings.Add(side + "'s record has no End record (the game was still running, or it crashed)");
            foreach (string w in f.Warnings) r.Warnings.Add(side + ": " + w);      // the reader's index warnings (an index outside the header's tables)
            if (f.Dropped > 0) r.Warnings.Add(side + "'s writer dropped " + f.Dropped + " day(s), event(s) or capture(s)");
        }

        public static DriftReport Compare(string label, DriftExpect expect, DriftHeader a, List<DayRecord> da, List<DriftEventLine> ea, DriftHeader b, List<DayRecord> db, List<DriftEventLine> eb)
        {
            if (expect == null) throw new ArgumentNullException("expect");
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            var r = new DriftReport { A = a, B = b, Label = label ?? "" };
            if (da == null) da = new List<DayRecord>();
            if (db == null) db = new List<DayRecord>();
            if (ea == null) ea = new List<DriftEventLine>();
            if (eb == null) eb = new List<DriftEventLine>();

            // ---- the headers ----
            CheckSame(r, "game build", a.GameBuild, b.GameBuild);
            CheckSame(r, "mod version", a.ModVersion, b.ModVersion);
            // a joiner loads no save file: its record names the host's save as "joined from <hash>"
            CheckSaveHash(r, JoinedSave.Plain(a.SaveHash), JoinedSave.Plain(b.SaveHash));
            Dictionary<string, string> fa = ParseFlags(a.Flags), fb = ParseFlags(b.Flags);
            var mismatch = new List<string>();
            foreach (string name in DriftHeader.MustMatch)
            {
                string va, vb;
                bool ha = fa.TryGetValue(name, out va), hb = fb.TryGetValue(name, out vb);
                if (ha != hb || (ha && va != vb)) mismatch.Add(name + " (A " + (ha ? (va ?? "on") : "absent") + ", B " + (hb ? (vb ?? "on") : "absent") + ")");
            }
            Check(r, "must-match flags equal (" + string.Join(", ", DriftHeader.MustMatch) + ")", mismatch.Count == 0, string.Join("; ", mismatch.ToArray()));
            CheckExpect(r, "A", a, expect);
            CheckExpect(r, "B", b, expect);
            if (!string.Equals(a.Skipped ?? "", b.Skipped ?? "", StringComparison.Ordinal))
                r.Warnings.Add("the skipped (inactive) agencies differ: A '" + (a.Skipped ?? "") + "', B '" + (b.Skipped ?? "") + "'");

            // ---- the events ----
            var trees = new List<string>(); var ends = new List<string>();
            ReadEvents(r, "A", ea, trees, ends);
            ReadEvents(r, "B", eb, trees, ends);
            Check(r, "no AI trees at load or in the checks", trees.Count == 0, string.Join("; ", trees.ToArray()));
            Check(r, "no end of game", ends.Count == 0, string.Join("; ", ends.ToArray()));

            // ---- the days ----
            Dictionary<int, DayRecord> ia = IndexDays(da, "A", r), ib = IndexDays(db, "B", r);
            // a joiner's first capture (keyed day 0) is its load at the join, not the host's day 0: nothing to compare
            if (JoinedSave.IsJoined(a.SaveHash) || JoinedSave.IsJoined(b.SaveHash))
            {
                ia.Remove(0); ib.Remove(0);
                r.Warnings.Add("a joined record's day 0 is its load at the join, not the host's: day 0 not compared");
            }
            if (expect.LastDay > 0)
            {
                foreach (int k in new List<int>(ia.Keys)) if (k >= expect.LastDay) ia.Remove(k);
                foreach (int k in new List<int>(ib.Keys)) if (k >= expect.LastDay) ib.Remove(k);
            }
            var common = new List<int>();
            int keyMismatch = 0, firstKeyMismatch = -1;
            var order = new List<int>(ia.Keys);
            order.Sort();
            foreach (int k in order)
            {
                DayRecord other;
                if (!ib.TryGetValue(k, out other)) continue;
                if (other.KeyTicks != ia[k].KeyTicks) { keyMismatch++; if (firstKeyMismatch < 0) firstKeyMismatch = k; continue; }
                common.Add(k);
            }
            r.CommonDays = common.Count;
            r.OnlyA = ia.Count - common.Count; r.OnlyB = ib.Count - common.Count;
            if (common.Count > 0) { r.FirstCommonDay = common[0]; r.LastCommonDay = common[common.Count - 1]; }
            if (keyMismatch > 0) r.Warnings.Add(keyMismatch + " day(s) have the same number in A and B but different keys (the first is day " + firstKeyMismatch + "): not compared");
            string gapsA = MissingDays(ia.Keys), gapsB = MissingDays(ib.Keys);
            if (gapsA.Length > 0) r.Warnings.Add("A is missing day(s) " + gapsA + " (dropped days)");
            if (gapsB.Length > 0) r.Warnings.Add("B is missing day(s) " + gapsB + " (dropped days)");
            // a side that stopped early (or lost days) has days the other lacks; --min-days turns that into a failure
            if (r.OnlyA > 0) r.Warnings.Add("A has " + r.OnlyA + " day(s) that B does not (B stopped early or dropped them)");
            if (r.OnlyB > 0) r.Warnings.Add("B has " + r.OnlyB + " day(s) that A does not (A stopped early or dropped them)");
            Check(r, "at least 2 common days", common.Count >= 2, common.Count + " common day(s)");
            // fail closed on an early stop: a side whose last day is more than a day past the last common day (the client may lag one day)
            int lastA = LastDayOf(ia), lastB = LastDayOf(ib), lastCommon = r.LastCommonDay;
            var early = new List<string>();
            if ((long)lastA > (long)lastCommon + 1) early.Add("B's last day is " + lastB + " while A reaches day " + lastA);
            if ((long)lastB > (long)lastCommon + 1) early.Add("A's last day is " + lastA + " while B reaches day " + lastB);
            Check(r, "no side stopped early", early.Count == 0, string.Join("; ", early.ToArray()) + " (the last common day is " + lastCommon + ")");

            r.Phase = ComputePhase(da, db, ia, ib, common);

            // ---- areas, fields and scalars ----
            CompareAreas(r, ia, ib, common);
            CompareScalars(r, ia, ib, common);
            return r;
        }

        // Equal and known: two records that both lost their hash have not shown they came from the same save.
        static void CheckSaveHash(DriftReport r, string x, string y)
        {
            x = x ?? ""; y = y ?? "";
            bool known = x != "" && x != "unknown" && y != "" && y != "unknown";
            string detail = x == y && known ? x : "A '" + x + "', B '" + y + "'";
            Check(r, "same save hash", x == y && known, detail + (known ? "" : " (an unknown hash proves nothing)"));
        }

        static void CheckSame(DriftReport r, string what, string x, string y)
        {
            x = x ?? ""; y = y ?? "";
            Check(r, "same " + what, x == y, x == y ? x : "A '" + x + "', B '" + y + "'");
        }

        // Records the check; a failed one is also a Failure ("<what>: <detail>").
        static void Check(DriftReport r, string what, bool ok, string detail)
        {
            r.Checks.Add(new CostCheck { Name = what, Pass = ok, Detail = detail ?? "" });
            if (!ok) r.Failures.Add(string.IsNullOrEmpty(detail) ? what : what + ": " + detail);
        }

        static void CheckExpect(DriftReport r, string side, DriftHeader h, DriftExpect e)
        {
            Dictionary<string, string> f = ParseFlags(h.Flags);
            var bad = new List<string>();
            bool ai = f.ContainsKey("ai-off"), na = f.ContainsKey("no-autosave");
            if (e.AiOff && !ai) bad.Add("ai-off expected but absent");
            if (!e.AiOff && ai) bad.Add("ai-off present but not expected");
            if (e.NoAutosave && !na) bad.Add("no-autosave expected but absent");
            if (!e.NoAutosave && na) bad.Add("no-autosave present but not expected");
            string sv; int seed = -1;
            bool hasSeed = f.TryGetValue("seed", out sv);
            if (hasSeed && (sv == null || !int.TryParse(sv, NumberStyles.None, Inv, out seed))) { bad.Add("seed '" + sv + "' is not a number"); hasSeed = false; seed = -1; }
            if (e.Seed >= 0 && seed != e.Seed) bad.Add(seed < 0 ? "seed=" + e.Seed + " expected but absent" : "seed=" + seed + " but seed=" + e.Seed + " expected");
            if (e.Seed < 0 && hasSeed) bad.Add("seed=" + seed + " present but not expected");
            Check(r, side + " flags match --expect '" + e + "'", bad.Count == 0, bad.Count == 0 ? h.Flags : side + " flags '" + h.Flags + "': " + string.Join("; ", bad.ToArray()));
        }

        // "ai-off,no-autosave,seed=12345,speed=25000": a bare flag maps to null, name=value to value.
        public static Dictionary<string, string> ParseFlags(string flags)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(flags)) return d;
            foreach (string raw in flags.Split(','))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                int eq = t.IndexOf('=');
                if (eq < 0) d[t] = null; else d[t.Substring(0, eq)] = t.Substring(eq + 1);
            }
            return d;
        }

        // "a=<A> b=<B> c=<C>[ text=<name>]" (DriftFile's rendering of an event); false when it does not parse.
        public static bool TryParseEventText(string text, out int a, out int b, out int c, out string name)
        {
            a = b = c = 0; name = null;
            if (text == null || !text.StartsWith("a=", StringComparison.Ordinal)) return false;
            int ib = text.IndexOf(" b=", 2, StringComparison.Ordinal);
            if (ib < 0) return false;
            int ic = text.IndexOf(" c=", ib + 3, StringComparison.Ordinal);
            if (ic < 0) return false;
            int it = text.IndexOf(" text=", ic + 3, StringComparison.Ordinal);
            string sa = text.Substring(2, ib - 2), sb = text.Substring(ib + 3, ic - ib - 3), sc = text.Substring(ic + 3, (it < 0 ? text.Length : it) - ic - 3);
            if (it >= 0) name = text.Substring(it + 6);
            return int.TryParse(sa, NumberStyles.AllowLeadingSign, Inv, out a) && int.TryParse(sb, NumberStyles.AllowLeadingSign, Inv, out b) && int.TryParse(sc, NumberStyles.AllowLeadingSign, Inv, out c);
        }

        static void ReadEvents(DriftReport r, string side, List<DriftEventLine> events, List<string> trees, List<string> ends)
        {
            int threw = 0, firstThrew = -1;
            foreach (DriftEventLine e in events)
            {
                if (e == null) continue;
                int a, b, c; string name;
                switch (e.Kind)
                {
                    case "ai":
                    case "aicheck":
                        if (!TryParseEventText(e.Text, out a, out b, out c, out name))
                            trees.Add(side + " day " + e.Day + " " + e.Kind + ": unreadable event '" + e.Text + "'");
                        else if (a != 0 || b != 0 || c != 0)
                            trees.Add(side + " day " + e.Day + " " + e.Kind + ": trees running " + a + ", paused " + b + ", loading " + c);
                        break;
                    case "endgame":
                        ends.Add(side + " day " + e.Day + ": " + e.Text);
                        break;
                    case "daythrew":
                        threw++; if (firstThrew < 0) firstThrew = e.Day;
                        break;
                    case "warn":
                    case "cap":
                    case "dropped":
                        r.Warnings.Add(side + " logged a '" + e.Kind + "' event on day " + e.Day + ": " + e.Text);
                        break;
                }
            }
            if (threw > 0) r.Warnings.Add(side + " had " + threw + " 'daythrew' event(s) (a day handler threw; the first on day " + firstThrew + ")");
        }

        static Dictionary<int, DayRecord> IndexDays(List<DayRecord> days, string side, DriftReport r)
        {
            var d = new Dictionary<int, DayRecord>();
            int dup = 0;
            foreach (DayRecord x in days)
            {
                if (x == null) continue;
                if (d.ContainsKey(x.Day)) { dup++; continue; }
                d[x.Day] = x;
            }
            if (dup > 0) r.Warnings.Add(side + " has " + dup + " repeated day record(s); the first of each was used");
            return d;
        }

        // "5, 17-18": the day numbers missing between the lowest and the highest present (at most 10 runs are listed).
        static string MissingDays(IEnumerable<int> present)
        {
            var set = new HashSet<int>(present);
            if (set.Count == 0) return "";
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (int d in set) { if (d < lo) lo = d; if (d > hi) hi = d; }
            if ((long)hi - lo > 100000) return "(too many to list)";
            var sb = new StringBuilder();
            int runs = 0;
            // long loop variables: with an int, a day number near int.MaxValue wraps and the loop never ends
            for (long d = lo; d <= hi; d++)
            {
                if (set.Contains((int)d)) continue;
                long e = d;
                while (e + 1 <= hi && !set.Contains((int)(e + 1))) e++;
                if (runs < 10) { if (sb.Length > 0) sb.Append(", "); sb.Append(d); if (e > d) sb.Append('-').Append(e); }
                runs++;
                d = e;
            }
            if (runs > 10) sb.Append(", ... (" + (runs - 10) + " more)");
            return sb.ToString();
        }

        // ================= phase, cost and rate =================

        static PhaseResult ComputePhase(List<DayRecord> da, List<DayRecord> db, Dictionary<int, DayRecord> ia, Dictionary<int, DayRecord> ib, List<int> common)
        {
            var p = new PhaseResult();
            var gaps = new List<double>(); double signed = 0;
            foreach (int k in common)
            {
                double g = (ia[k].AtTicks - ib[k].AtTicks) / TicksPerSecond;
                gaps.Add(Math.Abs(g)); signed += g;
            }
            if (gaps.Count > 0)
            {
                double sum = 0, max = 0;
                foreach (double g in gaps) { sum += g; if (g > max) max = g; }
                p.GapMaxS = max; p.GapMeanS = sum / gaps.Count; p.GapSignedMeanS = signed / gaps.Count;
                gaps.Sort();
                int idx = (int)Math.Ceiling(0.95 * gaps.Count) - 1;
                p.GapP95S = gaps[Math.Max(0, Math.Min(gaps.Count - 1, idx))];
            }
            Side(ia, out p.ElapsedMaxA, out p.RateA, out p.CostMeanMsA, out p.CostMaxMsA, out p.FrameMaxMsA);
            Side(ib, out p.ElapsedMaxB, out p.RateB, out p.CostMeanMsB, out p.CostMaxMsB, out p.FrameMaxMsB);
            return p;
        }

        // One record's own figures over all its days: the longest elapsed time, the game seconds per real second from its first
        // day to its last (from AtTicks and RealMs), the cost mean and max in ms, and the longest frame.
        static void Side(Dictionary<int, DayRecord> days, out double elapsedMax, out double rate, out double costMean, out double costMax, out int frameMax)
        {
            elapsedMax = 0; rate = 0; costMean = 0; costMax = 0; frameMax = 0;
            if (days.Count == 0) return;
            DayRecord first = null, last = null;
            double costSum = 0;
            foreach (DayRecord d in days.Values)
            {
                if (first == null || d.Day < first.Day) first = d;
                if (last == null || d.Day > last.Day) last = d;
                if (d.ElapsedDays > elapsedMax) elapsedMax = d.ElapsedDays;
                costSum += d.CostMicros; if (d.CostMicros / 1000.0 > costMax) costMax = d.CostMicros / 1000.0;
                if (d.FrameMaxMs > frameMax) frameMax = d.FrameMaxMs;
            }
            costMean = costSum / days.Count / 1000.0;
            double game = (last.AtTicks - first.AtTicks) / TicksPerSecond, real = (last.RealMs - first.RealMs) / 1000.0;
            if (real > 0 && game > 0) rate = game / real;
        }

        // ================= areas =================

        sealed class Pt { public long At; public double V; }
        sealed class Series { public readonly Dictionary<int, Pt> P = new Dictionary<int, Pt>(); }

        static string BlockKey(string area, string agency) { return area + "\u0001" + agency; }

        // Day -> block key -> sum, for one side.
        static Dictionary<int, Dictionary<string, AreaSum>> IndexBlocks(Dictionary<int, DayRecord> days)
        {
            var all = new Dictionary<int, Dictionary<string, AreaSum>>();
            foreach (KeyValuePair<int, DayRecord> kv in days)
            {
                var m = new Dictionary<string, AreaSum>(StringComparer.Ordinal);
                foreach (AreaSum a in kv.Value.Areas) { string key = BlockKey(a.Area, a.Agency); if (!m.ContainsKey(key)) m[key] = a; }
                all[kv.Key] = m;
            }
            return all;
        }

        static void Add(Dictionary<string, Series> map, string key, int day, long at, double v)
        {
            Series s;
            if (!map.TryGetValue(key, out s)) { s = new Series(); map[key] = s; }
            if (!s.P.ContainsKey(day)) s.P[day] = new Pt { At = at, V = v };
        }

        static Dictionary<string, Series> FieldSeries(Dictionary<int, DayRecord> days)
        {
            var map = new Dictionary<string, Series>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, DayRecord> kv in days)
                foreach (AreaSum a in kv.Value.Areas)
                {
                    if (a.Capped) continue;           // not comparable
                    foreach (FieldMagnitude m in a.Magnitudes) Add(map, BlockKey(a.Area, a.Agency) + "\u0001" + m.Name, kv.Key, kv.Value.AtTicks, m.Sum);
                }
            return map;
        }

        static Dictionary<string, Series> ScalarSeries(Dictionary<int, DayRecord> days)
        {
            var map = new Dictionary<string, Series>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, DayRecord> kv in days)
                foreach (DriftScalar s in kv.Value.Scalars) Add(map, s.Agency + "\u0001" + s.Name, kv.Key, kv.Value.AtTicks, s.Value);
            return map;
        }

        static void CompareAreas(DriftReport r, Dictionary<int, DayRecord> ia, Dictionary<int, DayRecord> ib, List<int> common)
        {
            Dictionary<int, Dictionary<string, AreaSum>> ba = IndexBlocks(ia), bb = IndexBlocks(ib);
            Dictionary<string, Series> fa = FieldSeries(ia), fb = FieldSeries(ib);
            Func<string, int> agencyRank = AgencyRank(r.A, r.B);

            // every block key seen on either side, in a fixed order: area table order, then the agency list
            var keys = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            foreach (var side in new[] { ba, bb })
                foreach (Dictionary<string, AreaSum> m in side.Values)
                    foreach (AreaSum a in m.Values) { string k = BlockKey(a.Area, a.Agency); if (!keys.ContainsKey(k)) keys[k] = new KeyValuePair<string, string>(a.Area, a.Agency); }
            var ordered = new List<KeyValuePair<string, string>>(keys.Values);
            ordered.Sort((x, y) =>
            {
                int c = AreaRank(x.Key).CompareTo(AreaRank(y.Key));
                if (c != 0) return c;
                c = string.CompareOrdinal(x.Key, y.Key);
                if (c != 0) return c;
                c = agencyRank(x.Value).CompareTo(agencyRank(y.Value));
                return c != 0 ? c : string.CompareOrdinal(x.Value, y.Value);
            });

            // the field names of every block, found once (a lookup per block, not a walk over every series per block)
            Dictionary<string, List<string>> fieldsByBlock = FieldsByBlock(fa, fb);
            var cappedAreas = new List<string>(); var missing = new List<string>(); var day0 = new List<string>(); var oneSided = new List<string>(); var negative = new List<string>(); var fieldGaps = new List<string>();
            foreach (KeyValuePair<string, string> blk in ordered)
            {
                string key = BlockKey(blk.Key, blk.Value), label = blk.Key + " " + blk.Value;
                var ar = new AreaResult { Area = blk.Key, Agency = blk.Value };
                var comparable = new List<int>();
                bool open = false; int openStart = 0; int missingDays = 0, firstMissing = -1, episodes = 0, negA = -1, negB = -1, negAValue = 0, negBValue = 0;
                foreach (int k in common)
                {
                    AreaSum sa, sb;
                    if (!ba[k].TryGetValue(key, out sa) || !bb[k].TryGetValue(key, out sb)) { missingDays++; if (firstMissing < 0) firstMissing = k; continue; }
                    if (sa.Capped || sb.Capped) { ar.Capped = true; continue; }
                    comparable.Add(k);
                    ar.DaysCompared++;
                    long cd = Math.Abs((long)sa.Count - sb.Count);
                    if (cd > ar.MaxCountDiff) ar.MaxCountDiff = cd;
                    if (sa.Count < 0 && negA < 0) { negA = k; negAValue = sa.Count; }
                    if (sb.Count < 0 && negB < 0) { negB = k; negBValue = sb.Count; }
                    bool dDiff = sa.Discrete != sb.Discrete;
                    if (sa.Rounded != sb.Rounded) { ar.DaysRoundedDiffer++; if (ar.FirstRounded < 0) ar.FirstRounded = k; }
                    if (sa.Exact != sb.Exact) { ar.DaysExactDiffer++; if (ar.FirstExact < 0) ar.FirstExact = k; }
                    if (dDiff)
                    {
                        ar.DaysDiscreteDiffer++; ar.LastDiscreteDiffer = k;
                        if (ar.FirstDiscrete < 0) { ar.FirstDiscrete = k; ar.FirstDiscreteKey = ia[k].KeyTicks; }
                        if (k == 0) day0.Add(label);
                        if (!open) { open = true; openStart = k; episodes++; }
                    }
                    else if (open)
                    {
                        open = false;
                        if ((long)k - openStart <= TransientDays) ar.TransientEpisodes++; else ar.Persistent = true;
                    }
                }
                if (open) ar.Persistent = true;
                ar.BackInSync = ar.DaysCompared > 0 && !open;
                ar.MissingDays = missingDays;
                if (negA >= 0) negative.Add(label + ": A has a negative item count (" + negAValue + ") on day " + negA);
                if (negB >= 0) negative.Add(label + ": B has a negative item count (" + negBValue + ") on day " + negB);
                // more than 3 transient episodes, or (with at least 2 episodes) Discrete differing on more than 5 % of the compared days, is not timing
                ar.Recurring = ar.TransientEpisodes > RecurringEpisodes || (episodes >= 2 && ar.DaysDiscreteDiffer * 20L > ar.DaysCompared);
                if (ar.Capped) cappedAreas.Add(label);
                if (missingDays > 0) missing.Add(label + " (" + missingDays + " day(s), the first day " + firstMissing + ")");

                // the field magnitudes of this block, in the field table's order
                List<string> names;
                if (!fieldsByBlock.TryGetValue(key, out names)) names = new List<string>();
                names.Sort((x, y) => { int c = FieldRank(x).CompareTo(FieldRank(y)); return c != 0 ? c : string.CompareOrdinal(x, y); });
                foreach (string n in names)
                {
                    Series sa, sb;
                    bool inA = fa.TryGetValue(key + "\u0001" + n, out sa), inB = fb.TryGetValue(key + "\u0001" + n, out sb);
                    if (!inA || !inB)
                    {
                        // a field series that exists in one record only is a structural difference (unless the block was never compared)
                        if (ar.DaysCompared > 0) { ar.OneSidedFields.Add(n); oneSided.Add("field series present in " + (inA ? "A" : "B") + " only: " + label + " " + n); }
                        continue;
                    }
                    Residual res = Analyse(sa, sb, comparable);
                    if (res.DaysMissing > 0) fieldGaps.Add("field " + label + " " + n + " is on one side only on " + res.DaysMissing + " day(s) that were otherwise compared; it was judged on the days both have");
                    ar.Fields.Add(new ScalarResult { Agency = blk.Value, Name = n, R = res });
                }
                r.Areas.Add(ar);
            }
            if (day0.Count > 0) r.Warnings.Add("day 0 differs in Discrete (the load itself diverged; structural first days are relative to it): " + string.Join(", ", day0.ToArray()));
            if (cappedAreas.Count > 0) r.Warnings.Add("capped (over its item cap on some day, so not comparable): " + string.Join(", ", cappedAreas.ToArray()));
            if (missing.Count > 0) r.Warnings.Add("block missing on one side on some common day: " + string.Join(", ", missing.ToArray()));
            foreach (string w in oneSided) r.Warnings.Add(w);
            foreach (string w in negative) r.Warnings.Add("negative item count (a count is never negative; the file is damaged or the recorder is wrong): " + w);
            foreach (string w in fieldGaps) r.Warnings.Add(w);
        }

        // block key -> its field names (in either record), found with one pass over both series maps. A series key is the block key,
        // a separator and the field name, so the field name starts after the LAST separator.
        static Dictionary<string, List<string>> FieldsByBlock(Dictionary<string, Series> fa, Dictionary<string, Series> fb)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var side in new[] { fa, fb })
                foreach (string sk in side.Keys)
                {
                    int cut = sk.LastIndexOf('\u0001');
                    if (cut < 0 || !seen.Add(sk)) continue;
                    string block = sk.Substring(0, cut), name = sk.Substring(cut + 1);
                    List<string> l;
                    if (!map.TryGetValue(block, out l)) { l = new List<string>(); map[block] = l; }
                    l.Add(name);
                }
            return map;
        }

        static int AreaRank(string area) { int i = DriftAreas.IndexOf(area); return i < 0 ? 1000 : i; }

        static int FieldRank(string name)
        {
            for (int i = 0; i < DriftFields.Names.Length; i++) if (string.Equals(DriftFields.Names[i], name, StringComparison.Ordinal)) return i;
            return 1000;
        }

        static int ScalarRank(string name)
        {
            for (int i = 0; i < DriftFields.ScalarNames.Length; i++) if (string.Equals(DriftFields.ScalarNames[i], name, StringComparison.Ordinal)) return i;
            return 1000;
        }

        // The agency's place in the header's list; the world ("-") last.
        static Func<string, int> AgencyRank(DriftHeader a, DriftHeader b)
        {
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DriftHeader h in new[] { a, b })
                if (h != null && h.Agencies != null)
                    foreach (string ag in h.Agencies) if (ag != null && !rank.ContainsKey(ag)) rank[ag] = rank.Count;
            return ag => { int i; return ag == DriftAreas.World ? 1000000 : (rank.TryGetValue(ag ?? "", out i) ? i : 500000); };
        }

        static void CompareScalars(DriftReport r, Dictionary<int, DayRecord> ia, Dictionary<int, DayRecord> ib, List<int> common)
        {
            Dictionary<string, Series> sa = ScalarSeries(ia), sb = ScalarSeries(ib);
            Func<string, int> agencyRank = AgencyRank(r.A, r.B);
            var keys = new List<string>();
            foreach (string k in sa.Keys) if (sb.ContainsKey(k)) keys.Add(k);
            keys.Sort((x, y) =>
            {
                string[] px = x.Split('\u0001'), py = y.Split('\u0001');
                int c = ScalarRank(px[1]).CompareTo(ScalarRank(py[1]));
                if (c != 0) return c;
                c = string.CompareOrdinal(px[1], py[1]);
                if (c != 0) return c;
                c = agencyRank(px[0]).CompareTo(agencyRank(py[0]));
                return c != 0 ? c : string.CompareOrdinal(px[0], py[0]);
            });
            foreach (string k in keys)
            {
                string[] p = k.Split('\u0001');
                r.Scalars.Add(new ScalarResult { Agency = p[0], Name = p[1], R = Analyse(sa[k], sb[k], common) });
            }
            // a scalar in one record only is a structural difference of the area it belongs to
            var lone = new List<string>();
            foreach (string k in sa.Keys) if (!sb.ContainsKey(k)) lone.Add("A only\u0001" + k);
            foreach (string k in sb.Keys) if (!sa.ContainsKey(k)) lone.Add("B only\u0001" + k);
            lone.Sort(StringComparer.Ordinal);
            foreach (string entry in lone)
            {
                string[] p = entry.Split('\u0001');       // side, agency, name
                r.Warnings.Add("scalar present in " + p[0] + ": " + p[1] + " " + p[2]);
                string area = ScalarArea(p[2]);
                foreach (AreaResult ar in r.Areas)
                    if (ar.Area == area && ar.Agency == p[1]) { ar.OneSidedFields.Add("scalar " + p[2]); break; }
            }
        }

        // The area a named scalar belongs to (DriftFields.ScalarNames).
        static string ScalarArea(string scalar)
        {
            switch (scalar)
            {
                case "money": case "profit": return DriftAreas.Money;
                case "research": return DriftAreas.Research;
                case "crew": return DriftAreas.Population;
                case "stock": return DriftAreas.Stock;
                case "offers": return DriftAreas.Market;
                case "bodies": return DriftAreas.Bodies;
                case "habitability-uncomputed": return DriftAreas.Habitability;
                default: return null;
            }
        }

        // ================= one series: raw, phase-corrected residual, windows, growth =================

        static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        // Relative difference |a - b| / max(|a|, |b|, 1). Equal non-finite values (both +Inf, both -Inf, both NaN) give 0; a
        // finite value against a non-finite one, or two different non-finite ones, give +Infinity.
        static double Rel(double a, double b)
        {
            bool fa = Finite(a), fb = Finite(b);
            if (fa && fb) return Math.Abs(a - b) / Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1.0);
            if (!fa && !fb)
            {
                if (double.IsNaN(a) && double.IsNaN(b)) return 0;
                return a == b ? 0 : double.PositiveInfinity;
            }
            return double.PositiveInfinity;
        }

        // B's value at the time A was sampled (tA), by linear interpolation between B's day k and its neighbour on the side tA lies
        // on (k-1 or k+1). With no such neighbour the other one is used, which extrapolates (the first and last days). A non-finite
        // value on either end, or no neighbour at all, keeps B's own day-k value.
        static double InterpolateB(Series sb, int k, Pt bk, long tA)
        {
            if (tA == bk.At) return bk.V;
            bool later = tA > bk.At;
            Pt bj;
            if (!sb.P.TryGetValue(later ? k + 1 : k - 1, out bj) && !sb.P.TryGetValue(later ? k - 1 : k + 1, out bj)) return bk.V;
            if (bj.At == bk.At || !Finite(bk.V) || !Finite(bj.V)) return bk.V;
            double w = (double)(tA - bk.At) / (double)(bj.At - bk.At);
            return bk.V + (bj.V - bk.V) * w;
        }

        static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var s = v.ToArray();
            Array.Sort(s);
            int n = s.Length;
            if (n % 2 == 1) return s[n / 2];
            double lo = s[n / 2 - 1], hi = s[n / 2];
            return lo == hi ? lo : (lo + hi) / 2;     // equal values (also two infinities) stay as they are
        }

        static Residual Analyse(Series sa, Series sb, List<int> days)
        {
            var res = new Residual { At = new double[WindowDays.Length] };
            var days2 = new List<int>(); var rel = new List<double>();
            double maxRaw = -1, maxRes = -1;
            foreach (int k in days)
            {
                Pt a, b;
                bool hasA = sa.P.TryGetValue(k, out a), hasB = sb.P.TryGetValue(k, out b);
                if (hasA != hasB) res.DaysMissing++;                 // on one side only that day: not compared
                if (!hasA || !hasB) continue;
                double raw = Rel(a.V, b.V);
                double resid = Rel(a.V, InterpolateB(sb, k, b, a.At));
                if (raw > maxRaw) { maxRaw = raw; res.DayOfMaxRaw = k; }
                if (resid > maxRes) { maxRes = resid; res.DayOfMaxResidual = k; }
                if (double.IsPositiveInfinity(resid)) res.Infinite = true;
                if (resid > 1e-6) { res.DaysOver1e6++; if (res.FirstOver1e6 < 0) res.FirstOver1e6 = k; }
                if (resid > 1e-3 && res.FirstOver1e3 < 0) res.FirstOver1e3 = k;
                days2.Add(k); rel.Add(resid);
            }
            res.MaxRaw = Math.Max(maxRaw, 0); res.MaxResidual = Math.Max(maxRes, 0);
            for (int i = 0; i < WindowDays.Length; i++) res.At[i] = WindowMedian(days2, rel, WindowDays[i] - WindowHalf, WindowDays[i] + WindowHalf);

            // the slope of the (finite) relative residual per 100 days, by least squares
            int n = 0; double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < days2.Count; i++)
            {
                if (!Finite(rel[i])) continue;
                double x = days2[i], y = rel[i];
                n++; sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double den = n * sxx - sx * sx;
            if (n >= 2 && den > 0) res.SlopePer100Days = (n * sxy - sx * sy) / den * 100.0;

            // grows: the median over the last 30 days against the median over days 20-40. Not judged on a run too short to keep
            // the two windows apart, and never for an infinite series (that is flagged as infinite).
            if (days2.Count > 0)
            {
                // the tail for the verdict: the last 30 days, or the whole run when it is shorter than 70 days
                int lastDay = days2[days2.Count - 1];
                res.TailMedian = lastDay < 70 ? Median(rel) : WindowMedian(days2, rel, lastDay - 29, lastDay);
            }
            if (days2.Count > 0 && !res.Infinite)
            {
                int last = days2[days2.Count - 1];
                if ((long)last - 29 > 40)
                {
                    double tail = WindowMedian(days2, rel, last - 29, last), baseline = WindowMedian(days2, rel, 20, 40);
                    if (!double.IsNaN(tail) && !double.IsNaN(baseline)) res.Grows = tail > 1e-6 && tail >= 4.0 * baseline;
                }
            }
            return res;
        }

        static double WindowMedian(List<int> days, List<double> rel, int from, int to)
        {
            var w = new List<double>();
            for (int i = 0; i < days.Count; i++) if (days[i] >= from && days[i] <= to) w.Add(rel[i]);
            return Median(w);
        }

        // ================= text =================

        static string F(double v)
        {
            if (double.IsNaN(v)) return "n/a";
            if (double.IsPositiveInfinity(v)) return "inf";
            if (double.IsNegativeInfinity(v)) return "-inf";
            return v.ToString("G4", Inv);
        }

        // A phase, rate or cost figure: 6 significant digits, but plain digits (no exponent) for a big number such as a rate.
        static string G(double v)
        {
            if (double.IsNaN(v)) return "n/a";
            if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
            return Math.Abs(v) >= 1e5 && Math.Abs(v) < 1e15 ? v.ToString("F0", Inv) : v.ToString("G6", Inv);
        }
        static string D(int day) { return day < 0 ? "never" : day.ToString(Inv); }

        static string Date(long keyTicks)
        {
            if (keyTicks <= 0 || keyTicks > DateTime.MaxValue.Ticks) return "?";
            return new DateTime(keyTicks).ToString("yyyy-MM-dd", Inv);
        }

        // A string that came from a file, made safe for the line-based text: control characters (a tab, a line break) and the
        // Unicode line separators become spaces, so a name cannot start a line of its own (a forged `check:` line).
        internal static string E(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool bad = c < ' ' || c == (char)0x7f || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029;
                if (bad && sb == null) { sb = new StringBuilder(s.Length); sb.Append(s, 0, i); }
                if (sb != null) sb.Append(bad ? ' ' : c);
            }
            return sb == null ? s : sb.ToString();
        }

        static string Side(DriftHeader h)
        {
            return E(h.Role) + " " + E(h.Profile) + " " + E(h.GameBuild) + " " + E(h.SaveHash) + " " + E(h.Flags);
        }

        public static string Text(DriftReport r)
        {
            var sb = new StringBuilder();
            sb.Append("run ").Append(E(r.Label)).Append(": A ").Append(Side(r.A)).Append(" | B ").Append(Side(r.B)).Append('\n');
            foreach (CostCheck c in r.Checks) sb.Append("check: ").Append(c.Pass ? "ok" : "FAIL").Append(' ').Append(E(c.Name)).Append(c.Detail.Length > 0 && !c.Pass ? ": " + E(c.Detail) : "").Append('\n');
            sb.Append("days: common ").Append(r.CommonDays);
            if (r.CommonDays > 0) sb.Append(" (day ").Append(r.FirstCommonDay).Append("..").Append(r.LastCommonDay).Append(')');
            sb.Append(", only A ").Append(r.OnlyA).Append(", only B ").Append(r.OnlyB).Append('\n');
            PhaseResult p = r.Phase;
            sb.Append("phase: gap max/mean/p95 ").Append(G(p.GapMaxS)).Append('/').Append(G(p.GapMeanS)).Append('/').Append(G(p.GapP95S))
              .Append(" game s; elapsed max A ").Append(G(p.ElapsedMaxA)).Append(" B ").Append(G(p.ElapsedMaxB))
              .Append("; rate A ").Append(G(p.RateA)).Append(" B ").Append(G(p.RateB))
              .Append("; frame max A ").Append(p.FrameMaxMsA).Append(" B ").Append(p.FrameMaxMsB)
              .Append("; cost mean/max A ").Append(G(p.CostMeanMsA)).Append('/').Append(G(p.CostMaxMsA)).Append(" B ").Append(G(p.CostMeanMsB)).Append('/').Append(G(p.CostMaxMsB))
              .Append("; signed mean gap A-B ").Append(G(p.GapSignedMeanS)).Append(" game s").Append('\n');
            foreach (AreaResult a in r.Areas)
            {
                sb.Append("area ").Append(E(a.Area)).Append(' ').Append(E(a.Agency)).Append(": discrete first ").Append(D(a.FirstDiscrete));
                if (a.FirstDiscrete >= 0) sb.Append(" (").Append(Date(a.FirstDiscreteKey)).Append(')');
                sb.Append(", rounded first ").Append(D(a.FirstRounded)).Append(", exact first ").Append(D(a.FirstExact))
                  .Append("; differing discrete ").Append(a.DaysDiscreteDiffer).Append('/').Append(a.DaysCompared).Append(" exact ").Append(a.DaysExactDiffer).Append('/').Append(a.DaysCompared)
                  .Append("; transient episodes ").Append(a.TransientEpisodes).Append("; persistent ").Append(a.Persistent ? "yes" : "no")
                  .Append("; back in sync ").Append(a.BackInSync ? "yes" : "no").Append("; max count diff ").Append(a.MaxCountDiff).Append(a.Capped ? ", capped" : "").Append(a.Recurring ? ", recurring" : "")
                  .Append(a.MissingDays > 0 ? ", missing " + a.MissingDays + " day(s)" : "").Append('\n');
            }
            foreach (AreaResult a in r.Areas)
                foreach (ScalarResult f in a.Fields)
                {
                    sb.Append("field ").Append(E(a.Area)).Append(' ').Append(E(a.Agency)).Append(' ').Append(E(f.Name)).Append(": ");
                    AppendResidual(sb, f.R);
                    sb.Append("; slope ").Append(F(f.R.SlopePer100Days)).Append("/100d").Append(f.R.Grows ? " grows" : "").Append(f.R.Infinite ? " infinite" : "").Append(f.R.DaysMissing > 0 ? " missing " + f.R.DaysMissing + " day(s)" : "").Append('\n');
                }
            foreach (ScalarResult s in r.Scalars)
            {
                sb.Append("scalar ").Append(E(s.Agency)).Append(' ').Append(E(s.Name)).Append(": ");
                AppendResidual(sb, s.R);
                sb.Append("; slope ").Append(F(s.R.SlopePer100Days)).Append("/100d; first >1e-6 ").Append(D(s.R.FirstOver1e6)).Append(" >1e-3 ").Append(D(s.R.FirstOver1e3))
                  .Append(s.R.Grows ? " grows" : "").Append(s.R.Infinite ? " infinite" : "").Append('\n');
            }
            foreach (string w in r.Warnings) sb.Append("warning: ").Append(E(w)).Append('\n');
            return sb.ToString();
        }

        static void AppendResidual(StringBuilder sb, Residual x)
        {
            sb.Append("raw ").Append(F(x.MaxRaw)).Append(" on day ").Append(x.DayOfMaxRaw).Append("; residual ").Append(F(x.MaxResidual)).Append(" on day ").Append(x.DayOfMaxResidual).Append("; at");
            for (int i = 0; i < WindowDays.Length; i++) sb.Append(i == 0 ? " " : " ").Append(WindowDays[i]).Append(' ').Append(F(x.At[i]));
        }

        // ================= markdown =================

        static string Md(string s) { return E(s).Replace('|', '/'); }

        // The suggested verdict of one block (the report's verdict is the author's, from these numbers and the scaling table).
        public static string Verdict(AreaResult a)
        {
            if (a.DaysCompared == 0) return "unknown (not compared)";             // capped, or missing from one record, on every common day
            if (a.Persistent || a.OneSidedFields.Count > 0) return "persistent structural";
            if (a.Recurring) return "recurring structural";
            foreach (ScalarResult f in a.Fields) if (f.R.Grows || f.R.Infinite) return "continuous drift";
            // a field that was compared on no day says nothing: not "none", not "phase noise"
            foreach (ScalarResult f in a.Fields) if (double.IsNaN(f.R.TailMedian)) return "unknown (field not compared)";
            // a constant gap that does not grow is not phase noise: phase noise has a small tail (the last 30 days, or the whole short run)
            foreach (ScalarResult f in a.Fields) if (f.R.TailMedian > 1e-6) return "continuous offset (not growing)";
            // phase noise is small: a field that was ever more than 1e-4 apart, and is quiet in the tail, healed
            foreach (ScalarResult f in a.Fields) if (f.R.MaxResidual > PhaseNoiseMaxResidual) return "transient offset (healed)";
            if (a.TransientEpisodes > 0) return "transient structural";
            if (a.DaysExactDiffer > 0) return "phase noise";
            return "none";
        }

        // The worst field of the blocks: the largest residual max.
        static ScalarResult Worst(IEnumerable<AreaResult> blocks)
        {
            ScalarResult w = null;
            foreach (AreaResult a in blocks)
                foreach (ScalarResult f in a.Fields)
                    if (w == null || f.R.MaxResidual > w.R.MaxResidual) w = f;
            return w;
        }

        static string Signature(AreaResult a)
        {
            ScalarResult w = Worst(new[] { a });
            return a.FirstDiscrete + "|" + a.DaysDiscreteDiffer + "|" + a.DaysExactDiffer + "|" + a.TransientEpisodes + "|" + a.Persistent + "|" + a.BackInSync + "|" + a.Capped + "|" + a.MissingDays + "|" + Verdict(a) + "|" + (w == null ? "" : w.Name);
        }

        // The run's tables as markdown: the checks, the phase row, the per-area table (the template's section 4 columns, with the
        // verdict only suggested and the last three columns left for the author), the scalars, and the warnings.
        // The table headers the comparer writes. The template (docs/research/drift.md) carries the same headers; a test checks that.
        public const string AreaTableHeader = "| Area | Agency | Structural first day | Differing days | Missing days | Transient episodes | Persistent | Back in sync | Worst field | Residual max | Slope per 100 d | Verdict (suggested) | Likely cause | M7 healing | M4 |";
        public const string PhaseTableHeader = "| Run | Common days | Only A | Only B | Gap max (s) | Gap mean (s) | Gap p95 (s) | Signed mean gap A-B (s) | Elapsed max A | Elapsed max B | Rate A | Rate B | Frame max A (ms) | Frame max B (ms) | Cost mean/max A (ms) | Cost mean/max B (ms) |";
        public const string ScalingFieldsHeader = "| Kind | Area | Agency | Name |";      // then, per run, "<label> (<speed>) raw max | <label> (<speed>) residual max |"
        public const string ScalingAreasHeader = "| Area | Agency |";                      // then, per run, "<label> (<speed>) transient episodes |"

        public static string Markdown(DriftReport r)
        {
            var sb = new StringBuilder();
            sb.Append("### ").Append(Md(r.Label)).Append("\n\n");
            sb.Append("A: ").Append(Md(Side(r.A))).Append("  \nB: ").Append(Md(Side(r.B))).Append("\n\n");
            sb.Append("| Check | Result |\n|---|---|\n");
            foreach (CostCheck c in r.Checks) sb.Append("| ").Append(Md(c.Name)).Append(" | ").Append(c.Pass ? "ok" : "**FAIL**").Append(c.Detail.Length > 0 && !c.Pass ? ": " + Md(c.Detail) : "").Append(" |\n");
            PhaseResult p = r.Phase;
            sb.Append('\n').Append(PhaseTableHeader).Append('\n');
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            sb.Append("| ").Append(Md(r.Label)).Append(" | ").Append(r.CommonDays).Append(r.CommonDays > 0 ? " (day " + r.FirstCommonDay + ".." + r.LastCommonDay + ")" : "").Append(" | ").Append(r.OnlyA).Append(" | ").Append(r.OnlyB)
              .Append(" | ").Append(G(p.GapMaxS)).Append(" | ").Append(G(p.GapMeanS)).Append(" | ").Append(G(p.GapP95S)).Append(" | ").Append(G(p.GapSignedMeanS))
              .Append(" | ").Append(G(p.ElapsedMaxA)).Append(" | ").Append(G(p.ElapsedMaxB)).Append(" | ").Append(G(p.RateA)).Append(" | ").Append(G(p.RateB))
              .Append(" | ").Append(p.FrameMaxMsA).Append(" | ").Append(p.FrameMaxMsB)
              .Append(" | ").Append(G(p.CostMeanMsA)).Append('/').Append(G(p.CostMaxMsA)).Append(" | ").Append(G(p.CostMeanMsB)).Append('/').Append(G(p.CostMaxMsB)).Append(" |\n\n");

            sb.Append(AreaTableHeader).Append('\n');
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            int i = 0;
            while (i < r.Areas.Count)
            {
                int j = i;
                while (j < r.Areas.Count && r.Areas[j].Area == r.Areas[i].Area) j++;
                bool same = true;                         // one row for the area when every agency behaves alike
                for (int k = i + 1; k < j && same; k++) if (Signature(r.Areas[k]) != Signature(r.Areas[i])) same = false;
                if (same && j - i > 1) AreaRow(sb, r.Areas[i], "all (" + (j - i) + ")", Worst(r.Areas.GetRange(i, j - i)));
                else for (int k = i; k < j; k++) AreaRow(sb, r.Areas[k], r.Areas[k].Agency, Worst(new[] { r.Areas[k] }));
                i = j;
            }
            sb.Append("\n| Agency | Scalar | Raw max | Residual max | Slope per 100 d | First over 1e-6 | First over 1e-3 | Grows |\n|---|---|---|---|---|---|---|---|\n");
            foreach (ScalarResult s in r.Scalars)
                sb.Append("| ").Append(Md(s.Agency)).Append(" | ").Append(Md(s.Name)).Append(" | ").Append(F(s.R.MaxRaw)).Append(" | ").Append(F(s.R.MaxResidual)).Append(" | ").Append(F(s.R.SlopePer100Days))
                  .Append(" | ").Append(D(s.R.FirstOver1e6)).Append(" | ").Append(D(s.R.FirstOver1e3)).Append(" | ").Append(s.R.Grows ? "yes" : "no").Append(s.R.Infinite ? " (infinite)" : "").Append(" |\n");
            if (r.Warnings.Count > 0)
            {
                sb.Append("\nWarnings:\n\n");
                foreach (string w in r.Warnings) sb.Append("- ").Append(Md(w)).Append('\n');
            }
            return sb.ToString();
        }

        static void AreaRow(StringBuilder sb, AreaResult a, string agencyCell, ScalarResult worst)
        {
            sb.Append("| ").Append(Md(a.Area)).Append(" | ").Append(Md(agencyCell)).Append(" | ")
              .Append(a.FirstDiscrete < 0 ? "never" : a.FirstDiscrete + " (" + Date(a.FirstDiscreteKey) + ")").Append(" | ")
              .Append(a.DaysDiscreteDiffer).Append('/').Append(a.DaysCompared).Append(" | ").Append(a.MissingDays).Append(" | ").Append(a.TransientEpisodes).Append(" | ")
              .Append(a.Persistent ? "yes" : "no").Append(" | ").Append(a.BackInSync ? "yes" : "no").Append(a.Capped ? " (capped)" : "").Append(" | ")
              .Append(worst == null ? "-" : Md(worst.Name)).Append(" | ").Append(worst == null ? "-" : F(worst.R.MaxResidual)).Append(" | ").Append(worst == null ? "-" : F(worst.R.SlopePer100Days))
              .Append(" | ").Append(Verdict(a)).Append(" |  |  |  |\n");
        }

        // ================= the step-size table =================

        static int SpeedOf(DriftReport r)
        {
            string v; int s;
            if (r.A != null && ParseFlags(r.A.Flags).TryGetValue("speed", out v) && v != null && int.TryParse(v, NumberStyles.None, Inv, out s)) return s;
            return int.MaxValue;
        }

        // Rows = fields and scalars, then areas; columns = runs, by speed (the run's label and its speed flag). Per field and scalar
        // the raw and the residual maximum; per area the transient episodes (and "P" for a persistent one).
        public static string ScalingMarkdown(IList<DriftReport> runs)
        {
            var sorted = new List<DriftReport>(runs);
            var idx = new Dictionary<DriftReport, int>();
            for (int i = 0; i < sorted.Count; i++) idx[sorted[i]] = i;
            sorted.Sort((x, y) => { int c = SpeedOf(x).CompareTo(SpeedOf(y)); return c != 0 ? c : idx[x].CompareTo(idx[y]); });
            var sb = new StringBuilder();
            sb.Append(ScalingFieldsHeader);
            foreach (DriftReport r in sorted) { string h = Md(r.Label) + " (" + (SpeedOf(r) == int.MaxValue ? "?" : SpeedOf(r).ToString(Inv)) + ")"; sb.Append(' ').Append(h).Append(" raw max | ").Append(h).Append(" residual max |"); }
            sb.Append("\n|---|---|---|---|");
            foreach (DriftReport r in sorted) sb.Append("---|---|");
            sb.Append('\n');

            // fields and scalars, in the order they first appear
            var rows = new List<string[]>();     // kind, area, agency, name
            var seen = new HashSet<string>();
            foreach (DriftReport r in sorted)
            {
                foreach (AreaResult a in r.Areas)
                    foreach (ScalarResult f in a.Fields)
                        if (seen.Add("field\u0001" + a.Area + "\u0001" + a.Agency + "\u0001" + f.Name)) rows.Add(new[] { "field", a.Area, a.Agency, f.Name });
                foreach (ScalarResult s in r.Scalars)
                    if (seen.Add("scalar\u0001-\u0001" + s.Agency + "\u0001" + s.Name)) rows.Add(new[] { "scalar", "-", s.Agency, s.Name });
            }
            Func<string, int> agencyRank = AgencyRank(sorted.Count > 0 ? sorted[0].A : null, sorted.Count > 0 ? sorted[0].B : null);
            rows.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x[0], y[0]);                  // fields before scalars
                if (c != 0) return c;
                c = x[0] == "field" ? AreaRank(x[1]).CompareTo(AreaRank(y[1])) : 0;
                if (c != 0) return c;
                c = x[0] == "field" ? FieldRank(x[3]).CompareTo(FieldRank(y[3])) : ScalarRank(x[3]).CompareTo(ScalarRank(y[3]));
                if (c != 0) return c;
                c = string.CompareOrdinal(x[3], y[3]);
                if (c != 0) return c;
                c = agencyRank(x[2]).CompareTo(agencyRank(y[2]));
                return c != 0 ? c : string.CompareOrdinal(x[2], y[2]);
            });
            foreach (string[] row in rows)
            {
                sb.Append("| ").Append(row[0]).Append(" | ").Append(Md(row[1])).Append(" | ").Append(Md(row[2])).Append(" | ").Append(Md(row[3])).Append(" |");
                foreach (DriftReport r in sorted)
                {
                    Residual x = FindResidual(r, row);
                    sb.Append(' ').Append(x == null ? "-" : F(x.MaxRaw)).Append(" | ").Append(x == null ? "-" : F(x.MaxResidual)).Append(" |");
                }
                sb.Append('\n');
            }

            sb.Append('\n').Append(ScalingAreasHeader);
            foreach (DriftReport r in sorted) sb.Append(' ').Append(Md(r.Label)).Append(" (").Append(SpeedOf(r) == int.MaxValue ? "?" : SpeedOf(r).ToString(Inv)).Append(") transient episodes |");
            sb.Append("\n|---|---|");
            foreach (DriftReport r in sorted) sb.Append("---|");
            sb.Append('\n');
            var areaRows = new List<string[]>(); seen.Clear();
            foreach (DriftReport r in sorted)
                foreach (AreaResult a in r.Areas)
                    if (seen.Add(a.Area + "\u0001" + a.Agency)) areaRows.Add(new[] { a.Area, a.Agency });
            areaRows.Sort((x, y) =>
            {
                int c = AreaRank(x[0]).CompareTo(AreaRank(y[0]));
                if (c != 0) return c;
                c = string.CompareOrdinal(x[0], y[0]);
                if (c != 0) return c;
                c = agencyRank(x[1]).CompareTo(agencyRank(y[1]));
                return c != 0 ? c : string.CompareOrdinal(x[1], y[1]);
            });
            foreach (string[] row in areaRows)
            {
                sb.Append("| ").Append(Md(row[0])).Append(" | ").Append(Md(row[1])).Append(" |");
                foreach (DriftReport r in sorted)
                {
                    AreaResult hit = null;
                    foreach (AreaResult a in r.Areas) if (a.Area == row[0] && a.Agency == row[1]) { hit = a; break; }
                    sb.Append(' ').Append(hit == null ? "-" : hit.TransientEpisodes + (hit.Persistent ? " (persistent)" : "")).Append(" |");
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static Residual FindResidual(DriftReport r, string[] row)
        {
            if (row[0] == "field")
            {
                foreach (AreaResult a in r.Areas)
                    if (a.Area == row[1] && a.Agency == row[2])
                        foreach (ScalarResult f in a.Fields) if (f.Name == row[3]) return f.R;
                return null;
            }
            foreach (ScalarResult s in r.Scalars) if (s.Agency == row[2] && s.Name == row[3]) return s.R;
            return null;
        }

        // ================= two dumps =================

        const int DiffLinesPerKind = 100;

        // Compares two checkpoint dumps (call DriftFile.JoinNames on each with its own record first, so names read as names).
        // The first line is the summary; then the items only in A, only in B, differing in structure (Discrete) and differing in
        // values only (Exact), each with its key's names and the values that differ. Each kind is cut at DiffLinesPerKind lines.
        public static List<string> DumpDiff(DumpFile a, DumpFile b)
        {
            var lines = new List<string>();
            if (a == null || b == null) { lines.Add("dump diff: a dump is missing"); return lines; }
            Dictionary<string, List<DumpItem>> ma = Group(a), mb = Group(b);
            var onlyA = new List<string>(); var onlyB = new List<string>(); var structure = new List<string>(); var values = new List<string>();
            var keys = new List<string>(ma.Keys);
            foreach (string k in mb.Keys) if (!ma.ContainsKey(k)) keys.Add(k);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                List<DumpItem> la, lb;
                ma.TryGetValue(k, out la); mb.TryGetValue(k, out lb);
                int na = la == null ? 0 : la.Count, nb = lb == null ? 0 : lb.Count;
                for (int i = 0; i < Math.Max(na, nb); i++)
                {
                    if (i >= nb) { onlyA.Add(Describe(la[i])); continue; }
                    if (i >= na) { onlyB.Add(Describe(lb[i])); continue; }
                    DumpItem x = la[i], y = lb[i];
                    if (x.Discrete == y.Discrete && x.Exact == y.Exact && x.Rounded == y.Rounded) continue;
                    string text = Describe(x) + ": " + ValueDiffs(x, y);
                    if (x.Discrete != y.Discrete) structure.Add(text); else values.Add(text);
                }
            }
            lines.Add("dump diff: day " + a.Head.Day + " (A, " + a.Items.Count + " items) against day " + b.Head.Day + " (B, " + b.Items.Count + " items); only in A " + onlyA.Count + ", only in B " + onlyB.Count
                + ", differ in structure " + structure.Count + ", differ in values only " + values.Count);
            if (a.Head.Day != b.Head.Day) lines.Add("warning: the dumps are from different days (A day " + a.Head.Day + ", B day " + b.Head.Day + ")");
            if (a.Head.Truncated != 0) lines.Add("warning: dump A is truncated (the capture ran out of room or hit its byte cap)");
            if (b.Head.Truncated != 0) lines.Add("warning: dump B is truncated (the capture ran out of room or hit its byte cap)");
            AddKind(lines, "only A", onlyA); AddKind(lines, "only B", onlyB); AddKind(lines, "structure", structure); AddKind(lines, "values", values);
            return lines;
        }

        static void AddKind(List<string> lines, string kind, List<string> items)
        {
            for (int i = 0; i < items.Count && i < DiffLinesPerKind; i++) lines.Add(kind + ": " + items[i]);
            if (items.Count > DiffLinesPerKind) lines.Add(kind + ": ... and " + (items.Count - DiffLinesPerKind) + " more");
        }

        static string ItemGroupKey(DumpItem it) { return (it.Area ?? "area" + it.AreaIndex) + "\t" + (it.Agency ?? it.AgencyIndex.ToString(Inv)) + "\t" + it.Key.ToString("x16", Inv); }

        static Dictionary<string, List<DumpItem>> Group(DumpFile d)
        {
            var m = new Dictionary<string, List<DumpItem>>(StringComparer.Ordinal);
            foreach (DumpItem it in d.Items)
            {
                List<DumpItem> l;
                string k = ItemGroupKey(it);
                if (!m.TryGetValue(k, out l)) { l = new List<DumpItem>(); m[k] = l; }
                l.Add(it);
            }
            return m;
        }

        // "stock ESA 7/site-a (key 00ab...)": the block, then the key's names (its "key" values).
        static string Describe(DumpItem it)
        {
            var key = new StringBuilder();
            if (it.Values != null)
                foreach (KeyValuePair<string, string> v in it.Values)
                    if (v.Key == "key") { if (key.Length > 0) key.Append('/'); key.Append(E(v.Value)); }
            return E(it.Area ?? "area" + it.AreaIndex) + " " + E(it.Agency ?? it.AgencyIndex.ToString(Inv)) + " " + (key.Length > 0 ? key.ToString() : "(no key values)") + " (key " + it.Key.ToString("x16", Inv) + ")";
        }

        static string ValueDiffs(DumpItem x, DumpItem y)
        {
            var left = new Dictionary<string, string>(StringComparer.Ordinal); var right = new Dictionary<string, string>(StringComparer.Ordinal);
            var names = new List<string>();
            Collect(x, left, names); Collect(y, right, names);
            var parts = new List<string>();
            int more = 0;
            foreach (string n in names)
            {
                string vl, vr;
                bool hl = left.TryGetValue(n, out vl), hr = right.TryGetValue(n, out vr);
                if (hl && hr && vl == vr) continue;
                if (parts.Count < 8) parts.Add(E(Fname(n)) + " A=" + (hl ? E(vl) : "(absent)") + " B=" + (hr ? E(vr) : "(absent)")); else more++;
            }
            if (more > 0) parts.Add("... and " + more + " more");
            if (parts.Count == 0) parts.Add("the hashes differ but every rendered value is equal");
            return string.Join("; ", parts.ToArray());
        }

        // field name + occurrence number -> rendered value (key values repeat their name)
        static void Collect(DumpItem it, Dictionary<string, string> into, List<string> names)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            var known = new HashSet<string>(names, StringComparer.Ordinal);     // beside the list, so a value costs a lookup, not a walk
            if (it.Values == null) return;
            foreach (KeyValuePair<string, string> v in it.Values)
            {
                int n; count.TryGetValue(v.Key, out n); count[v.Key] = n + 1;
                string k = v.Key + "\u0001" + n;
                into[k] = v.Value;
                if (known.Add(k)) names.Add(k);
            }
        }

        static string Fname(string k) { int i = k.IndexOf('\u0001'); string n = k.Substring(0, i); return k.EndsWith("\u00010", StringComparison.Ordinal) ? n : n + "#" + k.Substring(i + 1); }
    }
}
