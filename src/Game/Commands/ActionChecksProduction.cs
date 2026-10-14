using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;

namespace SolarExpanseMultiplayer
{
    // P1-P4 (DD3): the construction window's and build list's checks for the acting agency (ProductionCommand decides).
    public static partial class ActionChecks
    {
        static int CheckProduction(ActionRequest a)
        {
            Company c = GameAgencies.Find(a.Agency);
            ObjectInfo body = GameAgencies.Body(a.Body);
            CraftPlan plan;
            return ProductionWorld.Decide(a, c, body, true, out plan);
        }
    }
}
