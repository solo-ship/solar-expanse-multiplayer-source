using System;
using System.Collections.Generic;
using Company = Game.Company;
using GameManager = Manager.GameManager;

namespace SolarExpanseMultiplayer
{
    // The host's shared-world seam (IWorldDecisions, M7 Tasks 8-11): the one object the world rules raise through.
    // The market maker's run, a year's population growth and the end-game ship are entries the host's game has
    // already made; the numbers travel with them as the other recorded entries' do (Decided). The held completions
    // and activations go to the driver's note list (raised at the day event), and a rival's seam record lands in the
    // driver's own AI record (CommandService.AiRecordOpen/Close). A client's session sets its glue as the client half.
    public sealed partial class CommandSession : IWorldDecisions
    {
        // At every rules start (HumanAgencies.Start makes a new state): the host is the rules' host seam, a client's
        // glue their client seam. Until this runs the world rules run vanilla.
        public void RegisterWorld(IWorldClientSeam clientSide)
        {
            WorldRuleState st = HumanAgencies.State != null ? HumanAgencies.State.World : null;
            if (st == null) return;
            st.Host = host ? this : null;
            st.Client = host ? null : clientSide;
        }

        // The World Government's wire id: the agency whose escrow the maker's offers use.
        static string WorldGovernment()
        {
            foreach (Company c in GameManager.Instance.Companies)
                if (c != null && c.Definition != null && c.Definition.IsWorldGovernment) return GameAgencies.Wire(c);
            return null;
        }

        // One run as one or more entries (a modded market's run is longer than an entry holds); each carries the date
        // and the offer counter's step for its own adds. The rule's counter row spans the whole run: it must have
        // moved by exactly the adds, or the parts' rows would not add up on a client.
        public void MarketMaker(MarketOp[] ops, int value, long updateTicks, CounterRow[] counters)
        {
            if (ops == null || Service == null) return;
            string wg = WorldGovernment();
            if (wg == null) { HostFault("the market maker ran with no World Government"); return; }
            List<MarketOp[]> parts = MarketSplit.Parts(ops, ActionLimits.MaxMarketOps);
            int adds = MarketSplit.Adds(ops);
            long running = WorldCounters.Offers - adds;
            CounterRow run = OfferRow(counters);
            if (run != null)
            {
                if (run.After - run.Before != adds)
                {
                    HostFault("market: the offer counter moved by " + (run.After - run.Before) + " for " + adds
                        + " adds");
                    return;
                }
                running = run.Before;
            }
            foreach (MarketOp[] part in parts)
            {
                int n = MarketSplit.Adds(part);
                var numbers = new WorldNumbers { Ops = part };
                if (n > 0)
                    numbers.Counters = new[] { new CounterRow { Which = CounterKind.OfferCount, Before = running,
                        After = running + n } };
                running += n;
                var request = new ActionRequest { Kind = ActionKind.MarketMaker, Agency = wg, Value = value,
                    ItemId = value == 0 ? updateTicks : -1 };
                Decided(request, numbers);
            }
        }

        // A year's rows in call order, in parts of at most MaxGrowthRows; the last part says so (and a year with no
        // crewed body is one empty last part, so a client never waits for one that will not come).
        public void PopulationGrowth(GrowthRow[] rows, bool last)
        {
            if (Service == null) return;
            rows = rows ?? new GrowthRow[0];
            int per = ActionLimits.MaxGrowthRows;
            int parts = Math.Max(1, (rows.Length + per - 1) / per);
            for (int k = 0; k < parts; k++)
            {
                var part = new GrowthRow[Math.Min(per, rows.Length - k * per)];
                Array.Copy(rows, k * per, part, 0, part.Length);
                Decided(new ActionRequest { Kind = ActionKind.PopulationGrowth, Agency = null, Growth = part,
                    Value = last && k == parts - 1 ? 1 : 0 }, new ApplyNumbers());
            }
        }

        static CounterRow OfferRow(CounterRow[] rows)
        {
            if (rows == null) return null;
            foreach (CounterRow r in rows) if (r != null && r.Which == CounterKind.OfferCount) return r;
            return null;
        }

        public void EndGameShip(string agency, string shipType, long shipId, CounterRow[] counters)
        {
            if (Service == null) return;
            Decided(new ActionRequest { Kind = ActionKind.EndGameShip, Agency = agency, Body = 533, Item = shipType,
                ItemId = shipId }, new ApplyNumbers { Counters = counters, NewIds = new[] { shipId } });
        }

        // ---- the held contracts and the save flush: the driver's note list ----

        public void NoteCompletion(string agency, ContractRef objective)
        {
            CommandService s = Service;
            if (s != null) s.NoteCompletion(AgencyWire.From(agency), objective);
        }

        public void NoteActivation(string agency, string contract)
        {
            CommandService s = Service;
            if (s != null) s.NoteActivation(AgencyWire.From(agency), contract);
        }

        public void FlushNotes()
        {
            CommandService s = Service;
            if (s != null) s.FlushNotes();
        }

        // ---- a rival's seam record: the rules build it, the driver's record raises it ----

        public void AiRecordOpen(string agency)
        {
            CommandService s = Service;
            if (s != null) s.AiRecordOpen(AgencyWire.From(agency));
        }

        // Feeds the rules' record into the driver's (steps, ops, counters) and closes it: one 94 when there is
        // anything, then whatever was raised inside the call. A rival's observatory order is its own entry (40),
        // recorded, so the host's game is not asked to order it again.
        public void AiRecordClose(AiRecordResult result)
        {
            CommandService s = Service;
            if (s == null) return;
            try
            {
                if (result != null && s.AiRecordOpened)
                {
                    if (result.Steps != null) foreach (AiStep st in result.Steps) s.AiRecordStep(st);
                    if (result.Ops != null) foreach (MarketOp op in result.Ops) s.AiRecordOp(op);
                    if (result.Counters != null && result.Counters.Length > 0) s.AiRecordCounters(result.Counters);
                    if (result.Order != null) s.AiOrderRecorded(result.Order, result.OrderNumbers);
                }
            }
            finally { s.AiRecordClose(); }
        }

        // ---- the test profile's world triggers (host) ----

        void DevWorldPoll()
        {
            string year = DevTriggers.Take("yearnow");
            if (year != null)
            {
                if (!host) Log.Warn("dev: yearnow is the host's");
                else if (!time.MoveYearToNextDay()) Log.Warn("dev: yearnow: a TimeController field is missing");
                else Log.Info("dev: yearnow: the next day event runs the year event");
            }
            WorldDev.Poll(this, host);
        }
    }
}
