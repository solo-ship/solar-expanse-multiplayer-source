using System;

namespace SolarExpanseMultiplayer
{
    // This player's own speed request on a client, and what the host has been told of it (ClockClient is the game glue
    // around it). Clock.MaxScale is "no request of its own": in the host's vote it slows nobody (SpeedVote), so a
    // player who never asked simply follows the host.
    //
    // Two moments from the joiner's flow:
    //  - until this game has the session's world loaded (JoinerFlow.InGame: not while it goes to the menu or loads a
    //    snapshot, first join or full rejoin) the game's own speed calls are not this player's request: the game
    //    pauses itself while it loads, and counting that pause would let every joiner pause the whole session
    //    ("slowest REQUESTED speed wins"). In a running game they count, a drop included (a popup closing while the
    //    link is down puts its speed back). The player's own clicks always count: a click is a person's choice;
    //  - until the host hears this player (JoinerFlow.Playing, I-12: from its Loaded, or a quick rejoin's answer,
    //    until the link drops) nothing is sent, as the host would drop it, so the first message after that is the
    //    restore the host waits for. Without it the host never learns this request and the client, capped at it,
    //    never follows the host.
    // And the clock itself: from the start of a join or full-rejoin load until JoinerFlow.InGame this game is held at
    // 0 (Holding). Its world is half built then: running it fires day events before the game's managers are ready
    // (a catch-up at 200000 threw in ContractManager.ONEachDayChange). Nothing is measured while held, so the first
    // gap, any catch-up and the out-of-step check are the real world's.
    // A slow-down or pause goes out at once (the host acts on it at once too, RequestCoalescer), and until the host's
    // Clock shows it this client never runs faster than it (Waiting), even when the player clicks back up first: the
    // host stops where the request reaches it, and a client that ran on would end up past that stop, where nothing
    // corrects it while paused. Once answered, the next drift step lands on the host's date (DriftController
    // .LandSeconds): the client slowed first, so it is behind by what the host ran while the request travelled.
    public sealed class ClientRequest
    {
        public const long MinSendGapMs = 100;
        float own = Clock.MaxScale, sent = -1f;
        bool planning, sentPlanning, fromPlayer;
        int choice, sentChoice, seq, waitSeq;
        long lastSendMs = -MinSendGapMs, lastEarlyMs = -MinSendGapMs;
        public const long AnswerWaitMs = ClockRules.StaleClockMs;   // an answer that never comes stops holding at last
        bool waiting, land;
        float waitFor;       // while waiting: the slowest slow-down sent that the host has not answered yet
        long waitSinceMs;
        readonly JoinerFlow flow;
        DriftController drift = new DriftController();
        bool held, gateHeld;

        public ClientRequest(JoinerFlow flow)
        {
            if (flow == null) throw new ArgumentNullException("flow");
            this.flow = flow;
        }

        public float Own { get { return own; } }
        public bool Holding { get { return !flow.InGame; } }
        public bool Planning { get { return planning; } }

        // A speed call the game counted as this player's (ClockSync.LocalChange). Requests are always listed speeds,
        // snapped down; every counted request (the same value again included) raises the choice counter. False when
        // the call is not counted.
        public bool Local(float scale, bool byPlayer)
        {
            if (!flow.InGame && !byPlayer) return false;
            own = ClockRules.SnapDown(scale);
            choice++;
            fromPlayer |= byPlayer;
            return true;
        }

        public void Planner(bool open) { planning = open; }

        // The planner windows went with the world (ClockHooks.NewWorld: a load, the menu): no planner is open now, so
        // the restore after the load must not ask the host to keep everyone paused for one.
        public void NewWorld() { planning = false; }

        // Once a frame with the host's last Clock, before Measure: the answer is a Clock whose vote for this player
        // echoes the awaited slow-down's Seq or a later one (the host acted on it, or on a newer request that replaced
        // it); a vote's value alone can be an older Clock's. The next drift step lands.
        public void Heard(Vote[] votes, int player, long nowMs)
        {
            if (!waiting) return;
            if (SpeedVote.SeqOf(votes, player) < waitSeq && nowMs - waitSinceMs < AnswerWaitMs) return;
            waiting = false;
            land = true;
        }

        // A new link to the host: the next message puts this player's request back (the host takes the first message
        // after a join as a restore, never a choice, so a rejoin cannot end a drop hold).
        // An answer to the old link's slow-down is not coming either.
        public void LinkUp() { sent = -1f; waiting = false; land = false; }

        // What to ask the host for: this player's own request, or one step below the top speed while this client is
        // behind at the top speed.
        public float Want(bool askSlower) { return askSlower ? Math.Min(own, DriftController.StepBelowMax) : own; }

        // The message to send now, or null: on every counted request (at most one every 100 ms), when the planner flag
        // changes, or when the request itself changes. Sent commits it once the link has taken it. A slow-down or the
        // planner opening skips the 100 ms, but only one such early send in any 100 ms: at most 2 sends per 100 ms.
        public SpeedRequest Due(float want, long nowMs)
        {
            if (!flow.Playing) return null;
            bool changed = want != sent || planning != sentPlanning || choice != sentChoice;
            if (!changed) return null;
            bool slower = (planning ? 0f : want) < (sentPlanning ? 0f : sent);
            if (nowMs - lastSendMs < MinSendGapMs && !(slower && nowMs - lastEarlyMs >= MinSendGapMs)) return null;
            return new SpeedRequest { Scale = want, Planning = planning, Choice = choice, Player = fromPlayer,
                                      Seq = seq + 1 };
        }

        public void Sent(SpeedRequest r, long nowMs)
        {
            if (nowMs - lastSendMs < MinSendGapMs) lastEarlyMs = nowMs;
            if (r.Scale < sent)
            {
                waitFor = waiting ? Math.Min(waitFor, r.Scale) : r.Scale;
                waiting = true;
                waitSinceMs = nowMs;
                waitSeq = r.Seq;
            }
            seq = r.Seq;
            sent = r.Scale;
            sentPlanning = r.Planning;
            sentChoice = r.Choice;
            fromPlayer = false;
            lastSendMs = nowMs;
        }

        // The speed this client may run at: paused at once for its own planner; otherwise never faster than what this
        // player asked for while the host has not yet got that request (SpeedVote.ClientCap), nor than a slow-down it
        // has not answered yet.
        public float Target(float decided, float want, Vote[] votes, int player)
        {
            if (Holding) return 0f;
            float cap = planning ? 0f : SpeedVote.ClientCap(decided, want, SpeedVote.VoteOf(votes, player));
            return waiting ? Math.Min(cap, waitFor) : cap;
        }

        // What a speed call the game makes may actually run at (ClockSync.ClampLocal): nothing while held; otherwise
        // never above what this client runs at anyway while the host has not yet heard of the request.
        public float Allow(float requested, float decided, Vote[] votes, int player)
        {
            if (Holding) return 0f;
            float cap = SpeedVote.ClientCap(decided, ClockRules.SnapDown(requested), SpeedVote.VoteOf(votes, player));
            if (waiting) cap = Math.Min(cap, waitFor);
            return Math.Min(requested, cap);
        }

        // Once a frame, before anything else: notes a hold even on frames with no Clock from the host (the client is
        // away then), so a load that saw no Clock at all still ends in a fresh drift step.
        public bool NoteHold()
        {
            if (Holding) held = true;
            return Holding;
        }

        // Once a frame before Measure, with whether the command day gate holds this game's fixed step: the frame it
        // lets go, the gap the hold left is re-based (DriftController.Rebase), so a hold never becomes a step-down
        // request. The line for the log, or null.
        public string GateHeld(bool holding, double hostNow, double client, float hostScale)
        {
            bool released = gateHeld && !holding;
            gateHeld = holding;
            return released ? drift.Rebase(hostNow, client, hostScale) : null;
        }

        // Ends the drift controller's settle after a drop when no drift step will (see DriftController.EndSettle).
        public string EndSettle(string why) { return drift.EndSettle(why); }

        // This frame's drift step (DriftController), or false while held. The first step after a hold starts a fresh
        // controller (fresh = true; the caller restarts its own measuring too), so nothing from before the load counts.
        public bool Measure(double hostNow, double client, float hostScale, double ratePer1x, bool locked,
                            out ClockDecision d, out bool fresh, double enginePer1x = 0)
        {
            d = default(ClockDecision);
            fresh = false;
            if (NoteHold()) return false;
            if (held) { held = false; fresh = true; drift = new DriftController(); }
            d = drift.Decide(hostNow, client, hostScale, ratePer1x, locked, land, enginePer1x);
            land = false;
            if (waiting) d.AskSlower = false;   // behind only because this client slowed first: not a slow machine
            return true;
        }
    }
}
