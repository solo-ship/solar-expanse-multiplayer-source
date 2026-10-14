using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The native connection under a SteamPipe (SteamTransport's, in the game; a fake in the tests). Kept free of
    // Steamworks types so the pipe's rules run in the desktop tests.
    public interface ISteamWire
    {
        int Send(byte[] message);                        // one reliable message; Steam's EResult as an int
        void Close(int reason, string why, bool linger); // idempotent; reason in Steam's App range (1000-1999)
    }

    // What every Steam connection must be, and the numbers we give it. Pure, so the tests can feed it bad values.
    public static class SteamWireRules
    {
        public const int ResultOk = 1, ResultLimitExceeded = 25;
        public const int RetryWaitMs = 5;
        public const int IceDisable = 0;                       // P2P_Transport_ICE_Enable: no direct, NAT-punched route
        public const int FlagUnauthenticated = 1, FlagUnencrypted = 2, FlagRelayed = 16;
        // The host's link budget to one player (512 KiB/s, an M7 ruling). Steam's default send rate is 256 KiB/s fixed,
        // which would hold the link below its own budget, so our connections never go under it.
        public const int LinkBudgetBytesPerSecond = 512 * 1024;
        public const int SendRateMin = LinkBudgetBytesPerSecond, SendRateMax = 1024 * 1024;
        public const int SendBufferSize = 1024 * 1024;
        // End reasons we give (Steam's App range).
        public const int EndNormal = 1000, EndIce = 1002, EndInsecure = 1003, EndWrongPeer = 1004, EndRefused = 1005,
                         EndStopped = 1006, EndConfig = 1007;

        // ICE read back from a socket or connection: anything but 0 means Steam could hand each side the other's IP
        // address, which SECURITY.md promises never happens. null = what was read is fine.
        public static string IceProblem(bool read, int ice)
        {
            if (!read) return "ICE could not be read back";
            return ice == IceDisable ? null : "ICE reads " + ice + ", not 0: Steam could show IP addresses";
        }

        // Send rates read back from a connection: never under the link's budget, never min over max.
        public static string RateProblem(bool read, int min, int max)
        {
            if (!read) return "the send rates could not be read back";
            if (min < LinkBudgetBytesPerSecond || max < LinkBudgetBytesPerSecond)
                return "send rate min " + min + " / max " + max + " B/s is under the link budget "
                    + LinkBudgetBytesPerSecond;
            return min > max ? "send rate min " + min + " is over max " + max : null;
        }

        // The connection's m_nFlags once it is up. Unencrypted is never allowed. Unauthenticated is allowed only for
        // the one-PC test's localhost socket pair (no certificates exist for localhost identities), never for a peer.
        public static string FlagsProblem(int flags, bool allowUnauthenticated)
        {
            if ((flags & FlagUnencrypted) != 0) return "the connection is not encrypted";
            if ((flags & FlagUnauthenticated) != 0 && !allowUnauthenticated)
                return "the connection is not authenticated";
            return null;
        }

        // The Steam id at the other end against the one the code (or the door) named: Steam's certificates prove the
        // account, so this comparison is the whole pin. A connection with nothing to compare against is refused too.
        public static string PeerProblem(ulong expected, ulong remote)
        {
            if (expected == 0) return "there is no Steam id to check the other end against";
            return remote == expected ? null : "the Steam id at the other end is not the one we connected to";
        }

        // A received message's size, checked before a byte of it is copied: Steam lets a peer send up to 512 KiB, our
        // frames (prefix included) are at most maxBody + 4. null = within the cap.
        public static string MessageTooBig(int size, int maxBody)
        {
            if (size >= 0 && size <= maxBody + 4) return null;
            return "a Steam message of " + size + " bytes, over the frame cap of " + (maxBody + 4);
        }

        // One send's outcome: true = done; false = wait RetryWaitMs and try again; throws once the deadline is past or
        // Steam refused for any other reason (the link then closes as a blocked TCP write would).
        public static bool SendDone(int result, long elapsedMs, int sendTimeoutMs)
        {
            if (result == ResultOk) return true;
            if (result == ResultLimitExceeded)
            {
                if (elapsedMs < sendTimeoutMs) return false;
                throw new IOException("Steam's send buffer stayed full for " + sendTimeoutMs / 1000 + " s");
            }
            throw new IOException("Steam refused the send (EResult " + result + ")");
        }
    }

    // A Link's frames over one Steam connection: one Steam message carries one whole frame, its 4-byte prefix included.
    // The transport's pump thread hands each received message to Deliver; Link's reader takes them in ReadFrame, where
    // the prefix is checked against the message's length (Frame.Unwrap). The queue between them is bounded: a peer that
    // sends faster than the reader takes breaks a rule. Connection-state changes (Up, Lost) come from the main thread.
    public sealed class SteamPipe : IFramePipe
    {
        public const int MaxQueuedFrames = 512, MaxQueuedBytes = 4 * 1024 * 1024;

        readonly ISteamWire wire;
        readonly NetOptions opt;
        readonly object qLock = new object();
        readonly Queue<byte[]> queue = new Queue<byte[]>();
        int queuedBytes;
        bool up, closed, lostClean;
        string violation, lost;   // under qLock: why the next read throws

        public SteamPipe(ISteamWire wire, NetOptions opt, ulong remoteId)
        {
            this.wire = wire; this.opt = opt; RemoteId = remoteId;
        }

        public string Kind { get { return "steam"; } }
        public PeerIdentity Peer { get { return PeerIdentity.Steam(RemoteId); } }

        // The Steam id at the other end as Steam verified it (0 for the one-PC test pair). For the door and the names
        // (PeerIdentity); never logged in full.
        public ulong RemoteId { get; private set; }

        public bool IsUp { get { lock (qLock) return up; } }
        public bool IsClosed { get { lock (qLock) return closed; } }

        // Main thread: Steam says the connection is up. Its flags (encrypted; authenticated unless allowed) and, when
        // checkId, the Steam id at the other end against RemoteId (the host the code named, or the account the door
        // admitted) decide whether it may carry anything. null = up, writes may go; otherwise the reason, and the
        // connection is closed with nothing sent: a joiner's queued Hello (and its invite) never leaves.
        public string CheckUp(int flags, bool allowUnauthenticated, ulong remote, bool checkId)
        {
            string problem = SteamWireRules.FlagsProblem(flags, allowUnauthenticated);
            int end = SteamWireRules.EndInsecure;
            if (problem == null && checkId)
            {
                problem = SteamWireRules.PeerProblem(RemoteId, remote);
                end = SteamWireRules.EndWrongPeer;
            }
            if (problem == null) { MarkUp(); return null; }
            Lost(false, problem);   // the reader ends as a lost connection, a waiting write throws
            wire.Close(end, problem, false);
            return problem;
        }

        // The connection is up and passed its checks; writes may go.
        public void MarkUp()
        {
            lock (qLock) { up = true; Monitor.PulseAll(qLock); }
        }

        // Pump thread: one whole message. False = not taken (the pipe is closed, or it has broken a rule: the reader
        // then throws). Over either cap the peer is cut off at once; what it queued before is not worth reading.
        public bool Deliver(byte[] message)
        {
            lock (qLock)
            {
                if (closed || violation != null) return false;
                if (queue.Count >= MaxQueuedFrames || queuedBytes + message.Length > MaxQueuedBytes)
                {
                    violation = "more than " + MaxQueuedFrames + " frames or " + MaxQueuedBytes / 1024
                        + " KiB waiting to be read";
                    Monitor.PulseAll(qLock);
                    return false;
                }
                queue.Enqueue(message);
                queuedBytes += message.Length;
                Monitor.PulseAll(qLock);
                return true;
            }
        }

        // Pump thread: a message that broke a rule before it was even copied (over the frame cap).
        public void Violate(string why)
        {
            lock (qLock)
            {
                if (violation == null) violation = why;
                Monitor.PulseAll(qLock);
            }
        }

        // Main thread (the state callback) or the transport's Stop: the connection has ended. clean = the peer closed
        // it (the reader ends once it has read what came before); otherwise the read throws IOException.
        public void Lost(bool clean, string why)
        {
            lock (qLock)
            {
                if (lost == null) { lost = why; lostClean = clean; }
                Monitor.PulseAll(qLock);
            }
        }

        public byte[] ReadFrame(int maxBody)
        {
            byte[] m;
            lock (qLock)
            {
                while (true)
                {
                    if (closed) return null;
                    if (violation != null) throw new ProtocolException(violation);
                    if (queue.Count > 0) break;
                    if (lost != null)
                    {
                        if (lostClean) return null;
                        throw new IOException(lost);
                    }
                    Monitor.Wait(qLock);
                }
                m = queue.Dequeue();
                queuedBytes -= m.Length;
            }
            return Frame.Unwrap(m, maxBody);
        }

        // Steam keeps reliable messages in order and fragments them itself; a full send buffer is retried until the
        // link's send deadline, as a blocked TCP write would be. Waits (within the same deadline) for the connection to
        // come up first: a joiner's Hello is queued before Steam has finished connecting.
        public void WriteFrame(byte[] framed)
        {
            long start = NetClock.NowMs;
            lock (qLock)
            {
                while (!up)
                {
                    if (closed) throw new IOException("the Steam connection is closed");
                    if (lost != null) throw new IOException(lost);
                    long left = opt.SendTimeoutMs - (NetClock.NowMs - start);
                    if (left <= 0)
                        throw new IOException("the Steam connection did not come up within " + opt.SendTimeoutMs / 1000
                            + " s");
                    Monitor.Wait(qLock, (int)Math.Min(left, 100));
                }
            }
            while (true)
            {
                if (IsClosed) throw new IOException("the Steam connection is closed");
                if (SteamWireRules.SendDone(wire.Send(framed), NetClock.NowMs - start, opt.SendTimeoutMs)) return;
                Thread.Sleep(SteamWireRules.RetryWaitMs);
            }
        }

        public void ReaderDone() { }

        // Never calls the wire under qLock: the pump holds the transport's lock while it delivers, and the wire takes
        // that lock to close.
        public void Close(bool linger)
        {
            lock (qLock)
            {
                if (closed) return;
                closed = true;
                Monitor.PulseAll(qLock);
            }
            wire.Close(SteamWireRules.EndNormal, "closed", linger);
        }
    }
}
