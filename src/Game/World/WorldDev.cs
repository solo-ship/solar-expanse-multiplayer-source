using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using MarketOfferManager = Manager.MarketOfferManager;
using ContractManager = Manager.ContractManager;
using GameManager = Manager.GameManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Offer = Game.ObjectInfoDataScripts.Offer;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;

namespace SolarExpanseMultiplayer
{
    // The test profile's triggers for the shared world's rules (M7 Task 16): each makes the game do, now, what the
    // rule needs a run to see (the maker's yearly run, a World Government offer filled, the end-game ship, a drift
    // planted for the heal). They only act in a test profile (DevTriggers) and are written to the log; nothing here
    // is reached by a player. Main thread, once a second at the most.
    //
    //   maker [resource]      host: the World Government's yearly run now, or a re-quote of one resource
    //   fillmaker             host: the host's own agency takes one unit of a World Government offer (a request)
    //   market                any game: reads the price record of every listed resource at the player's home, so the
    //                         lazily made default entries exist here and not on the other game
    //   endgame               host: the game's own end-game ship (the asteroid pulled to Earth)
    //   nuke <body>           host: the host's agency asks to nuke the body, with its own bomb there (the real request)
    //   console <open|close|line> any game: opens or closes the game's developer console, or runs a command line through it (the path Enter takes)
    //   heal <fault> [agency] any game: plants one drift on THIS game (money, stock, deposit, exploration, contract or price) for the heal to mend
    public static class WorldDev
    {
        public static void Poll(CommandSession session, bool host)
        {
            Run("maker", host, true, session, Maker);
            Run("fillmaker", host, true, session, (s, arg) => FillMaker(s));
            Run("market", host, false, session, (s, arg) => Market());
            Run("endgame", host, true, session, (s, arg) => EndGame());
            Run("nuke", host, true, session, Nuke);
            Run("heal", host, false, session, (s, arg) => Heal(arg));
            Run("console", host, false, session, (s, arg) => ConsoleGuard.Dev(arg));
        }

        static void Run(string name, bool host, bool hostOnly, CommandSession session,
            Action<CommandSession, string> act)
        {
            string arg = DevTriggers.Take(name);
            if (arg == null) return;
            if (hostOnly && !host) { Log.Warn("dev: " + name + " is the host's"); return; }
            if (!HumanAgencies.Active) { Log.Warn("dev: " + name + ": no session"); return; }
            try { act(session, arg); }
            catch (Exception e) { Log.Warn("dev: " + name + " threw " + e.GetType().Name + ": " + e.Message); }
        }

        static Company Government()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            return gm != null ? gm.WorldGovernmentCompany : null;
        }

        // ---- maker ----

        static void Maker(CommandSession session, string arg)
        {
            Company wg = Government();
            AiCompanyBehaviour ai = wg != null ? wg.GetComponent<AiCompanyBehaviour>() : null;
            if (ai == null) { Log.Warn("dev: maker: the World Government has no behaviour"); return; }
            ResourceDefinition rd = string.IsNullOrEmpty(arg) ? null : WorldMarket.Resource(arg.Trim());
            if (!string.IsNullOrEmpty(arg) && rd == null) { Log.Warn("dev: maker: no resource " + arg); return; }
            // the yearly run is what the game schedules (TriggerPriceChanges2); one resource is the re-quote
            BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo m = typeof(AiCompanyBehaviour).GetMethod(rd == null ? "TriggerPriceChanges2"
                : "TriggerPriceChanges", any);
            if (m == null) { Log.Warn("dev: maker: the game has no such method"); return; }
            m.Invoke(ai, rd == null ? new object[0] : new object[] { rd });
            Log.Info("dev: maker: ran " + (rd == null ? "the yearly run" : "a re-quote of " + rd.ID));
        }

        // ---- fillmaker ----

        // The host's own agency asks for one unit of a World Government offer: the whole request path, so the fill, the
        // maker's next-day re-quote and (once the offer is done) its later cancel all happen as a player's would.
        static void FillMaker(CommandSession session)
        {
            Company wg = Government();
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (wg == null || gm == null || gm.Player == null) return;
            string me = GameAgencies.Wire(gm.Player), maker = GameAgencies.Wire(wg);
            foreach (Offer o in Manager.MarketOfferManager.Instance.Offerts)
            {
                if (o == null || o.OfferDone || o.Company != wg || o.CountLeft < 1 || o.Rd == null) continue;
                double left = Math.Floor(o.CountLeft);
                var fill = new MarketOp { Op = MarketOps.Fill, Offer = WorldMarket.KeyOf(o), Taker = me, Count = 1,
                    Price = o.PricePerUnit, Left = left };
                session.Click(new ActionRequest { Kind = ActionKind.OfferTake, Agency = me, Market = new[] { fill } });
                Log.Info("dev: fillmaker: " + me + " asked for 1 of " + left + " " + o.Rd.ID + " from " + maker
                    + " (offer " + o.ID + ")");
                return;
            }
            Log.Warn("dev: fillmaker: the World Government has no open offer to fill");
        }

        // ---- market ----

        static void Market()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            ObjectInfo home = gm != null && gm.Player != null ? gm.Player.mainObjectInfo : null;
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllResourceDefinitions;
            if (home == null || all == null) { Log.Warn("dev: market: no home or no resource list"); return; }
            int n = 0;
            foreach (ResourceDefinition rd in all.ListResourceDefinitionInMarketPlaceOffer)
            {
                home.GetResourceMarketData(rd);
                home.GetPrice(rd);
                n++;
            }
            Log.Info("dev: market: read the price record of " + n + " resources at " + home.id);
        }

        // ---- endgame ----

        static void EndGame()
        {
            MethodInfo m = typeof(ContractManager).GetMethod("ChangeTrajectoryAsteroidToEarth",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null || ContractManager.Instance == null)
            {
                Log.Warn("dev: endgame: the game has no such method");
                return;
            }
            m.Invoke(ContractManager.Instance, new object[] { null });
            Log.Info("dev: endgame: ChangeTrajectoryAsteroidToEarth ran");
        }

        // ---- nuke ----

        // The host's agency asks to nuke a body: the real request (what the window's click sends, CaptureWorld), with the
        // first NuclearBomb of the agency's on that body. No bomb there: the refusal NotABomb is what this shows.
        static void Nuke(CommandSession session, string arg)
        {
            int body;
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (gm == null || gm.Player == null || !int.TryParse((arg ?? "").Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out body) || body < 1) { Log.Warn("dev: nuke needs a body id"); return; }
            string me = GameAgencies.Wire(gm.Player);
            ObjectInfoData site = GameAgencies.Site(GameAgencies.Body(body), gm.Player);
            Facility bomb = null;
            if (site != null && site.ListFacility != null)
                foreach (Facility f in site.ListFacility)
                    if (f is NuclearBomb && f.facilityDescriptor != null) { bomb = f; break; }
            var a = new ActionRequest { Kind = ActionKind.NukeAsteroid, Agency = me, Body = body,
                Item = bomb != null ? bomb.facilityDescriptor.ID : "nuke", ItemId = bomb != null ? bomb.ID : 0 };
            session.Click(a);
            Log.Info("dev: nuke: " + me + " asked to nuke " + body + (bomb == null ? " (no bomb of its there)" : ""));
        }

        // ---- heal ----

        // One drift on this game only, in the groups the heal restores. The first word is the fault; the second (a wire
        // id) the agency, the local player's when none.
        static void Heal(string arg)
        {
            string[] w = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 0) { Log.Warn("dev: heal needs a fault: money, stock, deposit, exploration, contract or price"); return; }
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            Company c = w.Length > 1 ? GameAgencies.Find(w[1]) : gm != null ? gm.Player : null;
            if (c == null) { Log.Warn("dev: heal: no such agency"); return; }
            switch (w[0])
            {
                case "money":
                    c.MoneyController.ForCheatSetCurrentMony(c.MoneyController.CurrentMoney + 123456);
                    Log.Info("dev: heal: money of " + GameAgencies.Wire(c) + " raised by 123456 on this game");
                    return;
                case "stock": Drift(StockRows(c), "stock of " + GameAgencies.Wire(c)); return;
                case "deposit": Drift(DepositRows(c), "a deposit"); return;
                case "exploration": DriftExplored(c); return;
                case "contract": DriftContract(c); return;
                case "price": DriftPrice(); return;
                default:
                    Log.Warn("dev: heal: " + w[0] + " is not planted by this build (money, stock, deposit, "
                        + "exploration, contract, price)");
                    return;
            }
        }

        static List<RowResourcesData> StockRows(Company c)
        {
            ObjectInfoData d = GameAgencies.Site(c.mainObjectInfo, c);
            return d != null ? d.ListRowResourcesData : null;
        }

        static List<RowResourcesData> DepositRows(Company c)
        {
            return c.mainObjectInfo != null ? c.mainObjectInfo.ListRowResourcesData : null;
        }

        // The first row with something in it gets half again: a difference a Discrete tolerance cannot miss.
        static void Drift(List<RowResourcesData> rows, string what)
        {
            if (rows != null)
                foreach (RowResourcesData r in rows)
                {
                    if (r == null || r.Value < 1) continue;
                    r.Value = r.Value * 1.5;
                    Log.Info("dev: heal: " + what + ": a row raised by half on this game");
                    return;
                }
            Log.Warn("dev: heal: " + what + " has no row to drift");
        }

        // an explored row at the agency's home: its value raised by one
        static void DriftExplored(Company c)
        {
            ObjectInfoData d = GameAgencies.Site(c.mainObjectInfo, c);
            if (d != null && d.listExploredResourcesRows != null)
                foreach (var r in d.listExploredResourcesRows)
                {
                    if (r == null) continue;
                    r.Value += 1;
                    Log.Info("dev: heal: an explored row of " + GameAgencies.Wire(c) + " raised by one on this game");
                    return;
                }
            Log.Warn("dev: heal: exploration: no explored row at the home of " + GameAgencies.Wire(c));
        }

        // the first objective of the agency's first contract: its running count raised by one
        static void DriftContract(Company c)
        {
            foreach (var k in ContractManager.Instance.allContracts)
            {
                var d = WorldContracts.DataOf(k, c);
                if (d == null || d.ObjectivesDataList == null || d.ObjectivesDataList.Count == 0
                    || d.ObjectivesDataList[0] == null) continue;
                d.ObjectivesDataList[0].howMuchCurrent += 1;
                Log.Info("dev: heal: an objective of " + k.ContractDefinition.ID + " raised by one on this game");
                return;
            }
            Log.Warn("dev: heal: contract: " + GameAgencies.Wire(c) + " has no contract objective to drift");
        }

        // the first price entry: its clearing price raised by half
        static void DriftPrice()
        {
            Market();   // makes the lazily made entries exist
            var mgr = MarketOfferManager.Instance;
            var data = mgr == null ? null : (MarketOfferManager.ResourceMarketPriceData)typeof(MarketOfferManager)
                .GetField("resourceMarketPriceData", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mgr);
            if (data != null && data.resourceMarketPriceDataList != null)
                foreach (var e in data.resourceMarketPriceDataList)
                {
                    if (e == null) continue;
                    e.marketClearingPrice = e.marketClearingPrice * 1.5 + 1;
                    Log.Info("dev: heal: a price entry raised by half on this game");
                    return;
                }
            Log.Warn("dev: heal: price: no price entry");
        }
    }
}
