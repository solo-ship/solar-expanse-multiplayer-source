namespace SolarExpanseMultiplayer
{
    // The host driver's share of the observatory and asteroid amendment, apart from CommandService.cs so M6's
    // additions can sit beside it. ActionResult.Validate holds each of these numbers to the kinds that may carry it,
    // and every entry is validated after the apply and before it is sent (a failure is a HostFault, never a send).
    public sealed partial class CommandService
    {
        // The observatory draws, state, system name and asteroid spawns an apply made go into the entry as they are.
        static void ObservatoryNumbersInto(ActionResult r, ApplyNumbers n)
        {
            r.Draws = n.Draws;
            r.After = n.After;
            r.SystemName = n.SystemName;
            r.Spawns = n.Spawns;
        }

        // A request that can leave an observatory state, draw dice or spawn asteroids is sized with the widest too, so
        // the room the host asks for covers the entry it will send. Host-decided kinds are applied at once and are
        // checked after the apply instead.
        static void WidenForObservatory(ActionResult worst, ActionRequest a)
        {
            if (ActionKind.CarriesAfter(a.Kind)) worst.After = ObservatoryWorst.After();
            if (ActionKind.CarriesDraws(a.Kind)) worst.Draws = ObservatoryWorst.Draws(ActionLimits.MaxDraws);
            if (ActionKind.CarriesSpawns(a.Kind)) worst.Spawns = ObservatoryWorst.Spawns(ActionLimits.MaxSpawns);
        }
    }
}
