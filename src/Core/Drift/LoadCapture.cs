using System;
using System.Collections.Generic;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    public sealed class LoadCaptureResult
    {
        public int HostDay = -1;   // the host's day the joiner's capture is held against
        public readonly List<string> Lines = new List<string>();   // "ok   ..." and "FAIL ..." lines, in order
        public readonly List<string> Failures = new List<string>();
        public bool Ok { get { return Failures.Count == 0; } }
    }

    // A joiner's load capture (day 0 of its record: the state its snapshot load gave, keyed from the host's origin)
    // against the host's record on the day the join happened. Between the host's capture of day N and a join at N + 0.4
    // the habitability model does not step (it steps only at month events, which fall on day boundaries), so a joiner
    // whose load left the host's state as it was holds the host's exact habitability sums. A load that ran the game's
    // own warm-up (a game change, or -semp-keep-prewarm) moves them: Earth alone moves the temperature sum by
    // thousands.
    public static class LoadCapture
    {
        public const double Tolerance = 1e-9;   // relative; the fixed joiners matched to the last bit

        // The world areas compared (field sums, every field each side has).
        static readonly string[] Areas = { DriftAreas.Habitability };

        public static LoadCaptureResult Check(DriftRecordFile host, DriftRecordFile joiner)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (joiner == null) throw new ArgumentNullException("joiner");
            var r = new LoadCaptureResult();
            DayRecord capture = null;
            if (joiner.Days != null)
                foreach (DayRecord d in joiner.Days) if (d.Day == 0) { capture = d; break; }
            if (capture == null) { Fail(r, "the joiner's record has no load capture (day 0)"); return r; }
            DayRecord day = null;
            if (host.Days != null)
                foreach (DayRecord d in host.Days)
                    if (d.KeyTicks <= capture.AtTicks && (day == null || d.KeyTicks > day.KeyTicks)) day = d;
            if (day == null) { Fail(r, "the host's record has no day at or before the joiner's load"); return r; }
            r.HostDay = day.Day;
            int compared = 0;
            foreach (string area in Areas)
            {
                Dictionary<string, double> a = Sums(day, area), b = Sums(capture, area);
                foreach (KeyValuePair<string, double> e in a)
                {
                    double j;
                    string what = area + " " + e.Key + " (host day " + day.Day + ")";
                    if (!b.TryGetValue(e.Key, out j))
                    {
                        Fail(r, what + ": the joiner's capture has no such sum");
                        continue;
                    }
                    compared++;
                    double scale = Math.Max(1.0, Math.Max(Math.Abs(e.Value), Math.Abs(j)));
                    bool same = Math.Abs(e.Value - j) <= Tolerance * scale;   // a NaN on either side is never the same
                    string line = what + ": host " + G(e.Value) + ", joiner " + G(j);
                    if (same) r.Lines.Add("ok   load capture " + line);
                    else Fail(r, line + " (difference " + G(Math.Abs(e.Value - j)) + ")");
                }
                foreach (string k in b.Keys)
                    if (!a.ContainsKey(k)) Fail(r, area + " " + k + ": the host's day " + day.Day + " has no such sum");
            }
            if (compared == 0) Fail(r, "no habitability sums to compare");
            return r;
        }

        static void Fail(LoadCaptureResult r, string why)
        {
            r.Failures.Add(why);
            r.Lines.Add("FAIL load capture " + why);
        }

        // field name (with the agency when it is not the world's) -> sum, for one area of one day
        static Dictionary<string, double> Sums(DayRecord d, string area)
        {
            var m = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (AreaSum s in d.Areas)
            {
                if (s.Area != area) continue;
                foreach (FieldMagnitude f in s.Magnitudes)
                    m[(s.Agency == null || s.Agency == DriftAreas.World ? "" : s.Agency + " ") + f.Name] = f.Sum;
            }
            return m;
        }

        static string G(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public static string Text(LoadCaptureResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string l in r.Lines) sb.Append(l).Append('\n');
            sb.Append("RESULT: ").Append(r.Ok ? "ok" : "FAIL").Append(" (host day ").Append(r.HostDay).Append(")\n");
            return sb.ToString();
        }
    }
}
