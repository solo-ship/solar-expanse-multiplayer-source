using System;

namespace SolarExpanseMultiplayer
{
    // The host's sender for one member (DD8): saves can come every 30 s at 200000, so a transfer never queues behind another. One
    // runs at a time; Offer while one is queued or running replaces it (newest only). The glue pumps the active SnapshotSender
    // behind the command stream, so play is never held up.
    public sealed class SaveCopy
    {
        public const int EndCeilingMs = 120000;

        string queuedId; byte[] queuedGz;
        string activeId; byte[] activeGz; SnapshotSender active;
        public int Superseded { get; private set; }   // transfers replaced before they finished

        public bool Running { get { return active != null; } }
        public bool Queued { get { return queuedGz != null; } }
        public string ActiveSaveId { get { return activeId; } }
        public string QueuedSaveId { get { return queuedId; } }
        public SnapshotSender Sender { get { return active; } }

        // A newer save replaces a queued one, and a running one (which is dropped; the next TryStart begins the newer).
        public void Offer(string saveId, byte[] gz, long nowMs)
        {
            if (!SaveNames.IsSaveId(saveId) || gz == null || gz.Length < 1 || gz.Length > JoinLimits.MaxCompressed) return;
            if (queuedGz != null) Superseded++;
            queuedId = saveId; queuedGz = gz;
            if (active != null) { Superseded++; active = null; activeGz = null; activeId = null; }
        }

        // Starts the queued save when nothing is running. True: Sender, ActiveSaveId and the bytes (Gz) are the new transfer.
        public bool TryStart(long nowMs)
        {
            if (active != null || queuedGz == null) return false;
            activeId = queuedId; activeGz = queuedGz;
            queuedId = null; queuedGz = null;
            active = new SnapshotSender(activeGz, nowMs);
            return true;
        }

        public byte[] ActiveGz { get { return activeGz; } }

        // The run ended (every chunk acked, or the link dropped): the next queued save may start.
        public void Finish()
        {
            active = null; activeGz = null; activeId = null;
        }

        // The deadline for the END of a session's last copy: the join's scaling (the transfer at TotalBytesPerSecond plus the margin,
        // but not the join's 300 s floor, which alone is over the ceiling), at most 120 s.
        public long EndDeadlineMs(long bytes)
        {
            long b = bytes < 0 ? 0 : bytes > JoinLimits.MaxCompressed ? JoinLimits.MaxCompressed : bytes;
            return Math.Min(b * 1000 / JoinLimits.TotalBytesPerSecond + JoinLimits.TotalMarginMs, EndCeilingMs);
        }
    }

    // The client's receiver for a save copy: holds the bytes, writes nothing outside incoming\ (DD8). One copy at a time; a new
    // header replaces a half-received one. Chunks arrive in order and are acked as counts; the hash is checked at the end.
    public sealed class SaveCopyReceiver
    {
        readonly IncomingStore store;
        readonly Func<string> rosterSaveId;
        SaveCopyHeader header;
        byte[] buf;
        int received; long bytes;

        public SaveCopyReceiver(IncomingStore store, Func<string> rosterSaveId) { this.store = store; this.rosterSaveId = rosterSaveId; }

        public int Received { get { return received; } }
        public bool Receiving { get { return header != null; } }

        // null = taken (chunks may follow), else a fixed reason (nothing held). A header whose SaveId is not the roster's is refused.
        public string OnHeader(SaveCopyHeader h)
        {
            Reset();
            if (h == null) return "no header";
            string why = h.Validate();
            if (why != null) return why;
            string roster = rosterSaveId == null ? null : rosterSaveId();
            if (!SaveNames.IsSaveId(roster) || !string.Equals(roster, h.SaveId, StringComparison.Ordinal)) return "the copy is from another session";
            header = h;
            buf = new byte[h.CompressedSize];
            return null;
        }

        // The chunk at index, in order. Returns null while more is expected or when the copy is complete and held, "" never; else the
        // reason (the receiver resets). complete tells the copy is held.
        public string OnChunk(int index, byte[] data, int offset, int count, out bool complete)
        {
            complete = false;
            if (header == null) return "no copy is expected";
            if (index != received || index >= header.Chunks) { Reset(); return "a chunk came out of order"; }
            int want = ChunkMath.Length(header.CompressedSize, index);
            if (count != want || data == null || offset < 0 || offset > data.Length - count) { Reset(); return "a chunk has the wrong size"; }
            Buffer.BlockCopy(data, offset, buf, (int)bytes, count);
            bytes += count; received++;
            if (received < header.Chunks) return null;
            SaveCopyHeader h = header; byte[] gz = buf;
            Reset();
            string why = store.Put(h.SaveId, gz, h.Sha256, h.Info, h.LastHolders, h.Title, h.HostName);
            if (why != null) return why;
            complete = true;
            return null;
        }

        public void Reset() { header = null; buf = null; received = 0; bytes = 0; }
    }
}
