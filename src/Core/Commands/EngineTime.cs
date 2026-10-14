using System;

namespace SolarExpanseMultiplayer
{
    // The engine's world seconds and the game ticks the fixed step decides in (StepGate, both games). A cut lands the
    // engine on its target's seconds, (target - start) / 1e7 as a double. Read back by truncation, some of those
    // doubles come out a tick short (every day point from day 47 to 79 of a 2020 game, where the seconds pass 2^22,
    // and any tick of the day elsewhere): a client standing on its day point with the gate shut then "cut" to the
    // point the engine already stood on, the cut could not shorten the step, and the whole step ran past nextDay
    // without the gate (soak 6). Read back to the nearest tick, a time the engine was cut to is that tick; and a
    // target the engine has reached in its own seconds is stood at, never run past.
    public static class EngineTime
    {
        // The game ticks at `seconds` of the engine's world time from startTicks, to the nearest tick.
        public static long Ticks(long startTicks, double seconds)
        {
            double t = seconds * TimeSpan.TicksPerSecond;
            if (!(t > 0)) return startTicks;
            return startTicks + (t >= long.MaxValue / 2 ? long.MaxValue / 2 : (long)(t + 0.5));
        }

        // The world seconds the engine still has to run from `seconds` to stand at targetTicks.
        public static double SecondsTo(long startTicks, long targetTicks, double seconds)
        {
            return (targetTicks - startTicks) / (double)TimeSpan.TicksPerSecond - seconds;
        }

        // A client's step with this target (StepCut.None excluded) is dropped, standing where it is: the target is at
        // or behind its clock, or the engine is already there in its own seconds (no cut could shorten the step).
        public static bool Stands(long target, long now, double secondsTo)
        {
            return target <= now || !(secondsTo > 0);
        }
    }
}
