namespace SolarExpanseMultiplayer
{
    // DD-F6, the late launch: a checked launch (a facility craft or a catapult ship) whose ship is built after its
    // launch date launches at the end of the frame it was built in (RocketLauncher.StartOnLaunch). The host's verdict
    // for it (74 or 71) is decided at that moment on the host; a client builds the ship only when the host's
    // CraftFinished reaches it, so its own OnLaunch comes at a moment of its own and must not decide anything. This
    // is the rule for when a client's OnLaunch is held for the host's verdict and applied from Update.
    public static class LateLaunchRule
    {
        // true: hold the launch (the verdict is the host's). `now` and `launchTicks` are game ticks: a launch at its
        // date has now == launchTicks (the scheduled action ends the step exactly there) and is the ordinary watched
        // launch; later than that is late. `verdictApplied`: the host's outcome for this mission was already applied
        // here (a second ship of the mission), which then runs as vanilla under it.
        public static bool ShouldHold(bool checkedLaunch, bool watched, bool verdictApplied, long now, long launchTicks)
        {
            return checkedLaunch && !watched && !verdictApplied && now > launchTicks;
        }

        // A launch outcome heads the stream for a mission nobody watches: it may still be a late launch, whose ship
        // this game has just built. True: put it on the watch (at `now`, so no wait counts from its old date).
        public static bool AdmitsHead(bool checkedLaunch, bool watched, bool verdictApplied, bool missionLive,
            long now, long launchTicks)
        {
            return missionLive && ShouldHold(checkedLaunch, watched, verdictApplied, now, launchTicks);
        }
    }
}
