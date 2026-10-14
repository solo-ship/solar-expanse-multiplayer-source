using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // M3's facade, called by SempRoot after Session's calls. Each call does nothing at all without a test profile and an M3 flag
    // (-semp-drift, -semp-ai-off, -semp-no-autosave, -semp-random-seed, -semp-drift-speed, -semp-drift-days), and the state reader
    // does nothing without -semp-drift. Task 6 owns the load-time part (LoadTicks, the day-0 read, the allocation check); Task 7 adds
    // DriftWorld's calls and Task 8 the recorder (the per-day work, the writer, the hook).
    public static class Drift
    {
        // TimeController's private lastDay, read at save loaded before anything else (= CurrentTime then). The origin for AreaHasher and
        // RoundedDate, and day 0's key; day N's key is LoadTicks + N days. 0 until read.
        public static long LoadTicks { get; private set; }

        // A third label line in a test profile (null = none).
        public static string Status { get; private set; }

        // The allocation check ("docs/superpowers/plans/2026-10-10-m3-drift.md" Task 6): at load, while paused, back-to-back visits
        // (every CaptureEvery-th with the capture), tried up to MaxTries times. The brief's bounds are the constants below.
        public const int CheckVisits = 200, CheckCaptureEvery = 10, MaxTries = 3;
        public const double PassBytesPerVisit = 1024.0, FailBytesPerVisit = 10240.0;

        static bool worldActive;                            // the gate passed once: only then is DriftWorld touched
        static bool seen;                                   // a load has been handled (a second load is ignored)
        static StateField<Manager.TimeController, DateTime> lastDayRef, nextDayRef;
        static IdTable ids;
        static AreaHasher hasher;
        static DaySnapshot snapshot;
        static ItemCapture capture;
        static DriftRecorder recorder;                      // Task 8: null when not recording (no -semp-drift, or refused: the log says why)
        static Manager.TimeController timeRef;
        static int creepStage;                              // the paused-creep watch (Task 7's unexplained 3 h): 0 waiting, 1 first line logged, 2 second, 3 done
        static float creepAt;
        static long creepTicks;

        public static string CheckVerdict { get; private set; }     // PASS, BETWEEN, FAIL, or null (not run)

        public static void OnSaveLoaded()
        {
            try
            {
                long joined = joinOrigin;   // this load's only: taken (and cleared) on every load, flags or not
                joinOrigin = 0L;
                string joinedFrom = joinSaveId;
                joinSaveId = null;
                DevFlags f = Boot.Dev;
                if (Boot.Profile == null || Boot.DataDir == null || f == null || !AnyFlag(f)) return;   // a normal player: nothing at all
                // Before anything else, on EVERY load: the load time (a second load moves it).
                Manager.TimeController tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
                if (lastDayRef == null)
                {
                    lastDayRef = new StateField<Manager.TimeController, DateTime>("lastDay");
                    nextDayRef = new StateField<Manager.TimeController, DateTime>("nextDay");
                }
                if (lastDayRef.Ok) LoadTicks = lastDayRef.Get(tc).Ticks;
                if (joined != 0L)
                {
                    // A joiner keys its days from the HOST's origin (day N = origin + N days, as on the host), so the
                    // two records line up. The join loads mid-day, so the day-0 check does not apply; the comparison
                    // starts after the join (drift-run's --min-days).
                    LoadTicks = joined;
                    Log.Info("drift: joined: days keyed from the host's origin " + joined +
                             "; no day-0 check (a join loads mid-day); compare from the first day after it");
                }
                timeRef = tc; creepStage = 0;
                worldActive = true;             // set only here, after the gate (a test profile, a data folder and an M3 flag)
                DriftWorld.OnSaveLoaded();      // M3 Task 7: the world switches, first (they need only LoadTicks); each load, a second one too
                if (seen)
                {
                    DriftRecorder rec = recorder;
                    if (rec != null && !rec.Stopped)
                    {
                        // A second load moves the load time: nothing more is recorded in this process (the comparer fails the run's span).
                        rec.Stop("another save loaded");
                        Log.Warn("drift: another save loaded - record ended");
                    }
                    Log.Warn("drift: another save loaded (load ticks now " + LoadTicks + ") - the state reader and the allocation check run once, at the first load");
                    Status = "drift: another save loaded; not read again";
                    return;
                }
                seen = true;
                if (!GameHooks.IsolationOk) { Log.Warn("drift: not started: the test profile's protections are not all installed"); return; }
                if (!lastDayRef.Ok) { Log.Error("drift: not started: TimeController.lastDay is not readable (no load time)"); Status = "drift: no load time"; return; }
                long nextTicks = nextDayRef.Ok ? nextDayRef.Get(tc).Ticks : 0L;
                string date = new DateTime(LoadTicks).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                bool dayOk = nextTicks - LoadTicks == TimeSpan.TicksPerDay && tc.CurrentTime.Ticks == LoadTicks;
                if (joined != 0L) { }
                else if (dayOk)
                    Log.Info("drift: load ticks " + LoadTicks + " (" + date +
                             "); day0 check ok (nextDay is lastDay + 1 day, CurrentTime is lastDay)");
                else Log.Warn("drift: load ticks " + LoadTicks + " (" + date + "); day0 check WARNING: nextDay is not lastDay + 1 day, or CurrentTime (" + tc.CurrentTime.Ticks + ") is not lastDay");

                if (!f.DriftWanted) return;                 // the world switches (Task 7) need only LoadTicks
                Start(tc, date, joined != 0L, joinedFrom);
            }
            catch (Exception e)
            {
                Log.Error("drift: start failed: " + e);
                Status = "drift: failed, see the log";
            }
        }

        // The per-frame work: DriftWorld's baseline numbers and switches (Task 7), then the recorder's frame gap (Task 8: nothing else;
        // the days come from DriftProbe, the files from the writer thread) and the paused-creep watch.
        public static void Update()
        {
            if (!worldActive) return;       // a normal player never touches DriftWorld (cloud review M-5)
            DriftWorld.Update();
            DriftRecorder rec = recorder;
            if (rec != null) rec.Update();
            if (creepStage < 3) Creep();
        }

        // The recorder's stop (Task 8): the reason "quit" is what the cost gate's record-span check expects.
        public static void OnQuit()
        {
            if (!worldActive) return;
            DriftWorld.OnQuit();    // after a stop: where the game ended up
            try { DriftRecorder rec = recorder; if (rec != null) rec.Stop(DriftRecorder.StopQuit); }
            catch (Exception e) { Log.Error("drift: stop at quit failed: " + e); }
        }

        // A joiner's load (SnapshotLoad, in the save-loaded handler, before OnSaveLoaded) hands over the host's origin;
        // the next OnSaveLoaded keys the days from it and clears it. 0 = none.
        static long joinOrigin;
        static string joinSaveId;
        public static void JoinOrigin(long originTicks, string hostSaveId)
        {
            joinOrigin = originTicks; joinSaveId = hostSaveId;
        }

        // The save hash the host puts in the join header (null outside a drift run, or when it is not known).
        public static string HostSaveId()
        {
            return JoinedSave.HostId(Boot.Dev != null && Boot.Dev.DriftWanted, SaveHash);
        }

        // For the recorder's 30-day line: the label's third line.
        internal static void SetStatus(string text) { Status = text; }

        // Task 7's unexplained finding (a paused game's clock moved 3 hours between the stop and the quit): once the stop day is
        // reached and the speed reads 0, log the game time, then again 5 s and 30 s later, so a creep from fixed steps queued at the
        // moment of the pause shows up (a few game seconds at once) against a creep that goes on (every line later).
        static void Creep()
        {
            try
            {
                if (!DriftWorld.TargetReached || timeRef == null) return;
                float scale = timeRef.CurrentTimeScale, now = Time.realtimeSinceStartup;
                var inv = CultureInfo.InvariantCulture;
                if (creepStage == 0)
                {
                    if (scale != 0f) return;
                    creepStage = 1; creepAt = now; creepTicks = timeRef.CurrentTime.Ticks;
                    Log.Info("drift: paused creep: game time at the stop " + timeRef.CurrentTime.ToString("yyyy-MM-dd HH:mm:ss.fff", inv) + " (speed 0)");
                }
                else if ((creepStage == 1 && now - creepAt >= 5f) || (creepStage == 2 && now - creepAt >= 30f))
                {
                    double moved = (timeRef.CurrentTime.Ticks - creepTicks) / 1e7;
                    Log.Info("drift: paused creep: " + (now - creepAt).ToString("F1", inv) + " s of real time after the stop the game time is " + timeRef.CurrentTime.ToString("yyyy-MM-dd HH:mm:ss.fff", inv) +
                             " (speed " + scale.ToString("F0", inv) + "); moved " + moved.ToString("F1", inv) + " game s since the stop");
                    creepStage++;
                }
            }
            catch (Exception) { creepStage = 3; }
        }

        // The first 16 hex digits of the SHA-256 of the loaded save's .json.gz (the record header's save hash); "unknown" when it can't be read.
        static string SaveHash()
        {
            try
            {
                string name = Boot.AutoLoad ?? Boot.Seed;
                if (name == null || DataIsolation.Paths == null) return "unknown";
                string file = Path.Combine(DataIsolation.Paths.Saves, name + ".json.gz");
                if (!File.Exists(file)) return "unknown";
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(file))
                {
                    byte[] h = sha.ComputeHash(fs);
                    return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception) { return "unknown"; }
        }

        // Any M3 flag (DevFlags has no single property for it).
        static bool AnyFlag(DevFlags f)
        {
            return f.Drift || f.AiOff || f.NoAutosave || f.RandomSeed >= 0 || f.DriftSpeed > 0f || f.DriftDays > 0;
        }

        // ------------------------------------------------------------------ the day-0 read

        // joined: this game joined a host, so it has no save file to hash; it records the host's (hostSaveId).
        static void Start(Manager.TimeController tc, string date, bool joined, string hostSaveId)
        {
            Log.Info("drift: state reader starting (profile " + Boot.Profile + ", speed " + tc.CurrentTimeScale.ToString("F0", CultureInfo.InvariantCulture) + ")");
            StateReader.Prepare();
            if (!StateReader.IsPrepared || StateReader.Agencies.Length == 0)
            {
                Log.Error("drift: not started: the state reader could not prepare (no agencies or an error above)");
                Status = "drift: reader failed, see the log";
                return;
            }
            Log.Info("drift: agencies " + StateReader.Describe());

            int n = StateReader.Agencies.Length;
            ids = new IdTable(1024);
            hasher = new AreaHasher(LoadTicks, ids, n);
            snapshot = new DaySnapshot(DriftAreas.Blocks(n), DriftAreas.FieldSums(n), DriftFields.MaxScalars(n));
            capture = new ItemCapture(DriftAreas.CaptureItems(n), DriftAreas.CaptureValues(n));
            Log.Info("drift: buffers " + snapshot.Blocks.Length + " blocks, " + snapshot.Fields.Length + " field sums, " + snapshot.Scalars.Length + " scalars, capture " +
                     capture.Items.Length + " items / " + capture.Values.Length + " values (allocated once)");

            // Task 8: open the record (the writer thread, the header). null = not recording; the log says why. The day-0 read below is the
            // same either way.
            recorder = null;
            if (Boot.Dev.DriftWanted)
            {
                string hash = JoinedSave.RecordHash(joined, hostSaveId, SaveHash);
                if (hash == "unknown" || hash == JoinedSave.Mark(null))
                    Log.Warn("drift: the save's hash could not be worked out (the record header says unknown)");
                recorder = DriftRecorder.Create(tc, date, hash, ids, hasher, capture, snapshot);
            }

            // Day 0: ONE visit, with the capture.
            int gcBefore = GC.CollectionCount(0);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            hasher.BeginDay(snapshot, capture);
            StateReader.Visit(hasher);
            hasher.EndDay();
            StateReader.Scalars(snapshot);
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            int gcAfter = GC.CollectionCount(0);
            double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            snapshot.Head.Day = 0; snapshot.Head.KeyTicks = LoadTicks; snapshot.Head.AtTicks = tc.CurrentTime.Ticks;
            snapshot.Head.Scale = tc.CurrentTimeScale; snapshot.Head.CostMicros = (int)(ticks * 1000000L / System.Diagnostics.Stopwatch.Frequency);
            snapshot.Head.GcBefore = gcBefore; snapshot.Head.GcAfter = gcAfter; snapshot.Head.GcHit = (byte)(gcAfter != gcBefore ? 1 : 0); snapshot.Head.Captured = 1;

            int areas, items;
            CountAreasAndItems(out areas, out items);
            // The day-0 line (the outline's "drift: day <N> ..." shape, as far as Task 6 has the numbers).
            Log.Info("drift: day 0 (" + date.Substring(0, 10) + ") " + areas + " areas, " + items + " items, " + ms.ToString("F2", CultureInfo.InvariantCulture) +
                     " ms; capture step, JIT and first use included; GC-hit " + (gcAfter != gcBefore ? "yes" : "no") + "; gen0 " + gcBefore + " -> " + gcAfter);
            Log.Info("drift: day 0 counts " + CountsText());
            LogSums();
            Log.Info("drift: day 0 capture " + capture.ItemCount + " items, " + capture.ValueCount + " values, truncated " + (capture.Truncated ? "yes" : "no") +
                     ", capture overflow " + (hasher.CaptureOverflow ? "yes" : "no") + ", snapshot overflow " + (hasher.SnapshotOverflow ? "yes" : "no") +
                     "; blocks " + snapshot.BlockCount + ", field sums " + snapshot.FieldCount + ", scalars " + snapshot.ScalarCount + "; id table " + ids.Count + " names");
            Log.Info("drift: day 0 scalars " + ScalarsText());
            if (StateReader.AreaFailures > 0 || hasher.SnapshotOverflow || hasher.CaptureOverflow || StateReader.FieldsMissing > 0)
                Log.Warn("drift: day 0 is NOT clean: area failures " + StateReader.AreaFailures + ", fields missing " + StateReader.FieldsMissing +
                         ", snapshot overflow " + hasher.SnapshotOverflow + ", capture overflow " + hasher.CaptureOverflow);

            string dayStatus = "drift: day 0 read, " + items + " items in " + ms.ToString("F1", CultureInfo.InvariantCulture) + " ms";
            Status = dayStatus;

            // Task 8: day 0 goes to the writer (names first, then the day, then the capture) and the probe starts. The capture now belongs
            // to the writer until it has written the day-0 dump: the allocation check below waits for it.
            if (recorder != null)
            {
                recorder.RecordDayZero(snapshot);
                if (!recorder.Stopped) { dayStatus += "; recording"; Status = dayStatus; }     // the later labels (check verdict, session) keep it
                if (!recorder.WaitCaptureFree(5000))
                {
                    Log.Warn("drift: allocation check skipped: the day-0 dump was still being written after 5 s");
                    return;
                }
            }

            if (Boot.Dev.SessionWanted)
            {
                // The check holds the main thread for about a second; in a session that would show as a long frame and mark the run
                // disturbed. It is a solo-load check.
                Log.Info("drift: allocation check skipped (a session is wanted: it blocks the main thread for about a second)");
                Status = dayStatus + "; alloc check skipped (session)";
                return;
            }
            AllocationCheck(dayStatus);
        }

        static void CountAreasAndItems(out int areas, out int items)
        {
            var seenArea = new bool[DriftAreas.All.Length];
            areas = 0; items = 0;
            for (int i = 0; i < snapshot.BlockCount; i++)
            {
                int a = snapshot.Blocks[i].Area;
                if (a >= 0 && a < seenArea.Length && !seenArea[a]) { seenArea[a] = true; areas++; }
                items += snapshot.Blocks[i].Count;
            }
        }

        // "money: CNSA 1, ESA 1, ...; research: ...; bodies: - 150": every block's item count, by area, with the agency's ID.
        static string CountsText()
        {
            var sb = new StringBuilder();
            string[] agencies = StateReader.Agencies;
            for (int a = 0; a < DriftAreas.All.Length; a++)
            {
                if (a > 0) sb.Append("; ");
                sb.Append(DriftAreas.All[a]).Append(':');
                int total = 0, shown = 0;
                for (int i = 0; i < snapshot.BlockCount; i++)
                {
                    if (snapshot.Blocks[i].Area != a) continue;
                    int ag = snapshot.Blocks[i].Agency;
                    sb.Append(shown++ > 0 ? ", " : " ").Append(ag >= 0 && ag < agencies.Length ? agencies[ag] : "-").Append(' ').Append(snapshot.Blocks[i].Count);
                    total += snapshot.Blocks[i].Count;
                }
                sb.Append(" (total ").Append(total).Append(')');
            }
            return sb.ToString();
        }

        // One line per area: the wrapping sums of the block hashes over the agencies (so two loads of the same save can be compared).
        static void LogSums()
        {
            for (int a = 0; a < DriftAreas.All.Length; a++)
            {
                ulong e = 0, r = 0, d = 0; int count = 0, blocks = 0;
                unchecked
                {
                    for (int i = 0; i < snapshot.BlockCount; i++)
                    {
                        if (snapshot.Blocks[i].Area != a) continue;
                        e += snapshot.Blocks[i].Exact; r += snapshot.Blocks[i].Rounded; d += snapshot.Blocks[i].Discrete;
                        count += snapshot.Blocks[i].Count; blocks++;
                    }
                }
                Log.Info("drift: day 0 sum " + DriftAreas.All[a] + " blocks " + blocks + " items " + count + " exact " + e.ToString("x16", CultureInfo.InvariantCulture) +
                         " rounded " + r.ToString("x16", CultureInfo.InvariantCulture) + " discrete " + d.ToString("x16", CultureInfo.InvariantCulture));
            }
        }

        static string ScalarsText()
        {
            var sb = new StringBuilder();
            string[] agencies = StateReader.Agencies;
            for (int i = 0; i < snapshot.ScalarCount; i++)
            {
                if (i > 0) sb.Append("; ");
                int ag = snapshot.Scalars[i].Agency;
                sb.Append(ag >= 0 && ag < agencies.Length ? agencies[ag] : "-").Append(' ').Append(DriftFields.ScalarNames[snapshot.Scalars[i].Name]).Append('=')
                  .Append(snapshot.Scalars[i].Value.ToString("G6", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the allocation check

        // At load, while paused: CheckVisits visits back to back (the full day's work: BeginDay, Visit, EndDay, Scalars), every
        // CheckCaptureEvery-th with the capture. Nothing in the measured loop may allocate (no logging, no formatting): the numbers are
        // read before and after it. PASS: 0 collections and at most 1 KB a visit on average. A collection may be the game's own (it
        // allocates for its UI even paused), so a try with a collection is repeated, up to MaxTries. FAIL only if a collection recurs in
        // every try or a try without one shows over 10 KB a visit; anything between is reported as BETWEEN, never rounded to PASS.
        static void AllocationCheck(string dayStatus)
        {
            string verdict = null;
            int tries = 0;
            int bestTry = 0;                // the try with no collection and the lowest bytes per visit (0 = none)
            double bestPerVisit = 0.0;
            for (int attempt = 1; attempt <= MaxTries && verdict == null; attempt++)
            {
                tries = attempt;
                long plainSum = 0, plainMax = 0, capSum = 0, capMax = 0, plainMaxAll = 0, capMaxAll = 0;
                int plainN = 0, capN = 0, plainHit = 0, capHit = 0;
                int gcBefore = GC.CollectionCount(0);
                long memBefore = GC.GetTotalMemory(false);
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 1; i <= CheckVisits; i++)
                {
                    bool cap = i % CheckCaptureEvery == 0;
                    int g0 = GC.CollectionCount(0);
                    long v0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    hasher.BeginDay(snapshot, cap ? capture : null);
                    StateReader.Visit(hasher);
                    hasher.EndDay();
                    StateReader.Scalars(snapshot);
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - v0;
                    bool hit = GC.CollectionCount(0) != g0;
                    if (cap) { capN++; capSum += dt; if (dt > capMaxAll) capMaxAll = dt; if (hit) capHit++; else if (dt > capMax) capMax = dt; }
                    else { plainN++; plainSum += dt; if (dt > plainMaxAll) plainMaxAll = dt; if (hit) plainHit++; else if (dt > plainMax) plainMax = dt; }
                }
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                long memAfter = GC.GetTotalMemory(false);
                int gcAfter = GC.CollectionCount(0);

                int collections = gcAfter - gcBefore;
                long growth = memAfter - memBefore;
                double perVisit = growth / (double)CheckVisits;
                string tryVerdict;
                if (collections > 0) tryVerdict = attempt < MaxTries ? "collection, repeat" : "collection in every try";
                else if (perVisit <= PassBytesPerVisit) tryVerdict = "PASS";
                else if (perVisit > FailBytesPerVisit) tryVerdict = "FAIL";
                else tryVerdict = attempt < MaxTries ? "BETWEEN, repeat" : "BETWEEN";
                if (growth < 0) tryVerdict += " (note: negative growth; GetTotalMemory moves coarsely under Boehm, so a fall with no collection is not an allocation figure)";

                double freq = System.Diagnostics.Stopwatch.Frequency / 1000.0;   // ticks per ms
                var inv = CultureInfo.InvariantCulture;
                Log.Info("drift: allocation check try " + attempt + " of " + MaxTries + ": " + CheckVisits + " visits (" + capN + " with the capture) in " +
                         ((end - start) / freq).ToString("F0", inv) + " ms; collections (GC.CollectionCount(0)) " + gcBefore + " -> " + gcAfter + " (" + collections + "); " +
                         "GC.GetTotalMemory(false) " + memBefore + " -> " + memAfter + " (growth " + growth + " bytes, " + perVisit.ToString("F1", inv) + " bytes per visit); " +
                         "visit mean " + (plainN > 0 ? plainSum / freq / plainN : 0.0).ToString("F2", inv) + " ms, max " + (plainMax / freq).ToString("F2", inv) +
                         " ms not GC-hit (max of all " + (plainMaxAll / freq).ToString("F2", inv) + ", " + plainHit + " of " + plainN + " GC-hit); " +
                         "capture visit mean " + (capN > 0 ? capSum / freq / capN : 0.0).ToString("F2", inv) + " ms, max " + (capMax / freq).ToString("F2", inv) +
                         " ms not GC-hit (max of all " + (capMaxAll / freq).ToString("F2", inv) + ", " + capHit + " of " + capN + " GC-hit); " + tryVerdict);
                if (hasher.CaptureOverflow || hasher.SnapshotOverflow || StateReader.AreaFailures > 0)
                    Log.Warn("drift: allocation check try " + attempt + " saw an overflow or an area failure (capture " + hasher.CaptureOverflow + ", snapshot " +
                             hasher.SnapshotOverflow + ", failures " + StateReader.AreaFailures + ")");
                if (collections == 0)
                {
                    if (perVisit <= PassBytesPerVisit) { verdict = "PASS"; bestTry = attempt; bestPerVisit = perVisit; }
                    else if (perVisit > FailBytesPerVisit) { verdict = "FAIL"; bestTry = attempt; bestPerVisit = perVisit; }
                    else if (bestTry == 0 || perVisit < bestPerVisit) { bestTry = attempt; bestPerVisit = perVisit; }   // BETWEEN: keep the best, try again
                }
            }
            // No PASS or FAIL try: a BETWEEN try (the best of them gives the verdict), or a collection in every try.
            if (verdict == null) verdict = bestTry > 0 ? "BETWEEN" : "FAIL";
            CheckVerdict = verdict;
            string summary = "drift: allocation check " + verdict + " after " + tries + " " + (tries == 1 ? "try" : "tries") +
                             (bestTry > 0 ? " (verdict from the best try, try " + bestTry + ": " + bestPerVisit.ToString("F1", CultureInfo.InvariantCulture) + " bytes per visit, no collection"
                                          : " (a collection in every try") +
                             "; PASS needs 0 collections and at most " + (int)PassBytesPerVisit + " bytes; FAIL a collection in every try or over " + (int)FailBytesPerVisit + " bytes)";
            if (verdict == "PASS") Log.Info(summary);
            else if (verdict == "BETWEEN") Log.Warn(summary);
            else Log.Error(summary);
            Status = dayStatus + "; alloc check " + verdict;
        }
    }
}
