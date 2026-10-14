using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The host drives the clock. Every player's speed request (the host player's own clicks included) goes into a
    // SpeedVote; the slowest wins and the host applies it. A mission planner pauses everyone with a reason; a dropped
    // player pauses everyone until somebody asks for a speed (SpeedVote). A Clock message goes out 4 times a second and on
    // every speed change: a pause or slow-down at once, a speed-up at most every 50 ms (ClockRules.ShouldBroadcast). It is
    // sent at the end of the frame (EndOfFrame, from SempRoot.LateUpdate) with the speed the frame ended with, so a speed
    // set and taken back within one frame (M3 R5: the stop hold undoing a resume) never leaves the host. A player's
    // speed-ups are acted on at most 4 a second, keeping the latest, and a slow-down or pause at once
    // (RequestCoalescer); the rest are logged as coalesced.
    // Things that change the host's speed behind the vote's back (the autosave restoring the speed it saved, a popup)
    // are undone: when the speed has differed from the vote's for over a second, the vote is applied again.
    // M4 (I-12): with a join service, only players who have loaded steer the clock (their speed, vote and planner
    // flag), and only their drop holds everyone; a joiner's joining pause is one more reason in the vote.
    public sealed class ClockHost
    {
        const long SampleEveryMs = 2000, SummaryEveryMs = 10000, FightBackAfterMs = 1000;
        readonly SessionHost host;
        readonly SpeedVote vote = new SpeedVote();
        readonly SpeedChoices choices = new SpeedChoices();
        readonly RateMeter rate = new RateMeter();
        readonly EngineStep engine = new EngineStep();
        long lastClockMs = -ClockRules.HeartbeatMs, lastSampleMs, lastSummaryMs, differsSinceMs = -1;
        readonly RequestCoalescer coalescer = new RequestCoalescer();
        float lastSentScale = -1f, nextDevPoll;
        bool pending, devClockStopped;
        readonly HashSet<int> fresh = new HashSet<int>();   // joined, first message not yet seen
        int ignoredLogged;
        long lastIgnoredLogMs = -60000;
        ClockAdmission admission = new ClockAdmission(null);   // no join service: every player counts (M2)
        Action<int> afterLeft;

        public ClockHost(SessionHost host)
        {
            this.host = host;
            vote.Set(0, ClockSync.Ready ? ClockSync.Scale : 0f);   // the host player's request starts as the speed the save loaded with
            host.Received += OnReceived;
            host.Joined += id => fresh.Add(id);
            // N-2: the same player back on a new link while the host still had the old one (no Left in between). The new
            // client may be a new process (its choice counter restarts), and a request held for the old link is no longer
            // anyone's: the player is fresh again, its first message a restore, never a choice.
            host.Rejoined += id =>
            {
                fresh.Add(id);
                coalescer.Forget(id);
                Log.Info("clock: player " + id + " rejoined on a new link - its first message is a restore");
            };
            host.Left += id =>
            {
                // asked BEFORE the join service hears of the leave (afterLeft), which ends its in-game state there
                bool hold = admission.DropHolds(id);
                fresh.Remove(id);
                choices.Forget(id);
                coalescer.Forget(id);
                if (hold)
                {
                    Log.Info("clock: player " + id + " dropped - paused; any player's speed choice resumes");
                    vote.Drop(id);
                    Enforce("drop hold");
                }
                else
                {
                    vote.Remove(id);
                    vote.SetPlanning(id, false);
                    Log.Info("clock: player " + id + " left before it had loaded - no drop hold");
                }
                Action<int> after = afterLeft;
                if (after != null) after(id);
            };
            // after Left: a goodbye with the leave code ends that player's holds and requests, the drop hold included
            host.LeftForGood += PlayerLeft;
            Log.Info("clock: the host drives the clock; start speed " + ClockSync.Scale);
            // The statics go LAST: if anything above throws, Session logs it and clears them, and the game's own clock
            // calls are never routed to a half-built object (cloud review G-2).
            ClockSync.LocalPlanner = open => { vote.SetPlanning(0, open); Enforce(open ? "host player is planning a mission" : "host player closed the planner"); };
            // A load or the menu took the host player's planner windows with the old world, unclosed: its pause goes,
            // and FightBack puts the vote's speed back once the world runs (no Apply here: mid-load or at the menu).
            ClockSync.PlannerGone = () =>
            {
                if (!vote.IsPlanning(0)) return;
                vote.SetPlanning(0, false);
                Log.Info("clock: the host player's planner went with the old world - its pause is lifted");
            };
            ClockSync.LocalChange = (s, fromPlayer) =>
            {
                bool held = vote.DropHold;
                vote.Choose(0, s, fromPlayer);
                if (!fromPlayer && vote.DropHold) Log.Info("clock: game call while held - hold kept (host player)");
                Enforce("host player asked for " + s + ClockRules.HoldNote(held, vote.DropHold));
            };
        }

        // A slow-down or pause is acted on at once; a player's other requests at most 4 a second, keeping the latest
        // (RequestCoalescer): the rest wait in the coalescer and Update acts on them when their window has passed.
        void OnReceived(int player, Message m)
        {
            var r = m as SpeedRequest;
            if (r == null || !Admitted(player)) return;
            SpeedRequest now = coalescer.Offer(player, r, NetClock.NowMs);
            if (now != null) Act(player, now);
        }

        void Act(int player, SpeedRequest r)
        {
            // Range already checked by SpeedRequest.Validate. What counts as a speed choice is the client's Choice counter
            // going up (a click on the speed it already asked for included), and only a choice the player made (a click
            // or hotkey, not a call the game made) ends a drop hold. The first message after a (re)join is the client
            // putting its request back: it is never a choice, so a rejoin cannot end the hold, and opening the planner
            // does not end it either. A counter that went backwards is dropped. A player not in game (still joining, or
            // in a full rejoin) is not heard at all, and its fresh mark is kept for its first request after Loaded
            // (I-12).
            if (!Admitted(player)) return;
            bool handshake = fresh.Remove(player);
            RequestKind kind = choices.Classify(player, r.Choice, handshake);
            if (kind == RequestKind.Ignore) { Log.Info("clock: player " + player + "'s request ignored (its choice counter went backwards)"); return; }
            bool wasPlanning = vote.IsPlanning(player), held = vote.DropHold;
            vote.Take(player, r, kind);
            if (kind == RequestKind.NewChoice && !r.Player && vote.DropHold)
                Log.Info("clock: game call while held - hold kept (player " + player + ")");
            Enforce("player " + player + (handshake ? " (re)joined with " : " asked for ") + r.Scale
                + (r.Planning ? " and is planning a mission" : wasPlanning ? " and closed the planner" : "")
                + ClockRules.HoldNote(held, vote.DropHold));
        }

        bool Admitted(int player)
        {
            bool log;
            if (admission.Admit(player, out log)) return true;
            if (log) Log.Info("clock: player " + player + " is not in game - speed request ignored");
            return false;
        }

        // The join service (JoinHost): who is in game, and what to tell it after a leave (Left, which ends the in-game
        // state, so the clock asks first).
        public void UseJoinService(Func<int, bool> inGame, Action<int> afterLeft)
        {
            admission = new ClockAdmission(inGame);
            this.afterLeft = afterLeft;
        }

        // A player has loaded (or quick-rejoined): its first speed message is a restore, never a choice.
        public void PlayerInGame(int player)
        {
            fresh.Add(player);
            bool held = vote.DropHold;
            vote.Rejoined(player);   // DropRules decides whether a finished rejoin ends that player's drop hold
            if (held && !vote.DropHold) Enforce("player " + player + " is back - their drop hold is lifted");
            Log.Info("clock: player " + player + " is in game - its first speed message is a restore");
        }

        // The host's Carry on: the drop hold ends and the session runs at the slowest remaining request.
        public void CarryOn()
        {
            if (!vote.DropHold) return;
            vote.CarryOn();
            Enforce("the host carried on");
        }

        // A player left for good (their goodbye, not a dropped link): their vote and pauses are gone.
        public void PlayerLeft(int player)
        {
            vote.Leave(player);
            Enforce("player " + player + " left for good");
        }

        // A player's joining pause (JoinService, through JoinHost): on for every member of a batch until the batch
        // ends.
        public void SetJoining(int player, bool on)
        {
            vote.SetJoining(player, on);
            Enforce(on ? "player " + player + " is joining" : "player " + player + "'s joining pause lifted");
        }

        // A dropped player is being replayed what it missed (CommandService.ResumeStarted / ResumeFinished): the session is
        // paused with its name until the host releases it. The pause replaces that player's drop hold in the same call.
        public void SetCatchingUp(int player, bool on)
        {
            vote.SetCatchingUp(player, on);
            Enforce(on ? "player " + player + " is catching up" : "player " + player + "'s catch-up pause lifted");
        }

        void Enforce(string why)
        {
            float target = vote.Effective();
            if (target < 0 || ClockSync.Locked) return;
            if (Mathf.Approximately(ClockSync.Scale, target)) { Log.Info("clock: " + why + "; speed stays " + target); return; }
            if (ClockSync.CountDown || ClockSync.GameSilentPause) { pending = true; Log.Info("clock: " + why + "; countdown or autosave running, applied after it"); return; }
            ClockSync.Apply(target, false);
            Log.Info("clock: " + why + "; slowest request wins: " + target + " (votes " + Votes() + ")");
        }

        public void Update()
        {
            if (!ClockSync.Ready) return;
            long now = NetClock.NowMs;
            if (DevTriggers.Every(ref nextDevPoll, Time.realtimeSinceStartup)) DevPoll();
            if (pending && !ClockSync.CountDown && !ClockSync.GameSilentPause) { pending = false; Enforce("countdown or autosave over"); }
            int who, reportPlayer, reportCount;
            SpeedRequest held;
            while ((held = coalescer.Due(now, out who)) != null) Act(who, held);
            while (coalescer.NextReport(now, out reportPlayer, out reportCount))
                Log.Info("clock: player " + reportPlayer + ": " + reportCount + " request" + (reportCount == 1 ? "" : "s") + " coalesced (only the latest was acted on)");
            FightBack(now);
        }

        // At the end of the frame (SempRoot.LateUpdate, after Drift.Update and the game's own Update calls): the rate sample,
        // the Clock message and the log lines, all from the date and speed the frame ended with.
        public void EndOfFrame()
        {
            if (!ClockSync.Ready) return;
            long now = NetClock.NowMs;
            double game = ClockSync.GameSeconds;
            float scale = ClockSync.Scale;
            // A drop this frame still owes the engine's step at the old speed: the Clock and the rate stretch start
            // from the date the new speed starts from (ClockRules.SpeedStart), not one old-speed step short of it.
            double start = engine.Start(game);
            rate.Add(now, start, scale);
            if (rate.Ignored != ignoredLogged && now - lastIgnoredLogMs >= 5000)
            {
                ignoredLogged = rate.Ignored; lastIgnoredLogMs = now;
                Log.Warn("clock: rate sample ignored (" + rate.LastIgnored + "); " + rate.Ignored + " so far, rate kept at " + rate.RatePer1x.ToString("0.###", CultureInfo.InvariantCulture));
            }
            if (!devClockStopped && ClockRules.ShouldBroadcast(now, lastClockMs, scale, lastSentScale))
            {
                lastClockMs = now;
                lastSentScale = scale;
                if (start > game)
                    Log.Info(string.Format(CultureInfo.InvariantCulture, "clock: the Clock at {0:0} carries the "
                        + "engine's last step at the old speed ({1:0.0} game s)", scale, start - game));
                host.Broadcast(new Clock { Game = start, Start = ClockSync.StartSeconds, Scale = scale,
                                           RatePer1x = rate.RatePer1x,
                                           HostMs = now, Locked = ClockSync.Locked, Votes = vote.Snapshot(), Pauses = vote.Pauses() });
            }
            if (now - lastSampleMs >= SampleEveryMs)
            {
                lastSampleMs = now;
                Log.Info(string.Format(CultureInfo.InvariantCulture, "clock: sample game={0:0.000} scale={1:0} mode=host gap=0 daysBehind={2}", game, scale, ClockSync.DaysBehind));
            }
            if (now - lastSummaryMs >= SummaryEveryMs)
            {
                lastSummaryMs = now;
                Log.Info(string.Format(CultureInfo.InvariantCulture, "clock: host speed {0:0}, {1:0.###} game s per real s at 1x, votes {2}", scale, rate.RatePer1x, Votes()));
            }
        }

        // The speed must follow the vote. Something else (the autosave putting back the speed it saved before the vote
        // changed, a popup) can leave it different; after a second of that the vote is applied again. Not during the
        // countdown, the end-game lock or the autosave's own pause.
        void FightBack(long now)
        {
            float target = vote.Effective();
            if (target < 0 || ClockSync.Locked || ClockSync.CountDown || ClockSync.GameSilentPause || pending || Mathf.Approximately(ClockSync.Scale, target))
            {
                differsSinceMs = -1;
                return;
            }
            if (differsSinceMs < 0) { differsSinceMs = now; return; }
            if (now - differsSinceMs < FightBackAfterMs) return;
            differsSinceMs = -1;
            float was = ClockSync.Scale;
            Enforce("the speed was " + was + " behind the vote's back");
        }

        // Dev triggers: "speed" sets the host's speed the way a click would; "planner" (open or close) calls the game's
        // own planner methods, which the clock patch turns into a pause request with a reason; "clock" holding "stop" makes
        // the host stop sending Clock messages (as at its main menu: the link stays up on pings), "go" sends them again.
        void DevPoll()
        {
            string c = DevTriggers.Take("clock");
            if (c == "stop" || c == "go")
            {
                devClockStopped = c == "stop";
                Log.Info(devClockStopped ? "clock: dev trigger - no Clock messages will be sent until \"go\"" : "clock: dev trigger - sending Clock messages again");
            }
            string s = DevTriggers.Take("speed");
            float v;
            if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= Clock.MaxScale)
                ClockSync.PlayerSets(v);
            string p = DevTriggers.Take("planner");
            if (p == "open") ClockSync.DevPlanner(true);
            else if (p == "close") ClockSync.DevPlanner(false);
        }

        // DD-F10: "Player N is planning (m:ss)" in the status line
        readonly PlanningStatus planningStatus = new PlanningStatus();

        string Votes()
        {
            var sb = new StringBuilder();
            foreach (Vote v in vote.Snapshot()) sb.Append(sb.Length > 0 ? ", " : "").Append(v.Player).Append(':').Append(v.Scale.ToString(CultureInfo.InvariantCulture));
            string why = planningStatus.Describe(vote.Pauses(), NetClock.NowMs);
            if (why.Length > 0) sb.Append(" | ").Append(why);
            return sb.ToString();
        }

        // A save was loaded while the session runs: the date moved, so the rate stretch in progress means nothing.
        public void SaveLoaded() { rate.Reset(); Log.Info("clock: save loaded, rate measurement restarted"); }

        public string Status
        {
            get
            {
                string why = planningStatus.Describe(vote.Pauses(), NetClock.NowMs);
                return "clock: host, speed " + ClockSync.Scale + (why.Length > 0 ? " - " + why : ", votes " + Votes());
            }
        }
    }

    // The engine's last old-speed step after a drop (ClockRules.SpeedStart), read from the engine itself:
    // GravityEngine's private timeZoom is the speed its next fixed step runs at until that step has run, GetTimeZoom()
    // the speed set; both are the speed / 100000 (TimeController.SetTimeScaleGravityEngine). A build without the
    // field carries nothing (the short Clock of before) and says so once. Host and client each own one.
    internal sealed class EngineStep
    {
        readonly AccessTools.FieldRef<GravityEngine, float> zoom;

        public EngineStep()
        {
            try { zoom = AccessTools.FieldRefAccess<GravityEngine, float>("timeZoom"); }
            catch (Exception e)
            {
                Log.Warn("clock: GravityEngine.timeZoom not found, a drop's last old-speed step is not counted: "
                         + e.Message);
            }
        }

        // The date the speed now set starts from: game plus the engine's step still owed at the old speed.
        public double Start(double game)
        {
            GravityEngine ge = GravityEngine.instance;
            if (zoom == null || ge == null) return game;
            double fixedStep = Time.fixedDeltaTime;
            return ClockRules.SpeedStart(game, GravityScaler.GetWorldTimeSeconds(fixedStep * zoom(ge)),
                                         GravityScaler.GetWorldTimeSeconds(fixedStep * ge.GetTimeZoom()));
        }
    }
}
