using System;
using System.Globalization;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The client follows the host: the host's speed, and its date kept by running faster or pausing (DriftController;
    // the date is never set directly). Its own player's speed changes go to the host as requests, where the slowest
    // request wins; until the host has it, the client never runs faster than what its player just asked for.
    // An open mission planner is sent as a request with the planner flag: it pauses everyone, the host's Clock message
    // says who is planning (shown in the status line), and closing it takes only that pause away.
    // The request itself (what counts, when it goes out) is ClientRequest: until the host hears this player (JoinerFlow
    // .Playing) the game's own calls are not requests and nothing is sent, and the first message after that is the
    // restore the host waits for.
    // Without the host (not yet joined, or dropped) the client stays paused, re-asserting the pause while it is away.
    // The same when the host's Clock stops coming while the link is up (the host is at the main menu or a loading screen:
    // ClockRules.StaleClockMs, "waiting for the host's clock"); it follows again from the next Clock.
    public sealed class ClockClient
    {
        const long SampleEveryMs = 2000, AskSlowerHoldMs = 30000, StatsEveryMs = 250;
        readonly SessionClient client;
        DriftStats stats = new DriftStats(10000);
        readonly ClientRequest request;
        readonly EngineStep engine = new EngineStep();
        readonly HostPace pace = new HostPace();   // the host's achieved pace, from its Clocks
        Clock last;
        long lastAtMs, lastApplyMs, lastSampleMs, lastHoldMs, lastStatsMs, askSlowerUntilMs = -1;
        float nextDevPoll;
        float lastDecided;                    // the speed the last Update decided on (ClampLocal uses it between frames)
        float appliedFor = -1f, appliedGot;   // the target last applied and the speed the engine settled on (it may round)
        ClockMode lastMode = ClockMode.Follow;
        bool warnedStart, warnedOut, askSlowerOn, hostSilent, loadHeld, ended;
        long lastNaNLogMs = -60000;

        public string Status { get; private set; }
        // DD-F10: "Player N is planning (m:ss)" in the status line
        readonly PlanningStatus planningStatus = new PlanningStatus();

        // The command day gate holds this game's fixed step (Session wires it): the gap that opens then is the gate's,
        // not a slow game, so it never asks the host to step down (the hold is no speed vote, DESIGN "Time").
        public Func<bool> GateHolding;

        // M7 DD-W12: the speed this game runs at while it catches up with a paused host (0 once there), or -1 when no
        // catch-up runs. The host's Clock is paused then, so the follower would hold this game at 0.
        public Func<float> CatchUpSpeed;
        long lastCatchMs;
        bool wasCatching;

        // Game seconds this game still runs to stand at a paused host's date (CommandClient.PausedLandingLeft; 0 when
        // none). Session wires it. The step cut (StepGate) ends the run there, never past it.
        public Func<double> PausedLeft;
        bool pausedRun;

        public ClockClient(SessionClient client)
        {
            this.client = client;
            request = new ClientRequest(client.Flow);
            client.Received += OnReceived;
            // (Re)joined: our request goes back to the host once it hears us again (ClientRequest).
            client.Joined += request.LinkUp;
            client.Left += () =>
            {
                if (ended) return;   // the session is over: a late close must not pause a solo game
                last = null;
                pace.Reset();
                hostSilent = false;
                EndSettle("lost the host");
                Hold("lost the host");
            };
            Hold("waiting for the host's clock");
            // The statics go LAST: if anything above throws, Session logs it and clears them, and the game's own clock
            // calls are never routed to a half-built object.
            ClockSync.ClampLocal = Allowed;
            ClockSync.LocalPlanner = request.Planner;
            ClockSync.PlannerGone = request.NewWorld;
            ClockSync.LocalChange = (s, fromPlayer) =>
            {
                if (!request.Local(s, fromPlayer))
                    Log.Info("clock: not a request - the game's own call before this player is in game");
            };
        }

        // The session is over (the player is back at the menu for good): this clock follows and holds nothing more.
        public void Detach()
        {
            ended = true;
            client.Received -= OnReceived;
            client.Joined -= request.LinkUp;
            Status = null;
        }

        void Hold(string why)
        {
            Status = "clock: paused, " + why;
            if (!ClockSync.Ready) return;
            if (ClockSync.Scale != 0f && CanApply) ClockSync.Apply(0f, false);   // else the pause is put back by the away check in Update
            Log.Info("clock: paused, " + why);
        }

        void OnReceived(Message m)
        {
            var c = m as Clock;
            if (c == null) return;
            if (last == null) Log.Info("clock: following the host (host speed " + c.Scale + ")");
            last = c;
            lastAtMs = NetClock.NowMs;
            pace.Add(c.Game, c.HostMs, c.Scale);
        }

        public void Update()
        {
            if (ended || !ClockSync.Ready) return;
            if (DevTriggers.Every(ref nextDevPoll, Time.realtimeSinceStartup)) DevPoll();
            request.NoteHold();   // also on the away frames below, which return before the drift step
            if (CatchingUp()) return;
            bool away = last == null || !client.Connected;
            bool stopped = !away && ClockRules.IsStale(NetClock.NowMs - lastAtMs);
            if (stopped != hostSilent)
            {
                hostSilent = stopped;
                if (stopped) Log.Info("clock: no Clock from the host for over " + ClockRules.StaleClockMs / 1000 + " s - paused, waiting for the host's clock");
                else if (!away) Log.Info("clock: the host's clock is back - following it again");
            }
            if (away || stopped)
            {
                // Away from the host (or its clock has stopped): stay paused, and put the pause back if anything (a popup, the
                // autosave) lifts it.
                if (stopped) { Status = "clock: paused, waiting for the host's clock"; pace.Reset(); }
                EndSettle(stopped ? "the host's clock stopped" : "away from the host");
                KeepPaused();
                return;
            }
            long now = NetClock.NowMs;
            double game = ClockSync.GameSeconds;
            float scale = ClockSync.Scale;
            double rtt = client.RttMs > 0 ? client.RttMs : 0;
            long age = now - lastAtMs + (long)(rtt / 2);
            // The host's pace while it is known (it may run short of its speed), else its measured rate, else the
            // default: never unknown, so a joiner follows from its first step in game.
            double rate = DriftController.FollowRate(pace.RatePer1x, last.RatePer1x);
            double hostNow = DriftController.PredictHost(last.Game, last.Scale, rate, age);
            ClockDecision d;
            bool fresh;
            bool locked = last.Locked || ClockSync.Locked;
            request.Heard(last.Votes, client.PlayerId, now);   // the host's answer to a slow-down: land on its date
            string grace = request.GateHeld(Gated(), hostNow, game, last.Scale);
            if (grace != null) Log.Info("clock: " + grace);
            // Measured with this game's own last old-speed step after a drop it just made (ClockRules.SpeedStart).
            if (!request.Measure(hostNow, engine.Start(game), last.Scale, rate, locked, out d, out fresh,
                                 DriftController.PaceAgainst(pace.RatePer1x)))
            {
                // Loading the session's world (ClientRequest.Holding): held at 0, nothing measured.
                if (!loadHeld)
                {
                    loadHeld = true;
                    EndSettle("the session's game loads");   // a full rejoin's load: a new drift controller after
                    Log.Info("clock: paused while the session's game loads");
                }
                Status = "clock: paused, loading the session's game";
                lastDecided = 0f;
                KeepPaused();
                return;
            }
            // a settle after a drop started or ended: clock-compare reads these lines
            if (d.Note != null) Log.Info("clock: " + d.Note);
            if (fresh)
            {
                // Measuring starts over in the loaded world: the gap from here is the real one.
                stats = new DriftStats(10000);
                warnedOut = false;
                lastMode = ClockMode.Follow;
                askSlowerUntilMs = -1;
            }
            if (loadHeld)
            {
                loadHeld = false;
                Log.Info("clock: in game - following the host (gap " + Fmt(d.GapSeconds) + " s)");
            }
            if (Math.Abs(last.Start - ClockSync.StartSeconds) > 1)
            {
                if (!warnedStart) { warnedStart = true; Log.Error("clock: the host's save starts on a different date - not following (a date check only: it cannot tell two saves with the same start date apart)"); }
                Status = "clock: NOT FOLLOWING - the host has a different save";
                return;
            }

            // The request to the host: this player's own, or one step below the top speed while it is behind at the top
            // (once asked, held for at least 30 s so the request does not flap as the gap closes and reopens).
            long nowMs = NetClock.NowMs;
            bool behind = d.AskSlower && !Gated();
            if (behind) askSlowerUntilMs = nowMs + AskSlowerHoldMs;
            bool askSlower = behind || nowMs < askSlowerUntilMs;
            if (askSlower != askSlowerOn)
            {
                askSlowerOn = askSlower;
                Log.Info(askSlower ? "clock: behind at the top speed - asking the host to step down to " + DriftController.StepBelowMax + " for at least " + AskSlowerHoldMs / 1000 + " s"
                                   : "clock: step-down request over - asking for this player's own speed again");
            }
            float want = request.Want(askSlower);
            // Only listed speeds go out: catch-up and hold speeds stay local.
            SpeedRequest ask = request.Due(want, nowMs);
            if (ask != null && client.Send(ask))
            {
                request.Sent(ask, nowMs);
                Log.Info("clock: asked the host for " + want
                         + (want == Clock.MaxScale ? " (no request of its own)" : "")
                         + (ask.Planning ? " (planning a mission)" : "")
                         + (askSlower ? " (behind at the top speed)" : ""));
            }

            // The planner pauses this game at once; the host's answer follows.
            float target = request.Target(d.Scale, want, last.Votes, client.PlayerId);
            // The host is paused ahead of this game: run to its date (PausedLanding), so what it applied there shows
            // now and not when time runs again. This player's own pause or planner only keeps this game from passing
            // the host, and a paused host's date cannot be passed (the step ends there).
            double left = last.Scale == 0f && !locked ? PausedRunLeft() : 0;
            bool run = left > 0;
            if (run != pausedRun)
            {
                pausedRun = run;
                Log.Info(run ? string.Format(CultureInfo.InvariantCulture,
                        "clock: the host is paused {0:0.0} game s ahead - running to its date", left)
                    : "clock: paused run over (at the host's date, or the host runs)");
            }
            if (run) target = PausedLanding.Speed(left, rate);
            bool modeChanged = d.Mode != lastMode;
            // Not during the countdown, the end-game lock or the autosave's silent pause; afterwards it resumes at once.
            bool blocked = !CanApply || d.Mode == ClockMode.Locked;
            lastDecided = d.Scale;
            // After a snap (the engine rounded the speed) the speed it settled on counts as reached, so it is not re-applied every 250 ms.
            bool settled = target == appliedFor && ClockRules.Close(scale, appliedGot);
            if (!blocked && !settled && ClockRules.ApplyNow(target, scale, modeChanged, d.Landing, now - lastApplyMs))
            {
                bool silent = d.Mode == ClockMode.CatchUp || d.Mode == ClockMode.Hold || d.Mode == ClockMode.Ease
                              || d.Paced || run;
                appliedGot = ClockSync.Apply(target, silent);
                appliedFor = target;
                lastApplyMs = now;
                if (!silent) Log.Info("clock: speed " + target + " (host " + last.Scale + ")");
            }
            if (modeChanged) Log.Info("clock: " + lastMode + " -> " + d.Mode + " (gap " + Fmt(d.GapSeconds) + " s)");
            lastMode = d.Mode;

            if (double.IsNaN(d.GapSeconds) && now - lastNaNLogMs >= 60000)
            {
                lastNaNLogMs = now;
                Log.Info("clock: gap unknown (" + d.Mode + "); it neither counts nor clears the out-of-step check");
            }
            string summary = null;
            if (now - lastStatsMs >= StatsEveryMs) { lastStatsMs = now; summary = stats.Add(now, d.GapSeconds, d.Mode); }   // real samples, not frames
            if (summary != null) Log.Info(summary);
            if (stats.OutOfStep(now))
            {
                if (!warnedOut) { warnedOut = true; Log.Warn("clock: OUT OF STEP with the host by " + Fmt(d.GapSeconds) + " s for over 10 s"); }
            }
            else if (warnedOut) { warnedOut = false; Log.Info("clock: back in step"); }
            if (now - lastSampleMs >= SampleEveryMs)
            {
                lastSampleMs = now;
                Log.Info(string.Format(CultureInfo.InvariantCulture, "clock: sample game={0:0.000} scale={1:0} mode={2} gap={3} daysBehind={4}", game, scale, d.Mode, Fmt(d.GapSeconds), ClockSync.DaysBehind));
            }
            string why = request.Planning ? "Paused: you are planning a mission" : planningStatus.Describe(last.Pauses, NetClock.NowMs);
            Status = "clock: " + d.Mode + ", gap " + Fmt(d.GapSeconds) + " s, speed " + ClockSync.Scale + (warnedOut ? " - OUT OF STEP" : "") + (why.Length > 0 ? " - " + why : "");
        }

        // True while the catch-up runs this game's clock itself. When it ends the follower measures afresh.
        bool CatchingUp()
        {
            float speed = -1f;
            Func<float> want = CatchUpSpeed;
            try { if (want != null) speed = want(); }
            catch (Exception) { speed = -1f; }
            if (speed < 0f || !client.Connected)
            {
                if (wasCatching)
                {
                    wasCatching = false;
                    EndSettle("the catch-up is over");
                    stats = new DriftStats(10000);
                    Log.Info("clock: catch-up over - following the host again");
                }
                return false;
            }
            if (!wasCatching) { wasCatching = true; Log.Info("clock: catching up with the host at speed " + speed); }
            Status = "clock: catching up with the host";
            long t = NetClock.NowMs;
            if (t - lastCatchMs >= ClockRules.ApplyEveryMs && CanApply && !ClockRules.Close(ClockSync.Scale, speed))
            {
                lastCatchMs = t;
                ClockSync.Apply(speed, true);
            }
            return true;
        }

        double PausedRunLeft()
        {
            Func<double> f = PausedLeft;
            try { return f != null ? f() : 0; }
            catch (Exception) { return 0; }
        }

        bool Gated()
        {
            Func<bool> g = GateHolding;
            try { return g != null && g(); }
            catch (Exception) { return false; }
        }

        // No drift step will end a settle after a drop now: say it is over, for clock-compare.
        void EndSettle(string why)
        {
            string note = request.EndSettle(why);
            if (note != null) Log.Info("clock: " + note);
        }

        // Puts the pause back if anything (a popup, the autosave, the game's own load code) lifts it.
        void KeepPaused()
        {
            long t = NetClock.NowMs;
            if (t - lastHoldMs < ClockRules.ApplyEveryMs) return;
            lastHoldMs = t;
            if (ClockSync.Scale != 0f && CanApply) ClockSync.Apply(0f, false);
        }

        static bool CanApply { get { return ClockRules.CanApply(ClockSync.CountDown, ClockSync.Locked, ClockSync.GameSilentPause); } }

        // What the session lets a speed call run at right now (ClientRequest.Allow: nothing while the session's game
        // loads). Without the host, or while its clock has stopped: paused.
        float Allowed(float requested)
        {
            Clock c = last;
            if (c == null || !client.Connected || ClockRules.IsStale(NetClock.NowMs - lastAtMs)) return 0f;
            return request.Allow(requested, lastDecided, c.Votes, client.PlayerId);
        }

        static string Fmt(double gap)
        {
            return double.IsNaN(gap) ? "?" : gap.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }

        // Dev triggers: "speed" sets this game's speed the way a click would (it becomes this player's request);
        // "planner" (open or close) calls the game's own planner methods, which the clock patch turns into a pause.
        void DevPoll()
        {
            string s = DevTriggers.Take("speed");
            float v;
            if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= Clock.MaxScale)
                ClockSync.PlayerSets(v);
            string p = DevTriggers.Take("planner");
            if (p == "open") ClockSync.DevPlanner(true);
            else if (p == "close") ClockSync.DevPlanner(false);
        }
    }
}
