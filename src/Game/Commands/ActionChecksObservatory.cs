using Company = Game.Company;

namespace SolarExpanseMultiplayer
{
    // O1-O5 (DD14): the exploration window's checks for the acting agency (ObservatoryCommand decides).
    public static partial class ActionChecks
    {
        static int CheckObservatory(ActionRequest a)
        {
            // the apply records the dice and the state after through the rules; without them (the negative control)
            // the host would draw from the shared generator alone and the clients could not follow
            if (HumanAgencies.Dice == null) return RefuseCodes.NotAllowedHere;
            Company c = GameAgencies.Find(a.Agency);
            ObservatoryOrder order;
            return ObservatoryOrders.Decide(a, c, true, out order);
        }
    }
}
