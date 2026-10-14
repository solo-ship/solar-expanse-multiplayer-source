using System;

namespace SolarExpanseMultiplayer
{
    // The log line a world entry (market maker, growth, contract, rival, end game) leaves, one text for both games so
    // tools\command-compare.ps1 can hold the host's lines and each client's equal.
    public static class WorldLog
    {
        // null for the kinds that log none. growthPart counts the parts of a year's growth on from 0 and returns to 0 at
        // the last one.
        public static string EntryLine(ActionResult r, ref int growthPart)
        {
            ActionRequest a = r.Action;
            switch (a.Kind)
            {
                case ActionKind.MarketMaker:
                    return "market: maker " + (a.Value == 0 ? "yearly" : "requote") + " #" + r.Seq + " ops "
                        + (r.Ops == null ? 0 : r.Ops.Length);
                case ActionKind.PopulationGrowth:
                    string line = "growth: year " + new DateTime(r.AtTicks).Year + " #" + r.Seq + " rows "
                        + (a.Growth == null ? 0 : a.Growth.Length) + " part " + growthPart;
                    growthPart = a.Value == 1 ? 0 : growthPart + 1;
                    return line;
                case ActionKind.AiAction:
                    return "ai: " + a.Agency + " " + ActionKind.Name(a.Kind) + " steps "
                        + (r.Steps == null ? 0 : r.Steps.Length) + " #" + r.Seq;
                case ActionKind.ObjectiveDone: case ActionKind.ContractDone:
                    if (a.Contract == null) return null;
                    return "contract: " + a.Agency + " " + a.Contract.Contract + " objective " + a.Contract.Objective
                        + " chain " + (r.Chain == null ? 0 : r.Chain.Length) + " #" + r.Seq;
                case ActionKind.ContractActivated:
                    if (a.Contract == null) return null;
                    return "contract: " + a.Agency + " " + a.Contract.Contract + " activated #" + r.Seq;
                case ActionKind.EndGameShip:
                    return "endgame: ship " + a.ItemId + " for " + a.Agency + " #" + r.Seq;
                default: return null;
            }
        }
    }
}
