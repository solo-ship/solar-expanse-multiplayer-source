namespace SolarExpanseMultiplayer
{
    // A body's subtype as the state readers write it. The game gives a minable body with no deposits its fallback
    // subtype (AllScriptableObjectManager.DefaultObjectSubType, "FallbackObjectSubType") only while it loads a save in
    // which the body has none. A host that loaded its own save did that; a joiner loads the host's snapshot, where the
    // body now has its rolled deposits, so its subtype stays empty (as the host's would after reloading a save it wrote
    // itself). The readers write the fallback as no subtype; otherwise every first day after a join differs in the
    // world's bodies, which no heal restores, and the joiner rejoins every day.
    public static class BodySubtype
    {
        public static string Text(string name, bool isFallback)
        {
            return isFallback || name == null ? "" : name;
        }
    }
}
