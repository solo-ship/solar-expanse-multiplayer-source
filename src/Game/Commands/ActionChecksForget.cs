namespace SolarExpanseMultiplayer
{
    // ForgetObject (DD15): the trash button's check for the acting agency (ForgetCommand decides).
    public static partial class ActionChecks
    {
        static int CheckForget(ActionRequest a)
        {
            // the replacement asteroids a removal spawns are drawn by the rules for every game; without them (the
            // negative control) the host would spawn from the shared generator alone
            if (HumanAgencies.Spawns == null) return RefuseCodes.NotAllowedHere;
            return ForgetCommand.Decide(a.Agency, ForgetWorld.Facts(GameAgencies.Body(a.Body), a.Agency));
        }
    }
}
