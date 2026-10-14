using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // One connection (over an IFramePipe). A reader (frames -> rate limit -> decode -> sequence check -> inbox) on the thread that calls
    // Run, a writer thread draining a bounded send queue, and Tick (pings and timeouts) from the owner's beat thread.
    // Network threads never touch the game: everything for the game goes through the Inbox.
    public sealed class Link
    {
        static int lastId;
        public readonly int Id = Interlocked.Increment(ref lastId);
        public readonly long OpenedMs = NetClock.NowMs;

        readonly IFramePipe pipe;
        readonly NetOptions opt;
        readonly Inbox inbox;
        readonly Dir arriving;
        readonly BlockingCollection<byte[]> sendQ;
        readonly SeqCounter recvSeq = new SeqCounter();
        uint sendLast;   // last outgoing sequence number used (under sendLock); taken only once the message has encoded
        readonly RateLimiter limiter;
        readonly RateThrottle throttle;      // dev only (-semp-sim-rate): null unless NetOptions.SimRateKiBps is set
        readonly object sendLock = new object();
        bool closed, writerStarted, byeQueued;   // under sendLock
                long lastReceivedMs, lastPingMs;

        volatile bool accepted;
        public PeerIdentity Peer { get { return pipe.Peer; } }   // who the transport says is at the other end
        public bool Accepted { get { return accepted; } }   // handshake done (host: Hello accepted; client: Welcome received); set only by TryAccept
        public volatile bool Violation;       // closed for breaking the protocol
        public volatile bool ByTheHost;       // with Violation: the host's kick or ban, the account may come back
        public volatile bool Refused;         // client: the host sent Reject
        volatile bool replaced;
        public bool Replaced { get { return replaced; } }   // host: closed because the same player reconnected; the game gets no leave for it (set only by TryReplace)
        public int PlayerId;
        public long Token;                    // host: HostGate's per-connection token for PlayerId (Left/stale-link checks)
        public string Name;
        // Called on the reader thread for each message until Accepted: null to go on, or the reason to close.
        public Func<Link, Message, string> Handshake;
        public Action<Link> OnClosed;
        // Host side: called once, on the reader thread, as soon as the first whole frame of any type has been read after
        // TryAccept (proof that the Welcome arrived). Runs before that frame is decoded, so what it puts in the inbox comes
        // before the frame's own message.
        public Action<Link> OnFirstFrameAfterAccept;
        volatile bool firstFrameSeen;
        public bool FirstFrameSeen { get { return firstFrameSeen; } }
        // Client side: the receiver of a snapshot (M4). Every decoded message is shown to it on the reader thread BEFORE the inbox;
        // JoinHeader frames go to it as well as to the inbox; SnapshotChunk frames go ONLY to it (never to the inbox), and skip the
        // rate limiter's buckets while it says they are exempt. Setting it wires its SendAck to this link's Send.
        volatile IBulkSink bulk;
        public IBulkSink Bulk
        {
            get { return bulk; }
            set { bulk = value; if (value != null) value.SendAck = Send; }
        }
        static readonly ushort ChunkKind = Envelope.IdOf(typeof(SnapshotChunk));
        const int ProgressEvery = 64;   // chunks between progress events for the main thread (the final one is always sent)
        int bulkSinceEvent;
        public bool HelloRejoin;              // host: the Hello carried a rejoin key (set before TryAccept)
        // A Bye ended this link: the other side left on purpose. Carried on the closing event apart from the reason
        // text, so the joiner can tell a host that closed its game (no reconnect) from a lost connection (retried),
        // with the Bye's code (why the host ended it), which picks the joiner's words.
        volatile bool byeHeard;
        volatile int byeCode;
        public string CloseReason { get; private set; }
        // A reply queued just before the close that must still arrive (the host's Reject): the close lingers as for a
        // Bye, or a Steam connection drops what it has not sent yet and the joiner never learns why.
        public volatile bool LingerOnClose;
        readonly System.Collections.Generic.HashSet<string> unsendable = new System.Collections.Generic.HashSet<string>();   // kinds already logged (under sendLock)

        public Link(IFramePipe pipe, NetOptions opt, Inbox inbox, Dir arriving)
        {
            this.pipe = pipe; this.opt = opt; this.inbox = inbox; this.arriving = arriving;
            sendQ = new BlockingCollection<byte[]>(opt.MaxSendQueue);
            limiter = opt.NewLimiter != null ? opt.NewLimiter()
                : arriving == Dir.ToClient ? RateLimiter.HostLink() : RateLimiter.Default();
            if (opt.SimRateKiBps > 0) throttle = new RateThrottle(opt.SimRateKiBps);
            lastReceivedMs = OpenedMs;
        }

        public bool IsClosed { get { lock (sendLock) return closed; } }

        // Starts the writer and runs the reader on the calling thread until the link closes.
        public void Run()
        {
            lock (sendLock)
            {
                if (closed) { Kill(false); return; }
                writerStarted = true;
            }
            var w = new Thread(WriteLoop);
            w.IsBackground = true;
            w.Name = "SEMP link " + Id + " writer";
            w.Start();
            try { ReadLoop(); }
            finally { pipe.ReaderDone(); }
        }

        // Makes the handshake final, atomically with closing: false (and nothing queued or posted) when the link has already
        // been closed, so a link that died mid-handshake never becomes a player. welcome (host side; null on the client) is
        // queued and joined is put in the inbox under the same lock: no Welcome goes out on a dead link, a later Closed for
        // this link can never overtake Joined, and no Broadcast (which needs Accepted) can be queued before the Welcome.
        public bool TryAccept(Message welcome, NetEvent joined)
        {
            lock (sendLock)
            {
                if (closed) return false;
                if (welcome != null && Enqueue(welcome) != 0) return false;   // queue full or not encodable: the caller closes the link
                accepted = true;
                if (joined != null) inbox.Put(joined);
                return true;
            }
        }

        void ReadLoop()
        {
            string reason = "the other side closed the connection";
            try
            {
                while (!IsClosed)
                {
                    byte[] body = pipe.ReadFrame(opt.MaxBody);
                    if (body == null) break;
                    if (!firstFrameSeen && Accepted)
                    {
                        firstFrameSeen = true;
                        Action<Link> first = OnFirstFrameAfterAccept;
                        if (first != null && !IsClosed) { try { first(this); } catch (Exception) { } }
                    }
                    long now = NetClock.NowMs;
                    Interlocked.Exchange(ref lastReceivedMs, now);
                    IBulkSink sink = bulk;
                    // A chunk frame on a working client link goes to the sink alone. Whether it skips the buckets is the sink's call: it
                    // says yes only while armed and within the declared chunk count, so a hostile peer cannot use this for a flood.
                    bool chunkFrame = sink != null && arriving == Dir.ToClient && Accepted && body.Length >= Envelope.HeaderSize && ((body[0] << 8) | body[1]) == ChunkKind;
                    bool exempt = false;
                    if (chunkFrame)
                    {
                        try { exempt = sink.Exempt(body.Length + 4); }
                        catch (Exception e) { reason = "the snapshot receiver failed (" + e.GetType().Name + ")"; break; }
                    }
                    if (!exempt && !limiter.Allow(now, body.Length + 4)) { reason = "rate limit exceeded"; Violation = true; break; }
                    if (chunkFrame)
                    {
                        string stop = TakeChunk(sink, body);
                        if (stop != null) { reason = stop; Violation = true; break; }
                        continue;
                    }
                    Envelope.Decoded d = Envelope.Decode(body, arriving);   // also runs the message's Validate
                    if (d.Error != null) { reason = "bad message: " + d.Error; Violation = true; break; }
                    if (!recvSeq.Accept(d.Seq)) { reason = "message sequence " + d.Seq + " out of order"; Violation = true; break; }
                    if (Accepted && sink != null)
                    {
                        // The sink sees every message first, on this thread: ClaimResult(Ok) arms it before the header can arrive, whatever
                        // the main thread is doing with the inbox (I-4). A JoinHeader the sink refuses ends the link.
                        try
                        {
                            sink.OnControl(d.Message);
                            var header = d.Message as JoinHeader;
                            if (header != null && !sink.OnHeader(header)) { reason = "the snapshot header was refused"; Violation = true; break; }
                        }
                        catch (Exception e) { reason = "the snapshot receiver failed (" + e.GetType().Name + ")"; break; }
                    }
                    if (d.Message is SnapshotChunk) { reason = "a snapshot chunk with no receiver for it"; Violation = true; break; }
                    if (d.Message is Bye)
                    {
                        // Before the handshake the sender is a stranger: none of its text is echoed into our logs.
                        // On the client the sender is the host, already authenticated by the pin, so its reason is kept.
                        var bye = (Bye)d.Message;
                        reason = !Accepted && arriving == Dir.ToHost ? "peer said bye before joining"
                            : "left: " + TextSanitiser.Clean(bye.Reason, 100, "no reason given");
                        byeCode = bye.Code;
                        byeHeard = true;
                        break;
                    }
                    if (!Accepted)
                    {
                        string bad = Handshake == null ? "no handshake handler" : Handshake(this, d.Message);
                        if (bad != null) { reason = bad; break; }
                        continue;
                    }
                    if (d.Message is Hello || d.Message is Welcome || d.Message is Reject)
                    {
                        reason = d.Message.GetType().Name + " after the handshake is done"; Violation = true; break;
                    }
                    if (d.Message is Ping) { Send(new Pong { T = ((Ping)d.Message).T }); continue; }
                    // Pong goes to the inbox on purpose: the main thread can time the round trip (RTT) later.
                    inbox.Put(new NetEvent { Kind = NetEventKind.Message, Link = Id, PlayerId = PlayerId, Message = d.Message });
                }
            }
            catch (ProtocolException e) { reason = "bad frame: " + e.Message; Violation = true; }
            catch (EndOfStreamException e) { reason = "connection lost (" + e.Message + ")"; }   // the peer went away mid-frame: no rule was broken
            catch (Exception e) { reason = "connection lost (" + e.GetType().Name + ")"; }
            // a rule-breaker is told why (the fake client checks it), with Protocol as the goodbye's code (Shut);
            // no-op if already closed
            Close(reason, Violation);
        }

        // One chunk frame, for the sink: sequence check, the payload cap, the index, then the sink gets the bytes straight from the frame
        // (no per-chunk array). Null = taken; otherwise the reason the link must close as a violation. The inbox gets a progress event
        // every ProgressEvery chunks and one at the end (complete, or failed on the hash), never any chunk bytes.
        string TakeChunk(IBulkSink sink, byte[] body)
        {
            int len = body.Length - Envelope.HeaderSize;
            if (len < 5 || len > 4 + JoinLimits.ChunkSize) return "bad message: SnapshotChunk payload is " + len + " bytes";
            uint seq = ((uint)body[2] << 24) | ((uint)body[3] << 16) | ((uint)body[4] << 8) | body[5];
            if (!recvSeq.Accept(seq)) return "message sequence " + seq + " out of order";
            uint index = ((uint)body[Envelope.HeaderSize] << 24) | ((uint)body[Envelope.HeaderSize + 1] << 16) | ((uint)body[Envelope.HeaderSize + 2] << 8) | body[Envelope.HeaderSize + 3];
            if (index > int.MaxValue) return "bad message: SnapshotChunk Index is out of range";
            bool ok, finished;
            try
            {
                ok = sink.OnChunk((int)index, body, Envelope.HeaderSize + 4, len - 4);
                finished = ok && sink.Finished;
            }
            catch (Exception e) { return "the snapshot receiver failed (" + e.GetType().Name + ")"; }
            if (!ok) return "a snapshot chunk was out of place";
            if (finished || ++bulkSinceEvent >= ProgressEvery)
            {
                bulkSinceEvent = 0;
                inbox.Put(new NetEvent { Kind = NetEventKind.BulkProgress, Link = Id, PlayerId = PlayerId, Final = finished });
            }
            return null;
        }

        void WriteLoop()
        {
            try
            {
                foreach (byte[] f in sendQ.GetConsumingEnumerable())
                {
                    if (throttle != null) Pace(f.Length);
                    pipe.WriteFrame(f);
                }
            }
            catch (Exception e) { Close("send failed (" + e.GetType().Name + ")", false); }
            finally
            {
                bool bye;
                lock (sendLock) bye = byeQueued;
                Kill(bye || LingerOnClose);
            }
        }

        // The simulated bandwidth's wait, in short slices: once the link is closed what is still queued (its Bye) goes
        // at once, so a slow simulated link never holds a close up for long.
        void Pace(int bytes)
        {
            long now = NetClock.NowMs, until = now + throttle.DelayMs(now, bytes);
            for (long left; !IsClosed && (left = until - NetClock.NowMs) > 0; ) Thread.Sleep((int)Math.Min(left, 50));
        }

        // linger: a Bye was just sent (see TlsPipe.Close). Runs on the writer thread, never the caller's.
        void Kill(bool linger)
        {
            pipe.Close(linger);
        }

        // Never throws (I-13): a message that cannot be encoded (over its cap, invalid) is not sent, Send returns false and the
        // kind is logged once per link. A full queue closes the link, as before.
        public bool Send(Message m)
        {
            int result;
            lock (sendLock)
            {
                if (closed) return false;
                result = Enqueue(m);
            }
            if (result == 2) Close("the other side is not keeping up (send queue full)", false);
            return result == 0;
        }

        // Under sendLock. 0 = queued; 1 = could not be encoded (nothing queued, no sequence number used); 2 = the queue is
        // full (the number is given back, so the peer never sees a gap for a message that was never sent).
        int Enqueue(Message m)
        {
            byte[] body; string error;
            if (!Envelope.TryEncode(m, unchecked(sendLast + 1), out body, out error))
            {
                string kind = m == null ? "(null)" : m.GetType().Name;
                if (unsendable.Add(kind)) Log.Warn("net: a " + kind + " was not sent (" + TextSanitiser.OneLine(error) + "); further ones of that kind on this link are dropped silently");
                return 1;
            }
            byte[] f = Frame.Wrap(body);
            sendLast = unchecked(sendLast + 1);
            if (sendQ.TryAdd(f)) return 0;
            sendLast = unchecked(sendLast - 1);
            return 2;
        }

        // Dev and test tools only (the fake client's hostile sends): bytes exactly as given.
        public bool SendBytes(byte[] raw)
        {
            lock (sendLock) return !closed && sendQ.TryAdd(raw);
        }

        // The next outgoing sequence number, for SendBytes.
        public uint TakeSeq() { lock (sendLock) return ++sendLast; }

        // From the owner's beat thread: handshake deadline, idle timeout, pings.
        public void Tick(long now)
        {
            if (IsClosed) return;
            if (!Accepted)
            {
                if (now - OpenedMs > opt.HandshakeTimeoutMs) Close("handshake timed out", false);
                return;
            }
            if (now - Interlocked.Read(ref lastReceivedMs) > opt.IdleTimeoutMs)
            {
                Close("timed out (nothing received for " + opt.IdleTimeoutMs / 1000 + " s)", false);
                return;
            }
            if (now - lastPingMs >= opt.PingIntervalMs)
            {
                lastPingMs = now;
                Send(new Ping { T = now });
            }
        }

        public void Close(string reason, bool sayBye) { Close(reason, sayBye, 0); }

        // byeCode: why this side ends the link (a JoinCodes value), sent in the Bye; 0 says none, except on a link
        // closed for a broken rule, which says Protocol.
        public void Close(string reason, bool sayBye, int byeCode)
        {
            Shut(reason, sayBye, NetEventKind.Closed, false, byeCode);
        }

        // Host: the same player came back on a new link. Closes this one (with a Bye) only if it was still open, and marks it
        // Replaced under the same lock, so its leave is suppressed exactly when the caller is told true. False: it had already
        // closed by itself, its own Closed was (or is being) reported, and the new link must not claim to replace it.
        public bool TryReplace(string reason) { return Shut(reason, true, NetEventKind.Closed, true, 0); }

        // Closed before it was ever a working link (TLS failed). Any link that closes before its handshake is done is
        // reported as Failed, not Closed: it never was a player.
        public void Fail(string reason) { Shut(reason, false, NetEventKind.Failed, false, 0); }

        // Idempotent. sayBye queues a Bye with the reason before the queue closes; the writer sends what is queued and
        // then closes the socket (a stuck write ends after SendTimeoutMs).
        // Returns whether this call did the closing.
        bool Shut(string reason, bool sayBye, NetEventKind kind, bool replacing, int sayCode)
        {
            if (sayCode == 0 && Violation) sayCode = JoinCodes.Protocol;
            bool kill, wasAccepted;
            lock (sendLock)
            {
                if (closed) return false;
                closed = true;
                if (replacing) replaced = true;
                wasAccepted = accepted;   // read under the lock TryAccept takes: a link is accepted or closed, never both
                CloseReason = reason;
                if (sayBye)
                {
                    try
                    {
                        var bye = new Bye { Reason = TextBudget.FitBytes(TextSanitiser.OneLine(reason), 150),
                                            Code = sayCode };
                        byeQueued = Enqueue(bye) == 0;
                    }
                    catch (Exception) { }
                }
                sendQ.CompleteAdding();
                kill = !writerStarted;
            }
            if (kill) Kill(false);
            if (!wasAccepted) kind = NetEventKind.Failed;   // never became a working link: not a player leaving
            // The owner's books (the gate) are updated before the game hears about it.
            Action<Link> cb = OnClosed;
            if (cb != null) { try { cb(this); } catch (Exception) { } }
            // A replaced link is not a leave: the same player is already back on a new link (the game got Joined for it).
            if (!replaced)
                inbox.Put(new NetEvent
                {
                    Kind = kind, Link = Id, PlayerId = wasAccepted ? PlayerId : 0, Reason = reason, Refused = Refused,
                    Violation = Violation, Bye = byeHeard, ByeCode = byeHeard ? byeCode : 0,
                });
            return true;
        }
    }
}
