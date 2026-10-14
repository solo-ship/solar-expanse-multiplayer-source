namespace SolarExpanseMultiplayer
{
    // A preset's cargo row as the planner loads it. The game restores a row's site from the id the preset saved
    // (CargoData.idObjectInfoToRevert); a preset a session applied carries none (the request names no start), and
    // the planner's module row then throws drawing its dropdown, leaving a dead row in every later plan. Such a row
    // takes the planner's own start, the site the game binds the preset's modules from (ValidateCargoModules).
    public static class PresetRows
    {
        public static bool NeedsSite(bool hasSite, bool fromGravityAssist) { return !hasSite && !fromGravityAssist; }
    }
}
