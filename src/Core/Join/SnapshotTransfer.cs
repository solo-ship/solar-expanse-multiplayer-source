using System;
using System.Security.Cryptography;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The snapshot transfer (plan "Interfaces, Transfer", Design decisions 5 and 6). The host cuts one compressed array into
    // 32 KiB chunks and sends at most a window of them ahead of the joiner's last ack (SnapshotSender, main thread). The joiner's
    // SnapshotReceiver is a sink that Net's Link calls on its READER thread: it arms itself there, takes the header and the chunks,
    // checks every wall and acks by itself, so a long main-thread frame can never starve the window or race the header.

    // The arithmetic of cutting a snapshot into chunks: every chunk but the last is exactly JoinLimits.ChunkSize.
    public static class ChunkMath
    {
        // Chunks needed for size bytes (0 for an empty or negative size).
        public static int Count(long size)
        {
            if (size <= 0) return 0;
            if (size > (long)int.MaxValue * JoinLimits.ChunkSize) return int.MaxValue;
            return (int)((size + JoinLimits.ChunkSize - 1) / JoinLimits.ChunkSize);
        }

        // The length chunk index must have (0 when the index is outside 0..Count-1).
        public static int Length(long size, int index)
        {
            if (index < 0 || index >= Count(size)) return 0;
            long left = size - (long)index * JoinLimits.ChunkSize;
            return (int)Math.Min(JoinLimits.ChunkSize, left);
        }
    }

    public static class Sha256Hex
    {
        public static string Of(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            return Of(data, 0, data.Length);
        }

        // Lower-case hex of the SHA-256 of data[offset .. offset + count).
        public static string Of(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException("data");
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(data, offset, count);
            var sb = new StringBuilder(64);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // The host's side of one joiner's transfer: which chunk is next, how many may be in flight, and which acks are acceptable.
    // One per joiner, over the batch's one compressed array (nothing is copied here). Main thread only.
    public sealed class SnapshotSender
    {
        readonly byte[] data;
        readonly int count;
        readonly int ackBudget;
        int next, acked, acks;
        long lastProgressMs;

        public SnapshotSender(byte[] compressed, long startMs)
        {
            if (compressed == null || compressed.Length == 0) throw new ArgumentException("there is no snapshot to send");
            if (compressed.Length > JoinLimits.MaxCompressed) throw new ArgumentException("the snapshot is over the " + JoinLimits.MaxCompressed + " byte cap");
            data = compressed;
            count = ChunkMath.Count(compressed.Length);
            ackBudget = count / JoinLimits.AckEvery + 1;   // the honest sink sends one ack per AckEvery chunks and one on the last
            lastProgressMs = startMs;
        }

        public int Count { get { return count; } }
        public int Next { get { return next; } }          // chunks handed out so far
        public int Acked { get { return acked; } }        // chunks the joiner says it has
        public bool Done { get { return acked == count; } }
        public long LastProgressMs { get { return lastProgressMs; } }   // the start, then the time of the last good ack

        // Another chunk may go: some are left and fewer than window are in flight.
        public bool CanSend(int window) { return next < count && next - acked < window; }

        // The next chunk, as a slice of the batch's array (no copy). The caller checks CanSend first.
        public ArraySegment<byte> Take()
        {
            if (next >= count) throw new InvalidOperationException("every chunk has been handed out");
            int len = ChunkMath.Length(data.Length, next);
            var seg = new ArraySegment<byte>(data, next * JoinLimits.ChunkSize, len);
            next++;
            return seg;
        }

        // 0 = taken. Otherwise a JoinCodes value, and nothing changed: the ack goes backwards, does not advance, is past what was
        // sent, or is more acks than the honest sink could send (so a flood of acks costs the host nothing and ends in a violation).
        public int Ack(int nextReceived, long nowMs)
        {
            if (nextReceived <= acked) return JoinCodes.Protocol;       // backwards, or not advancing
            if (nextReceived > next) return JoinCodes.Protocol;         // more than was sent
            if (acks >= ackBudget) return JoinCodes.Protocol;           // over the ack budget
            acked = nextReceived;
            acks++;
            lastProgressMs = nowMs;
            return 0;
        }
    }

    // The joiner's sink. All of OnControl/OnHeader/OnChunk/Exempt run on the link's reader thread; the properties and Reset are for
    // the main thread. One lock guards the state; nothing slow (the hash, the ack, the extra header check) runs inside it.
    public sealed class SnapshotReceiver : IBulkSink
    {
        // The largest chunk frame on the wire: the 4-byte length prefix, the 6-byte envelope header, the 4-byte index, 32 KiB.
        const int MaxChunkFrame = 4 + Envelope.HeaderSize + 4 + JoinLimits.ChunkSize, MinChunkFrame = 4 + Envelope.HeaderSize + 4 + 1;

        readonly object gate = new object();
        readonly Func<bool> expecting;
        readonly string ownSaveVersion;
        readonly int ownPlayer;
        Func<Message, bool> sendAck;
        readonly Func<JoinHeader, string> headerCheck;
        bool armed, complete, finished;
        JoinHeader header;
        byte[] buffer;
        int received, errorCode, generation;
        long bytes;
        string error;

        // expecting: asked once for EVERY ClaimResult, Ok or not, on the reader thread: true only when that answer is
        // the first to a claim the joiner sent (the driver clears its claim as it answers), so only a ClaimResult(Ok)
        // that answers an outstanding claim arms the sink; an Ok after a refusal, or a second Ok, leaves it unarmed.
        // headerCheck: the driver's own header rules (Task 8's HeaderRules.Check(h, ownSaveVersion, ownPlayer): boundaries, seats, config
        // shape); null = ok, anything else refuses the header. REQUIRED: a receiver with no rules would fail open. Called on the reader
        // thread, outside the lock, after the receiver's own checks.
        public SnapshotReceiver(Func<bool> expecting, string ownSaveVersion, int ownPlayer, Func<JoinHeader, string> headerCheck)
        {
            if (expecting == null) throw new ArgumentNullException("expecting");
            if (headerCheck == null) throw new ArgumentNullException("headerCheck");
            this.headerCheck = headerCheck;
            this.expecting = expecting;
            this.ownSaveVersion = TextSanitiser.Clean(ownSaveVersion, 32, "?");   // JoinHeader.Validate clips SaveVersion the same way
            this.ownPlayer = ownPlayer;
        }

        // Set per link by Link.Bulk's setter, so a reconnect gets the new link's Send. Null = no link, so not armed.
        public Func<Message, bool> SendAck
        {
            get { lock (gate) return sendAck; }
            set { lock (gate) sendAck = value; }
        }

        public JoinHeader Header { get { lock (gate) return header; } }
        public int Received { get { lock (gate) return received; } }
        public long Bytes { get { lock (gate) return bytes; } }
        public bool Armed { get { lock (gate) return armed && sendAck != null; } }
        public bool Complete { get { lock (gate) return complete; } }     // all chunks, the sum exact and the hash equal
        public bool Finished { get { lock (gate) return finished; } }     // complete, or failed at the end (the hash)
        public int ErrorCode { get { lock (gate) return errorCode; } }    // 0, or the first JoinCodes value that went wrong
        public string Error { get { lock (gate) return error; } }         // fixed text, never anything the peer sent

        // ---- the sink (reader thread) ----

        public void OnControl(Message m)
        {
            if (m == null) return;
            var claim = m as ClaimResult;
            if (claim != null)
            {
                // The attempt this answer belongs to is fixed before `expecting` is asked (outside the lock: it is the
                // driver's flag). A Reset in between starts a new attempt, which this answer must not arm.
                int gen0;
                lock (gate) gen0 = generation;
                bool answers = false;
                Func<bool> asked = expecting;
                try { answers = asked != null && asked(); } catch (Exception) { answers = false; }
                bool arm = claim.Ok && answers;
                lock (gate) { if (gen0 == generation) armed = arm; }
                return;
            }
            if (m is JoinRefused || m is Bye) lock (gate) armed = false;
        }

        public bool OnHeader(JoinHeader h)
        {
            int gen0;
            lock (gate)
            {
                if (finished) return false;   // an outcome is already recorded: a stray header changes nothing (the link closes on false)
                if (!(armed && sendAck != null)) return false;   // unarmed: the link closes as a violation; nothing is recorded, so a later arming starts clean
                if (header != null) return Fail(JoinCodes.HeaderInvalid, "a second header arrived");
                if (errorCode != 0) return false;
                if (h == null) return Fail(JoinCodes.HeaderInvalid, "no header");
                int code = Check(h);
                if (code != 0) return false;
                gen0 = generation;
            }
            string bad;
            try { bad = headerCheck(h); }
            catch (Exception e) { bad = "the header check failed (" + e.GetType().Name + ")"; }
            lock (gate)
            {
                if (gen0 != generation) return false;   // Reset ran while the rules were being asked: that attempt is over, and so is its verdict
                if (bad != null) return Fail(JoinCodes.HeaderInvalid, "the header breaks the rules");
                if (header != null || errorCode != 0 || !(armed && sendAck != null)) return false;   // Reset or a disarm ran meanwhile
                buffer = new byte[h.CompressedSize];   // only now, and exactly the declared size (checked against the cap above)
                header = h;
                return true;
            }
        }

        // Under the lock: 0 when the header passes, else the failure is recorded and its code returned.
        int Check(JoinHeader h)
        {
            if (h.CompressedSize < 1 || h.CompressedSize > JoinLimits.MaxCompressed) { Fail(JoinCodes.OverCap, "the compressed size is outside 1.." + JoinLimits.MaxCompressed); return errorCode; }
            if (h.UncompressedSize < 1 || h.UncompressedSize > JoinLimits.MaxUncompressed) { Fail(JoinCodes.OverCap, "the uncompressed size is outside 1.." + JoinLimits.MaxUncompressed); return errorCode; }
            if (h.ChunkCount != ChunkMath.Count(h.CompressedSize)) { Fail(JoinCodes.SizeMismatch, "the chunk count does not match the size"); return errorCode; }
            if (!IsLowerHex64(h.Sha256)) { Fail(JoinCodes.HeaderInvalid, "the hash is not 64 lower-case hex characters"); return errorCode; }
            if (h.SaveVersion == null || TextSanitiser.Clean(h.SaveVersion, 32, "?") != ownSaveVersion) { Fail(JoinCodes.HeaderInvalid, "the host's save version is not ours"); return errorCode; }
            if (h.YourPlayer != ownPlayer) { Fail(JoinCodes.HeaderInvalid, "the header is for another player"); return errorCode; }
            return 0;
        }

        static bool IsLowerHex64(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        public bool OnChunk(int index, byte[] frame, int offset, int count)
        {
            Func<Message, bool> ack = null;
            int ackNext = 0;
            byte[] toHash = null;
            string wantHash = null;
            int gen;
            lock (gate)
            {
                if (finished) return false;   // an outcome is already recorded (complete, or the hash failed): a stray chunk changes nothing
                if (!(armed && sendAck != null) || header == null) return false;   // unarmed, or before the header: a violation at the link; nothing is recorded
                if (errorCode != 0 || received >= header.ChunkCount) return false;   // failed already, or the last chunk is being hashed: nothing more to record
                if (index != received) return Fail(JoinCodes.SizeMismatch, "a chunk arrived out of order");
                if (count != ChunkMath.Length(header.CompressedSize, index)) return Fail(JoinCodes.SizeMismatch, "a chunk has the wrong size");
                if (frame == null || offset < 0 || count < 0 || offset > frame.Length - count) return Fail(JoinCodes.Malformed, "a chunk lies about its frame");
                Buffer.BlockCopy(frame, offset, buffer, index * JoinLimits.ChunkSize, count);   // copied: the frame is the link's
                received++;
                bytes += count;
                gen = generation;
                bool last = received == header.ChunkCount;
                if (last || received % JoinLimits.AckEvery == 0) { ack = sendAck; ackNext = received; }
                if (last) { toHash = buffer; wantHash = header.Sha256; }
            }
            if (ack != null)
            {
                try { ack(new SnapshotAck { Next = ackNext }); }
                catch (Exception) { }   // a link that is going down: the transfer's own checks decide what happens next
            }
            if (toHash != null)
            {
                bool same = Sha256Hex.Of(toHash) == wantHash;   // outside the lock: 16 MiB takes a while
                lock (gate)
                {
                    if (gen != generation) return true;   // Reset ran meanwhile
                    finished = true;
                    if (same) complete = true;
                    else Fail(JoinCodes.HashMismatch, "the snapshot's hash is not the header's");
                }
            }
            return true;
        }

        // True: a chunk frame of this size skips both rate buckets. Armed, the header passed, nothing has gone wrong, chunks are still
        // owed, and the frame is no bigger than a chunk frame can be. (Only chunk-typed frames are asked.)
        public bool Exempt(int frameBytes)
        {
            if (frameBytes < MinChunkFrame || frameBytes > MaxChunkFrame) return false;
            lock (gate) return armed && sendAck != null && header != null && errorCode == 0 && !finished && received < header.ChunkCount;
        }

        // The buffer, once, when the transfer is complete; null otherwise (and after the first take).
        public byte[] TakeCompressed()
        {
            lock (gate)
            {
                if (!complete) return null;
                byte[] b = buffer;
                buffer = null;
                return b;
            }
        }

        // A new attempt on the same link (a full rejoin after a failed check): everything is forgotten, and a new ClaimResult(Ok)
        // is needed to arm it again.
        public void Reset()
        {
            lock (gate)
            {
                generation++;
                armed = complete = finished = false;
                header = null;
                buffer = null;
                received = 0;
                bytes = 0;
                errorCode = 0;
                error = null;
            }
        }

        // Under the lock. Records the first failure only. Always returns false, so a refusal reads "return Fail(...)".
        bool Fail(int code, string why)
        {
            if (errorCode == 0) { errorCode = code; error = why; }
            return false;
        }
    }
}
