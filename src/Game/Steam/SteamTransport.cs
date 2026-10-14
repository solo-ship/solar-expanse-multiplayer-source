using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace SolarExpanseMultiplayer
{
    // What the door sees of an incoming connection before it is accepted: Steam's verified identity only.
    public sealed class IncomingConnection
    {
        public ulong RemoteId;
        public bool IndividualAccount;
        public int Flags;
        public double WaitedSeconds;   // how long the door has held it so far (0 the first time it is asked)
    }

    // Steam P2P connections for one side of a session (owned by SteamNet; no statics). One poll group, one listen
    // socket (host), our outgoing connection (joiner); one pump thread drains the poll group into each connection's
    // SteamPipe; connection-state changes arrive as a callback on the main thread through the game's own Steam pump.
    // Every native call made off the main thread, and Stop, take `native`, so once Stop has run (or the game's Steam
    // has shut down) no call is made at all.
    public sealed class SteamTransport
    {
        const int PumpBatch = 64, PumpSleepMs = 5;
        // The door's answer that keeps a connection in Connecting, to be asked again on the next Tick (DoorGrace). A
        // held connection is never accepted on a timer alone: the door decides each time, and past MaxHoldMs, or with
        // MaxHeld already waiting, it is refused.
        public const string AskAgain = "the door asks again shortly";
        const int MaxHeld = 8;
        const long MaxHoldMs = 8000, AskEveryMs = 250;
        sealed class Held { public HSteamNetConnection H; public SteamNetConnectionInfo_t Info; public long SinceMs; }
        readonly List<Held> held = new List<Held>();   // main thread only
        long nextAsk;

        readonly SteamReady ready;
        readonly NetOptions opt;
        readonly string side;
        readonly object native = new object();
        readonly Dictionary<uint, Conn> conns = new Dictionary<uint, Conn>();   // under native
        bool alive;                                                              // under native
        HSteamListenSocket listen = HSteamListenSocket.Invalid;
        HSteamNetPollGroup poll = HSteamNetPollGroup.Invalid;
        Callback<SteamNetConnectionStatusChangedCallback_t> status;
        Thread pump;
        volatile bool stopping;
        Func<IncomingConnection, string> door;
        Action<SteamPipe> admitted;
        int listenIce = int.MinValue;
        int mainThread, stateCallbacks, offMainThread;

        public SteamTransport(SteamReady ready, NetOptions opt, string side)
        {
            this.ready = ready; this.opt = opt; this.side = side;
        }

        public bool Started { get { lock (native) return alive; } }
        public bool Listening { get { return listen != HSteamListenSocket.Invalid; } }
        public bool HasPollGroup { get { return poll != HSteamNetPollGroup.Invalid; } }
        public int ListenIce { get { return listenIce; } }   // as read back from the listen socket
        public int OpenConnections { get { lock (native) return conns.Count; } }
        public bool PumpRunning { get { Thread t = pump; return t != null && t.IsAlive; } }
        public int StateCallbacks { get { return stateCallbacks; } }
        public int StateCallbacksOffMainThread { get { return offMainThread; } }   // must stay 0

        void Say(string s) { Log.Info("steam " + side + ": " + s); }

        // Main thread. The poll group, the state callback and the pump; false (nothing made) unless Steam is up.
        public bool Begin()
        {
            lock (native) if (alive) return true;
            ready.Refresh();
            if (!ready.Usable) { Say("not started: " + (ready.Why ?? ready.Problem)); return false; }
            HSteamNetPollGroup g = SteamNetworkingSockets.CreatePollGroup();
            if (g == HSteamNetPollGroup.Invalid) { Say("not started: Steam made no poll group"); return false; }
            lock (native) { poll = g; alive = true; }
            mainThread = Thread.CurrentThread.ManagedThreadId;
            status = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
            stopping = false;
            pump = new Thread(Pump) { IsBackground = true, Name = "SEMP steam pump " + side };
            pump.Start();
            Say("started (poll group, state callback, pump thread)");
            return true;
        }

        // Host, main thread: a P2P listen socket on virtual port 0, ICE off and read back as 0 or it is closed again.
        // door (null until the door exists: everyone) decides before AcceptConnection; admitted gets each connection
        // once it is up and has passed its checks (LinkHost.AddSteamLink, as a TLS connection would go).
        public bool Listen(Func<IncomingConnection, string> door, Action<SteamPipe> admitted)
        {
            if (!Begin()) return false;
            if (Listening) return true;
            SteamNetworkingConfigValue_t[] o = SteamConfig.Options();
            HSteamListenSocket s = SteamNetworkingSockets.CreateListenSocketP2P(0, o.Length, o);
            if (s == HSteamListenSocket.Invalid) { Say("no listen socket: Steam refused it"); return false; }
            int ice;
            bool read = SteamConfig.ReadListenIce(s, out ice);
            string problem = SteamWireRules.IceProblem(read, ice);
            if (problem != null)
            {
                bool closed = SteamNetworkingSockets.CloseListenSocket(s);
                Say("listen socket closed again: " + problem + " (closed " + closed + ")");
                return false;
            }
            this.door = door; this.admitted = admitted;
            listenIce = ice;
            lock (native) listen = s;
            Say("listening on virtual port 0; ICE on the listen socket reads " + ice);
            return true;
        }

        // Joiner, main thread: connects to the host's Steam id. The pipe comes up once Steam says Connected and the
        // connection's flags and identity are checked; until then its writes wait. null = refused (logged).
        public SteamPipe Connect(ulong hostId)
        {
            if (!Begin()) return null;
            var id = new SteamNetworkingIdentity();
            id.SetSteamID64(hostId);
            SteamNetworkingConfigValue_t[] o = SteamConfig.Options();
            HSteamNetConnection h = SteamNetworkingSockets.ConnectP2P(ref id, 0, o.Length, o);
            if (h == HSteamNetConnection.Invalid) { Say("connect: Steam gave no connection"); return null; }
            return Adopt(h, hostId, false, false, "outgoing");
        }

        // Test profiles only (the one-PC test): a pair of connections inside this process. With network loopback they
        // run over real packets (pacing, Steam's message cap) but carry localhost identities with no certificates, so
        // the flags check refuses them unless allowUnauthenticated (then only for this pair). Refused: both null.
        public bool Pair(bool networkLoopback, bool allowUnauthenticated, out SteamPipe a, out SteamPipe b,
                         out string why)
        {
            a = b = null; why = null;
            lock (native) if (!alive) { why = "the transport is not started"; return false; }
            var id1 = new SteamNetworkingIdentity(); id1.SetLocalHost();
            var id2 = new SteamNetworkingIdentity(); id2.SetLocalHost();
            HSteamNetConnection c1, c2;
            if (!SteamNetworkingSockets.CreateSocketPair(out c1, out c2, networkLoopback, ref id1, ref id2))
            { why = "Steam made no socket pair"; return false; }
            bool set = SteamConfig.SetOnPair(c1) & SteamConfig.SetOnPair(c2);
            a = Adopt(c1, 0, false, true, "pair 1");
            b = a == null ? null : Adopt(c2, 0, false, true, "pair 2");
            if (a != null && b != null)
                Find(a).AllowUnauthenticated = Find(b).AllowUnauthenticated = allowUnauthenticated;
            if (a == null || b == null)
            {
                why = "a pair connection failed its settings check" + (set ? "" : " (a setting was refused)");
                Drop(c1, a); Drop(c2, b);
                a = b = null;
                return false;
            }
            // a pair is Connected from the start: no state callback brings it up, so it is checked here
            why = Up(a, true) ?? Up(b, true);
            if (why != null) { Drop(c1, a); Drop(c2, b); a = b = null; return false; }
            return true;
        }

        void Drop(HSteamNetConnection h, SteamPipe p)
        {
            if (p != null) { p.Lost(false, "refused"); p.Close(false); return; }
            lock (native)
                if (alive && SteamReady.NativeAlive)
                    SteamNetworkingSockets.CloseConnection(h, SteamWireRules.EndConfig, "refused", false);
        }

        // A new connection of ours: its settings read back (ICE 0 on every P2P one, our send rates) or it is closed;
        // then it joins the poll group and gets its pipe. Only the one-PC test's pair is not P2P.
        SteamPipe Adopt(HSteamNetConnection h, ulong expect, bool incoming, bool pair, string what)
        {
            string line;
            string problem = SteamConfig.CheckConnection(h, !pair, out line);
            if (problem != null)
            {
                lock (native)
                    if (alive) SteamNetworkingSockets.CloseConnection(h, SteamWireRules.EndIce, "settings", false);
                Say(what + " connection closed: " + problem + " (" + line + ")");
                return null;
            }
            var c = new Conn(this, h, incoming, expect);
            c.Pipe = new SteamPipe(c, opt, expect);
            lock (native)
            {
                if (!alive) return null;
                if (!SteamNetworkingSockets.SetConnectionPollGroup(h, poll))
                {
                    SteamNetworkingSockets.CloseConnection(h, SteamWireRules.EndConfig, "poll group", false);
                    Say(what + " connection closed: it could not join the poll group");
                    return null;
                }
                conns[h.m_HSteamNetConnection] = c;
            }
            Say(what + " connection: " + line);
            return c.Pipe;
        }

        // Main thread: the connection says Connected. SteamPipe.CheckUp decides: the flags (encrypted; authenticated
        // unless allowed) and the Steam id at the other end, which must be the one we connected to (outgoing: the
        // host the join code names, the pin) or the one the door admitted (incoming). null = up.
        string Up(SteamPipe p, bool fromPair)
        {
            Conn c = Find(p);
            if (c == null) return "the connection is gone";
            SteamNetConnectionInfo_t info = default(SteamNetConnectionInfo_t);
            bool got = false;
            lock (native) if (alive) got = SteamNetworkingSockets.GetConnectionInfo(c.Handle, out info);
            if (!got) { c.Fail(SteamWireRules.EndConfig, "no connection info"); return "no connection info"; }
            string problem = p.CheckUp(info.m_nFlags, c.AllowUnauthenticated, info.m_identityRemote.GetSteamID64(),
                                       !fromPair);
            string what = c.Incoming ? "incoming" : fromPair ? "pair" : "outgoing";
            if (problem != null)
            {
                Say(what + " connection refused: " + problem + " (flags " + SteamConfig.Flags(info.m_nFlags) + ")");
                return problem;
            }
            // never the id itself: "present" says Steam named an account
            string remote = info.m_identityRemote.IsLocalHost() ? "localhost"
                : info.m_identityRemote.GetSteamID64() != 0 ? "present" : "none";
            Say(what + " connection up: flags " + SteamConfig.Flags(info.m_nFlags) + ", remote " + remote);
            return null;
        }

        Conn Find(SteamPipe p)
        {
            lock (native) foreach (Conn c in conns.Values) if (c.Pipe == p) return c;
            return null;
        }

        // The game's Steam pump, main thread. Only our own connections and our own listen socket are looked at: every
        // transport in this process sees every connection's changes.
        void OnStatus(SteamNetConnectionStatusChangedCallback_t cb)
        {
            stateCallbacks++;
            if (Thread.CurrentThread.ManagedThreadId != mainThread) offMainThread++;
            try
            {
                Conn c;
                lock (native)
                {
                    if (!alive) return;
                    conns.TryGetValue(cb.m_hConn.m_HSteamNetConnection, out c);
                }
                ESteamNetworkingConnectionState state = cb.m_info.m_eState;
                if (c == null)
                {
                    if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                        && Listening && cb.m_info.m_hListenSocket == listen)
                    {
                        if (FindHeld(cb.m_hConn) == null) Incoming(cb.m_hConn, cb.m_info, NetClock.NowMs);
                    }
                    else if (Unhold(cb.m_hConn))
                    {
                        // a held connection that ended while it waited: its handle must still be freed
                        SteamNetworkingSockets.CloseConnection(cb.m_hConn, SteamWireRules.EndRefused, "gave up", false);
                        Say("a held incoming connection ended while the door waited");
                    }
                    return;
                }
                switch (state)
                {
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                        if (!c.Pipe.IsUp && Up(c.Pipe, false) == null && c.Incoming)
                        {
                            Action<SteamPipe> a = admitted;
                            try { if (a != null) a(c.Pipe); else c.Fail(SteamWireRules.EndRefused, "nobody takes it"); }
                            catch (Exception e)
                            {
                                c.Fail(SteamWireRules.EndRefused, "admit failed");
                                Say("admit failed: " + e.GetType().Name);
                            }
                        }
                        break;
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                    case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                        bool byPeer = cb.m_info.m_eState
                                      == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
                        // the peer's debug text is the peer's words: only the reason number is kept
                        string why = (byPeer ? "the other side closed the Steam connection"
                                             : "the Steam connection was lost")
                            + " (end reason " + cb.m_info.m_eEndReason + ")";
                        c.Pipe.Lost(byPeer, why);
                        c.Release();
                        Say(why);
                        break;
                }
            }
            catch (Exception e) { Say("state callback failed: " + e.GetType().Name + ": " + e.Message); }
        }

        // Host: a connection waits in Connecting until accepted. The door decides from Steam's identity alone; a door
        // that throws refuses. sinceMs: when the connection first reached the door (it may be held and asked again).
        void Incoming(HSteamNetConnection h, SteamNetConnectionInfo_t info, long sinceMs)
        {
            CSteamID remote = info.m_identityRemote.GetSteamID();
            long now = NetClock.NowMs;
            var facts = new IncomingConnection
            {
                RemoteId = remote.m_SteamID, IndividualAccount = remote.BIndividualAccount(), Flags = info.m_nFlags,
                WaitedSeconds = (now - sinceMs) / 1000.0,
            };
            string refusal;
            try { refusal = door != null ? door(facts) : null; }
            catch (Exception e) { refusal = "the door failed (" + e.GetType().Name + ")"; }
            if (refusal == AskAgain)
            {
                Held x = FindHeld(h);
                if (x == null && held.Count >= MaxHeld) refusal = "too many connections are waiting at the door";
                else if (x == null)
                {
                    held.Add(new Held { H = h, Info = info, SinceMs = sinceMs });
                    Say("incoming connection held: the door asks again shortly");
                    return;
                }
                else if (now - x.SinceMs >= MaxHoldMs) refusal = "held at the door too long";
                else return;
            }
            Unhold(h);
            if (refusal != null)
            {
                SteamNetworkingSockets.CloseConnection(h, SteamWireRules.EndRefused, "not invited", false);
                Say("incoming connection refused before accepting: " + refusal);
                return;
            }
            EResult r = SteamNetworkingSockets.AcceptConnection(h);
            if (r != EResult.k_EResultOK)
            {
                SteamNetworkingSockets.CloseConnection(h, SteamWireRules.EndRefused, "accept failed", false);
                Say("incoming connection: AcceptConnection said " + r);
                return;
            }
            Adopt(h, remote.m_SteamID, true, false, "incoming");
        }

        // Main thread, every frame (SteamNet.Update): the held connections are put to the door again.
        public void Tick()
        {
            if (held.Count == 0) return;
            long now = NetClock.NowMs;
            if (now < nextAsk) return;
            nextAsk = now + AskEveryMs;
            bool up;
            lock (native) up = alive && SteamReady.NativeAlive && Listening;
            if (!up) { held.Clear(); return; }
            foreach (Held x in held.ToArray()) Incoming(x.H, x.Info, x.SinceMs);
        }

        Held FindHeld(HSteamNetConnection h)
        {
            foreach (Held x in held) if (x.H == h) return x;
            return null;
        }

        bool Unhold(HSteamNetConnection h)
        {
            Held x = FindHeld(h);
            return x != null && held.Remove(x);
        }

        void Pump()
        {
            var ptrs = new IntPtr[PumpBatch];
            while (!stopping)
            {
                int n = 0;
                try
                {
                    lock (native)
                    {
                        if (!alive || !SteamReady.NativeAlive) break;
                        n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(poll, ptrs, PumpBatch);
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                SteamNetworkingMessage_t m = SteamNetworkingMessage_t.FromIntPtr(ptrs[i]);
                                Conn c;
                                if (!conns.TryGetValue(m.m_conn.m_HSteamNetConnection, out c)) continue;
                                // checked before anything is copied: Steam lets a peer send up to 512 KiB a message
                                string tooBig = SteamWireRules.MessageTooBig(m.m_cbSize, opt.MaxBody);
                                if (tooBig != null) { c.Pipe.Violate(tooBig); continue; }
                                var bytes = new byte[m.m_cbSize];
                                Marshal.Copy(m.m_pData, bytes, 0, m.m_cbSize);
                                c.Pipe.Deliver(bytes);
                            }
                            finally { SteamNetworkingMessage_t.Release(ptrs[i]); }
                        }
                    }
                }
                catch (Exception e) { Say("pump: " + e.GetType().Name + ": " + e.Message); Thread.Sleep(100); }
                if (n < PumpBatch) Thread.Sleep(PumpSleepMs);
            }
        }

        // Main thread (and quit). Every connection closed (lingering, so a queued goodbye still goes), the listen
        // socket closed, the poll group destroyed, the callback dropped. Idempotent. If the game's Steam has already
        // shut down no Steam call is made: Steam dropped everything with it.
        public void Stop()
        {
            stopping = true;
            Thread t = pump;
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
            int closed = 0;
            bool can, listenClosed = false, pollGone = false, hadListen, hadPoll;
            var pipes = new List<SteamPipe>();
            lock (native)
            {
                if (!alive && poll == HSteamNetPollGroup.Invalid) return;
                can = alive && SteamReady.NativeAlive;
                foreach (Conn c in conns.Values)
                {
                    c.Closed = true;
                    pipes.Add(c.Pipe);
                    if (can && SteamNetworkingSockets.CloseConnection(c.Handle, SteamWireRules.EndStopped, "stopped",
                                                                      true))
                        closed++;
                }
                int count = conns.Count;
                conns.Clear();
                foreach (Held x in held)
                    if (can) SteamNetworkingSockets.CloseConnection(x.H, SteamWireRules.EndStopped, "stopped", false);
                held.Clear();
                hadListen = listen != HSteamListenSocket.Invalid;
                hadPoll = poll != HSteamNetPollGroup.Invalid;
                if (can && hadListen) listenClosed = SteamNetworkingSockets.CloseListenSocket(listen);
                if (can && hadPoll) pollGone = SteamNetworkingSockets.DestroyPollGroup(poll);
                listen = HSteamListenSocket.Invalid;
                poll = HSteamNetPollGroup.Invalid;
                alive = false;
                Say("stopped: " + (can ? "closed " + closed + " of " + count + " connection(s)"
                        + (hadListen ? ", listen socket closed " + listenClosed : "")
                        + (hadPoll ? ", poll group destroyed " + pollGone : "")
                    : "Steam had already shut down, so no Steam call was made (" + count
                        + " connection(s) dropped with it)"));
            }
            foreach (SteamPipe p in pipes) p.Lost(false, "the Steam transport stopped");
            try { if (status != null) status.Dispose(); } catch (Exception) { }
            status = null;
            door = null; admitted = null;
        }

        // One native connection: the wire under its SteamPipe.
        sealed class Conn : ISteamWire
        {
            readonly SteamTransport t;
            public readonly HSteamNetConnection Handle;
            public readonly bool Incoming;
            public bool AllowUnauthenticated;   // the one-PC test's pair only
            public readonly ulong Expect;
            public SteamPipe Pipe;
            public bool Closed;   // under t.native

            public Conn(SteamTransport t, HSteamNetConnection h, bool incoming, ulong expect)
            {
                this.t = t; Handle = h; Incoming = incoming; Expect = expect;
            }

            // Every frame reliable, on one lane: our receiver needs the sequence numbers in order.
            public int Send(byte[] message)
            {
                var pin = GCHandle.Alloc(message, GCHandleType.Pinned);
                try
                {
                    lock (t.native)
                    {
                        if (Closed || !t.alive || !SteamReady.NativeAlive) return (int)EResult.k_EResultNoConnection;
                        long number;
                        return (int)SteamNetworkingSockets.SendMessageToConnection(Handle, pin.AddrOfPinnedObject(),
                            (uint)message.Length, Constants.k_nSteamNetworkingSend_Reliable, out number);
                    }
                }
                finally { pin.Free(); }
            }

            public void Close(int reason, string why, bool linger)
            {
                lock (t.native)
                {
                    if (Closed) return;
                    Closed = true;
                    t.conns.Remove(Handle.m_HSteamNetConnection);
                    if (t.alive && SteamReady.NativeAlive)
                        SteamNetworkingSockets.CloseConnection(Handle, reason, why, linger);
                }
            }

            // Main thread: refused after it was adopted; the pipe's reader ends as a lost connection.
            public void Fail(int reason, string why)
            {
                Pipe.Lost(false, why);
                Close(reason, why, false);
            }

            // Steam ended it: the handle must still be closed to free it.
            public void Release()
            {
                Close(SteamWireRules.EndNormal, "ended", false);
            }
        }
    }
}
