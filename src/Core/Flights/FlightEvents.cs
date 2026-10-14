namespace SolarExpanseMultiplayer
{
    // The stream entries a client's own step must apply itself, inside the game's call, never from Update: a landing
    // entry (an impact's 59 or an orbit pull's 72) applies inside the ship's landing, and a checked launch's outcome
    // (74, or the 71 of reason 1 or 2) inside the game's OnLaunch. Everything behind such an entry waits at the head of
    // the stream until then.
    public static class FlightEvents
    {
        // 71's reasons (Value): 1 power or switched off, 2 catapult units, 3 stalled build, 4 cyclical self-cancel,
        // 5 the target went.
        public const int CancelPower = 1, CancelCatapult = 2, CancelTargetGone = 5;

        // An impact (a 59 that names the mission that caused it; a plain removal has none) or an orbit pull.
        public static bool IsLanding(ActionRequest a)
        {
            return a != null && ((a.Kind == ActionKind.ObjectRemoved && a.ItemId >= 0)
                || a.Kind == ActionKind.AsteroidPulled);
        }

        // The host's outcome of a checked launch: it launched, or the facility check cancelled it.
        public static bool IsLaunch(ActionRequest a)
        {
            return a != null && (a.Kind == ActionKind.MissionLaunched
                || (a.Kind == ActionKind.MissionCancelled
                    && (a.Value == CancelPower || a.Value == CancelCatapult)));
        }

        // How many entries directly after this one belong to its apply (a landing's re-plans and cancels).
        public static int GroupSize(ActionRequest a) { return IsLanding(a) ? a.Count : 0; }

        // An entry that may follow a landing entry inside its group: a re-plan (57) or a reason-5 cancel (71).
        public static bool InGroup(ActionRequest a)
        {
            return a != null && (a.Kind == ActionKind.ObservatoryReplan
                || (a.Kind == ActionKind.MissionCancelled && a.Value == CancelTargetGone));
        }
    }
}
