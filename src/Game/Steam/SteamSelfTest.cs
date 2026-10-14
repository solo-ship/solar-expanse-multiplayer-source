using System;
using System.Collections.Generic;
using System.Threading;
using Steamworks;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The one-PC Steam test (dev trigger "steam-selftest", test profiles only). One account on one PC cannot make a
    // real relay connection, so this runs what one PC can: readiness and the relay, then the hostile frame list over
    // network-loopback socket pairs (real packets through Steam) with the real SteamPipe and Link, then a listen socket
    // with ICE read back, then the teardown. Every result is one "steam-selftest: PASS|FAIL <case>: ..." line. Never
    // asks for a ticket, never invites, sets no presence, touches no stats; no Steam id is logged. The main thread
    // steps through it (state callbacks need the game's pump running); the frame cases run on a worker.
    public sealed class SteamSelfTest
    {
        readonly SteamNet net;
        readonly NetOptions opt = new NetOptions();
        int step;
        float stepAt;
        SteamTransport t;
        Thread worker;
        volatile bool workerDone, aborted;
        int pass, fail;
        readonly object countLock = new object();

        public bool Done { get; private set; }

        public SteamSelfTest(SteamNet net)
        {
            this.net = net;
            stepAt = Time.realtimeSinceStartup;
            Log.Info("steam-selftest: started");
        }

        float InStep { get { return Time.realtimeSinceStartup - stepAt; } }
        void Next(int s) { step = s; stepAt = Time.realtimeSinceStartup; }

        void Report(bool ok, string name, string detail)
        {
            lock (countLock) { if (ok) pass++; else fail++; }
            Log.Info("steam-selftest: " + (ok ? "PASS " : "FAIL ") + name + ": " + detail);
        }

        public void Abort(string why)
        {
            aborted = true;
            Report(false, "aborted", why);
            Finish();
        }

        void Finish()
        {
            if (Done) return;
            Done = true;
            if (t != null) { t.Stop(); net.Forget(t); }
            Log.Info("steam-selftest: DONE " + pass + " passed, " + fail + " failed");
        }

        public void Update()
        {
            switch (step)
            {
                case 0:
                    net.Ready.Refresh();
                    if (net.Ready.Up)
                    {
                        CSteamID me = SteamUser.GetSteamID();
                        Report(true, "steam-ready", "the game's Steam manager initialised, callbacks running, "
                            + "logged on; Steamworks.NET " + Steamworks.Version.SteamworksNETVersion
                            + ", SDK " + Steamworks.Version.SteamworksSDKVersion
                            + "; own Steam id " + (me.IsValid() && me.BIndividualAccount() ? "present" : "NOT valid"));
                        Next(1);
                    }
                    else if (InStep > 60f) { Report(false, "steam-ready", net.Ready.Why); Finish(); }
                    break;
                case 1:
                    net.Ready.StartRelay();
                    Next(2);
                    break;
                case 2:
                    net.Ready.Refresh();
                    if (net.Ready.Relay == RelayState.Ready)
                    {
                        Report(true, "relay", "Steam's relay network is current after "
                            + InStep.ToString("0.0") + " s");
                        Next(3);
                    }
                    else if (net.Ready.Relay == RelayState.Failed || InStep > 35f)
                    { Report(false, "relay", net.Ready.RelayReason ?? "no answer"); Next(3); }
                    break;
                case 3:
                    t = net.NewTransport("selftest", opt);
                    if (t == null || !t.Begin()) { Report(false, "transport", "could not start"); Finish(); break; }
                    Report(true, "transport", "poll group, state callback and pump thread started");
                    worker = new Thread(Cases) { IsBackground = true, Name = "SEMP steam selftest" };
                    worker.Start();
                    Next(4);
                    break;
                case 4:
                    if (workerDone) Next(5);
                    else if (InStep > 240f) { Report(false, "frame cases", "not finished in 240 s"); Next(5); }
                    break;
                case 5:
                    // nothing is accepted: the self-test's door refuses everyone
                    bool listening = t.Listen(c => "the self-test takes no connections", p => p.Close(false));
                    Report(listening && t.ListenIce == SteamWireRules.IceDisable, "listen-socket",
                        listening ? "P2P listen socket on virtual port 0; ICE reads " + t.ListenIce
                                  : "no listen socket");
                    Next(6);
                    break;
                case 6:
                    t.Stop();
                    Next(7);
                    break;
                case 7:
                    if (t.PumpRunning && InStep < 3f) break;
                    bool clean = !t.Started && !t.Listening && !t.HasPollGroup && t.OpenConnections == 0
                                 && !t.PumpRunning;
                    Report(clean, "teardown", "started " + t.Started + ", listen socket " + t.Listening
                        + ", poll group " + t.HasPollGroup + ", connections " + t.OpenConnections
                        + ", pump running " + t.PumpRunning);
                    Report(t.StateCallbacks > 0 && t.StateCallbacksOffMainThread == 0, "state-callbacks",
                        t.StateCallbacks + " connection-state callback(s) through the game's pump, "
                        + t.StateCallbacksOffMainThread + " off the main thread");
                    Finish();
                    break;
            }
        }

        // ---- the worker: frame cases over network-loopback socket pairs ----

        void Cases()
        {
            try
            {
                Good();
                Hostile("oversize-message", p => p.WriteFrame(Body(65537)), "over the frame cap");
                // the largest frame passes the pipe and the pump whole, and is refused only by the decoder
                Hostile("max-frame", p => p.WriteFrame(Body(65536)), "bad message");
                Hostile("oversize-length", p => p.WriteFrame(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 1, 2, 3 }),
                    "bad frame");
                Hostile("negative-length", p => p.WriteFrame(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 }),
                    "bad frame");
                Hostile("zero-length", p => p.WriteFrame(new byte[] { 0, 0, 0, 0 }), "bad frame");
                Hostile("prefix-says-more", p => p.WriteFrame(Prefixed(200, 100)), "disagrees");
                Hostile("prefix-says-less", p => p.WriteFrame(Prefixed(10, 100)), "disagrees");
                Hostile("shorter-than-prefix", p => p.WriteFrame(new byte[] { 0, 0 }), "shorter than a frame prefix");
                Hostile("flood", p =>
                {
                    for (uint i = 1; i <= 600; i++) p.WriteFrame(Frame.Wrap(Envelope.Encode(new Ping { T = i }, i)));
                }, "rate limit");
                CloseMidFrame();
                Unauthenticated();
            }
            catch (Exception e) { Report(false, "frame cases", "threw " + e.GetType().Name + ": " + e.Message); }
            finally { workerDone = true; }
        }

        // A whole frame (a Ping header) whose body is this many bytes.
        static byte[] Body(int bodyBytes)
        {
            return Frame.Wrap(Envelope.Raw(4, 1, new byte[bodyBytes - Envelope.HeaderSize]));
        }

        static byte[] Prefixed(int says, int bodyBytes)
        {
            var m = new byte[4 + bodyBytes];
            m[0] = (byte)(says >> 24); m[1] = (byte)(says >> 16); m[2] = (byte)(says >> 8); m[3] = (byte)says;
            return m;
        }

        bool NewPair(string name, out SteamPipe a, out SteamPipe b)
        {
            string why = "aborted";
            a = b = null;
            if (aborted || !t.Pair(true, true, out a, out b, out why))
            {
                a = b = null;
                Report(false, name, "no socket pair: " + why);
                return false;
            }
            return true;
        }

        static Link Start(SteamPipe p, Inbox inbox, Dir arriving, NetOptions opt)
        {
            var l = new Link(p, opt, inbox, arriving);
            l.TryAccept(null, null);
            new Thread(l.Run) { IsBackground = true, Name = "SEMP steam selftest link" }.Start();
            return l;
        }

        // The healthy path: a Ping crosses Steam, the other Link answers, the Pong comes back; a Bye ends the other
        // end.
        void Good()
        {
            SteamPipe pa, pb;
            if (!NewPair("good", out pa, out pb)) return;
            var ia = new Inbox(null); var ib = new Inbox(null);
            Link a = Start(pa, ia, Dir.ToHost, opt), b = Start(pb, ib, Dir.ToClient, opt);
            a.Send(new Ping { T = 12345 });
            NetEvent pong = Wait(ia, e => e.Kind == NetEventKind.Message && e.Message is Pong, 8000);
            b.Close("self-test done", true);
            NetEvent end = Wait(ia, e => e.Kind == NetEventKind.Closed || e.Kind == NetEventKind.Failed, 8000);
            bool ok = pong != null && ((Pong)pong.Message).T == 12345 && end != null && end.Bye && !end.Violation;
            Report(ok, "good", (pong != null ? "Ping -> Pong round trip" : "no Pong") + "; "
                + (end == null ? "the other end never closed"
                   : "closed with " + (end.Bye ? "a Bye" : "no Bye") + ": " + end.Reason));
            a.Close("self-test done", false);
        }

        // One hostile send from a bare SteamPipe into a Link: the Link must close as a violation for the expected
        // reason, and nothing the peer sent may reach the inbox.
        void Hostile(string name, Action<SteamPipe> send, string expect)
        {
            SteamPipe pa, pb;
            if (!NewPair(name, out pa, out pb)) return;
            var inbox = new Inbox(null);
            Link rx = Start(pb, inbox, Dir.ToHost, opt);
            string sendNote = "";
            try { send(pa); }
            catch (Exception e) { sendNote = "; the send stopped: " + e.GetType().Name; }
            var seen = new List<NetEvent>();
            NetEvent end = Wait(inbox, e => e.Kind == NetEventKind.Closed || e.Kind == NetEventKind.Failed, 8000, seen);
            bool leaked = seen.Exists(e => e.Kind == NetEventKind.Message);
            bool ok = end != null && end.Violation && end.Reason != null && end.Reason.Contains(expect) && !leaked;
            Report(ok, name, (end == null ? "the link stayed open" : "closed" + (end.Violation ? " as a violation" : "")
                + ": " + end.Reason) + (leaked ? "; a message REACHED the inbox" : "") + sendNote);
            pa.Close(false);
            rx.Close("self-test done", false);
        }

        // A whole frame, then the first half of one, then the sender closes at once. Steam keeps message boundaries, so
        // the half arrives as one message whose prefix promises more (a violation) or not at all (a plain close); the
        // half is never read as a frame either way.
        void CloseMidFrame()
        {
            SteamPipe pa, pb;
            if (!NewPair("close-mid-frame", out pa, out pb)) return;
            var inbox = new Inbox(null);
            Link rx = Start(pb, inbox, Dir.ToHost, opt);
            pa.WriteFrame(Frame.Wrap(Envelope.Encode(new Ping { T = 1 }, 1)));
            pa.WriteFrame(Prefixed(200, 100));
            pa.Close(false);
            var seen = new List<NetEvent>();
            NetEvent end = Wait(inbox, e => e.Kind == NetEventKind.Closed || e.Kind == NetEventKind.Failed, 8000, seen);
            bool leaked = seen.Exists(e => e.Kind == NetEventKind.Message);
            bool ok = end != null && !leaked && (!end.Violation || end.Reason.Contains("disagrees"));
            Report(ok, "close-mid-frame", end == null ? "the link stayed open"
                : (end.Violation ? "the half frame was refused: " : "closed as the sender went: ") + end.Reason);
            rx.Close("self-test done", false);
        }

        // The flags check through the real path: the same localhost pair without the test's allowance is refused.
        void Unauthenticated()
        {
            SteamPipe a = null, b = null;
            string why = "aborted";
            bool made = !aborted && t.Pair(true, false, out a, out b, out why);
            Report(!made && why != null && why.Contains("not authenticated"), "unauthenticated-refused",
                made ? "the unauthenticated pair was ACCEPTED" : "refused: " + why);
            if (made) { a.Close(false); b.Close(false); }
        }

        static NetEvent Wait(Inbox inbox, Func<NetEvent, bool> match, int ms, List<NetEvent> seen = null)
        {
            var batch = new List<NetEvent>();
            long until = NetClock.NowMs + ms;
            while (NetClock.NowMs < until)
            {
                batch.Clear();
                inbox.Drain(NetClock.NowMs, 100, batch);
                foreach (NetEvent e in batch)
                {
                    if (match(e)) return e;
                    if (seen != null) seen.Add(e);
                }
                Thread.Sleep(10);
            }
            return null;
        }
    }
}
