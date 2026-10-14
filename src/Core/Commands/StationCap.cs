namespace SolarExpanseMultiplayer
{
    // How many facilities a space station takes (ObjectInfo.GetCountMaxFacilitySpaceStation, D 125261-125279): its
    // descriptor's MaxFacilityAmount for each connected segment, counting the first and at most capacity + 1. The game
    // walks the segments through the station view on the local player's data, which only the station's owner has, so
    // every other game finds no view and answers "no cap". In a session the walk is the owner's on every game.
    public static class StationCap
    {
        // The game's answer when there is no segment view: no cap.
        public const long None = int.MaxValue;

        // segments: the views walked from the first (0 when there is no view); capacity: SpaceStationConfig's
        // OrbitStationCapacity. The product is an int, as the game's is.
        public static long Of(int segments, int capacity, int maxFacilityAmount)
        {
            if (segments < 1) return None;
            int n = segments < capacity + 1 ? segments : capacity + 1;
            return maxFacilityAmount * n;
        }
    }
}
