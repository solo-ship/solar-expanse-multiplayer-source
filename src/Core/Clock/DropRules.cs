namespace SolarExpanseMultiplayer
{
    // The one switch for what a finished rejoin does to the drop hold. PROVISIONAL (DECISIONS 2026-10-14, the clock's
    // UI part): true means the dropped player coming back ends their own drop hold, so the game resumes at the slowest
    // vote without anyone pressing a speed. false is the older rule: only a speed choice or Carry on ends it.
    public static class DropRules
    {
        public const bool RejoinLiftsDropHold = true;
    }
}
