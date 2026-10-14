using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The joining side: TCP, TLS 1.2 with the host's certificate pinned by its SHA-256, then Hello -> Welcome.
    // Plain (unencrypted) connections are never made.
    public sealed class LinkClient
    {
        readonly NetOptions opt;
        volatile Link link;
        volatile bool stopped, done;
        readonly object gate = new object();   // orders "publish the link, then check stopped" against "set stopped, then read the link"
        volatile string stopReason = "closed";

        // Test seams (null in the game): TestBeforeLinkSet runs on the connect thread just before the link is published;
        // TestBeforeAccept runs in the Welcome handler just before the link becomes a working one; TestSocketSetUp hands the
        // tests the socket once its options are set (they read them back later).
#pragma warning disable 0649   // assigned only by the tests
        internal Action TestBeforeLinkSet;
        internal Action<Link> TestBeforeAccept;
        internal Action<TcpClient> TestSocketSetUp;
#pragma warning restore 0649

        public readonly Inbox Inbox;
        Hello myHello;   // what this client said about itself: the version refusal names its side
        public string RejoinKey { get; private set; }
        public Link Link { get { return link; } }

        // The receiver of the snapshot (M4). May be set at any time, before or after the link exists: it is handed to the link as
        // soon as there is one, and the link wires its SendAck to its own Send.
        IBulkSink bulk;
        public IBulkSink Bulk
        {
            get { lock (gate) return bulk; }
            set
            {
                Link l;
                lock (gate) { bulk = value; l = link; }
                if (l != null) l.Bulk = value;
            }
        }

        public LinkClient(NetOptions opt)
        {
            this.opt = opt;
            Inbox = new Inbox(new LagSim(opt.SimLatencyMs, opt.SimJitterMs, opt.SimLossPercent, opt.SimSeed));
        }

        // The pin as written in a join code (64 hex characters). Anything else never connects: a Failed note arrives.
        public void Connect(IPAddress address, int port, string fingerprintHex, Hello hello)
        {
            byte[] pin = CertPin.ParseHex(fingerprintHex);
            if (pin == null)
            {
                done = true;
                Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = "could not connect: the host's key pin is missing or not 64 hex characters" });
                return;
            }
            Connect(address, port, pin, hello);
        }

        // Connects on a background thread. The result arrives in the Inbox: Joined, or Failed / Closed with the reason.
        public void Connect(IPAddress address, int port, byte[] fingerprint, Hello hello)
        {
            if (fingerprint == null || fingerprint.Length != 32)
            {
                done = true;
                Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = "could not connect: the host's key pin (SHA-256) is missing or the wrong length" });
                return;
            }
            // JoinCode.Parse already refuses such a code; this keeps every other caller from reaching past the home
            // network, and it answers before any socket exists.
            if (!BindAddress.Allowed(address))
            {
                done = true;
                Inbox.Put(new NetEvent { Kind = NetEventKind.Failed,
                                         Reason = "could not connect: " + JoinCode.NotLocal + " (" + address + ")" });
                return;
            }
            fingerprint = (byte[])fingerprint.Clone();
            myHello = hello;
            Spawn("SEMP client", () => Run(address, port, fingerprint, hello));
            Spawn("SEMP client beat", BeatLoop);
        }

        // Every network thread body is wrapped: an unexpected exception ends that thread with a note, never the game.
        void Spawn(string name, ThreadStart body)
        {
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = name + " stopped (" + e.GetType().Name + ")" }); }
            });
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        void Run(IPAddress address, int port, byte[] fingerprint, Hello hello)
        {
            TcpClient c = null;
            try
            {
                c = new TcpClient(address.AddressFamily);
                c.NoDelay = true;
                c.ReceiveTimeout = opt.HandshakeTimeoutMs;
                c.SendTimeout = opt.SendTimeoutMs;
                Action<TcpClient> socketSeam = TestSocketSetUp;
                if (socketSeam != null) socketSeam(c);
                IAsyncResult ar = c.BeginConnect(address, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(opt.HandshakeTimeoutMs)) throw new TimeoutException("no answer from " + address + ":" + port);
                c.EndConnect(ar);
                // The pin is the whole trust decision: chain, name and date problems of a self-signed certificate are expected.
                var ssl = new SslStream(c.GetStream(), false, CertPin.Validator(fingerprint));
                Link l = Publish(new TlsPipe(c, ssl, opt));
                if (l == null) return;
                ssl.AuthenticateAsClient("semp-host", null, SslProtocols.Tls12, false);
                if (!ssl.IsEncrypted || !ssl.IsAuthenticated || ssl.SslProtocol != SslProtocols.Tls12)
                    throw new AuthenticationException("not an encrypted TLS 1.2 connection");
                c.ReceiveTimeout = 0;
                l.Send(hello);   // queued; the writer starts in Run
                l.Run();
            }
            catch (Exception e)
            {
                string why = e is AuthenticationException
                    ? "TLS handshake failed: the host's key does not match the join code, or it is not a SEMP host (" + e.Message + ")"
                    : "could not connect (" + e.GetType().Name + ": " + e.Message + ")";
                Link l = link;
                if (l != null) l.Fail(why);
                else
                {
                    try { if (c != null) c.Close(); } catch (Exception) { }
                    Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = why });
                }
            }
            finally { done = true; }
        }

        // The new link becomes this client's; null when Close ran before it was published (it is closed here, since
        // Close had nothing to close then).
        Link Publish(IFramePipe pipe)
        {
            var l = new Link(pipe, opt, Inbox, Dir.ToClient);
            l.Handshake = OnWelcome;
            Action seam = TestBeforeLinkSet;
            if (seam != null) seam();
            bool closeNow;
            IBulkSink sink;
            lock (gate) { link = l; closeNow = stopped; sink = bulk; }
            if (sink != null) l.Bulk = sink;
            if (closeNow)
            {
                l.Close(stopReason, false);
                return null;
            }
            return l;
        }

        // Over a Steam connection SteamTransport has opened to the host: the same Hello -> Welcome and the same events
        // as Connect. The pin is the host's Steam id, checked by the pipe when Steam says the connection is up (Steam's
        // certificates prove the account); the Hello waits in the pipe until then. null pipe (no Steam connection) =
        // a Failed note, as for any connect that never started.
        public void ConnectSteam(IFramePipe pipe, Hello hello)
        {
            if (pipe == null)
            {
                done = true;
                Inbox.Put(new NetEvent { Kind = NetEventKind.Failed,
                                         Reason = "could not connect: no Steam connection to the host" });
                return;
            }
            myHello = hello;
            Spawn("SEMP client", () => RunPipe(pipe, hello));
            Spawn("SEMP client beat", BeatLoop);
        }

        void RunPipe(IFramePipe pipe, Hello hello)
        {
            try
            {
                Link l = Publish(pipe);
                if (l == null) return;
                l.Send(hello);   // queued; the pipe holds it until Steam has connected
                l.Run();
            }
            catch (Exception e)
            {
                string why = "could not connect (" + e.GetType().Name + ": " + e.Message + ")";
                Link l = link;
                if (l != null) l.Fail(why);
                else
                {
                    try { pipe.Close(false); } catch (Exception) { }
                    Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = why });
                }
            }
            finally { done = true; }
        }

        string OnWelcome(Link l, Message m)
        {
            var w = m as Welcome;
            if (w != null)
            {
                l.PlayerId = w.PlayerId;
                RejoinKey = w.Rejoin;   // before Joined can be seen
                Action<Link> seam = TestBeforeAccept;
                if (seam != null) seam(l);
                // Atomic with closing: a link closed meanwhile (Close, deadline, host gone) is never reported as joined.
                if (!l.TryAccept(null, new NetEvent { Kind = NetEventKind.Joined, Link = l.Id, PlayerId = w.PlayerId, Name = TextSanitiser.Clean(w.HostName, 24, "Host") }))
                    return "the connection closed during the handshake";
                return null;
            }
            var r = m as Reject;
            if (r != null)
            {
                l.Refused = true;
                // After a valid credential the host says which versions differ (I-7): shown as plain words.
                Hello mine = myHello;
                string versions = r.VersionSentence(mine == null ? null : mine.GameVersion,
                    mine == null ? null : mine.ModVersion, mine == null ? null : mine.GameBuild);
                if (versions != null) return "refused by the host: " + versions + " (code " + r.Code + ")";
                return "refused by the host: " + TextSanitiser.Clean(r.Reason, 200, "no reason given") + " (code " + r.Code + ")";
            }
            l.Violation = true;
            return "expected Welcome first, got " + m.GetType().Name;
        }

        public bool Send(Message m)
        {
            Link l = link;
            return l != null && l.Accepted && l.Send(m);
        }

        public void Close(string reason) { Close(reason, 0); }

        // byeCode: why this side ends the link, sent in its Bye (JoinCodes.Left: the player left on purpose).
        public void Close(string reason, int byeCode)
        {
            Link l;
            lock (gate) { stopReason = reason; stopped = true; l = link; }
            if (l != null) l.Close(reason, true, byeCode);
        }

        void BeatLoop()
        {
            while (!stopped && !done)
            {
                try
                {
                    Link l = link;
                    if (l != null) l.Tick(NetClock.NowMs);
                }
                catch (Exception) { }
                Thread.Sleep(100);
            }
        }
    }
}
