using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // M7 Task 9 (DD-W6): what one seam-method call of a rival's AI did to the shared world, as the steps and ops of
    // one AiAction. Pure: the game's recorders feed it, the seam turns the result into an entry. Only the outermost
    // world call is recorded (the game's nested work is not), an Enabled write repeated with the same value is
    // dropped, and a money payment followed by resources put on the site is one BuyDirect step.
    public sealed class AiRecord
    {
        readonly List<AiStep> steps = new List<AiStep>();
        readonly List<MarketOp> ops = new List<MarketOp>();
        readonly HashSet<string> enabled = new HashSet<string>();
        int depth;
        double paid;

        public string Agency;
        public bool Overflow;        // more steps and ops than one entry holds: the host faults, never truncates

        // A world method started: true when it is the outermost one, which is the one that is recorded.
        public bool Enter() { return ++depth == 1; }

        public void Leave() { if (depth > 0) depth--; }

        public bool Empty { get { return steps.Count == 0 && ops.Count == 0; } }

        public AiStep[] Steps() { return steps.ToArray(); }

        public MarketOp[] Ops() { return ops.ToArray(); }

        void Add(AiStep s)
        {
            if (steps.Count + ops.Count >= ActionLimits.MaxAiSteps) { Overflow = true; return; }
            steps.Add(s);
        }

        // The topic a rival just finished: always the first step.
        public void Complete(string topic)
        {
            if (steps.Count >= ActionLimits.MaxAiSteps) { Overflow = true; return; }
            steps.Insert(0, new AiStep { Op = AiOps.Complete, Item = topic });
        }

        public void Research(string topic) { Add(new AiStep { Op = AiOps.Research, Item = topic }); }

        public void Remove(int body, string resource, double amount)
        {
            if (amount > 0) Add(new AiStep { Op = AiOps.Remove, Body = body, Item = resource, Amount = amount });
        }

        public void Build(int body, string item, long itemId, double count)
        {
            Add(new AiStep { Op = AiOps.Build, Body = body, Item = item, ItemId = itemId, Amount = count });
        }

        // Facility.Enabled written: the two energy classes write it twice with the same value in one call.
        public void Enable(int body, string facility, long itemId, long value)
        {
            if (!enabled.Add(body + "/" + itemId + "/" + value)) return;
            Add(new AiStep { Op = AiOps.Enable, Body = body, Item = facility, ItemId = itemId, Amount = value });
        }

        // RemoveMoney: held until the resources it bought arrive.
        public void Pay(double cost) { if (cost > 0) paid += cost; }

        // AddResources: with money paid just before, the purchase at the main object (BuyResources); else not a step.
        public bool Bought(int body, string resource, double amount)
        {
            if (paid <= 0 || amount <= 0) return false;
            Add(new AiStep { Op = AiOps.BuyDirect, Body = body, Item = resource, Amount = amount, Price = paid / amount });
            paid = 0;
            return true;
        }

        public void Op(MarketOp op)
        {
            if (op == null) return;
            if (steps.Count + ops.Count >= ActionLimits.MaxAiSteps) { Overflow = true; return; }
            ops.Add(op);
        }
    }

    // M7 Task 8 (DD-W5): one year event's growth rows on the host, split into entries of at most `max` rows. A year
    // with no crewed body is still one (empty) part, so a client never waits for a 91 that does not come.
    public static class GrowthParts
    {
        public static List<GrowthRow[]> Split(IList<GrowthRow> rows, int max)
        {
            var parts = new List<GrowthRow[]>();
            int at = 0, n = rows == null ? 0 : rows.Count;
            do
            {
                int take = System.Math.Min(max, n - at);
                var part = new GrowthRow[take];
                for (int i = 0; i < take; i++) part[i] = rows[at + i];
                parts.Add(part);
                at += take;
            } while (at < n);
            return parts;
        }
    }

    // M7 Task 10 (DD-W7): the held completions and activations of a client, in the order they were held, each once.
    // The key names (agency, contract, objective); an objective of -1 is an activation.
    public sealed class HeldNotes
    {
        readonly List<string> order = new List<string>();
        readonly HashSet<string> keys = new HashSet<string>();

        public static string Key(string agency, string contract, int objective)
        {
            return agency + "|" + contract + "|" + objective;
        }

        public int Count { get { return order.Count; } }

        public bool Add(string key)
        {
            if (!keys.Add(key)) return false;
            order.Add(key);
            return true;
        }

        public bool Remove(string key)
        {
            if (!keys.Remove(key)) return false;
            order.Remove(key);
            return true;
        }

        public bool Has(string key) { return keys.Contains(key); }

        public string[] InOrder() { return order.ToArray(); }

        public void Clear() { order.Clear(); keys.Clear(); }
    }
}
