using System;
using Company = Game.Company;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;

namespace SolarExpanseMultiplayer
{
    // O1-O5: the exploration window's own world calls for the acting agency's observatory (D 134070-134372). The host
    // records the dice the order draws and the observatory's state after; a client replays those dice and must end
    // where the host's observatory ended (DD14's after-check). The apply owns the dice record or replay for these
    // kinds on both sides.
    public static partial class ActionApply
    {
        static ApplyOutcome ApplyObservatory(ActionRequest a, ApplyNumbers given)
        {
            bool host = given == null;
            Company c = GameAgencies.Find(a.Agency);
            ObservatoryOrder order;
            int code = ObservatoryOrders.Decide(a, c, host, out order);
            if (code != 0) return host ? Refused(code) : OutOfStep(a, "it names no observatory here");
            ObservatoryDrawState dice = HumanAgencies.Dice;
            if (dice == null)
            {
                if (host) return Refused(RefuseCodes.NotAllowedHere);
                return OutOfStep(a, "no observatory rules are running");
            }
            bool opened = host ? dice.BeginRecord() : dice.BeginReplay(given.Draws);
            if (!opened)
            {
                // another record or replay is open: whatever it is, this entry's dice would mix with it
                if (host) throw new InvalidOperationException("an observatory record is open already");
                return OutOfStep(a, "an observatory replay is open already");
            }
            ObsDraw[] draws = null;
            string replay = null;
            bool taken;
            try { taken = Order(a, order); }
            finally
            {
                if (host) draws = dice.EndRecord();
                else replay = dice.EndReplay();
            }
            ObsAfter after = ObservatoryWorld.AfterOf(order.Observatory);
            if (!host)
            {
                string problem = replay ?? ObservatoryCommand.AfterProblem(given.After, after);
                return problem != null ? OutOfStep(a, problem) : Done(a, null, null);
            }
            // the checks passed, yet the game turned the order down: the window's "stop first" may already have
            // changed the observatory, so the entry still goes out and every game makes the same call
            if (!taken) Log.Warn("command: the game would not take " + ActionKind.Name(a.Kind) + " for " + a.Agency);
            return Done(a, new ApplyNumbers { Draws = draws, After = after }, null);
        }

        // The window's calls, with its "stop first" for an order on a busy observatory. False when the game's own
        // order said no.
        static bool Order(ActionRequest a, ObservatoryOrder order)
        {
            ObservatoryFacility o = order.Observatory;
            if (ObservatoryCommand.StopsFirst(a.Kind, ObservatoryWorld.Mode(o))) o.StopOperation();
            switch (a.Kind)
            {
                case ActionKind.ObservatoryExamine:
                    return o.OrderExploration(order.Body != null ? (object)order.Body : order.Group);
                case ActionKind.ObservatoryDiscover: return o.OrderDiscovery(order.Group);
                case ActionKind.ObservatorySurvey: o.AssignToExoplanetSurvey(); return true;
                case ActionKind.ObservatoryStop: o.StopOperation(); return true;
                default: o.EnableNotifications = a.Value == 1; return true;
            }
        }

        // A client whose world does not take the host's entry as the host did: out of step (a full rejoin heals it).
        static ApplyOutcome OutOfStep(ActionRequest a, string why)
        {
            Log.Warn("command: " + ActionKind.Name(a.Kind) + " " + a.Agency + ": " + why);
            return Refused(RefuseCodes.NotAllowedHere);
        }
    }
}
