using System;
using System.IO;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // ONE background thread writes the record AND every dump, so the main thread does no file IO and formats nothing.
    // It allocates nothing per day either (a reused buffer, one Write call per record).
    //
    // Hand-off. The main thread owns a DaySnapshot between Acquire and Submit; after Submit only the writer thread touches
    // it, until it has been written and goes back to the free pair. Up to TWO days can be in flight (one being written,
    // one queued); a third Acquire returns null and the day is dropped and counted: never a block, never an allocation.
    // The capture is handed over the same way (SubmitCapture / CaptureFree). Names and events cross threads only through
    // the fixed rings (SubmitNames, SubmitEvent). The IdTable stays on the main thread; the writer thread never sees it.
    //
    // Order. Each wake-up writes, in this order: names, events, the queued days, then a queued capture. A name is therefore
    // on disk before the events, and the dump, that use it and are submitted after it (day records carry no name ids, so
    // nothing in a day depends on the order). Call SubmitNames BEFORE SubmitEvent and SubmitCapture.
    //
    // Threads. Every counter and ring index changes under one lock (`gate`), which is never held across IO; Dropped,
    // Bytes, Error and Capped are safe to read from any thread. A cap on the record (maxBytes) stops writing and sets
    // Capped; the End record always fits (a few bytes are kept back). Everything the cap refuses (a day, an event, a name,
    // and each day Acquire then refuses) is counted in Dropped, so the End record carries it and a capped run fails the
    // "nothing dropped" gate (cloud review D-2). A dump that cannot be written is counted too and costs only that dump (D-1). A dump has its own cap (DriftPaths.MaxDumpBytes).
    // Dump count (DriftPaths.MaxDumps) is the recorder's rule, not enforced here.
    public sealed class DriftWriter : IDisposable
    {
        public const int EventRing = 256, NameRing = 4096;
        const int EndReserve = 16;

        struct NameEntry { public int Id; public string Name; }

        readonly object gate = new object();
        readonly BinaryWriter bw;
        readonly DriftHeader header;
        readonly string recordFileName;
        readonly string recordDir;          // the record's own folder: a dump must lie inside it (null only for the tests' stream writers)
        readonly long maxBytes;
        readonly Thread thread;

        // snapshots: 0 free, 1 acquired (the main thread fills it), 2 queued, 3 being written
        readonly DaySnapshot[] snaps = new DaySnapshot[2];
        readonly byte[] snapState = new byte[2];
        readonly int[] queue = new int[2];
        int qHead, queueCount;

        readonly EventRecord[] events = new EventRecord[EventRing];
        int evHead, evCount;
        readonly NameEntry[] nameRing = new NameEntry[NameRing];
        int nmHead, nmCount;

        ItemCapture capture; DumpHead captureHead; string capturePath;
        bool captureQueued, captureWriting;
        bool closing;
        volatile string error;
        volatile bool capped;
        long bytes;
        int dropped;
        int lastDay = -1;

        [ThreadStatic] static string openError;

        // Opens the record file (creating its folder) and writes the header on this thread, then starts the writer thread.
        // Allocates the two DaySnapshots, the rings and the buffers once. Never throws: a failure sets Error.
        public DriftWriter(string path, DriftHeader header, int maxBlocks, int maxFields, int maxScalars, long maxBytes)
            : this(OpenOrNull(path), FileNameOrEmpty(path), DirOrNull(path), header, maxBlocks, maxFields, maxScalars, maxBytes)
        {
        }

        // [add] Over any stream (the tests' slow, blocking and failing fakes). The record file name goes into every dump head.
        // Never throws, whatever the arguments: a null header or a negative size starts the writer in the Error state (Error
        // set, every Submit false), and the stream it was given is closed.
        internal DriftWriter(Stream stream, string recordFileName, DriftHeader header, int maxBlocks, int maxFields, int maxScalars, long maxBytes)
            : this(stream, recordFileName, null, header, maxBlocks, maxFields, maxScalars, maxBytes)
        {
        }

        // recordDir: where the record lives; SubmitCapture refuses a dump path outside it. null (the stream writers of the tests)
        // checks only the extension.
        internal DriftWriter(Stream stream, string recordFileName, string recordDir, DriftHeader header, int maxBlocks, int maxFields, int maxScalars, long maxBytes)
        {
            this.recordDir = recordDir;
            this.header = header; this.recordFileName = recordFileName ?? ""; this.maxBytes = maxBytes;
            try
            {
                if (header == null) throw new ArgumentNullException("header");
                snaps[0] = new DaySnapshot(maxBlocks, maxFields, maxScalars);       // throws for a negative size
                snaps[1] = new DaySnapshot(maxBlocks, maxFields, maxScalars);
            }
            catch (Exception e)
            {
                error = Describe(e);
                snaps[0] = null; snaps[1] = null;
                if (stream != null) { try { stream.Close(); } catch (Exception) { } }
                return;
            }
            if (stream == null) { error = openError ?? "the record file could not be opened"; return; }
            try
            {
                bw = new BinaryWriter(stream);
                int n = DriftFile.EncodeRecordStart(header);
                DriftFile.Emit(bw, n);
                bw.Flush();
                Interlocked.Exchange(ref bytes, n);
                if (n + EndReserve > maxBytes) capped = true;
                // Kept only once it has started: Close/Dispose join `thread`, and joining an unstarted thread throws.
                var t = new Thread(Run);
                t.IsBackground = true;
                t.Name = "semp-drift-writer";
                t.Start();
                thread = t;
            }
            catch (Exception e)
            {
                error = Describe(e);
                try { stream.Close(); } catch (Exception) { }
            }
        }

        static Stream OpenOrNull(string path)
        {
            openError = null;
            try
            {
                if (string.IsNullOrEmpty(path)) { openError = "no record path"; return null; }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
            }
            catch (Exception e) { openError = Describe(e); return null; }
        }

        static string DirOrNull(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path)); } catch (Exception) { return null; }
        }

        static string FileNameOrEmpty(string path)
        {
            try { return path == null ? "" : Path.GetFileName(path); } catch (Exception) { return ""; }
        }

        static string Describe(Exception e) { return e.GetType().Name + ": " + e.Message; }

        // ---- main thread ----

        // A free snapshot to fill (cleared), or null when both are in flight (the day is dropped and counted), or when the
        // record has hit its cap (the day is counted too), or when the writer has failed or is closing (nothing is counted then). A snapshot acquired but never submitted
        // is handed out again.
        public DaySnapshot Acquire()
        {
            lock (gate)
            {
                if (error != null || closing) return null;
                if (capped) { dropped++; return null; }          // the record is full: every day offered from now on is counted (the End record says so)
                for (int i = 0; i < 2; i++) if (snapState[i] == 1) { snaps[i].Clear(); return snaps[i]; }
                for (int i = 0; i < 2; i++) if (snapState[i] == 0) { snapState[i] = 1; snaps[i].Clear(); return snaps[i]; }
                dropped++;
                return null;
            }
        }

        // Hands the snapshot to the writer thread. False (and the snapshot is freed) when the writer has failed, is closing
        // or has reached its cap; false for a snapshot that was not acquired from this writer.
        public bool Submit(DaySnapshot s)
        {
            lock (gate)
            {
                int i = s == snaps[0] ? 0 : (s == snaps[1] ? 1 : -1);
                if (i < 0 || snapState[i] != 1) return false;
                if (error != null || closing || capped) { snapState[i] = 0; return false; }
                snapState[i] = 2;
                queue[(qHead + queueCount) & 1] = i;
                queueCount++;
                Monitor.Pulse(gate);
                return true;
            }
        }

        // Into the fixed ring; a full ring drops the event and counts it.
        public bool SubmitEvent(EventRecord e)
        {
            lock (gate)
            {
                if (error != null || closing) return false;
                if (evCount == EventRing) { dropped++; return false; }
                events[evHead] = e;
                evHead = (evHead + 1) % EventRing;
                evCount++;
                Monitor.Pulse(gate);
                return true;
            }
        }

        // MAIN THREAD: copies the pending (id, existing string reference) pairs, names.PendingFrom up to names.Count, into
        // the preallocated name ring (no allocation: the strings are the game's own) and calls names.MarkWritten itself.
        // The writer thread writes names ONLY from that ring. A full ring drops the rest and counts each (Dropped > 0 fails
        // the "nothing dropped" gate, so an overflow fails closed). Call it after the day-0 visit (the whole table) and
        // whenever a visit added a name, and BEFORE the SubmitEvent and SubmitCapture that use them. NameRing is 4096
        // because the spike's dumps held 266 distinct strings at day 0 and 316 at day 390 (about 13 times less).
        public bool SubmitNames(IdTable names)
        {
            if (names == null) return false;
            int from = names.PendingFrom, to = names.Count;
            if (from >= to) return true;
            bool ok = true;
            lock (gate)
            {
                if (error != null || closing) return false;
                for (int id = from; id < to; id++)
                {
                    if (nmCount == NameRing) { dropped++; ok = false; continue; }
                    nameRing[nmHead].Id = id;
                    nameRing[nmHead].Name = names.NameOf(id);
                    nmHead = (nmHead + 1) % NameRing;
                    nmCount++;
                }
                Monitor.Pulse(gate);
            }
            names.MarkWritten(to);
            return ok;
        }

        // Hands the capture to the writer thread, which writes it as the file dumpPath and then frees it. False (and
        // counted as a drop) while the previous capture is still queued or being written: the checkpoint is skipped.
        // Until CaptureFree is true again the capture must not be touched.
        public bool SubmitCapture(ItemCapture c, DumpHead d, string dumpPath)
        {
            lock (gate)
            {
                if (c == null || string.IsNullOrEmpty(dumpPath) || error != null || closing) return false;
                if (!DumpPathOk(dumpPath)) { dropped++; return false; }                // not ours to write: refused, and counted
                if (captureQueued || captureWriting) { dropped++; return false; }
                capture = c; captureHead = d; capturePath = dumpPath;
                captureQueued = true;
                Monitor.Pulse(gate);
                return true;
            }
        }

        // A dump is a .semp-dump file inside the record's own folder (a path that came from anywhere else is never written).
        bool DumpPathOk(string dumpPath)
        {
            try
            {
                if (!string.Equals(Path.GetExtension(dumpPath), ".semp-dump", StringComparison.OrdinalIgnoreCase)) return false;
                return recordDir == null || PathRules.IsInside(recordDir, dumpPath);
            }
            catch (Exception) { return false; }
        }

        public bool CaptureFree { get { lock (gate) { return !captureQueued && !captureWriting; } } }

        // [add] For a checkpoint skipped WITHOUT a SubmitCapture call (the capture was not free, so it was not filled): counts the
        // skip in Dropped, so the End record and the "nothing dropped" gate see it. Never call SubmitCapture with a capture that
        // was not filled this day: if the writer has just freed it, the stale contents would be written as a new dump.
        public void NoteSkippedCapture()
        {
            lock (gate) { dropped++; }
        }

        public bool Capped { get { return capped; } }
        public string Error { get { return error; } }
        public long Bytes { get { return Interlocked.Read(ref bytes); } }
        public int Dropped { get { return Volatile.Read(ref dropped); } }   // days, events, names and captures, a dump that failed to write, and everything the cap refuses; Dropped > 0: an E warn event and a WARN at stop

        // Drains everything submitted so far, writes the End record, flushes and closes. Waits up to waitMs for the writer
        // thread; if it is still busy then, Error says so (the thread is left to finish and close the file).
        public void Close(int waitMs)
        {
            lock (gate)
            {
                if (!closing) { closing = true; Monitor.PulseAll(gate); }
            }
            Thread t = thread;
            if (t != null && !t.Join(waitMs < 0 ? 0 : waitMs) && error == null) error = "the writer did not finish within " + waitMs + " ms";
        }

        public void Dispose() { Close(2000); }

        // ---- the writer thread ----

        void Run()
        {
            try
            {
                DriftFile.PrepareThread();
                while (true)
                {
                    int nNames, nEvents, nDays, nameTail, eventTail;
                    bool doCapture;
                    lock (gate)
                    {
                        while (nmCount == 0 && evCount == 0 && queueCount == 0 && !captureQueued && !closing) Monitor.Wait(gate);
                        if (nmCount == 0 && evCount == 0 && queueCount == 0 && !captureQueued) break;   // closing, and nothing left
                        nNames = nmCount; nEvents = evCount; nDays = queueCount; doCapture = captureQueued;
                        nameTail = (nmHead - nmCount + NameRing) % NameRing;
                        eventTail = (evHead - evCount + EventRing) % EventRing;
                        if (doCapture) { captureQueued = false; captureWriting = true; }
                    }
                    WriteNames(nameTail, nNames);
                    WriteEvents(eventTail, nEvents);
                    for (int k = 0; k < nDays; k++) WriteOneDay();
                    if (doCapture) WriteCapture();
                    bw.Flush();
                }
                int n = DriftFile.EncodeEnd(lastDay, Dropped);
                DriftFile.Emit(bw, n);
                Interlocked.Add(ref bytes, n);
                bw.Flush();
            }
            catch (Exception e) { error = Describe(e); }
            finally
            {
                try { bw.Close(); } catch (Exception e) { if (error == null) error = Describe(e); }
            }
        }

        // True when n more bytes fit under the cap (and keep the End record's room); otherwise the cap is reached.
        bool Room(int n)
        {
            if (capped || Interlocked.Read(ref bytes) + n + EndReserve > maxBytes)
            {
                capped = true;
                lock (gate) { dropped++; }                       // a refusal is a drop: not silent (D-2)
                return false;
            }
            return true;
        }

        void WriteNames(int tail, int n)
        {
            if (n == 0) return;
            for (int k = 0; k < n; k++)
            {
                int i = (tail + k) % NameRing;
                int len = DriftFile.EncodeName(nameRing[i].Id, nameRing[i].Name);
                nameRing[i].Name = null;
                if (Room(len)) { DriftFile.Emit(bw, len); Interlocked.Add(ref bytes, len); }
            }
            lock (gate) { nmCount -= n; }
        }

        void WriteEvents(int tail, int n)
        {
            if (n == 0) return;
            for (int k = 0; k < n; k++)
            {
                int len = DriftFile.EncodeEvent(events[(tail + k) % EventRing]);
                if (Room(len)) { DriftFile.Emit(bw, len); Interlocked.Add(ref bytes, len); }
            }
            lock (gate) { evCount -= n; }
        }

        void WriteOneDay()
        {
            DaySnapshot s; int idx;
            lock (gate) { idx = queue[qHead]; snapState[idx] = 3; s = snaps[idx]; }
            int len = DriftFile.EncodeDay(s);
            if (Room(len)) { DriftFile.Emit(bw, len); Interlocked.Add(ref bytes, len); lastDay = s.Head.Day; }
            lock (gate) { snapState[idx] = 0; qHead = (qHead + 1) & 1; queueCount--; }
        }

        void WriteCapture()
        {
            ItemCapture c; DumpHead h; string path;
            lock (gate) { c = capture; h = captureHead; path = capturePath; }
            try
            {
                bool created = false;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096))
                    using (var w = new BinaryWriter(fs))
                    {
                        created = true;
                        DriftFile.WriteDump(w, header, recordFileName, h, c);
                        w.Flush();
                    }
                }
                catch (Exception)
                {
                    // One dump that cannot be written (folder gone, disk full, a lock) costs that dump, not the record (D-1):
                    // count it, keep recording, and remove the partial file only if THIS call created it (a file that was
                    // already there and could not be opened is not ours to delete).
                    lock (gate) { dropped++; }
                    if (created) { try { File.Delete(path); } catch (Exception) { } }
                }
            }
            finally
            {
                lock (gate) { captureWriting = false; capture = null; capturePath = null; }
            }
        }
    }
}
