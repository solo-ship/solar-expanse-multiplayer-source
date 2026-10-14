namespace SolarExpanseMultiplayer
{
    // When a client applies an ordinary entry: once its own clock has reached the game time the host applied it at
    // (ActionResult.AtTicks), never earlier. A client that applied a day's entries as soon as it entered the day ran
    // them up to a game day before the host did: a rival's AiAction met stock a landing earlier in that day had not
    // yet brought ("short here beyond the tolerance"), and a build it started counted its progress from the wrong
    // time. The wait ends at the day point at the latest (the client always stands there before it may cross, and an
    // entry the host made in the day's last minute is due there), and whenever the client is standing (a hold or a
    // parked step), so no wait can stop a clock that is waiting for something else.
    public static class HostTick
    {
        // The game time the entry waits for: the host's tick, or the day point when that is earlier.
        public static long Until(long atTicks, long nextDayTicks)
        {
            long dayPoint = AlignedBoundary.Cut(nextDayTicks);
            return atTicks < dayPoint ? atTicks : dayPoint;
        }

        // Does an entry the host applied at atTicks wait on a client whose clock is at now?
        public static bool Waits(long now, long atTicks, long nextDayTicks, bool standing)
        {
            return !standing && now < Until(atTicks, nextDayTicks);
        }
    }
}
