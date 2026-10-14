namespace SolarExpanseMultiplayer
{
    // Every line a player reads about a command. The words live in UiStrings (Design decision 12, the cmd.* keys);
    // these methods are lookups. Every line is built on the player's own machine from a code; nothing here turns text
    // from the other side into a line. The names that go into a line (Bankrupt's agency, Planning's and CatchingUp's
    // player, the contract) are the local game's own names, cleaned anyway.
    public static class CommandText
    {
        public static string Refused(int code)
        {
            return UiText.Get(RefuseCodes.IsKnown(code) ? "cmd.refused." + code : "cmd.refused.unknown");
        }

        // The status line while a player holds a planner pause: "Player 2 is planning (1:05)".
        public static string Planning(string playerName, int seconds)
        {
            if (seconds < 0) seconds = 0;
            return UiText.Get("cmd.planning", TextSanitiser.Clean(playerName, 24, "A player"), seconds / 60,
                (seconds % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Shown in the planner when the host refused the flight it was sent.
        public static string FlightRefusedInPlanner(int code)
        {
            return UiText.Get("cmd.flightRefused", Refused(code));
        }

        public static string Waiting() { return UiText.Get("cmd.waiting"); }

        // The note beside a button that is greyed out in a session.
        public static string NotYetInMultiplayer() { return UiText.Get("cmd.notYet"); }

        public static string Bankrupt(string agencyName, bool yours)
        {
            if (yours) return UiText.Get("cmd.bankrupt.yours");
            return UiText.Get("cmd.bankrupt.other", TextSanitiser.Clean(agencyName, 24, "An agency"));
        }

        // The status line while a player's game catches up after a short drop (PauseWhy.CatchingUp).
        public static string CatchingUp(string playerName)
        {
            return UiText.Get("cmd.catchingUp", TextSanitiser.Clean(playerName, 24, "A player"));
        }

        // A short drop could not be resumed: the player rejoins in full.
        public static string ResumeFailed() { return UiText.Get("cmd.resumeFailed"); }

        public static string ContractRewardNotice(string contractName)
        {
            return UiText.Get("cmd.contractReward", TextSanitiser.Clean(contractName, 48, "a contract"));
        }

        public static string OutOfStep() { return UiText.Get("cmd.outOfStep"); }

        // The reconnect cap reached by out-of-step rejoins (JoinLimits.MaxReconnects).
        public static string KeptFallingOutOfStep() { return UiText.Get("cmd.keptOutOfStep"); }

        // The session is refused at its start: the mods add more asteroid kinds than a removal's spawns can carry.
        public static string TooManyAsteroidKinds(int count, int max)
        {
            return UiText.Get("cmd.asteroidKinds", count, max);
        }
    }
}
