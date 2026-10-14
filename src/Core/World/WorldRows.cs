using System;
using System.Collections.Generic;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // The reading side of GroupSink's row layout (M7 DD-W10): what a client's restore takes out of the host's heal rows.
    // GroupSink is the one writer on both games (the host's group prints and heal rows, a client's own group prints),
    // so the indexes here are StateReader's visit order as GroupSink keeps it: key parts then string values joined by
    // '/', every other value in visit order, a date as two numbers.
    public static class WorldRows
    {
        // money: the "money" row's CurrentMoney, TotalProfit, survey cost, DiscoveredSystemsCount, ProductionCountID;
        // a name counter row: its count
        public const string MoneyKey = "money";
        public const int MoneyCurrent = 0, MoneyProductionCount = 4, MoneyValues = 5, NameCount = 0;
        // stock (area 4) and deposits (12): "body/resource/state"; stock Value, Balance; deposit Value, MiningFactor
        public const int StockValue = 0, DepositValue = 0, DepositMiningFactor = 1;
        // population (5): "body"; Crew, CrewInWork
        public const int Crew = 0;

        // A heal row carries finite values only, inside +-AreaValue.MaxSum (HealRow.Validate). A rival's money is
        // +Infinity in vanilla (CompanyBehaviour.EnableAI adds it), so a rival's money group could never be sent and a
        // joiner whose rival money group differed went out of step. The host writes an infinite value as the wire's own
        // limit, which no finite value goes out as (one that large is not sendable, as before: NaN here), and the
        // restore reads the limit back as the infinity it stands for.
        public static double[] ToWire(double[] values)
        {
            if (values == null) return new double[0];
            var w = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                if (double.IsPositiveInfinity(v)) w[i] = AreaValue.MaxSum;
                else if (double.IsNegativeInfinity(v)) w[i] = -AreaValue.MaxSum;
                else w[i] = Math.Abs(v) < AreaValue.MaxSum ? v : double.NaN;
            }
            return w;
        }

        public static double FromWire(double v)
        {
            if (v >= AreaValue.MaxSum) return double.PositiveInfinity;
            return v <= -AreaValue.MaxSum ? double.NegativeInfinity : v;
        }

        // The balance a money row restores. A person's agency never has an infinite balance (the session keeps the
        // game's AI money away from it), so one from the host is refused. Null, or why not.
        public static string MoneyOf(double wire, bool human, out double money)
        {
            money = FromWire(wire);
            if (double.IsNaN(money)) return "the money row's balance is not a number";
            if (human && double.IsInfinity(money)) return "an infinite balance for an agency a person plays";
            return null;
        }

        // A stock or deposit key: "body/resource/state".
        public static bool ResourceKey(string key, out int body, out string resource, out int state)
        {
            body = 0; state = 0; resource = null;
            if (key == null) return false;
            string[] part = key.Split(WorldPrint.KeySeparator);
            if (part.Length != 3) return false;
            resource = part[1];
            return Int(part[0], out body) && Int(part[2], out state);
        }

        public static bool Int(string s, out int value)
        {
            return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        // The research group's rows as M5's ResearchState (ObjectRestore applies that) and the finished topics:
        // "done/<id>" (no values), "slot/<id>" (progress, complete), "queue/<pos>/<id>", "partial/<id>" (progress),
        // "observatory-bonus" (a value no restore sets). null = read; else why the rows are not research rows.
        public static string Research(IList<HealRow> rows, out ResearchState state, out List<string> done)
        {
            state = null;
            done = new List<string>();
            var queue = new SortedDictionary<int, string>();
            var paused = new List<string>();
            var progress = new List<double>();
            string running = null;
            double runningProgress = 0;
            foreach (HealRow r in rows ?? new HealRow[0])
            {
                string[] part = r == null || r.Key == null ? new string[0] : r.Key.Split(WorldPrint.KeySeparator);
                double[] v = r == null ? null : r.Values ?? new double[0];
                string head = part.Length > 0 ? part[0] : null;
                if (head == "done" && part.Length == 2) done.Add(part[1]);
                else if (head == "slot" && part.Length == 2 && v.Length >= 1)
                {
                    running = part[1].Length == 0 ? null : part[1];
                    runningProgress = running == null ? 0 : v[0];
                }
                else if (head == "queue" && part.Length == 3)
                {
                    int at;
                    if (!Int(part[1], out at) || queue.ContainsKey(at)) return "a research queue row " + r.Key;
                    queue[at] = part[2];
                }
                else if (head == "partial" && part.Length == 2 && v.Length >= 1)
                {
                    paused.Add(part[1]);
                    progress.Add(v[0]);
                }
                else if (head != "observatory-bonus") return "not a research row: " + (r == null ? "none" : r.Key);
            }
            state = new ResearchState { Running = running, Progress = runningProgress,
                Queue = new List<string>(queue.Values).ToArray(), Paused = paused.ToArray(),
                PausedProgress = progress.ToArray() };
            return null;
        }
    }
}
