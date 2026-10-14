using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // The host's side: a TLS 1.2 listener (the host's certificate), the Hello check (HostGate), a table of links.
    // Nothing a connection sends is acted on until its Hello is accepted; then only its messages reach the Inbox.
    public sealed class LinkHost
    {
        readonly IPAddress bind;
        readonly int requestedPort;
        readonly X509Certificate2 cert;
        readonly HostGate gate;
        readonly string hostName;
        readonly NetOptions opt;
        readonly object linksLock = new object();
        readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
        TcpListener listener;
        volatile bool stopped;

        // Test seams (null in the game): TestBeforeAdd runs on the accept thread before a new link joins the table;
        // TestBeforeAccept runs in the Hello handler just before the link becomes a player; TestSocketSetUp hands the tests
        // each accepted socket once its options are set (they read them back later).
#pragma warning disable 0649   // assigned only by the tests
        internal Action TestBeforeAdd;
        internal Action<Link> TestBeforeAccept;
        internal Action<Link> TestAfterCheck;   // runs in the Hello handler right after the gate said yes, before the link has its player id
        internal Action<TcpClient> TestSocketSetUp;
#pragma warning restore 0649

        public readonly Inbox Inbox;
        public int Port { get; private set; }

        // The host's Steam lobby, for a Steam link's Hello (any thread; null = no lobby): whether that account is a
        // member, which lets a Hello with no invite in, and its Steam name, which the roster shows instead of the
        // Hello's.
        public Func<ulong, bool> LobbyMember;
        public Func<ulong, string> SteamName;
        // Any thread: a Steam connection's handshake is over (its account may connect again). failed: it closed before
        // the Welcome (deadline, refused Hello, breach); a refusal at the pending cap is the host's state, not failed.
        public Action<ulong, bool> SteamHandshakeEnded;

        // What the socket is really bound to (set by Start): the join code carries this, so a joiner is sent exactly
        // where the host listens.
        public IPAddress Address { get; private set; }

        public LinkHost(IPAddress bind, int port, X509Certificate2 cert, HostGate gate, string hostName, NetOptions opt)
        {
            // DevFlags already refuses any other -semp-bind; this keeps every other caller to the same rule, so nothing
            // can make the host listen on every interface or a public address.
            if (!BindAddress.Allowed(bind))
                throw new ArgumentException("a host listens only on " + BindAddress.Rule + ", not " + bind);
            this.bind = bind; requestedPort = port; this.cert = cert; this.gate = gate; this.hostName = hostName; this.opt = opt;
            Inbox = new Inbox(new LagSim(opt.SimLatencyMs, opt.SimJitterMs, opt.SimLossPercent, opt.SimSeed));
        }

        public void Start()
        {
            listener = new TcpListener(bind, requestedPort);
            listener.Start();
            var at = (IPEndPoint)listener.LocalEndpoint;
            Port = at.Port;
            Address = at.Address;
            Spawn("SEMP accept", AcceptLoop);
            Spawn("SEMP host beat", BeatLoop);
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

        void AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception) { if (stopped) return; Thread.Sleep(100); continue; }
                if (PendingCount() >= opt.MaxPending)
                {
                    try { c.Close(); } catch (Exception) { }
                    Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = "too many connections in their handshake; one refused" });
                    continue;
                }
                try
                {
                    c.NoDelay = true;
                    c.ReceiveTimeout = opt.HandshakeTimeoutMs;   // TLS must finish in time; cleared after
                    c.SendTimeout = opt.SendTimeoutMs;
                    Action<TcpClient> socketSeam = TestSocketSetUp;
                    if (socketSeam != null) socketSeam(c);
                    var ssl = new SslStream(c.GetStream(), false);
                    Link link = NewLink(new TlsPipe(c, ssl, opt));
                    Action seam = TestBeforeAdd;
                    if (seam != null) seam();
                    lock (linksLock) links[link.Id] = link;
                    if (stopped)
                    {
                        // Stop took its snapshot before this link was added: nobody else would close it.
                        link.Close("the host is shutting down", false);
                        continue;
                    }
                    Spawn("SEMP link " + link.Id, () => Serve(link, c, ssl));
                }
                catch (Exception e)
                {
                    try { c.Close(); } catch (Exception) { }
                    Inbox.Put(new NetEvent { Kind = NetEventKind.Failed, Reason = "accept failed (" + e.GetType().Name + ")" });
                }
            }
        }

        Link NewLink(IFramePipe pipe)
        {
            var link = new Link(pipe, opt, Inbox, Dir.ToHost);
            link.Handshake = OnHello;
            link.OnClosed = OnLinkClosed;
            link.OnFirstFrameAfterAccept = OnFirstFrame;
            return link;
        }

        // A connection SteamTransport has accepted (Steam encrypts and authenticates it, so there is no TLS step): the
        // same pending cap, Hello check, gate and table as a TLS connection. False = refused, and the pipe is closed.
        // Call it after Start (the beat thread runs the handshake deadline).
        public bool AddSteamLink(IFramePipe pipe)
        {
            if (stopped) { pipe.Close(false); SteamEnded(pipe.Peer, false); return false; }
            if (PendingCount() >= opt.MaxPending)
            {
                pipe.Close(false);
                Inbox.Put(new NetEvent { Kind = NetEventKind.Failed,
                                         Reason = "too many connections in their handshake; one refused" });
                SteamEnded(pipe.Peer, false);
                return false;
            }
            Link link = NewLink(pipe);
            lock (linksLock) links[link.Id] = link;
            if (stopped)
            {
                link.Close("the host is shutting down", false);
                return false;
            }
            Spawn("SEMP link " + link.Id, link.Run);
            return true;
        }

        void Serve(Link link, TcpClient c, SslStream ssl)
        {
            try
            {
                ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12, false);
                if (!ssl.IsEncrypted || !ssl.IsAuthenticated || ssl.SslProtocol != SslProtocols.Tls12)
                    throw new AuthenticationException("not an encrypted TLS 1.2 connection");
                c.ReceiveTimeout = 0;
            }
            catch (Exception e)
            {
                link.Fail("TLS handshake failed (" + e.GetType().Name + ": " + e.Message + ")");
                return;
            }
            link.Run();
        }

        string OnHello(Link link, Message m)
        {
            var h = m as Hello;
            if (h == null) { link.Violation = true; return "expected Hello first, got " + m.GetType().Name; }
            PeerIdentity peer = link.Peer;
            bool member = false;
            string steamName = null;
            if (peer != null && peer.Kind == PeerKind.Steam)
            {
                Func<ulong, bool> isMember = LobbyMember;
                Func<ulong, string> nameOf = SteamName;
                try
                {
                    member = isMember != null && isMember(peer.SteamId);
                    steamName = nameOf != null ? nameOf(peer.SteamId) : null;   // every Steam peer: Steam's name
                }
                catch (Exception) { member = false; steamName = null; }
            }
            HostGate.Verdict v = gate.Check(h, peer, member, steamName);
            if (!v.Ok)
            {
                link.LingerOnClose = link.Send(new Reject
                {
                    Code = v.RejectCode, Reason = TextBudget.FitBytes(v.Reason, 150),
                    HostGameVersion = v.HostGameVersion, HostModVersion = v.HostModVersion,
                    HostGameBuild = v.HostGameBuild,
                });
                return "refused: " + v.Reason;
            }
            Action<Link> afterCheck = TestAfterCheck;
            if (afterCheck != null) afterCheck(link);
            // From here the gate counts this player as connected on this link's token: whatever happens next, closing
            // this link must call Left (OnLinkClosed does, for any link that has a PlayerId; the failure path below covers
            // a close that came before the ids were set). The ids are published under linksLock (Snapshot takes it too, so a
            // later Hello's replace scan sees them), and only then is the gate asked whether this is still the latest
            // connection: a Hello for the same player that overtook this one between Check and here has already scanned for
            // links to replace and missed this one, so this one must give way itself (I-C).
            lock (linksLock)
            {
                link.Token = v.Token;
                link.PlayerId = v.PlayerId;
                link.Name = v.Name;
                link.HelloRejoin = h.Rejoin != null;
            }
            if (!gate.IsCurrent(v.PlayerId, v.Token)) return "superseded by a newer connection of the same player";
            int oldLink = 0;
            if (v.Replaces)
            {
                // The same player, back on a new connection. Close the old link FIRST (its teardown carries its own,
                // now stale, token, so it cannot un-mark the new connection), and tell the game nothing about it:
                // the game sees only Joined for this link.
                // An old link that had already closed by itself reported its own leave: it is not "replaced".
                foreach (Link old in Snapshot())
                    if (old != link && old.PlayerId == v.PlayerId && old.TryReplace("replaced by a new connection"))
                        oldLink = old.Id;
            }
            Action<Link> seam = TestBeforeAccept;
            if (seam != null) seam(link);
            var welcome = new Welcome { PlayerId = v.PlayerId, Rejoin = v.Rejoin, HostName = TextBudget.FitBytes(TextSanitiser.Clean(hostName, 64, "Host"), 192) };
            var joined = new NetEvent { Kind = NetEventKind.Joined, Link = link.Id, PlayerId = v.PlayerId, Name = v.Name, Replaces = oldLink != 0, Rejoin = h.Rejoin != null };
            if (!link.TryAccept(welcome, joined))
            {
                // Closed (deadline, Stop, Kick) while we were deciding: never a player, so no Joined. Make sure the gate
                // lets go (idempotent with OnLinkClosed), and if this took over from a link the game knows, tell the game
                // that player is gone after all, since the old link's own leave was suppressed.
                gate.Undo(v.PlayerId, v.Token);   // the reservation Check made goes back (N-1)
                gate.Left(v.PlayerId, v.Token);
                if (oldLink != 0)
                    Inbox.Put(new NetEvent { Kind = NetEventKind.Closed, Link = oldLink, PlayerId = v.PlayerId, Reason = "replaced by a connection that then failed" });
                return "the connection closed during the handshake";
            }
            SteamEnded(link.Peer, false);   // in: the account may connect again (a rejoin later)
            return null;
        }

        // The first frame after the Welcome arrived: the gate commits (the invite or key is spent), and the game is told,
        // ahead of that frame's own message (this runs before the frame is decoded; the message is put in the inbox after).
        void OnFirstFrame(Link l)
        {
            if (l.PlayerId <= 0) return;
            if (gate.Confirm(l.PlayerId, l.Token))   // nothing is announced for a connection the gate had already given back or replaced
                Inbox.Put(new NetEvent { Kind = NetEventKind.Confirmed, Link = l.Id, PlayerId = l.PlayerId, Name = l.Name, Rejoin = l.HelloRejoin });
        }

        void SteamEnded(PeerIdentity peer, bool failed)
        {
            Action<ulong, bool> ended = SteamHandshakeEnded;
            if (ended == null || peer == null || peer.Kind != PeerKind.Steam) return;
            try { ended(peer.SteamId, failed); }
            catch (Exception) { }
        }

        void OnLinkClosed(Link l)
        {
            lock (linksLock) links.Remove(l.Id);
            if (!l.Accepted) SteamEnded(l.Peer, true);   // never got in: a deadline, a refusal or a breach
            if (l.PlayerId <= 0) return;
            // A rule-breaker loses its rejoin keys, but only while its link is the player's current one (the gate
            // compares the token: a stale link's misbehaviour must not take the player's new connection away).
            if (l.Violation) gate.Revoke(l.PlayerId, l.Token, !l.ByTheHost);
            else
            {
                // What the Check reserved goes back if no first frame committed it (N-1). Always asked: the gate does nothing
                // after a Confirm or for a stale token, and the link's "first frame seen" flag is set BEFORE the Confirm runs, so
                // keying this on it could leave a reservation stuck when the close lands in between (M-1).
                gate.Undo(l.PlayerId, l.Token);
                gate.Left(l.PlayerId, l.Token);   // a stale token (replaced link) changes nothing
            }
        }

        List<Link> Snapshot() { lock (linksLock) return new List<Link>(links.Values); }

        public int PendingCount()
        {
            int n = 0;
            foreach (Link l in Snapshot()) if (!l.Accepted) n++;
            return n;
        }

        public int Broadcast(Message m)
        {
            int n = 0;
            foreach (Link l in Snapshot()) if (l.Accepted && l.Send(m)) n++;
            return n;
        }

        public bool Send(int linkId, Message m)
        {
            Link l;
            lock (linksLock) links.TryGetValue(linkId, out l);
            return l != null && l.Accepted && l.Send(m);
        }

        public void Kick(int linkId, string reason) { Kick(linkId, reason, false, 0); }

        public void Kick(int linkId, string reason, bool revokeKeys) { Kick(linkId, reason, revokeKeys, 0); }

        // revokeKeys: the player loses its rejoin keys as a rule-breaker does (OnLinkClosed revokes for a Violation
        // link). code: why (a JoinCodes value), in the goodbye, so the joiner is told it was removed and why.
        public void Kick(int linkId, string reason, bool revokeKeys, int code)
        {
            Link l;
            lock (linksLock) links.TryGetValue(linkId, out l);
            if (l == null) return;
            if (revokeKeys)
            {
                l.ByTheHost = HostKick.ByTheHost(code);   // set before Violation: the close reads both
                l.Violation = true;
            }
            l.Close(reason, true, code);
        }

        // Every link's goodbye says HostLeft: the host closed the session, so a joiner does not take it for a kick.
        public void Stop(string reason)
        {
            stopped = true;
            try { listener.Stop(); } catch (Exception) { }
            foreach (Link l in Snapshot()) l.Close(reason, true, JoinCodes.HostLeft);
        }

        void BeatLoop()
        {
            while (!stopped)
            {
                try
                {
                    long now = NetClock.NowMs;
                    foreach (Link l in Snapshot()) l.Tick(now);
                }
                catch (Exception) { }   // one bad tick must not end the beat
                Thread.Sleep(100);
            }
        }
    }
}
