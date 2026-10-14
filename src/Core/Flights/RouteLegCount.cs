namespace SolarExpanseMultiplayer
{
    // A route's trip count (CycleMissionsData.IncreasesCount): the host's route controller adds one when it plans a
    // leg and the game's own test below holds. A client's controller is held (the host plans every leg), so a client
    // adds the same one when it applies the copy of such a leg; otherwise the route's count, part of the world's
    // missions print, differs from the host's from the first return leg on and the joiner rejoins.
    public static class RouteLegCount
    {
        // The controller's three cases, in its order: the leg starts at the route's B; the route's A has a low orbit
        // that is B; B is an interstellar construction facility under the low orbit A has.
        public static bool Counts(bool startIsB, bool aLowOrbitIsB, bool bInterstellarUnderALowOrbit)
        {
            return startIsB || aLowOrbitIsB || bInterstellarUnderALowOrbit;
        }

        // Only the copy of a host-planned route leg counts: an entry of kind 70 made by the route controller.
        public static bool IsRouteLeg(int kind, int creator)
        {
            return kind == ActionKind.FlightPlanned && creator == MissionCreators.Cyclical;
        }
    }
}
