using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // Task 8's second cost gate ("Observer-effect criteria" in the M3 plan), offline: pure, no file IO. The numbers on both
    // sides come from the same code: DriftWorld's `drift: world day` lines in the off and the on runs alike (rate, frames,
    // collections, stalls), plus the on runs' records (the cost of every read and its GC-hit tag).

    // One parsed `drift: world day` line. Each line covers the 30 game days before it: FrameMaxMs, FramesOver100, Gen0 (the
    // collections in that window, not a running total) and Stalls are that window's; Rate includes the stall seconds and
    // RateWithoutStalls leaves them out. Speed is the game speed setting the line reports.
    public sealed class WorldDayLine { public int Day; public double Rate, RateWithoutStalls, Speed; public int FrameMaxMs, FramesOver100, Gen0, Stalls; public double StallSeconds; }

    public sealed class CostRun
    {
        public string Label; public bool RecorderOn; public List<WorldDayLine> World; public DriftRecordFile Record;   // Record is null for an off run
        public DateTime Launch = DateTime.MinValue;     // [add] the mod log's `starting via` timestamp; MinValue = not found
    }

    public sealed class CostCheck { public string Name; public bool Pass; public string Detail; }

    public static class CostGate
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string Num = @"([-+0-9.eE]+)";
        static readonly Regex WorldRx = new Regex(
            @"drift: world day (\d+): rate " + Num + @" game s per real s at speed " + Num + @" \(" + Num + @" without stalls\); frame max (\d+) ms; frames over 100 ms (\d+); gen0 (\d+); stalls (\d+) \(" + Num + @" s\)");
        static readonly Regex LaunchRx = new Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) .*starting via ");

        // The limits of the plan's "Observer-effect criteria".
        const double ReadMeanMs = 5.0, ReadMaxMs = 20.0, CaptureMaxMs = 50.0;
        const long GcHitOneIn = 50;                  // at most 2 % of a run's reads may be GC-hit: hits x 50 <= reads
        const double RateFloor = 0.02, RateCap = 0.05, ExtraFrameMs = 20.0, Over100Share = 0.10, Over100Floor = 3.0;
        const long ReadMaxMicros = 20000, CaptureMaxMicros = 50000;
        const int ListMax = 12;
        const string StopQuit = "quit";               // the reason of the `stop` event at the normal end of a run (OnApplicationQuit)

        // ================= the mod log =================

        // The `drift: world day` lines of a mod log, in order. False (with the reason) for a line that does not parse and for
        // a log with none: an off run must log them too.
        public static bool TryParseModLog(IEnumerable<string> lines, out List<WorldDayLine> world, out string error)
        {
            world = null; error = null;
            if (lines == null) { error = "no log lines were given"; return false; }
            var list = new List<WorldDayLine>();
            int n = 0;
            foreach (string line in lines)
            {
                n++;
                if (line == null || line.IndexOf("drift: world day ", StringComparison.Ordinal) < 0) continue;
                Match m = WorldRx.Match(line);
                if (!m.Success) { error = "line " + n + ": a 'drift: world day' line that does not parse: " + (line.Length > 200 ? line.Substring(0, 200) : line); return false; }
                var w = new WorldDayLine();
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, Inv, out w.Day)
                    || !Dbl(m.Groups[2].Value, out w.Rate) || !Dbl(m.Groups[3].Value, out w.Speed) || !Dbl(m.Groups[4].Value, out w.RateWithoutStalls)
                    || !int.TryParse(m.Groups[5].Value, NumberStyles.None, Inv, out w.FrameMaxMs) || !int.TryParse(m.Groups[6].Value, NumberStyles.None, Inv, out w.FramesOver100)
                    || !int.TryParse(m.Groups[7].Value, NumberStyles.None, Inv, out w.Gen0) || !int.TryParse(m.Groups[8].Value, NumberStyles.None, Inv, out w.Stalls)
                    || !Dbl(m.Groups[9].Value, out w.StallSeconds))
                { error = "line " + n + ": a number in a 'drift: world day' line is out of range"; return false; }
                list.Add(w);
            }
            if (list.Count == 0) { error = "no 'drift: world day' lines (a recorder-off run logs them too)"; return false; }
            world = list;
            return true;
        }

        static bool Dbl(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, Inv, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        // The time of the first `starting via` line: when this launch began.
        public static bool TryParseLaunch(IEnumerable<string> lines, out DateTime launch)
        {
            launch = DateTime.MinValue;
            if (lines == null) return false;
            foreach (string line in lines)
            {
                if (line == null) continue;
                Match m = LaunchRx.Match(line);
                if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", Inv, DateTimeStyles.None, out launch)) return true;
            }
            launch = DateTime.MinValue;
            return false;
        }

        // ================= the gate =================

        static void Add(List<CostCheck> checks, string name, bool pass, string detail) { checks.Add(new CostCheck { Name = name, Pass = pass, Detail = detail ?? "" }); }

        static string Name(CostRun r, int i) { return string.IsNullOrEmpty(r.Label) ? "run" + (i + 1) : r.Label; }

        static string Ms(long micros) { return (micros / 1000.0).ToString("0.0", Inv); }

        static string Signed(double v) { return (v >= 0 ? "+" : "-") + Math.Abs(v).ToString("0.##", Inv); }

        static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            double[] s = v.ToArray();
            Array.Sort(s);
            int n = s.Length;
            return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
        }

        static double Mean(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            double sum = 0;
            foreach (double x in v) sum += x;
            return sum / v.Count;
        }

        sealed class WorldFigures
        {
            public bool Measured; public int LastDay, ZeroRateWindows; public double Collections, Rate, Longest, Over100, Stalls, StallSeconds;
            public HashSet<double> Speeds = new HashSet<double>();
        }

        // Per run: the collections (the sum of the windows' gen0), the rate (the harmonic mean of the windows' rates without
        // stall seconds: every window is the same 30 game days, so that is total game time over total real time), the longest
        // frame, the frames over 100 ms and the stalls.
        static WorldFigures Figures(CostRun r)
        {
            var f = new WorldFigures();
            if (r.World == null || r.World.Count == 0) return f;
            double inv = 0; int rated = 0;
            foreach (WorldDayLine w in r.World)
            {
                if (w.Day > f.LastDay) f.LastDay = w.Day;
                f.Collections += w.Gen0; f.Over100 += w.FramesOver100; f.Stalls += w.Stalls; f.StallSeconds += w.StallSeconds;
                if (w.FrameMaxMs > f.Longest) f.Longest = w.FrameMaxMs;
                f.Speeds.Add(w.Speed);
                if (w.RateWithoutStalls > 0) { inv += 1.0 / w.RateWithoutStalls; rated++; }
                if (w.RateWithoutStalls <= 0 || w.Rate <= 0) f.ZeroRateWindows++;     // a paused window: a gate run never pauses
            }
            f.Rate = rated > 0 ? rated / inv : double.NaN;
            f.Measured = !double.IsNaN(f.Rate);
            return f;
        }

        public static List<CostCheck> Evaluate(IList<CostRun> runs)
        {
            var checks = new List<CostCheck>();
            if (runs == null || runs.Count == 0) { Add(checks, "runs", false, "no runs were given"); return checks; }
            var offs = new List<int>(); var ons = new List<int>();
            for (int i = 0; i < runs.Count; i++) (runs[i].RecorderOn ? ons : offs).Add(i);
            var fig = new WorldFigures[runs.Count];
            for (int i = 0; i < runs.Count; i++) fig[i] = Figures(runs[i]);

            Interleaving(checks, runs);
            Comparable(checks, runs, fig);
            Reads(checks, runs, offs, ons, fig);
            Garbage(checks, runs, offs, ons, fig);
            Rate(checks, runs, offs, ons, fig);
            Frames(checks, runs, offs, ons, fig);
            Stalls(checks, runs, fig);
            Dropped(checks, runs, ons);
            RecordSpan(checks, runs, ons, fig);
            RecordReadable(checks, runs, ons);
            GcTags(checks, runs, ons);
            PausedWindows(checks, runs, fig);
            return checks;
        }

        // ---- the order of the runs ----

        static void Interleaving(List<CostCheck> checks, IList<CostRun> runs)
        {
            var problems = new List<string>();
            int offN = 0, onN = 0;
            for (int i = 0; i < runs.Count; i++) { if (runs[i].RecorderOn) onN++; else offN++; }
            if (offN != onN) problems.Add("not interleaved: " + offN + " off run(s) and " + onN + " on run(s)");
            for (int i = 0; i < runs.Count; i++)
            {
                bool expectOn = i % 2 == 1;
                if (runs[i].RecorderOn != expectOn) { problems.Add("not interleaved: run " + (i + 1) + " (" + Name(runs[i], i) + ") is " + (runs[i].RecorderOn ? "on" : "off") + ", expected " + (expectOn ? "on" : "off") + " (off, on, off, on, ...)"); break; }
            }
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].Launch == DateTime.MinValue) { problems.Add("launch time of run " + (i + 1) + " (" + Name(runs[i], i) + ") unknown: its mod log has no 'starting via' line"); continue; }
                if (i > 0 && runs[i - 1].Launch != DateTime.MinValue && runs[i].Launch <= runs[i - 1].Launch)
                    problems.Add("launch order disagrees with the order given: run " + (i + 1) + " (" + Name(runs[i], i) + ") launched at " + runs[i].Launch.ToString("HH:mm:ss.fff", Inv)
                        + ", not after run " + i + " (" + Name(runs[i - 1], i - 1) + ") at " + runs[i - 1].Launch.ToString("HH:mm:ss.fff", Inv));
            }
            Add(checks, "interleaving and launch order", problems.Count == 0,
                problems.Count == 0 ? runs.Count + " runs, off and on alternating, launch times increasing" : string.Join("; ", problems.ToArray()));
        }

        static void Comparable(List<CostCheck> checks, IList<CostRun> runs, WorldFigures[] fig)
        {
            var problems = new List<string>();
            var speeds = new HashSet<double>(); var lastDays = new HashSet<int>();
            for (int i = 0; i < runs.Count; i++)
            {
                if (!fig[i].Measured) { problems.Add(Name(runs[i], i) + " has no usable 'drift: world day' lines (unmeasured)"); continue; }
                foreach (double s in fig[i].Speeds) speeds.Add(s);
                lastDays.Add(fig[i].LastDay);
            }
            if (speeds.Count > 1) problems.Add("the runs report different speeds");
            if (lastDays.Count > 1) problems.Add("the runs cover different days (their last world line differs)");
            Add(checks, "runs comparable", problems.Count == 0, problems.Count == 0 ? "same speed (" + FirstOf(speeds) + ") and same days covered (last world line on day " + FirstOf(lastDays) + ")" : string.Join("; ", problems.ToArray()));
        }

        static string FirstOf<T>(HashSet<T> set) { foreach (T x in set) return Convert.ToString(x, Inv); return "?"; }

        // ---- the cost of the reads (the on runs' records) ----

        sealed class Read { public string Run; public int Day; public int Micros; public bool GcHit, Captured; }

        static void Reads(List<CostCheck> checks, IList<CostRun> runs, List<int> offs, List<int> ons, WorldFigures[] fig)
        {
            var all = new List<Read>(); var perRun = new List<string>(); var unmeasured = new List<string>();
            var gcShare = new List<string>(); var day0 = new List<string>();
            foreach (int i in ons)
            {
                CostRun r = runs[i];
                if (r.Record == null || r.Record.Days == null || r.Record.Days.Count == 0) { unmeasured.Add(Name(r, i) + " has no record with days"); continue; }
                long sum = 0; int hits = 0, total = 0;
                foreach (DayRecord d in r.Record.Days)
                {
                    // day 0 (the load-time capture, JIT included) is not a daily read: it is reported on its own line
                    if (d.Day == 0) { day0.Add(Name(r, i) + " " + Ms(d.CostMicros) + " ms" + (d.GcHit ? ", GC-hit" : "") + (d.Captured ? ", capture step" : "")); continue; }
                    all.Add(new Read { Run = Name(r, i), Day = d.Day, Micros = d.CostMicros, GcHit = d.GcHit, Captured = d.Captured });
                    sum += d.CostMicros; total++; if (d.GcHit) hits++;
                }
                perRun.Add(Name(r, i) + " " + (total == 0 ? "-" : Ms(sum / total)));
                if (hits * GcHitOneIn > total) gcShare.Add(Name(r, i) + ": " + hits + " of " + total + " reads GC-hit (" + (100.0 * hits / total).ToString("0.00", Inv) + " %, limit 2 %)");
            }
            string miss = unmeasured.Count > 0 ? "unmeasured: " + string.Join("; ", unmeasured.ToArray()) : null;
            Add(checks, "day 0 (reported)", true, day0.Count == 0 ? "no day-0 record in any on run" : "the load-time read, left out of the figures below: " + string.Join("; ", day0.ToArray()));

            // the mean over ALL reads
            if (all.Count == 0 || miss != null) Add(checks, "read mean (all reads)", false, miss ?? "no reads were recorded");
            else
            {
                long sum = 0; foreach (Read x in all) sum += x.Micros;
                double mean = sum / (double)all.Count / 1000.0;
                Add(checks, "read mean (all reads)", mean <= ReadMeanMs, "mean " + mean.ToString("0.00", Inv) + " ms over " + all.Count + " reads (limit " + ReadMeanMs.ToString("0", Inv) + " ms); per run: " + string.Join(", ", perRun.ToArray()));
            }

            // the maximum over plain reads that are not GC-hit
            Judge(checks, "read max (not GC-hit)", all, miss, x => !x.Captured && !x.GcHit, ReadMaxMicros, "reads");

            // GC-hit reads: listed, at most 2 % of a run's reads, each within 20 ms + the off runs' longest frame
            double offLongest = Median(Longest(runs, offs, fig));
            string needOff = double.IsNaN(offLongest) ? "the off runs' longest frame is unmeasured" : null;
            JudgeGcHit(checks, "GC-hit reads", all, miss ?? needOff, x => !x.Captured && x.GcHit, ReadMaxMs, offLongest, gcShare);

            // capture steps (checkpoint days)
            Judge(checks, "capture step max (not GC-hit)", all, miss, x => x.Captured && !x.GcHit, CaptureMaxMicros, "capture steps");
            JudgeGcHit(checks, "GC-hit capture steps", all, miss ?? needOff, x => x.Captured && x.GcHit, CaptureMaxMs, offLongest, null);
        }

        static List<double> Longest(IList<CostRun> runs, List<int> idx, WorldFigures[] fig)
        {
            var l = new List<double>();
            foreach (int i in idx) if (fig[i].Measured) l.Add(fig[i].Longest);
            return l;
        }

        static string Entry(Read x) { return x.Run + " day " + x.Day + " " + Ms(x.Micros) + " ms"; }

        static string Listing(List<Read> hits)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hits.Count && i < ListMax; i++) { if (i > 0) sb.Append("; "); sb.Append(Entry(hits[i])); }
            if (hits.Count > ListMax) sb.Append("; ... and " + (hits.Count - ListMax) + " more");
            return sb.ToString();
        }

        // The largest cost among the reads the filter picks must not pass the limit; no such read is unmeasured.
        static void Judge(List<CostCheck> checks, string name, List<Read> all, string missing, Func<Read, bool> pick, long limitMicros, string what)
        {
            if (missing != null) { Add(checks, name, false, missing); return; }
            var over = new List<Read>(); int n = 0; Read max = null;
            foreach (Read x in all)
            {
                if (!pick(x)) continue;
                n++;
                if (max == null || x.Micros > max.Micros) max = x;
                if (x.Micros > limitMicros) over.Add(x);
            }
            if (n == 0) { Add(checks, name, false, "no " + what + " were measured (unmeasured)"); return; }
            if (over.Count > 0) { over.Sort((p, q) => q.Micros.CompareTo(p.Micros)); Add(checks, name, false, over.Count + " of " + n + " " + what + " over " + Ms(limitMicros) + " ms: " + Listing(over)); }
            else Add(checks, name, true, "max " + Ms(max.Micros) + " ms (" + Entry(max) + ") over " + n + " " + what + " (limit " + Ms(limitMicros) + " ms)");
        }

        static void JudgeGcHit(List<CostCheck> checks, string name, List<Read> all, string missing, Func<Read, bool> pick, double baseMs, double offLongest, List<string> shareProblems)
        {
            if (missing != null) { Add(checks, name, false, missing); return; }
            double bound = baseMs + offLongest;
            var hits = new List<Read>(); var over = new List<Read>();
            foreach (Read x in all)
            {
                if (!pick(x)) continue;
                hits.Add(x);
                if (x.Micros / 1000.0 > bound + 1e-9) over.Add(x);
            }
            var problems = new List<string>();
            if (shareProblems != null) problems.AddRange(shareProblems);
            if (over.Count > 0) { over.Sort((p, q) => q.Micros.CompareTo(p.Micros)); problems.Add(over.Count + " over " + bound.ToString("0.0", Inv) + " ms: " + Listing(over)); }
            string list = hits.Count == 0 ? "none" : hits.Count + " listed: " + Listing(hits);
            Add(checks, name, problems.Count == 0, (problems.Count == 0 ? "" : string.Join("; ", problems.ToArray()) + ". ") + list + " (bound " + bound.ToString("0.0", Inv) + " ms)");
        }

        // ---- collections, rate, frames, stalls ----

        static void Garbage(List<CostCheck> checks, IList<CostRun> runs, List<int> offs, List<int> ons, WorldFigures[] fig)
        {
            const string name = "garbage", confirm = "garbage confirmation";     // collections per run: the medians of the first 3 + 3; and the mean of 5 + 5
            var off = new List<double>(); var on = new List<double>();
            foreach (int i in offs) if (fig[i].Measured) off.Add(fig[i].Collections);
            foreach (int i in ons) if (fig[i].Measured) on.Add(fig[i].Collections);
            if (off.Count != offs.Count || on.Count != ons.Count)
            {
                Add(checks, name, false, "a run has no 'drift: world day' lines (unmeasured)");
                Add(checks, confirm, false, "a run has no 'drift: world day' lines (unmeasured)");
                return;
            }
            if (off.Count < 3 || on.Count < 3)
            {
                Add(checks, name, false, "needs 3 off and 3 on runs; got " + off.Count + " off and " + on.Count + " on");
                Add(checks, confirm, false, "unconfirmed: 5 + 5 runs are needed; got " + off.Count + " off and " + on.Count + " on");
                return;
            }
            double offMed = Median(off.GetRange(0, 3)), onMed = Median(on.GetRange(0, 3)), d3 = onMed - offMed;
            bool pass = d3 <= 1.0 + 1e-9;
            var sb = new StringBuilder("first 3 + 3 medians: off " + offMed.ToString("0.##", Inv) + ", on " + onMed.ToString("0.##", Inv) + ": " + Signed(d3) + " (limit +1)");
            Add(checks, name, pass, sb.ToString());
            // the confirmation is its own check: without 5 + 5 runs the result is not trusted, so it fails; the medians above still show the 3 + 3 result
            if (off.Count >= 5 && on.Count >= 5)
            {
                double d5 = Mean(on) - Mean(off);
                Add(checks, confirm, d5 <= 1.0 + 1e-9, "mean of " + off.Count + " + " + on.Count + " runs: off " + Mean(off).ToString("0.##", Inv) + ", on " + Mean(on).ToString("0.##", Inv) + ": " + Signed(d5) + " (limit +1)");
            }
            else Add(checks, confirm, false, "unconfirmed: 5 + 5 runs are needed; got " + off.Count + " off and " + on.Count + " on");
        }

        static void Rate(List<CostCheck> checks, IList<CostRun> runs, List<int> offs, List<int> ons, WorldFigures[] fig)
        {
            const string name = "rate (without stall seconds)";     // game s per real s
            var off = new List<double>(); var on = new List<double>();
            foreach (int i in offs) if (fig[i].Measured) off.Add(fig[i].Rate);
            foreach (int i in ons) if (fig[i].Measured) on.Add(fig[i].Rate);
            if (off.Count == 0 || on.Count == 0 || off.Count != offs.Count || on.Count != ons.Count) { Add(checks, name, false, "a run has no usable rate (unmeasured)"); return; }
            double offMed = Median(off), onMed = Median(on), min = double.MaxValue, max = 0;
            foreach (double x in off) { if (x < min) min = x; if (x > max) max = x; }
            double spread = (max - min) / offMed, tol = Math.Max(RateFloor, Math.Min(spread, RateCap)), dev = (onMed - offMed) / offMed;
            Add(checks, name, Math.Abs(dev) <= tol + 1e-12,
                "off median " + offMed.ToString("0", Inv) + " (spread " + (100 * spread).ToString("0.00", Inv) + " %" + (spread > RateCap ? ", capped at 5 %" : "") + "), on median " + onMed.ToString("0", Inv)
                + ": " + (100 * dev).ToString("+0.00;-0.00;0.00", Inv) + " % (limit " + (100 * tol).ToString("0.00", Inv) + " %, two-sided)");
        }

        static void Frames(List<CostCheck> checks, IList<CostRun> runs, List<int> offs, List<int> ons, WorldFigures[] fig)
        {
            var offL = new List<double>(); var onL = new List<double>(); var offO = new List<double>(); var onO = new List<double>();
            foreach (int i in offs) if (fig[i].Measured) { offL.Add(fig[i].Longest); offO.Add(fig[i].Over100); }
            foreach (int i in ons) if (fig[i].Measured) { onL.Add(fig[i].Longest); onO.Add(fig[i].Over100); }
            if (offL.Count == 0 || onL.Count == 0 || offL.Count != offs.Count || onL.Count != ons.Count)
            {
                Add(checks, "longest frame", false, "a run has no frame figures (unmeasured)");
                Add(checks, "frames over 100 ms", false, "a run has no frame figures (unmeasured)");
                return;
            }
            // the medians over the runs of each run's own figure; the on runs against the off runs
            double offLm = Median(offL), onLm = Median(onL);
            Add(checks, "longest frame", onLm <= offLm + ExtraFrameMs + 1e-9,
                "off median " + offLm.ToString("0.#", Inv) + " ms, on median " + onLm.ToString("0.#", Inv) + " ms (limit the off runs' + 20 ms = " + (offLm + ExtraFrameMs).ToString("0.#", Inv) + " ms)");
            double offOm = Median(offO), onOm = Median(onO), allowed = Math.Max(offOm * (1 + Over100Share), offOm + Over100Floor);
            Add(checks, "frames over 100 ms", onOm <= allowed + 1e-9,
                "off median " + offOm.ToString("0.#", Inv) + ", on median " + onOm.ToString("0.#", Inv) + " (limit max(10 % more, +3) = " + allowed.ToString("0.##", Inv) + ")");
        }

        static void Stalls(List<CostCheck> checks, IList<CostRun> runs, WorldFigures[] fig)
        {
            var parts = new List<string>();
            for (int i = 0; i < runs.Count; i++)
                if (fig[i].Measured && fig[i].Stalls > 0) parts.Add(Name(runs[i], i) + ": " + fig[i].Stalls.ToString("0", Inv) + " stall(s), " + fig[i].StallSeconds.ToString("0.0", Inv) + " s");
            // known noise: reported, not judged; the rate leaves their seconds out
            Add(checks, "stalls (reported)", true, parts.Count == 0 ? "none logged" : string.Join("; ", parts.ToArray()));
        }

        // ---- the record covers the run, and stopped normally ----

        // An on run's record must start at day 0 and reach the world lines' last day (within one day), and must not hold a
        // `stop` event for any reason but the normal quit: a recorder that stopped early, or another save loaded, did not
        // measure the run (the world lines would still look fine).
        static void RecordSpan(List<CostCheck> checks, IList<CostRun> runs, List<int> ons, WorldFigures[] fig)
        {
            var problems = new List<string>();
            foreach (int i in ons)
            {
                CostRun r = runs[i]; string n = Name(r, i);
                if (r.Record == null || r.Record.Days == null || r.Record.Days.Count == 0) { problems.Add(n + " has no record days (unmeasured)"); continue; }
                int first = int.MaxValue, last = int.MinValue;
                foreach (DayRecord d in r.Record.Days) { if (d.Day < first) first = d.Day; if (d.Day > last) last = d.Day; }
                if (first != 0) problems.Add(n + ": the record starts on day " + first + ", not day 0");
                if (!fig[i].Measured) problems.Add(n + ": no world lines to measure the record against");
                else if (last < fig[i].LastDay - 1) problems.Add(n + ": the record ends on day " + last + ", the world lines reach day " + fig[i].LastDay);
                if (r.Record.Events != null)
                    foreach (DriftEventLine e in r.Record.Events)
                    {
                        if (e == null || e.Kind != "stop") continue;
                        int a, b, c; string reason;
                        if (!DriftAnalysis.TryParseEventText(e.Text, out a, out b, out c, out reason)) problems.Add(n + ": an unreadable stop event '" + e.Text + "'");
                        else if (reason == null || !string.Equals(reason, StopQuit, StringComparison.OrdinalIgnoreCase))
                            problems.Add(n + ": the recorder stopped on day " + e.Day + " for '" + (reason ?? "no reason given") + "', not the normal quit");
                    }
            }
            Add(checks, "record span", problems.Count == 0, problems.Count == 0 ? "every on record starts on day 0, reaches the last world line, and stopped with the normal quit (or not at all)" : string.Join("; ", problems.ToArray()));
        }

        // ---- the reader had nothing to say about the record ----

        // Any reader warning on an on run's record (an index outside the header's tables) is a recorder bug: the run fails.
        static void RecordReadable(List<CostCheck> checks, IList<CostRun> runs, List<int> ons)
        {
            var problems = new List<string>();
            foreach (int i in ons)
            {
                CostRun r = runs[i];
                if (r.Record == null || r.Record.Warnings == null) continue;
                foreach (string w in r.Record.Warnings) problems.Add(Name(r, i) + ": " + w);
            }
            Add(checks, "record readable", problems.Count == 0, problems.Count == 0 ? "the reader found nothing wrong in any on record" : string.Join("; ", problems.ToArray()));
        }

        // ---- the GC tag agrees with the collection counts ----

        static void GcTags(List<CostCheck> checks, IList<CostRun> runs, List<int> ons)
        {
            var problems = new List<string>(); int shown = 0, bad = 0;
            foreach (int i in ons)
            {
                CostRun r = runs[i];
                if (r.Record == null || r.Record.Days == null) continue;
                foreach (DayRecord d in r.Record.Days)
                {
                    if (d.GcHit == (d.GcBefore != d.GcAfter)) continue;
                    bad++;
                    if (shown++ < ListMax) problems.Add(Name(r, i) + " day " + d.Day + " (tagged " + (d.GcHit ? "GC-hit" : "not GC-hit") + ", collection counts " + d.GcBefore + " and " + d.GcAfter + ")");
                }
            }
            if (bad > ListMax) problems.Add("... and " + (bad - ListMax) + " more");
            Add(checks, "GC tags consistent", problems.Count == 0, problems.Count == 0 ? "every day's GC-hit tag agrees with its collection counts" : string.Join("; ", problems.ToArray()));
        }

        // ---- no paused windows ----

        static void PausedWindows(List<CostCheck> checks, IList<CostRun> runs, WorldFigures[] fig)
        {
            var problems = new List<string>();
            for (int i = 0; i < runs.Count; i++)
                if (fig[i].ZeroRateWindows > 0) problems.Add(Name(runs[i], i) + ": " + fig[i].ZeroRateWindows + " window(s) with a rate of 0 (a gate run never pauses)");
            Add(checks, "paused window", problems.Count == 0, problems.Count == 0 ? "no window with a rate of 0 in any run" : string.Join("; ", problems.ToArray()));
        }

        // ---- nothing dropped ----

        static void Dropped(List<CostCheck> checks, IList<CostRun> runs, List<int> ons)
        {
            var problems = new List<string>();
            foreach (int i in ons)
            {
                CostRun r = runs[i]; string n = Name(r, i);
                if (r.Record == null) { problems.Add(n + " has no record (unmeasured)"); continue; }
                if (r.Record.Dropped > 0) problems.Add(n + ": the writer dropped " + r.Record.Dropped + " day(s), event(s) or capture(s)");
                if (r.Record.Truncated) problems.Add(n + ": the record is cut off");
                else if (!r.Record.Ended) problems.Add(n + ": the record has no End record");
                var days = new HashSet<int>(); int lo = int.MaxValue, hi = int.MinValue;
                foreach (DayRecord d in r.Record.Days) { days.Add(d.Day); if (d.Day < lo) lo = d.Day; if (d.Day > hi) hi = d.Day; }
                if (days.Count > 0 && (long)hi - lo + 1 != days.Count) problems.Add(n + ": " + ((long)hi - lo + 1 - days.Count) + " day(s) are missing between day " + lo + " and day " + hi);
            }
            Add(checks, "nothing dropped", problems.Count == 0, problems.Count == 0 ? "no day, event or capture was dropped in any on run" : string.Join("; ", problems.ToArray()));
        }
    }
}
