using System;
using System.Collections.Generic;
using System.Globalization;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;

namespace SolarExpanseMultiplayer
{
    // The client's restores of a healed group (M7 DD-W10): the host's rows set into this game through the game's own
    // setters. The rows are GroupSink's (the one writer on both games) and are read back through WorldRows. What is
    // built here is money, research (M5's ObjectRestore), stock, population, deposits, and (WorldRestoreMore) the
    // exploration, contracts and market; every other area answers a
    // reason, which puts the client out of step (the full rejoin), never a guess. The caller holds Applying while this
    // runs. Main thread only.
    public static partial class WorldRestore
    {
        // null = restored; else why not.
        public static string Restore(HealData[] pieces)
        {
            if (pieces == null || pieces.Length == 0 || pieces[0] == null) return "no heal pieces";
            HealData first = pieces[0];
            var rows = new List<HealRow>();
            foreach (HealData p in pieces)
            {
                if (p == null || p.Rows == null || p.Area != first.Area || p.Group != first.Group || p.Agency
                    != first.Agency)
                    return "heal pieces of different groups";
                rows.AddRange(p.Rows);
            }
            switch (first.Area)
            {
                case 0: return Money(first.Agency, rows);
                case 1: return Research(first.Agency, rows);
                case 4: return Stock(first.Agency, first.Group, rows);
                case 5: return Population(first.Agency, first.Group, rows);
                case 6: return Exploration(first.Agency, first.Group, rows);
                case 9: return Contracts(first.Agency, first.Group, rows);
                case 10: return Market(first.Group, rows);
                case 12: return Deposits(first.Group, rows);
                default:
                    return "the " + (first.Area < DriftAreas.All.Length ? DriftAreas.All[first.Area] : "area "
                        + first.Area)
                        + " area is not restored by this build";
            }
        }

        // ---- money: the controller's balance, the name counters and the production counter ----

        // money row: CurrentMoney (a rival's is +Infinity, sent as the wire's limit), TotalProfit, survey cost,
        // DiscoveredSystemsCount, ProductionCountID; a counter row: its name and one count. The group is the agency's
        // whole money area, so the name counters become the host's exactly (one only this game has goes). TotalProfit
        // is a record the game keeps and a restore does not touch.
        static string Money(string agency, List<HealRow> rows)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null || c.MoneyController == null) return "no agency " + agency;
            Dictionary<string, int> names = c.CountSN;
            if (names == null) return "no name counters";
            var hostNames = new HashSet<string>();
            foreach (HealRow r in rows)
            {
                if (r.Values == null || r.Values.Length < 1) return "a money row with no values";
                if (r.Key == WorldRows.MoneyKey)
                {
                    if (r.Values.Length < WorldRows.MoneyValues)
                        return "the money row has " + r.Values.Length + " values, not " + WorldRows.MoneyValues;
                    double money;
                    bool human = HumanAgencies.IsHuman(c);
                    string bad = WorldRows.MoneyOf(r.Values[WorldRows.MoneyCurrent], human, out money);
                    if (bad != null) return bad;
                    c.MoneyController.ForCheatSetCurrentMony(money);
                    c.productionCountID = (int)r.Values[WorldRows.MoneyProductionCount];
                }
                else
                {
                    names[r.Key] = (int)r.Values[WorldRows.NameCount];
                    hostNames.Add(r.Key);
                }
            }
            foreach (string key in new List<string>(names.Keys)) if (!hostNames.Contains(key)) names.Remove(key);
            return null;
        }

        // ---- research: M5's restore of what runs and waits; what is finished cannot be set ----

        static string Research(string agency, List<HealRow> rows)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null || c.ResearchManager == null) return "no research of " + agency;
            ResearchState state;
            List<string> done;
            string bad = WorldRows.Research(rows, out state, out done);
            if (bad != null) return bad;
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllResearchDefinition;
            foreach (string id in done)
            {
                var d = all != null ? all.GetByID(id) : null;
                if (d == null || d.ID != id || !c.ResearchManager.IsComplete(d))
                    return id + " is finished on the host and not here";
            }
            return ObjectRestore.RestoreResearch(c, state);
        }

        // ---- stock and deposits: a row's Value ----

        static string Stock(string agency, string group, List<HealRow> rows)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null) return "no agency " + agency;
            int body;
            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out body)) return "stock group "
                + group;
            ObjectInfoData d = GameAgencies.Site(GameAgencies.Body(body), c);
            if (d == null) return "no site of " + agency + " at body " + body;
            foreach (HealRow r in rows)
            {
                RowResourcesData row = FindRow(d.ListRowResourcesData, r.Key, body);
                if (row == null || r.Values == null || r.Values.Length < 1) return "no stock row " + r.Key;
                row.Value = r.Values[WorldRows.StockValue];   // Balance is a rate the game recomputes: left alone
            }
            return null;
        }

        static string Deposits(string group, List<HealRow> rows)
        {
            int body;
            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture,
                out body)) return "deposit group " + group;
            ObjectInfo oi = GameAgencies.Body(body);
            if (oi == null) return "no body " + body;
            foreach (HealRow r in rows)
            {
                RowResourcesData row = FindRow(oi.ListRowResourcesData, r.Key, body);
                if (row == null || r.Values == null || r.Values.Length < 2) return "no deposit row " + r.Key;
                row.Value = r.Values[WorldRows.DepositValue];
                double mf = r.Values[WorldRows.DepositMiningFactor];
                if (mf >= 0) row.MiningFactor = (float)mf;
            }
            return null;
        }

        // key: body / resource id / state (WorldRows.ResourceKey).
        static RowResourcesData FindRow(List<RowResourcesData> list, string key, int body)
        {
            int bodyKey, state;
            string resource;
            if (list == null || !WorldRows.ResourceKey(key, out bodyKey, out resource, out state) || bodyKey != body)
                return null;
            foreach (RowResourcesData r in list)
                if (r != null && r.resourceTypeIDSave != null && r.resourceTypeIDSave.id == resource
                    && (int)r.ResourceState == state) return r;
            return null;
        }

        // ---- population: the crew through the setter (the game recomputes the crew in work) ----

        static string Population(string agency, string group, List<HealRow> rows)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null) return "no agency " + agency;
            int body;
            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture,
                out body)) return "population group " + group;
            ObjectInfoData d = GameAgencies.Site(GameAgencies.Body(body), c);
            if (d == null || d.CrewResource == null) return "no crew site of " + agency + " at body " + body;
            // the host lists a body only while it has crew; no row is no crew
            double crew = 0;
            foreach (HealRow r in rows)
            {
                if (r.Key != group || r.Values == null || r.Values.Length < 1) return "a population row " + r.Key;
                crew = r.Values[WorldRows.Crew];
            }
            d.CrewResource.Value = crew;
            return null;
        }
    }
}
