using System;

namespace SolarExpanseMultiplayer
{
    // The host's plan hold (DD-F3): an entry's launch must be at or after the end of its day, so while a plan is
    // pending (a game-made plan, or the host's own copy of an accepted flight) the host's day cut becomes a wait. It
    // ends when no plan is pending, or at PlanHoldMaxMs, when the plans still pending are given up (the game side
    // handles each per creator). Pure: the caller gives the game time and the real time.
    public enum PlanHoldState { None, Hold, Ended, Expired }

    public sealed class PlanHold
    {
        // standing this close to the cut counts as standing at it (the step that ends at the cut lands a tick off)
        public const long SlackTicks = TimeSpan.TicksPerSecond / 2;

        long forDay = long.MinValue;
        long startedMs = -1;
        bool expired;

        public long HeldMs { get; private set; }

        public void Reset()
        {
            forDay = long.MinValue;
            startedMs = -1;
            expired = false;
            HeldMs = 0;
        }

        // None: not at the cut (or nothing pending, nothing held). Hold: take no step. Ended: the wait is over, the
        // plans finished (HeldMs says how long). Expired: PlanHoldMaxMs passed; give the plans up. After Expired the
        // day takes no more holds.
        public PlanHoldState Check(long nowTicks, long cutTicks, long nextDayTicks, bool pending, long nowMs)
        {
            if (nextDayTicks != forDay)
            {
                forDay = nextDayTicks;
                startedMs = -1;
                expired = false;
            }
            bool at = nowTicks >= cutTicks - SlackTicks && nowTicks < nextDayTicks;
            if (expired || !at)
            {
                // a hold the game time moved out of (a load, the day turning) is over without a word
                if (startedMs >= 0 && !at) { HeldMs = nowMs - startedMs; startedMs = -1; return PlanHoldState.Ended; }
                return PlanHoldState.None;
            }
            if (!pending)
            {
                if (startedMs < 0) return PlanHoldState.None;
                HeldMs = nowMs - startedMs;
                startedMs = -1;
                return PlanHoldState.Ended;
            }
            if (startedMs < 0) startedMs = nowMs;
            if (nowMs - startedMs >= ActionLimits.PlanHoldMaxMs)
            {
                HeldMs = nowMs - startedMs;
                startedMs = -1;
                expired = true;
                return PlanHoldState.Expired;
            }
            return PlanHoldState.Hold;
        }
    }
}
