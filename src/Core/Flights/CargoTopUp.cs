using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // A client's copy of a flight takes its resource cargo with the game's own removal (CreateFly's cargo step), which
    // refuses anything more than 1e-7 short. The host took the same cargo at its own moment in the day, from a stock
    // the world compare holds equal only within the stock tolerance, so a rival that loads all it has (an AI fuel row)
    // can be a hair short here. Each resource the rows name (summed, as the removal takes them one after another) is
    // topped up by what it lacks when that is within the stock area's tolerance, the rule every other removal of an
    // entry follows (FlightApply.Shortfall); more is out of step.
    public static class CargoTopUp
    {
        const int StockArea = 4;

        // rows: (resource id, mass) in the order the removal takes them; onHand: what the site holds of one. The
        // amount to add per resource before the removal (none when nothing is short), or null with the problem.
        public static Dictionary<string, double> Plan(IList<KeyValuePair<string, double>> rows,
            Func<string, double> onHand, out string problem)
        {
            List<string> left;
            return Plan(rows, onHand, false, out problem, out left);
        }

        // gameMade: the host's flight came from the game's own CreateFly (a FlightPlanned), which goes on past a removal
        // that fails (it logs "okHelp=<ids>" and flies with those rows untaken; its fuel fallback only touches rows of
        // the ship's own propellant). A resource short beyond the tolerance there was short on the host as well, the
        // stocks being equal within it, so it is left to the game's removal (named in `left`), not out of step. A
        // player's request is checked by the host before its step: such a shortfall is out of step.
        public static Dictionary<string, double> Plan(IList<KeyValuePair<string, double>> rows,
            Func<string, double> onHand, bool gameMade, out string problem, out List<string> left)
        {
            problem = null;
            left = new List<string>();
            var need = new Dictionary<string, double>();
            var order = new List<string>();
            foreach (KeyValuePair<string, double> r in rows ?? new KeyValuePair<string, double>[0])
            {
                if (r.Key == null) continue;
                if (!need.ContainsKey(r.Key)) { need[r.Key] = 0; order.Add(r.Key); }
                need[r.Key] += r.Value;
            }
            var add = new Dictionary<string, double>();
            foreach (string id in order)
            {
                double have = onHand(id), clamped;
                if (!WorldTolerance.Shortfall(StockArea, have, need[id], out clamped))
                {
                    if (gameMade) { left.Add(id); continue; }
                    problem = id + " short: has " + Num(have) + ", the cargo takes " + Num(need[id]);
                    return null;
                }
                if (clamped > 0) add[id] = clamped;
            }
            return add;
        }

        static string Num(double v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
    }
}
