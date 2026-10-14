using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // M3 Task 8: the recorder. At every day boundary (DriftProbe.OnDay, inside FixedUpdate) it fills a preallocated DaySnapshot through
    // StateReader and AreaHasher and hands it to DriftWriter's background thread; every CheckpointEvery-th day the same visit also fills
    // the preallocated ItemCapture, which the writer thread writes as a binary dump. The main thread does no file IO and formats
    // nothing per item or per day (the allocation budget, docs/research/gc.md): the only allocations in steady state are a new name
    // (one IdTable entry), an event, the 30-day log line, a WARN, and on a checkpoint the new dump's path string.
    // Everything is inside try/catch; MaxFailures failures in a row stop the recorder (one ERROR) and the game runs on.
    // Main thread only. Paths come from DriftPaths and are checked with DriftPaths.Inside before the writer sees them.
    public sealed class DriftRecorder
    {
        const long TicksPerDay = TimeSpan.TicksPerDay;
        public const int MaxFailures = 5;                                  // days that failed in a row before the recorder stops itself
        public const string StopQuit = "quit";                             // the reason of the normal end of a run (CostGate.StopQuit)
        const int SlowReadMicros = 20000, SlowCaptureMicros = 50000;       // the cost gate's read and capture-step limits
        const long SlowWarnEveryMs = 10000;                                // a slow-day WARN at most once per 10 real seconds
        const int CloseWaitMs = 2000;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Action<DayTick> DayHandler = HandleDay;            // made once: no delegate per call
        static readonly Action<Exception> ThrewHandler = HandleThrew;
        static DriftRecorder current;

        public static DriftRecorder Current { get { return current; } }

        public string Role { get { return role; } }
        public int LastDay { get { return lastDay; } }                     // the last day that reached the writer; -1 before day 0
        public string RecordPath { get { return recordPath; } }
        public bool Stopped { get { return stopped; } }

        readonly DriftWriter writer;
        readonly IdTable ids;
        readonly AreaHasher hasher;
        readonly ItemCapture capture;
        readonly Manager.TimeController tc;
        readonly string dataDir, role, recordPath;
        readonly DateTime started;
        readonly long loadTicks, startStamp;
        readonly bool aiOff = Boot.Dev != null && Boot.Dev.AiOff;
        readonly bool[] areaSeen = new bool[DriftAreas.All.Length];

        bool stopped, hooked;
        int lastDay = -1, failures, dumps, lastThrewDay = -1, lastAreaFailures;
        bool endgameSent;
        bool warnedDrop, warnedOverflow, warnedCaptureOverflow, warnedMisaligned, warnedCap, warnedWriter;
        long lastFrameStamp, lastSlowWarnMs = -SlowWarnEveryMs;
        int frameMaxMs;

        // the 30-day window (day 0 and the checkpoint reads are in it; day 0 is not)
        int winReads, winHits, winMaxMicros, winFrameMaxMs;
        long winMicros, winAtTicks0, winRealMs0;

        // the latest dump, reported at the next checkpoint and at stop (never formatted on the writer thread)
        string dumpPath; int dumpDay, dumpItems, dumpMicros; bool dumpHit, dumpTruncated;

        DriftRecorder(Manager.TimeController tc, DriftWriter writer, IdTable ids, AreaHasher hasher, ItemCapture capture, string dataDir, string role,
                      string recordPath, DateTime started, long loadTicks)
        {
            this.tc = tc; this.writer = writer; this.ids = ids; this.hasher = hasher; this.capture = capture; this.dataDir = dataDir;
            this.role = role; this.recordPath = recordPath; this.started = started; this.loadTicks = loadTicks;
            startStamp = Stopwatch.GetTimestamp();
        }

        // ------------------------------------------------------------------ start

        // The role of this game's record: host, client or solo.
        public static string RoleOf(DevFlags f)
        {
            if (f != null && f.Host) return "host";
            if (f != null && (f.JoinCode != null || f.JoinFile != null)) return "client";
            return "solo";
        }

        // Opens the record (the header written, the writer thread started) and returns the recorder, or logs why not and returns null.
        // It refuses when the probe is not installed, when the drift folder is over 1 GiB, or when the path is not under the drift
        // folder. Allocates the writer's two DaySnapshots (the same size as `shape`) and its rings once. Nothing is recorded yet:
        // RecordDayZero does that, and starts the hook.
        public static DriftRecorder Create(Manager.TimeController tc, string date, string saveHash, IdTable ids, AreaHasher hasher, ItemCapture capture, DaySnapshot shape)
        {
            string dataDir = Boot.DataDir;
            if (!GameHooks.IsInstalled(HookPlan.DriftProbe)) { Log.Error("drift: not started: the day probe (DriftProbe) is not installed"); return null; }
            if (dataDir == null || shape == null) { Log.Error("drift: not started: no data folder"); return null; }
            long folder = DriftPaths.FolderBytes(dataDir);
            if (folder > DriftPaths.MaxFolderBytes)
            {
                Log.Error("drift: not started: the drift folder holds " + (folder == long.MaxValue ? "an unknown number of" : folder.ToString(Inv)) + " bytes (recording refuses to start above " +
                          DriftPaths.MaxFolderBytes + "); move the old files out first");
                return null;
            }
            DateTime started = DateTime.Now;
            string role = RoleOf(Boot.Dev);
            string path = DriftPaths.RecordFile(dataDir, role, started);
            if (path == null || !DriftPaths.Inside(dataDir, path)) { Log.Error("drift: not started: the record path is not under the drift folder (" + (path ?? "no path") + ")"); return null; }

            var h = new DriftHeader();
            h.Format = DriftFile.Format; h.Profile = Boot.Profile ?? ""; h.Role = role; h.ModVersion = ModInfo.Version; h.GameBuild = Session.GameBuildId ?? "";
            h.SaveHash = saveHash ?? "unknown"; h.Flags = DriftWorld.Flags ?? ""; h.Skipped = StateReader.Skipped ?? "";
            h.StartedLocal = started.ToString("yyyy-MM-dd HH:mm:ss", Inv); h.LoadTicks = Drift.LoadTicks; h.Agencies = StateReader.Agencies;

            var writer = new DriftWriter(path, h, shape.Blocks.Length, shape.Fields.Length, shape.Scalars.Length, DriftPaths.MaxRecordBytes);
            if (writer.Error != null)
            {
                Log.Error("drift: not started: the record could not be opened (" + path + "): " + writer.Error);
                writer.Close(CloseWaitMs);
                return null;
            }
            var r = new DriftRecorder(tc, writer, ids, hasher, capture, dataDir, role, path, started, Drift.LoadTicks);
            current = r;
            Log.Info("drift: recording to " + path + " (role " + role + ", day 0 " + date + ", save " + h.SaveHash + ")");
            return r;
        }

        // Day 0, taken at save loaded by Drift (one visit, with the capture, JIT included): copied into the writer's snapshot, the
        // names first, then the day, then the dump. Starts the probe's hook. `day0` is Drift's own snapshot and is not touched again
        // by the recorder; `capture` is handed to the writer, so Drift must wait for WaitCaptureFree before it reuses it.
        public void RecordDayZero(DaySnapshot day0)
        {
            try
            {
                DaySnapshot s = writer.Acquire();
                if (s == null) { Log.Error("drift: day 0 could not be recorded: " + (writer.Error ?? "no free snapshot")); Stop("failed"); return; }
                s.Head = day0.Head;
                s.Head.Day = 0; s.Head.KeyTicks = loadTicks; s.Head.RealMs = 0; s.Head.ElapsedDays = 0.0;
                s.BlockCount = day0.BlockCount; s.FieldCount = day0.FieldCount; s.ScalarCount = day0.ScalarCount;
                Array.Copy(day0.Blocks, s.Blocks, day0.BlockCount);
                Array.Copy(day0.Fields, s.Fields, day0.FieldCount);
                Array.Copy(day0.Scalars, s.Scalars, day0.ScalarCount);
                winAtTicks0 = s.Head.AtTicks; winRealMs0 = 0;
                Hand(s, capture, 0);
                SubmitAi(DriftEventKind.Ai, 0);                    // the rivals' trees at load: any above 0 fails the comparer's check
                lastDay = 0;
                lastFrameStamp = 0;
                DriftProbe.OnDay = DayHandler;
                DriftProbe.OnDayThrew = ThrewHandler;
                hooked = true;
            }
            catch (Exception e)
            {
                Log.Error("drift: day 0 could not be recorded: " + e);
                Stop("failed");
            }
        }

        // Waits (at load, while paused) until the writer thread has written the day-0 dump and freed the capture, so the allocation
        // check can reuse it. False when it did not become free in time.
        public bool WaitCaptureFree(int waitMs)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)waitMs * Stopwatch.Frequency / 1000;
            while (!writer.CaptureFree)
            {
                if (Stopwatch.GetTimestamp() > deadline) return false;
                Thread.Sleep(5);
            }
            return true;
        }

        // ------------------------------------------------------------------ every frame

        // The frame gap, nothing else: dumps are written by the writer thread.
        public void Update()
        {
            if (stopped) return;
            CheckEnd();                                        // a paused game that has ended must still be noted
            long now = Stopwatch.GetTimestamp();
            if (lastFrameStamp != 0)
            {
                int gap = (int)((now - lastFrameStamp) * 1000L / Stopwatch.Frequency);
                if (gap > frameMaxMs) frameMaxMs = gap;
                if (gap > winFrameMaxMs) winFrameMaxMs = gap;
            }
            lastFrameStamp = now;
        }

        // ------------------------------------------------------------------ each day

        static void HandleDay(DayTick t) { DriftRecorder r = current; if (r != null) r.OnDay(t); }

        static void HandleThrew(Exception e) { DriftRecorder r = current; if (r != null) r.OnThrew(e); }

        void OnDay(DayTick t)
        {
            if (stopped) return;
            try
            {
                long delta = t.KeyTicks - loadTicks;
                int day = (int)(delta / TicksPerDay);
                if (day <= lastDay) return;                        // a day already recorded
                if (delta % TicksPerDay != 0 && !warnedMisaligned)
                {
                    warnedMisaligned = true;
                    Log.Warn("drift: day " + day + " key " + t.KeyTicks + " is not the load time plus whole days");
                }
                if (writer.Error != null) { if (!warnedWriter) { warnedWriter = true; Log.Error("drift: the writer failed: " + writer.Error + " - recording stopped"); } Stop("failed"); return; }

                int g0 = GC.CollectionCount(0);                    // 1. the collections before, and the clock starts
                long t0 = Stopwatch.GetTimestamp();
                DaySnapshot s = writer.Acquire();                  // 2. a free snapshot, or the day is dropped (the writer counts it)
                if (s == null)
                {
                    if (!warnedDrop) { warnedDrop = true; Log.Warn("drift: day " + day + " dropped: no free snapshot (writer busy, capped or failed); later drops are counted only"); }
                    return;
                }
                bool checkpoint = day % DriftPaths.CheckpointEvery == 0;
                ItemCapture cap = null;
                if (checkpoint)                                    // 3. the capture, when the writer has finished with the last one
                {
                    if (dumps >= DriftPaths.MaxDumps)
                    {
                        if (!warnedCap) { warnedCap = true; Log.Warn("drift: dump skipped (cap: " + DriftPaths.MaxDumps + " dumps in a run); later checkpoints are counted only"); }
                        writer.NoteSkippedCapture();
                    }
                    else if (writer.CaptureFree) cap = capture;
                    else
                    {
                        writer.NoteSkippedCapture();
                        Log.Warn("drift: dump skipped (day " + day + ": writer busy with the last dump)");
                    }
                }
                hasher.BeginDay(s, cap);                           // 4. the visit; on a checkpoint it is also the capture copy
                StateReader.Visit(hasher);
                hasher.EndDay();
                StateReader.Scalars(s);
                long at = tc.CurrentTime.Ticks;
                long nowStamp = Stopwatch.GetTimestamp();          // 5. the head
                s.Head.Day = day; s.Head.KeyTicks = t.KeyTicks; s.Head.AtTicks = t.AtTicks != 0L ? t.AtTicks : at; s.Head.ElapsedDays = t.ElapsedDays;
                s.Head.Scale = tc.CurrentTimeScale;
                s.Head.RealMs = (nowStamp - startStamp) * 1000L / Stopwatch.Frequency;
                s.Head.FrameMaxMs = frameMaxMs; frameMaxMs = 0;
                s.Head.Captured = (byte)(cap != null ? 1 : 0);
                int g1 = GC.CollectionCount(0);                    // 6. the clock stops, and the collections after
                int micros = (int)((nowStamp - t0) * 1000000L / Stopwatch.Frequency);
                s.Head.CostMicros = micros; s.Head.GcBefore = g0; s.Head.GcAfter = g1; s.Head.GcHit = (byte)(g0 != g1 ? 1 : 0);

                winReads++; winMicros += micros;
                if (g0 != g1) winHits++; else if (micros > winMaxMicros) winMaxMicros = micros;
                int limit = cap != null ? SlowCaptureMicros : SlowReadMicros;
                if (micros > limit) SlowDay(day, micros, g0 != g1, nowStamp);
                CheckVisit(day, cap != null);

                int areas = 0, items = 0;
                if (checkpoint) { Counts(s, out areas, out items); }
                int capItems = cap != null ? cap.ItemCount : 0;
                DayHead head = s.Head;

                if (checkpoint) LogDump();                         // the PREVIOUS dump's result (30 days on, long finished), before the new one is queued
                Hand(s, cap, day);                                 // 7. names first, then the day, then the capture
                lastDay = day;
                failures = 0;
                if (checkpoint) SubmitAi(DriftEventKind.AiCheck, day);     // every 30 days, outside the timed read
                CheckEnd();
                if (checkpoint) ThirtyDays(day, head, areas, items, cap != null, capItems);
            }
            catch (Exception e)
            {
                failures++;
                if (failures == 1 || failures >= MaxFailures) Log.Error("drift: day work failed (" + failures + " of " + MaxFailures + " in a row): " + e);
                if (failures >= MaxFailures) Stop("failed");
            }
        }

        // The rivals' trees, from DriftWorld's own count, as an `ai` (day 0) or `aicheck` (every 30 days) event: A = running, B = paused,
        // C = loading threads. Only with -semp-ai-off (with the AI on the trees are expected). -1 for all three when the count could not
        // be taken, or when an earlier check failed and this one reads clean: the comparer fails any value above 0 or unreadable, so a
        // run whose check broke cannot pass. One event per 30 days: an allowed allocation.
        void SubmitAi(byte kind, int day)
        {
            if (!aiOff) return;
            try
            {
                int total = DriftWorld.AiTreesRunning();
                int a = DriftWorld.AiRunning, b = DriftWorld.AiPaused, c = DriftWorld.AiLoading;
                if (total < 0 || (total == 0 && DriftWorld.AiCheckFailed)) { a = -1; b = -1; c = -1; }
                writer.SubmitEvent(new EventRecord { Day = day, Kind = kind, A = a, B = b, C = c, TextId = -1 });
            }
            catch (Exception e) { Log.Error("drift: could not record the AI check: " + e.Message); }
        }

        // The first time DriftWorld sees the end of the game (a run that reaches it is invalid): one `endgame` event, A = the last day.
        void CheckEnd()
        {
            if (endgameSent || !DriftWorld.EndOfGame) return;
            endgameSent = true;
            writer.SubmitEvent(new EventRecord { Day = lastDay < 0 ? 0 : lastDay, Kind = DriftEventKind.EndGame, A = lastDay, B = 0, C = 0, TextId = -1 });
        }

        // The names first (a name is on disk before any event or dump that uses its id), then the day, then the checkpoint's capture.
        // `s` belongs to the writer from Submit on, so nothing reads it after.
        void Hand(DaySnapshot s, ItemCapture cap, int day)
        {
            DayHead head = s.Head;
            int capItems = cap != null ? cap.ItemCount : 0;
            if (ids.Count != ids.PendingFrom) writer.SubmitNames(ids);
            writer.Submit(s);
            if (cap != null) SubmitDump(cap, day, head, capItems);
        }

        void SubmitDump(ItemCapture cap, int day, DayHead head, int items)
        {
            string path = DriftPaths.DumpFile(dataDir, started, day);
            if (path == null || !DriftPaths.Inside(dataDir, path))
            {
                writer.NoteSkippedCapture();
                Log.Error("drift: dump skipped (day " + day + ": the dump path is not under the drift folder)");
                return;
            }
            DumpHead d;
            d.Day = day; d.KeyTicks = head.KeyTicks; d.AtTicks = head.AtTicks; d.ItemCount = 0; d.ValueCount = 0; d.Truncated = 0;
            d.CaptureMicros = head.CostMicros; d.CaptureGcHit = head.GcHit;
            if (writer.SubmitCapture(cap, d, path))
            {
                dumps++;
                dumpPath = path; dumpDay = day; dumpItems = items; dumpMicros = head.CostMicros; dumpHit = head.GcHit != 0; dumpTruncated = cap.Truncated;
            }
            else Log.Warn("drift: dump skipped (day " + day + ": the writer refused it); counted as dropped");
        }

        // A day the visit could not trust is a warn event, once per kind: the comparer lists it as a warning.
        void CheckVisit(int day, bool captured)
        {
            if (hasher.SnapshotOverflow && !warnedOverflow)
            {
                warnedOverflow = true;
                writer.SubmitEvent(new EventRecord { Day = day, Kind = DriftEventKind.Warn, A = 1, B = 0, C = 0, TextId = -1 });
                Log.Warn("drift: day " + day + ": the snapshot ran out of room (the day is incomplete)");
            }
            if (captured && hasher.CaptureOverflow && !warnedCaptureOverflow)
            {
                warnedCaptureOverflow = true;
                writer.SubmitEvent(new EventRecord { Day = day, Kind = DriftEventKind.Cap, A = 2, B = 0, C = 0, TextId = -1 });
                Log.Warn("drift: day " + day + ": the capture ran out of room (the dump is marked truncated)");
            }
            int af = StateReader.AreaFailures;
            if (af != lastAreaFailures)
            {
                lastAreaFailures = af;
                writer.SubmitEvent(new EventRecord { Day = day, Kind = DriftEventKind.Warn, A = 3, B = af, C = 0, TextId = -1 });
                Log.Warn("drift: day " + day + ": the state reader has had " + af + " area failure(s) (see the ERROR lines above)");
            }
        }

        void SlowDay(int day, int micros, bool gcHit, long nowStamp)
        {
            long nowMs = nowStamp * 1000L / Stopwatch.Frequency;
            if (nowMs - lastSlowWarnMs < SlowWarnEveryMs) return;
            lastSlowWarnMs = nowMs;
            Log.Warn("drift: slow day " + day + ": " + (micros / 1000.0).ToString("F2", Inv) + " ms" + (gcHit ? " (GC-hit)" : ""));
        }

        void Counts(DaySnapshot s, out int areas, out int items)
        {
            Array.Clear(areaSeen, 0, areaSeen.Length);
            areas = 0; items = 0;
            for (int i = 0; i < s.BlockCount; i++)
            {
                int a = s.Blocks[i].Area;
                if (a >= 0 && a < areaSeen.Length && !areaSeen[a]) { areaSeen[a] = true; areas++; }
                items += s.Blocks[i].Count;
            }
        }

        // The 30-day line (an allowed allocation), then the window starts again.
        void ThirtyDays(int day, DayHead head, int areas, int items, bool captured, int capItems)
        {
            double realSeconds = (head.RealMs - winRealMs0) / 1000.0, gameSeconds = (head.AtTicks - winAtTicks0) / 1e7;
            double rate = realSeconds > 0.0 ? gameSeconds / realSeconds : 0.0;
            double mean = winReads > 0 ? winMicros / (double)winReads / 1000.0 : 0.0;
            Log.Info("drift: day " + day + " (" + new DateTime(head.AtTicks).ToString("yyyy-MM-dd", Inv) + ") " + areas + " areas, " + items + " items, " +
                     (head.CostMicros / 1000.0).ToString("F2", Inv) + " ms" + (captured ? " (capture step, " + capItems + " captured)" : "") +
                     "; last 30 days mean " + mean.ToString("F2", Inv) + " max " + (winMaxMicros / 1000.0).ToString("F2", Inv) + " (not GC-hit), GC-hit reads " + winHits +
                     "; rate " + rate.ToString("F0", Inv) + " game s per real s; frame max " + winFrameMaxMs + " ms; gen0 " + head.GcAfter + "; dropped " + writer.Dropped);
            Drift.SetStatus("drift: recording, day " + day + ", mean " + mean.ToString("F1", Inv) + " ms, " + writer.Dropped + " dropped");
            winReads = 0; winHits = 0; winMaxMicros = 0; winMicros = 0; winFrameMaxMs = 0;
            winAtTicks0 = head.AtTicks; winRealMs0 = head.RealMs;
        }

        // The last dump's result: from the file the writer thread wrote, never formatted over there.
        void LogDump()
        {
            string path = dumpPath;
            if (path == null) return;
            dumpPath = null;
            long bytes = -1;
            try { var fi = new FileInfo(path); if (fi.Exists) bytes = fi.Length; } catch (Exception) { }
            if (bytes < 0) Log.Warn("drift: dump of day " + dumpDay + " not found (" + path + ")");
            else Log.Info("drift: dump written " + Path.GetFileName(path) + " (day " + dumpDay + ", " + dumpItems + " items, " + bytes + " bytes, capture " +
                          (dumpMicros / 1000.0).ToString("F2", Inv) + " ms" + (dumpHit ? ", GC-hit" : "") + (dumpTruncated ? ", truncated" : "") + ")");
        }

        // A day handler threw: the postfix did not run, the day is re-run by the game and recorded once when it completes. One event and
        // one WARN per day N.
        void OnThrew(Exception ex)
        {
            if (stopped) return;
            try
            {
                int day = (int)((DriftProbe.PrevNextTicks - loadTicks) / TicksPerDay);
                if (day == lastThrewDay) return;
                lastThrewDay = day;
                string type = ex.GetType().Name;
                bool isNew;
                int id = ids.IdOf(type, out isNew);
                if (ids.Count != ids.PendingFrom) writer.SubmitNames(ids);
                writer.SubmitEvent(new EventRecord { Day = day, Kind = DriftEventKind.DayThrew, A = 0, B = 0, C = 0, TextId = id });
                Log.Warn("drift: day event threw " + type + " on day " + day);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ stop

        // Ends the record: a `stop` event with the reason ("quit" at the normal end), a `dropped` event and a WARN if anything was
        // dropped, then Close(2000), which drains the writer and writes the End record. Safe to call twice.
        public void Stop(string why)
        {
            if (stopped) return;
            stopped = true;
            try
            {
                if (hooked)
                {
                    if (DriftProbe.OnDay == DayHandler) DriftProbe.OnDay = null;
                    if (DriftProbe.OnDayThrew == ThrewHandler) DriftProbe.OnDayThrew = null;
                }
                bool isNew;
                int reason = ids.IdOf(why ?? "unknown", out isNew);
                if (ids.Count != ids.PendingFrom) writer.SubmitNames(ids);
                int dropped = writer.Dropped;
                writer.SubmitEvent(new EventRecord { Day = lastDay < 0 ? 0 : lastDay, Kind = DriftEventKind.Stop, A = lastDay, B = dropped, C = 0, TextId = reason });
                if (dropped > 0)
                {
                    writer.SubmitEvent(new EventRecord { Day = lastDay < 0 ? 0 : lastDay, Kind = DriftEventKind.Dropped, A = dropped, B = 0, C = 0, TextId = -1 });
                    Log.Warn("drift: writer dropped " + dropped + " (days, events, captures) (" + Path.GetFileName(recordPath) + ")");
                }
                writer.Close(CloseWaitMs);
                LogDump();
                if (writer.Error != null) Log.Error("drift: the writer reported an error: " + writer.Error);
                if (writer.Capped) Log.Warn("drift: the record reached its size cap (" + DriftPaths.MaxRecordBytes + " bytes)");
                Log.Info("drift: record ended (" + (why ?? "unknown") + ") at day " + lastDay + ", " + writer.Bytes + " bytes, dropped " + writer.Dropped + " (" + Path.GetFileName(recordPath) + ")");
            }
            catch (Exception e) { Log.Error("drift: stop failed: " + e); }
        }
    }
}
