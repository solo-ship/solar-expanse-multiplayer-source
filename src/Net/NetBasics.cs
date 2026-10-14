using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SolarExpanseMultiplayer
{
    // A monotonic millisecond clock shared by the network code (never the wall clock: it can jump).
    public static class NetClock
    {
        static readonly Stopwatch Watch = Stopwatch.StartNew();
        public static long NowMs { get { return Watch.ElapsedMilliseconds; } }
    }

    public sealed class NetOptions
    {
        public int MaxBody = Frame.DefaultMaxBody;
        public int HandshakeTimeoutMs = 10000;   // TCP accept to Welcome, TLS included
        public int IdleTimeoutMs = 10000;        // nothing received for this long: the link is dead
        public int PingIntervalMs = 1000;
        public int SendTimeoutMs = 5000;         // a write blocked this long (the other side stopped reading): dead
        public int MaxPending = 8;               // host: connections still in their handshake
        public int MaxSendQueue = 512;           // frames waiting to be sent; more means the other side can't keep up
        public int SimLatencyMs, SimJitterMs, SimLossPercent;   // dev only: delays added to what this side receives
        public int SimSeed = 1;
        public int SimRateKiBps;                 // dev only: this side's outgoing bytes are throttled to this (0 = off)
        // null = the side's own: the host's per-client limiter (RateLimiter.Default), and on a client the limiter sized
        // for its link to the host (RateLimiter.HostLink). Tests set it to something tighter.
        public Func<RateLimiter> NewLimiter;
    }

    // Confirmed (M4, host side) comes after Joined: the first frame arrived on the new link, so the Welcome did. It is put in
    // the inbox BEFORE that frame's own message.
    public enum NetEventKind { Joined, Message, Closed, Failed, Confirmed, BulkProgress }

    // What the network threads hand to the main thread.
    public sealed class NetEvent
    {
        public NetEventKind Kind;
        public int Link;          // Link.Id (0 for a connection that failed before it had one)
        public int PlayerId;      // 0 until the handshake is done
        public string Name;       // Joined: the player's (host side) or the host's (client side) sanitised name
        public Message Message;   // Message
        public string Reason;     // Closed / Failed
        public bool Refused;      // Closed: the host said no (Reject) - don't retry
        public bool Violation;    // Closed / Failed: this side closed the link because the other broke the protocol
        public bool Replaces;     // Joined (host side): took over from a still-open link of the same player; that old link's leave is never reported
        public bool Rejoin;       // Joined / Confirmed (host side): the Hello carried a rejoin key, not an invite
        public bool Final;        // BulkProgress: the transfer has ended (complete, or failed at the end)
        public bool Bye;          // Closed / Failed: a Bye from the other side ended the link (it left on purpose)
        public int ByeCode;       // Closed / Failed with Bye: that Bye's Code (why the host ended it; 0 = not said)
        // when the network thread put it in the inbox (NetClock ms; under -semp-sim-lag, when the simulated delivery
        // happens): what the host's request limiter must see, not the moment the main thread got round to it
        public long AtMs;
    }

    // Dev-only network trouble for one-PC tests: each delivery waits latency +- jitter; "loss" looks the way loss
    // looks over TCP (a retransmit: an extra max(200 ms, 3 x latency)). Order is always kept.
    public sealed class LagSim
    {
        readonly int latency, jitter, lossPercent;
        readonly Random rng;
        long last;

        public LagSim(int latencyMs, int jitterMs, int lossPercent, int seed)
        {
            latency = Clamp(latencyMs, 0, 5000);
            jitter = Clamp(jitterMs, 0, latency);
            this.lossPercent = Clamp(lossPercent, 0, 50);
            rng = new Random(seed);
        }

        static int Clamp(int v, int min, int max) { return v < min ? min : v > max ? max : v; }

        public bool Active { get { return latency > 0 || lossPercent > 0; } }

        public long DeliverAt(long nowMs)
        {
            lock (rng)
            {
                long t = nowMs + latency + (jitter > 0 ? rng.Next(-jitter, jitter + 1) : 0);
                if (lossPercent > 0 && rng.Next(100) < lossPercent) t += Math.Max(200, 3 * latency);
                if (t < nowMs) t = nowMs;
                if (t < last) t = last;   // TCP never reorders
                last = t;
                return t;
            }
        }
    }

    // Dev-only bandwidth for one-PC tests (-semp-sim-rate): a token bucket over a link's outgoing frames, so a snapshot
    // crosses loopback at about the speed of a home upload. Tokens (bytes) build up at the rate, at most BurstBytes; a
    // frame may take the bucket below zero (a frame can be bigger than the burst), and the next one waits until it is
    // back at zero. Pure: the caller passes the time and sleeps for the answer, so tests can drive it with any clock.
    public sealed class RateThrottle
    {
        public const int BurstBytes = 16 * 1024;
        readonly long rate;    // bytes a second
        long milli;            // tokens in thousandths of a byte, so a millisecond's worth is exact at any rate
        long lastMs;
        bool started;

        public RateThrottle(int kibPerSecond)
        {
            if (kibPerSecond < 1) throw new ArgumentOutOfRangeException("kibPerSecond", "the rate must be positive");
            rate = kibPerSecond * 1024L;
            milli = BurstBytes * 1000L;
        }

        // How long to wait (ms) before sending a frame of this many bytes at nowMs; the frame's bytes are spent at
        // once. A clock that steps back adds nothing.
        public long DelayMs(long nowMs, int bytes)
        {
            if (started && nowMs > lastMs)
            {
                milli += (nowMs - lastMs) * rate;
                if (milli > BurstBytes * 1000L) milli = BurstBytes * 1000L;
            }
            if (!started || nowMs > lastMs) lastMs = nowMs;
            started = true;
            long wait = milli < 0 ? (-milli + rate - 1) / rate : 0;
            milli -= (long)bytes * 1000;
            return wait;
        }
    }

    // Network threads put events in; the main thread takes them out with Drain. Each event waits for its delivery
    // time (now, unless the dev lag simulation is on).
    public sealed class Inbox
    {
        readonly object gate = new object();
        readonly Queue<KeyValuePair<long, NetEvent>> queue = new Queue<KeyValuePair<long, NetEvent>>();
        readonly LagSim lag;
        public const int MaxQueued = 20000;   // beyond this, messages (and failure notes) are dropped: a stalled main thread must not grow memory
        public int Dropped { get; private set; }

        public Inbox(LagSim lag) { this.lag = lag != null && lag.Active ? lag : null; }

        public void Put(NetEvent e)
        {
            long now = NetClock.NowMs;
            lock (gate)
            {
                // Joined, Confirmed and Closed are never dropped: the main thread's picture of who is connected must stay true,
                // and a lost Confirmed would leave the gate's invite or key reserved and a rejoin's drop untimed. The same for the
                // FINAL bulk progress event (the transfer ended: complete, or failed on its hash); intermediate progress may be dropped.
                bool final = e.Kind == NetEventKind.BulkProgress && e.Final;
                if (queue.Count >= MaxQueued && !final && e.Kind != NetEventKind.Joined && e.Kind != NetEventKind.Confirmed && e.Kind != NetEventKind.Closed) { Dropped++; return; }
                queue.Enqueue(new KeyValuePair<long, NetEvent>(lag == null ? now : lag.DeliverAt(now), e));
            }
        }

        public int Drain(long nowMs, int max, List<NetEvent> into)
        {
            int n = 0;
            lock (gate)
                while (n < max && queue.Count > 0 && queue.Peek().Key <= nowMs)
                {
                    KeyValuePair<long, NetEvent> kv = queue.Dequeue();
                    kv.Value.AtMs = kv.Key;
                    into.Add(kv.Value);
                    n++;
                }
            return n;
        }

        public int Count { get { lock (gate) return queue.Count; } }
    }

    // The host's certificate is trusted only by its SHA-256 (from the join code). Chain and name errors are expected
    // for a self-signed certificate, and validity dates are ignored on purpose (a pin identifies one key; the host
    // makes a new certificate when it needs a new identity), so the pin is the whole trust decision.
    public static class CertPin
    {
        public static byte[] Sha256(X509Certificate c)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(c.GetRawCertData());
        }

        // False for a null certificate and for a pin that is not exactly 32 bytes; compared in constant time.
        public static bool Matches(X509Certificate c, byte[] expected)
        {
            if (c == null || expected == null || expected.Length != 32) return false;
            byte[] got;
            try { got = Sha256(c); }
            catch (Exception) { return false; }
            if (got == null || got.Length != 32) return false;
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= got[i] ^ expected[i];
            return diff == 0;
        }

        // The pin as it appears in a join code: exactly 64 hex characters (either case) or null. Nothing else is accepted
        // (no spaces, prefixes or separators).
        public static byte[] ParseHex(string hex)
        {
            if (hex == null || hex.Length != 64) return null;
            var b = new byte[32];
            for (int i = 0; i < 64; i++)
            {
                int v = Nibble(hex[i]);
                if (v < 0) return null;
                b[i / 2] = (byte)((i % 2 == 0) ? v << 4 : b[i / 2] | v);
            }
            return b;
        }

        static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        public static bool Matches(X509Certificate c, string expectedHex) { return Matches(c, ParseHex(expectedHex)); }

        // The client's SslStream validation callback: policy errors do not matter, only the pin does.
        public static RemoteCertificateValidationCallback Validator(byte[] expected)
        {
            byte[] pin = expected == null ? null : (byte[])expected.Clone();
            return (sender, cert, chain, errors) => Matches(cert, pin);
        }
    }
}
