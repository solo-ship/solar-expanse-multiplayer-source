using System;
using System.Collections.Generic;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    public sealed class ExploreRow { public string Resource; public double Value; public bool Preliminary, Selected; }

    // One site's exploration group: the site row (discovered, being discovered) and its explored-resource rows.
    public sealed class ExploreGroup
    {
        public bool Discovered, Being;
        public readonly List<ExploreRow> Rows = new List<ExploreRow>();
    }

    public sealed class ObjectiveRow { public int Index; public bool Complete; public double Current; }

    // One contract's group for one agency: its state and start, and one row per objective.
    public sealed class ContractGroup
    {
        public int State, AgencyState;
        public long StartTicks;
        public readonly List<ObjectiveRow> Objectives = new List<ObjectiveRow>();
    }

    public sealed class OfferRow
    {
        public int Id, Where;
        public string Company, Resource;
        public bool Buy;
        public double Price, Amount, Completed;
        public long StartTicks;
    }

    public sealed class PriceRow { public int Body; public string Resource; public double Price; }

    // The reading side of GroupSink's rows for the exploration, contracts and market areas (M7 DD-W10; W-S7): what a
    // client's restore takes out of the host's heal rows. The layout is StateReader's visit order as GroupSink keeps it
    // (key parts, then string values, joined by '/'; every other value in order; a date as days then ticks in the day),
    // so a reader that disagrees with the writer fails the test that feeds one through the other. Each returns null
    // when the rows are read, else why they are not that area's rows.
    public static class HealRows
    {
        public const string PriceGroup = "price";

        static readonly char[] Sep = { WorldPrint.KeySeparator };

        // exploration: "site" -> [discovered, being]; "site/resource" -> [value, preliminary, full, selected]
        public static string Exploration(IList<HealRow> rows, string group, out ExploreGroup result)
        {
            result = new ExploreGroup();
            bool site = false;
            foreach (HealRow r in rows ?? new HealRow[0])
            {
                string[] part = Parts(r);
                if (part == null || part[0] != group) return "not an exploration row of " + group + ": " + KeyOf(r);
                if (part.Length == 1)
                {
                    if (site || r.Values.Length < 2) return "the site row of " + group;
                    site = true;
                    result.Discovered = r.Values[0] != 0;
                    result.Being = r.Values[1] != 0;
                }
                else if (part.Length == 2 && r.Values.Length >= 4)
                    result.Rows.Add(new ExploreRow { Resource = part[1], Value = r.Values[0],
                        Preliminary = r.Values[1] != 0, Selected = r.Values[3] != 0 });
                else return "an exploration row " + KeyOf(r);
            }
            return site ? null : "no site row for " + group;
        }

        // contracts: "id" -> [state, agency state, start days, start ticks]; "id/k" -> [complete, current]
        public static string Contract(IList<HealRow> rows, string group, out ContractGroup result)
        {
            result = new ContractGroup();
            bool head = false;
            foreach (HealRow r in rows ?? new HealRow[0])
            {
                string[] part = Parts(r);
                if (part == null || part[0] != group) return "not a row of contract " + group + ": " + KeyOf(r);
                if (part.Length == 1)
                {
                    if (head || r.Values.Length < 4) return "the contract row of " + group;
                    head = true;
                    result.State = (int)r.Values[0];
                    result.AgencyState = (int)r.Values[1];
                    result.StartTicks = Ticks(r.Values[2], r.Values[3]);
                }
                else
                {
                    int k;
                    if (part.Length != 2 || r.Values.Length < 2 || !WorldRows.Int(part[1], out k) || k < 0)
                        return "an objective row " + KeyOf(r);
                    result.Objectives.Add(new ObjectiveRow { Index = k, Complete = r.Values[0] != 0,
                        Current = r.Values[1] });
                }
            }
            return head ? null : "no contract row for " + group;
        }

        // market: group "price" -> "price/body/resource" -> [price]; else the offer id's group -> "id/company/resource"
        // -> [buy, price, amount, completed, start days, start ticks, where, done]; a done offer is not restored
        public static string Offers(IList<HealRow> rows, string group, List<OfferRow> offers)
        {
            int id;
            if (!WorldRows.Int(group, out id) || id < 1) return "the offer group " + group;
            foreach (HealRow r in rows ?? new HealRow[0])
            {
                string[] part = Parts(r);
                if (part == null || part.Length < 3 || part[0] != group || r.Values.Length < 8)
                    return "an offer row " + KeyOf(r);
                if (r.Values[7] != 0) continue;
                int where = (int)r.Values[6];
                string company = part.Length == 3 ? part[1] : string.Join("/", part, 1, part.Length - 2);
                offers.Add(new OfferRow { Id = id, Company = company, Resource = part[part.Length - 1],
                    Buy = r.Values[0] != 0, Price = r.Values[1], Amount = r.Values[2], Completed = r.Values[3],
                    StartTicks = Ticks(r.Values[4], r.Values[5]), Where = where });
            }
            return null;
        }

        public static string Prices(IList<HealRow> rows, List<PriceRow> prices)
        {
            foreach (HealRow r in rows ?? new HealRow[0])
            {
                string[] part = Parts(r);
                int body;
                if (part == null || part.Length != 3 || part[0] != PriceGroup || r.Values.Length < 1
                    || !WorldRows.Int(part[1], out body))
                    return "a price row " + KeyOf(r);
                prices.Add(new PriceRow { Body = body, Resource = part[2], Price = r.Values[0] });
            }
            return null;
        }

        // A date written as whole days and the ticks within the day (GroupSink.Date).
        public static long Ticks(double days, double inDay)
        {
            return (long)days * TimeSpan.TicksPerDay + (long)inDay;
        }

        static string[] Parts(HealRow r)
        {
            return r == null || r.Key == null || r.Values == null ? null : r.Key.Split(Sep);
        }

        static string KeyOf(HealRow r) { return r == null ? "none" : r.Key; }
    }
}
