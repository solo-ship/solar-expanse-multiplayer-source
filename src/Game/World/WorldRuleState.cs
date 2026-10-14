using System.Collections.Generic;
using Company = Game.Company;

namespace SolarExpanseMultiplayer
{
    // What a rival's seam record hands the host when it closes (DD-W6): one AiAction's steps and ops with the counters
    // the task moved, or the observatory order a rival gave (Order, kind 40; then Steps and Ops are empty).
    public sealed class AiRecordResult
    {
        public string Agency;
        public AiStep[] Steps;
        public MarketOp[] Ops;
        public CounterRow[] Counters;
        public ActionRequest Order;
        public ApplyNumbers OrderNumbers;   // the order's dice and the observatory's state after
    }

    // The host half of M7's world rules: what the rules raise. The host's CommandSession implements it over
    // CommandService (CommandSessionWorld.cs) and sets it at every rules start (RegisterWorld); with none set the
    // rules run the game as it was and record nothing (not with -semp-no-world-rules, the negative control).
    public interface IWorldDecisions
    {
        // The market maker finished a run (kind 90): value 0 yearly, 1 next-day re-quote; updateTicks the date of
        // lastOfferPricesUpdateTime after a yearly run; counters the offer counter's row over the whole run.
        void MarketMaker(MarketOp[] ops, int value, long updateTicks, CounterRow[] counters);

        // A year event's growth (kind 91), one part at a time; `last` marks the final part.
        void PopulationGrowth(GrowthRow[] rows, bool last);

        // A completion or an activation the rules held (DD-W7): the host notes it and raises it at DayEventDone.
        void NoteCompletion(string agency, ContractRef objective);
        void NoteActivation(string agency, string contract);

        // The host is about to write a file save: raise every noted completion and activation now.
        void FlushNotes();

        // A rival's seam method started or ended on the host. Close hands the result (nothing when it has no steps).
        void AiRecordOpen(string agency);
        void AiRecordClose(AiRecordResult result);

        // The host's own game did something the clients cannot be told: the stream resets.
        void HostFault(string why);

        // The host's own end-game flight (ChangeTrajectoryAsteroidToEarth) built its puller ship (kind 96), raised
        // before the flight's plan (70); counters the ship counter's row.
        void EndGameShip(string agency, string shipType, long shipId, CounterRow[] counters);
    }

    // The client half: the year event's rows, the UI-only objectives and out-of-step reports. Implemented by the client
    // glue (Task 12).
    public interface IWorldClientSeam
    {
        // The new year's 91 applied ahead of the year handlers; a reason is out of step.
        string ApplyInEventNow(int kind, long localDay);
        GrowthBook Growth { get; }
        string CloseInEvent(int kind);
        long LocalDay { get; }
        void OutOfStep(string why);
        // The local player's own UI-only objective completed: report it to the host (kind 88).
        void ObjectiveUi(ContractRef objective);
    }

    // A market maker run in progress on the host.
    public sealed class MakerRecord
    {
        public readonly List<MarketOp> Ops = new List<MarketOp>();
        public int Value;
        public Company Maker;
        public long OffersBefore;
    }

    // A rival's seam record in progress on the host.
    public sealed class AiOpen
    {
        public AiRecord Rec;
        public Company Company;
        public long ProductionBefore, OffersBefore;
        public ActionRequest Order;
        public ApplyNumbers OrderNumbers;
        public bool OrderDice;              // the order's dice record is open
    }

    // Everything M7's world rules remember between one game call and the next, in the one object the HumanAgencies
    // root owns (RuleState.World): a load or a new session starts it again.
    public sealed class WorldRuleState
    {
        public IWorldDecisions Host;
        public IWorldClientSeam Client;

        public MakerRecord Maker;
        public List<GrowthRow> GrowthRows;     // the host's open year record
        public GrowthBook Book;                // a client's open year event
        public bool YearOpen;
        public System.Action<double> YearList;  // the game's year handlers, while the wrapper runs them
        public AiOpen Ai;                      // the rival record open on the host
        public bool Loading;                   // inside ExtractAllFromSaveData and its AfterLoadState loop
        public int AiOpenDepth;                // seam methods nested in the open record
        public readonly HeldNotes Held = new HeldNotes();
        public Company Discoverer;
        public bool FileSaving;
        public List<ChainRow> Chain;           // the 92 being applied: what it completed, in order
        public int FinishDepth;

        // The rules act only once the glue behind them is set (the host's raise seam, a client's apply seam); until
        // then the game runs as it was.
        public bool Wired { get { return HumanAgencies.Host != null ? Host != null : Client != null; } }

        public void Reset()
        {
            Maker = null;
            GrowthRows = null;
            Book = null;
            YearOpen = false;
            YearList = null;
            Ai = null;
            AiOpenDepth = 0;
            Loading = false;
            Held.Clear();
            Discoverer = null;
            FileSaving = false;
            Chain = null;
            FinishDepth = 0;
        }
    }
}
