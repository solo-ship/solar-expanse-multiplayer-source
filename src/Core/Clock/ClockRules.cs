using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Small pure rules of the clock sync, kept out of the game glue so they can be tested.
    public static class ClockRules
    {
        // The host sends a Clock message at least this often (a heartbeat), in milliseconds: 4 a second.
        public const long HeartbeatMs = 250;

        // A Clock older than this means the host's clock has stopped (it is at the main menu or a loading screen, so it
        // sends none, though pings keep the link up): 8 missed heartbeats. The client holds paused until the next Clock.
        public const long StaleClockMs = 8 * HeartbeatMs;

        public static bool IsStale(long clockAgeMs) { return clockAgeMs > StaleClockMs; }

        // The date the speed just set starts from. The engine moves the date in fixed steps (0.01 s in this game) and
        // takes a new speed only after the next one (GravityEngine's pending time zoom), so the step after a drop
        // still runs at the old speed: a Clock sent in that frame was one old-speed step short (2.0 s at 1x after
        // 200000), and a client measuring its own date there missed its own. stepAtEngine: game seconds the engine's
        // next step adds; stepAtScale: what one step at the speed set adds. Only a drop is carried: after a speed-up
        // the difference is under one step at the new speed (0.01 s at the host's speed), and carrying it would move
        // the date back.
        public static double SpeedStart(double game, double stepAtEngine, double stepAtScale)
        {
            double owed = stepAtEngine - stepAtScale;
            return owed > 0 ? game + owed : game;
        }

        // A speed-up is broadcast no more often than this after the last Clock: a flapping speed must not flood the clients.
        public const long ChangeGapMs = 50;

        // The most Clock messages ShouldBroadcast lets out in any ChangeGapMs (the flood bound, see there).
        public const int MaxSendsPerChangeGap = 6;

        // Whether the host sends a Clock message now (asked once a frame, at the end of the frame, with the speed the frame
        // ended with and the last one sent):
        //  - a pause, or a drop to a lower listed level (SnapUp), at once: a client must never run on at a speed the host
        //    has left (M3 R5: a pause held back 50 ms left a client 6574 game s ahead);
        //  - any other change (a speed-up, or an in-between speed on the same level, e.g. the countdown's) after ChangeGapMs;
        //  - nothing changed: the heartbeat, HeartbeatMs after the last send.
        // Flood bound: every send that is not a drop comes at least ChangeGapMs after the one before, and the level can drop
        // at most 5 times in a row (6 listed levels, pause included). So any ChangeGapMs holds at most 6 sends
        // (MaxSendsPerChangeGap): at most 120 a second, whatever the speed does.
        public static bool ShouldBroadcast(long nowMs, long lastSentMs, float scale, float lastSentScale)
        {
            if (scale == lastSentScale) return nowMs - lastSentMs >= HeartbeatMs;
            if (SnapUp(scale) < SnapUp(lastSentScale)) return true;
            return nowMs - lastSentMs >= ChangeGapMs;
        }

        // The speeds the game runs at (0 = pause): baseTimeScale times multiTime2..multiTime6, read from the game when
        // a session starts (SetListed). A speed request is always one of these.
        public static readonly float[] Fallback = { 0f, 5000f, 25000f, 50000f, 100000f, 200000f };

        // The session's list. Fallback outside a session, which only tests and tools rely on. Set once, on the main
        // thread, before the host starts.
        static volatile float[] listed = Fallback;

        public static float[] Listed { get { return listed; } }

        // Sets the session's list; null when it was accepted, else why not (the list is then left as it was, and the
        // session must not start). Six values, pause first, strictly rising, the last one Clock.MaxScale
        // (the drift controller's top-speed rule, the clamp and the "no request" sentinel all rely on it).
        public static string SetListed(float[] values)
        {
            if (values == null) return "no speed list";
            if (values.Length != Fallback.Length)
                return "the speed list has " + values.Length + " values, not " + Fallback.Length;
            if (values[0] != 0f) return "the speed list does not start with a pause";
            for (int i = 0; i < values.Length; i++)
            {
                float v = values[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) return "the speed list has a value that is not a number";
                if (i > 0 && !(v > values[i - 1])) return "the speed list does not rise";
            }
            if (values[values.Length - 1] != Clock.MaxScale)
                return "the speed list does not end at " + Clock.MaxScale;
            listed = (float[])values.Clone();
            return null;
        }

        // Back to the fallback (a session ended).
        public static void ResetListed() { listed = Fallback; }

        public static bool IsListed(float scale)
        {
            foreach (float l in Listed) if (l == scale) return true;
            return false;
        }

        // The largest listed speed at or below this one (a pause for anything below the lowest running speed or not a number).
        public static float SnapDown(float scale)
        {
            if (!(scale > 0f)) return 0f;
            float best = 0f;
            foreach (float l in Listed) if (l <= scale) best = l;
            return best;
        }

        // The smallest listed speed at or above this one (the top speed when above it).
        public static float SnapUp(float scale)
        {
            foreach (float l in Listed) if (l >= scale) return l;
            return Listed[Listed.Length - 1];
        }

        // Same speed to within 0.1% (the engine may round a continuous speed a little); a pause is only close to a pause.
        public static bool Close(float a, float b)
        {
            if (a == 0f || b == 0f) return a == b;
            return Math.Abs(a - b) <= 0.001f * Math.Max(Math.Abs(a), Math.Abs(b));
        }

        // The client sets a speed only when nothing else owns the clock: not during the launch countdown, the end-game
        // lock, or the autosave's silent pause (it is put back after that).
        public static bool CanApply(bool countDown, bool locked, bool silentPause)
        {
            return !countDown && !locked && !silentPause;
        }

        // What a speed request did to a drop hold, for the host's log line. The game has often taken the speed already
        // (the host player's own call), so the line reads "speed stays" while the hold has in fact ended; a reader of
        // the log must still see that the choice resumed the session.
        public static string HoldNote(bool heldBefore, bool heldAfter)
        {
            if (heldBefore && !heldAfter) return " - its choice ends the drop hold";
            return heldBefore ? " - the drop hold stays (not a choice)" : "";
        }

        // A client re-applies a running speed at most this often: each SetTimescale call also writes a Unity log line.
        public const long ApplyEveryMs = 250;

        // Worth a SetTimescale call: a pause or unpause, or over 2% apart.
        public static bool Differs(float a, float b)
        {
            if ((a == 0f) != (b == 0f)) return true;
            return Math.Abs(a - b) > 0.02f * Math.Max(Math.Max(a, b), DriftController.BaseScale);
        }

        // Whether the client sets its target speed this frame (ClockClient): only when it differs, and then at once for
        // a pause, an unpause or a new drift mode, otherwise at most every ApplyEveryMs. A landing's speed falls a
        // little every frame and cannot wait 250 ms (it would carry the client past a stopped host), but setting it
        // every frame writes a Unity log line each time: it is set again once the speed running is more than
        // LandResetShare off the target, so it never runs above target / (1 - LandResetShare) (DriftController
        // .LandSeconds is sized for that).
        public const float LandResetShare = 0.1f;

        public static bool ApplyNow(float target, float scale, bool modeChanged, bool landing, long sinceApplyMs)
        {
            if (!Differs(target, scale)) return false;
            if (modeChanged || target == 0f || scale == 0f) return true;
            if (landing) return Math.Abs(target - scale) > LandResetShare * Math.Max(target, scale);
            return sinceApplyMs >= ApplyEveryMs;
        }
    }

    // This game's mission planner pause. The game pauses when a planner window gets its data (PlanMissionWindow.SetData)
    // and unpauses when one hides (PlanMissionWindow.Hide), which it also does for an already hidden planner. A count
    // alone would keep everyone paused for good if it ever paused twice and unpaused once, so a close with no planner
    // window left open lifts the pause whatever the count says. While a window IS still open the pause stands: such a
    // close takes one off the count but never below 1 (the count only matters while a window is still open).
    public sealed class PlannerCount
    {
        int n;
        public bool IsOpen { get { return n > 0; } }
        public int Count { get { return n; } }
        // A session ended: whatever the game had open belongs to no session now.
        public void Reset() { n = 0; }
        // True when this is the first (everyone pauses now).
        public bool Open() { n++; return n == 1; }
        // True when the planner pause lifts now. windowOpen: a planner window is still open, so the pause stays (this
        // close only takes one off the count, down to 1); with none open, every open is cleared.
        public bool Close(bool windowOpen)
        {
            if (n == 0) return false;
            if (windowOpen) { if (n > 1) n--; return false; }
            n = 0;
            return true;
        }
    }

    public enum RequestKind
    {
        Restore,     // the first message after a (re)join: puts the request back, never a choice
        NewChoice,   // the player's choice counter went up: a speed choice, even with the same value
        Update,      // same counter, maybe a different value (the client's step-down): applied, never a choice
        Ignore       // the counter went backwards: dropped (not a violation)
    }

    // The host's view of each client's choice counter (SpeedRequest.Choice), so a click on the speed already asked for
    // still counts as a choice and an old or repeated message does not.
    public sealed class SpeedChoices
    {
        readonly Dictionary<int, int> last = new Dictionary<int, int>();

        public RequestKind Classify(int player, int choice, bool first)
        {
            int was;
            if (first || !last.TryGetValue(player, out was)) { last[player] = choice; return RequestKind.Restore; }
            if (choice < was) return RequestKind.Ignore;
            if (choice == was) return RequestKind.Update;
            last[player] = choice;
            return RequestKind.NewChoice;
        }

        public void Forget(int player) { last.Remove(player); }
    }
}
