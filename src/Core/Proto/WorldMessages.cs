using System;

namespace SolarExpanseMultiplayer
{
    // Protocol 6's shared-world types (M7 plan "Protocol 6"; DD-W2..W10). Plain classes with fixed schemas: every list is
    // length-checked before anything uses it, every double is finite and in range, every id is a content id or an agency
    // id. No type here carries free text: nothing from the other side reaches a player as a line (CommandText builds
    // them from codes).

    // Where an entry came from (ActionResult.Origin). A client checks it against its HumanSet and the kind.
    public static class EntryOrigin { public const int Player = 0, Host = 1, Ai = 2; }

    public static class MarketOps
    {
        public const int Add = 1, Cancel = 2, Fill = 3, Instant = 4, PriceDiff = 5, Lock = 6;
    }

    public static class AiOps
    {
        // Complete: Item = the topic the rival just finished (DD-W6 item 13); the first step of its entry, alone or
        // followed by one Research step
        public const int Build = 1, Research = 2, Enable = 3, BuyDirect = 4, Remove = 5, Lock = 6, Complete = 7;
    }

    // What a heal request is answered with (HealData.Code).
    public static class HealCodes { public const int Ok = 0, TooBig = 1, TooMany = 2, NotRestorable = 3, NoPrint = 4; }

    // The offer an op names: exactly one live offer must match it (DD-W3).
    public sealed class OfferKey
    {
        public int Id;                    // Offer.ID, > 0 (0 only in an Add of a request: the host decides it)
        public string Agency;             // the maker
        public string Resource;           // content id
        public bool Buy;                  // buySell: true = the maker buys
        public int Where;                 // whereOfferID, 1..MaxBodyId
        // offerStartDate. 0 in an Add of a request (the host's time at its add), and on a listed offer vanilla made
        // with no date: a new game's opening World Government offers and the offers a single-player game's own
        // window made all carry DateTime 0.
        public long StartTicks;

        // pending: the key of an Add in a request, which has no id and no start time yet
        internal string Validate(bool pending)
        {
            string e = pending
                ? (Id != 0 ? "Offer.Id is the host's to decide" : StartTicks != 0 ? "Offer.StartTicks is the host's to decide" : null)
                : CommandCheck.Range(Id, "Offer.Id", 1, int.MaxValue) ?? JoinCheck.Ticks(StartTicks, "Offer.StartTicks", true);
            return e ?? JoinCheck.Agency(Agency, "Offer.Agency", true) ?? CommandCheck.Id(Resource, "Offer.Resource")
                ?? CommandCheck.Range(Where, "Offer.Where", 1, ActionLimits.MaxBodyId);
        }
    }

    public sealed class MarketOp
    {
        public int Op;                    // MarketOps
        public OfferKey Offer;            // Cancel, Fill, Lock: which offer (exactly one live match); Add: the new offer's
                                          // key (Id = the id it must take); null for Instant and PriceDiff
        public double Count;              // Add: countToBuySell (whole, 1..MaxOfferCount); Fill and Instant:
                                          // units; Cancel (host-made): the units the host's CancelOffer refunded
        public double Price;              // Add: price per unit (whole); Fill and Instant: the price the actor saw (an
                                          // Instant request: the limit price); PriceDiff: maxPriceDifferencePercentage;
                                          // Cancel (host-made): price per unit
        public string Taker;              // Fill and Instant: the agency that takes or trades; Lock: the locking rival
        public string Resource;           // Instant and PriceDiff: content id
        public int Body;                  // Instant and PriceDiff: the body
        public int Value;                 // Instant: 0 buy, 1 sell; Lock: 1 locked by Taker, 0 free; Cancel (host-made):
                                          // 1 = the offer was done when cancelled (DD-W3)
        // A take request's (OfferTake, Fill) expected remaining count: the units the dialog saw left on the offer, a whole
        // number of at least Count. The host refuses OfferChanged when the offer has fewer left. 0 everywhere else.
        public double Left;

        // request: the form a client sends (a Cancel carries only its key; an Instant only its limit price); else the
        // form the host makes (a Cancel carries its outcome, an Instant its whole trade)
        internal string Validate(bool request)
        {
            string e = CommandCheck.Range(Op, "Op", MarketOps.Add, MarketOps.Lock);
            if (e != null) return e;
            if (request && (Op == MarketOps.PriceDiff || Op == MarketOps.Lock)) return "a request carries no " + OpName();
            e = Message.Finite(Count, "Op.Count", 0, ActionLimits.MaxMoney)
                ?? Message.Finite(Price, "Op.Price", 0, ActionLimits.MaxMoney)
                ?? Message.Finite(Left, "Op.Left", 0, ActionLimits.MaxOfferCount)
                ?? CommandCheck.Range(Body, "Op.Body", 0, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Value, "Op.Value", 0, 1);
            if (e != null) return e;
            if (Taker != null) { e = JoinCheck.Agency(Taker, "Op.Taker", true); if (e != null) return e; }
            if (Resource != null) { e = CommandCheck.Id(Resource, "Op.Resource"); if (e != null) return e; }
            if (Left != 0 && !(request && Op == MarketOps.Fill)) return "Op.Left only rides on a take request";
            switch (Op)
            {
                case MarketOps.Add:
                    if (Offer == null) return "an Add needs its offer";
                    e = Offer.Validate(request);
                    if (e != null) return e;
                    // every offer the host's game adds is dated now (its own, a player's, the maker's, a rival's)
                    if (!request && Offer.StartTicks == 0) return "an Add carries the host's start time";
                    // the window's count is vanilla's int, as large as the agency's money or stock: thousands of
                    // units are an ordinary offer, and the host checks what the agency can pay or deliver
                    if (Count < 1 || Count > ActionLimits.MaxOfferCount || Count != Math.Floor(Count))
                        return "an Add's Count is a whole number of 1.." + ActionLimits.MaxOfferCount;
                    if (Price != Math.Floor(Price)) return "an Add's Price is a whole number";
                    return Taker != null || Resource != null || Body != 0 || Value != 0 ? "an Add carries only an offer, a count and a price" : null;
                case MarketOps.Cancel:
                    if (Offer == null) return "a Cancel needs its offer";
                    e = Offer.Validate(false);
                    if (e != null) return e;
                    if (Taker != null || Resource != null || Body != 0) return "a Cancel carries only an offer and its outcome";
                    if (request) return Count != 0 || Price != 0 || Value != 0 ? "a Cancel in a request carries only its offer" : null;
                    if (Count < 1 || Count > ActionLimits.MaxOfferCount || Count != Math.Floor(Count))
                        return "a Cancel's refunded Count is a whole number of 1.." + ActionLimits.MaxOfferCount;
                    return Price <= 0 ? "a Cancel's Price is above 0" : null;
                case MarketOps.Fill:
                    if (Offer == null) return "a Fill needs its offer";
                    e = Offer.Validate(false) ?? JoinCheck.Agency(Taker, "Fill.Taker", true);
                    if (e != null) return e;
                    if (Count <= 0) return "a Fill's Count is above 0";
                    if (Resource != null || Body != 0 || Value != 0) return "a Fill carries only an offer, a taker, a count and a price";
                    if (!request) return null;
                    if (Left < 1 || Left != Math.Floor(Left)) return "a take request carries the whole count it saw left";
                    return Left < Count ? "a take request cannot ask for more than it saw left" : null;
                case MarketOps.Instant:
                    if (Offer != null) return "an Instant uses no offer";
                    if (request)
                        return Taker != null || Resource != null || Body != 0 || Count != 0 || Value != 0
                            ? "an Instant in a request carries only its limit price" : null;
                    e = JoinCheck.Agency(Taker, "Instant.Taker", true) ?? CommandCheck.Id(Resource, "Instant.Resource")
                        ?? CommandCheck.Range(Body, "Instant.Body", 1, ActionLimits.MaxBodyId);
                    return e ?? (Count <= 0 ? "an Instant's Count is above 0" : null);
                case MarketOps.PriceDiff:
                    if (Offer != null || Taker != null || Count != 0 || Value != 0) return "a PriceDiff carries only a resource, a body and a percentage";
                    e = CommandCheck.Id(Resource, "PriceDiff.Resource") ?? CommandCheck.Range(Body, "PriceDiff.Body", 1, ActionLimits.MaxBodyId);
                    return e;
                default:   // Lock
                    if (Offer == null) return "a Lock needs its offer";
                    e = Offer.Validate(false);
                    if (e != null) return e;
                    if (Count != 0 || Price != 0 || Resource != null || Body != 0) return "a Lock carries only an offer, a rival and a value";
                    return (Value == 1) != (Taker != null) ? "a Lock names its rival exactly when it locks" : null;
            }
        }

        string OpName()
        {
            switch (Op) { case MarketOps.PriceDiff: return "PriceDiff"; case MarketOps.Lock: return "Lock"; default: return "op"; }
        }

        internal static string ValidateList(MarketOp[] ops, bool request, string field)
        {
            if (ops == null) return null;
            if (ops.Length > ActionLimits.MaxMarketOps) return field + " has more than " + ActionLimits.MaxMarketOps + " ops";
            foreach (MarketOp o in ops)
            {
                if (o == null) return "an empty op in " + field;
                string e = o.Validate(request);
                if (e != null) return e;
            }
            return null;
        }

        internal MarketOp Copy()
        {
            return new MarketOp { Op = Op, Offer = Offer == null ? null : Offer.Copy(), Count = Count, Price = Price,
                Taker = Taker, Resource = Resource, Body = Body, Value = Value, Left = Left };
        }

        internal static MarketOp[] CopyList(MarketOp[] ops)
        {
            if (ops == null) return null;
            var c = new MarketOp[ops.Length];
            for (int i = 0; i < c.Length; i++) c[i] = ops[i] == null ? null : ops[i].Copy();
            return c;
        }
    }

    static class OfferKeyCopy
    {
        internal static OfferKey Copy(this OfferKey k)
        {
            return new OfferKey { Id = k.Id, Agency = k.Agency, Resource = k.Resource, Buy = k.Buy, Where = k.Where, StartTicks = k.StartTicks };
        }
    }

    // One thing a rival's task did (DD-W6). Replayed in order under the entry's keyed apply.
    public sealed class AiStep
    {
        public int Op;                    // AiOps
        public int Body;                  // the site (Build, Enable, BuyDirect, Remove)
        public string Item;               // content id: build descriptor, topic, facility, resource
        public long ItemId = -1;          // Enable: the facility's item id; Build: the new item's id (also in NewIds)
        public double Amount;             // Remove and BuyDirect: units; Build: count; Enable: the Enabled value
        public double Price;              // BuyDirect: the price per unit paid

        internal string Validate()
        {
            string e = CommandCheck.Range(Op, "Step.Op", AiOps.Build, AiOps.Complete)
                ?? CommandCheck.Range(Body, "Step.Body", 0, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(ItemId, "Step.ItemId", -1, int.MaxValue)
                ?? Message.Finite(Amount, "Step.Amount", 0, ActionLimits.MaxMoney)
                ?? Message.Finite(Price, "Step.Price", 0, ActionLimits.MaxMoney);
            if (e != null) return e;
            if (Item != null) { e = CommandCheck.Id(Item, "Step.Item"); if (e != null) return e; }
            if (Op == AiOps.Lock) return null;   // the AI's own lock on a craft or module: no world change, nothing else fixed
            if (Item == null) return "a step needs its Item";
            bool site = Op == AiOps.Build || Op == AiOps.Enable || Op == AiOps.BuyDirect || Op == AiOps.Remove;
            if (site != (Body >= 1)) return site ? "a step needs its Body" : "this step has no Body";
            if ((Op == AiOps.Build || Op == AiOps.BuyDirect || Op == AiOps.Remove) && Amount <= 0) return "a step needs an Amount above 0";
            if (Op == AiOps.Enable && ItemId < 0) return "an Enable step names its facility";
            if (Op != AiOps.Build && Op != AiOps.Enable && ItemId != -1) return "this step has no ItemId";
            if (Op == AiOps.BuyDirect ? Price <= 0 : Price != 0) return Op == AiOps.BuyDirect ? "a BuyDirect step needs its Price" : "this step has no Price";
            if ((Op == AiOps.Research || Op == AiOps.Complete) && Amount != 0) return "this step has no Amount";
            return null;
        }

        internal static string ValidateList(AiStep[] steps)
        {
            if (steps == null) return null;
            if (steps.Length > ActionLimits.MaxAiSteps) return "Steps has more than " + ActionLimits.MaxAiSteps + " entries";
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] == null) return "an empty step";
                string e = steps[i].Validate();
                if (e != null) return e;
                // a Complete is only ever the first step, alone or followed by exactly one Research step (DD-W6 item 13)
                if (steps[i].Op == AiOps.Complete && i != 0) return "a Complete step comes first";
            }
            if (steps.Length > 0 && steps[0].Op == AiOps.Complete
                && !(steps.Length == 1 || (steps.Length == 2 && steps[1].Op == AiOps.Research)))
                return "a Complete step is alone or followed by one Research step";
            return null;
        }
    }

    public sealed class GrowthRow
    {
        public string Agency; public int Body; public int Delta;   // Delta -1e6..1e6

        internal string Validate()
        {
            return JoinCheck.Agency(Agency, "Growth.Agency", true) ?? CommandCheck.Range(Body, "Growth.Body", 1, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Delta, "Growth.Delta", -(long)ActionLimits.MaxGrowth, (long)ActionLimits.MaxGrowth);
        }

        internal static string ValidateList(GrowthRow[] rows)
        {
            if (rows == null) return null;
            if (rows.Length > ActionLimits.MaxGrowthRows) return "Growth has more than " + ActionLimits.MaxGrowthRows + " rows";
            // one row per (agency, body): the game subscribes the roll once for each
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (GrowthRow r in rows)
            {
                if (r == null) return "an empty growth row";
                string e = r.Validate();
                if (e != null) return e;
                if (!seen.Add(r.Agency + "|" + r.Body)) return "a body rolled twice in one growth part";
            }
            return null;
        }

        internal static GrowthRow[] CopyList(GrowthRow[] rows)
        {
            if (rows == null) return null;
            var c = new GrowthRow[rows.Length];
            for (int i = 0; i < c.Length; i++)
                c[i] = rows[i] == null ? null : new GrowthRow { Agency = rows[i].Agency, Body = rows[i].Body, Delta = rows[i].Delta };
            return c;
        }
    }

    public sealed class ContractRef
    {
        public string Contract; public int Objective = -1;   // content id; -1 = the whole contract

        internal string Validate()
        {
            return CommandCheck.Id(Contract, "Contract") ?? CommandCheck.Range(Objective, "Contract.Objective", -1, ActionLimits.MaxObjective);
        }

        internal ContractRef Copy() { return new ContractRef { Contract = Contract, Objective = Objective }; }
    }

    // One objective completed, or (Finished) one contract finished and rewarded.
    public sealed class ChainRow
    {
        public string Agency; public ContractRef Done; public bool Finished;

        internal string Validate()
        {
            string e = JoinCheck.Agency(Agency, "Chain.Agency", true);
            if (e != null) return e;
            if (Done == null) return "a chain row needs its contract";
            e = Done.Validate();
            if (e != null) return e;
            return Finished != (Done.Objective == -1) ? "a finished row names a contract, an objective row names an objective" : null;
        }

        internal static string ValidateList(ChainRow[] rows)
        {
            if (rows == null) return null;
            if (rows.Length > ActionLimits.MaxChainRows) return "Chain has more than " + ActionLimits.MaxChainRows + " rows";
            foreach (ChainRow r in rows)
            {
                if (r == null) return "an empty chain row";
                string e = r.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    // The world print (DD-W9): one Discrete hash per DriftAreas index.
    public sealed class AreaPrint
    {
        public int Area; public string Discrete;   // 16 hex

        internal string Validate(int index)
        {
            return Area != index ? "World prints are one per area, in order" : Message.Hex(Discrete, "World.Discrete", 16, true);
        }
    }

    // One block's field sum on a value day. Field is the DriftFields id the sum is of (a missions block sums
    // costFuel, 49, and deltaV, 50), not a place in a group print's sums.
    public sealed class AreaValue
    {
        public int Area; public string Agency; public int Field; public double Sum;

        public const double MaxSum = 1e18;

        internal string Validate()
        {
            string e = CommandCheck.Range(Area, "Value.Area", 0, DriftAreas.All.Length - 1);
            if (e != null) return e;
            e = DriftAreas.PerAgency(Area) ? JoinCheck.Agency(Agency, "Value.Agency", true)
                : (Agency != null ? "a world area has no agency" : null);
            return e ?? CommandCheck.Range(Field, "Value.Field", 0, DriftFields.Names.Length - 1)
                ?? Message.Finite(Sum, "Value.Sum", -MaxSum, MaxSum);
        }
    }

    public sealed class GroupPrint
    {
        public int Area; public string Agency; public string Group;   // Group: the item key's first segment, <= 64 bytes
        public int Count; public string Discrete;                     // 16 hex
        public double[] Sums;                                         // value days only, else null; at most 16

        internal string Validate()
        {
            string e = CommandCheck.Range(Area, "Group.Area", 0, DriftAreas.All.Length - 1);
            if (e != null) return e;
            e = DriftAreas.PerAgency(Area) ? JoinCheck.Agency(Agency, "Group.Agency", true)
                : (Agency != null ? "a world area has no agency" : null);
            if (e != null) return e;
            if (!ActionShapes.IsIdText(Group, ActionLimits.MaxGroupBytes)) return "Group is not a group name";
            e = CommandCheck.Range(Count, "Group.Count", 0, ActionLimits.MaxUnits) ?? Message.Hex(Discrete, "Group.Discrete", 16, true);
            if (e != null) return e;
            if (Sums == null) return null;
            if (Sums.Length > ActionLimits.MaxGroupSums) return "Group.Sums has more than " + ActionLimits.MaxGroupSums + " entries";
            foreach (double d in Sums)
            {
                e = Message.Finite(d, "Group.Sum", -AreaValue.MaxSum, AreaValue.MaxSum);
                if (e != null) return e;
            }
            return null;
        }
    }

    // M3's ItemCapture key and its exact values, as the host read them.
    public sealed class HealRow
    {
        public string Key; public double[] Values;   // Key <= 128 bytes in a content id's form; at most 16 finite values

        internal string Validate()
        {
            if (!ActionShapes.IsIdText(Key, ActionLimits.MaxHealKeyBytes)) return "Row.Key is not an item key";
            if (Values == null || Values.Length > ActionLimits.MaxHealValues) return "Row.Values is missing or longer than " + ActionLimits.MaxHealValues;
            foreach (double d in Values)
            {
                string e = Message.Finite(d, "Row.Value", -AreaValue.MaxSum, AreaValue.MaxSum);
                if (e != null) return e;
            }
            return null;
        }
    }

    // Client -> host: a world area differed at Day. Agency and Group null: send the area's group prints for Day. Group set
    // (with Agency for a per-agency area, null Agency for a world area): heal that group at the next close.
    public sealed class HealRequest : Message
    {
        public long Day;              // the day whose print differed
        public int Area;              // DriftAreas index
        public string Agency;
        public string Group;

        public override string Validate()
        {
            string e = CommandCheck.Day(Day, "Day") ?? CommandCheck.Range(Area, "Area", 0, DriftAreas.All.Length - 1)
                ?? JoinCheck.Agency(Agency, "Agency", false);
            if (e != null) return e;
            if (Group == null) return null;
            if (!ActionShapes.IsIdText(Group, ActionLimits.MaxGroupBytes)) return "Group is not a group name";
            return DriftAreas.PerAgency(Area) != (Agency != null) ? "a group is named with an agency exactly in a per-agency area" : null;
        }
    }

    // Host -> a client that asked: the area's group prints, or one group's rows in pieces. Code != Ok carries no data.
    public sealed class HealData : Message
    {
        public int Code;              // HealCodes
        public long Day; public uint AfterSeq;   // rows: restored at the client's day-event point of Day, after AfterSeq;
                                                 // group prints: Day = the asked day, AfterSeq 0
        public int Area; public string Agency; public string Group;
        public int Piece, Pieces;     // 0..Pieces-1 of 1..MaxHealPieces (a group's rows, or an area's group prints over MaxGroupPrints in all)
        public GroupPrint[] Groups;   // the answer to a group-print request; at most MaxGroupsPerPiece a piece
        public HealRow[] Rows;        // the answer to a group heal; at most MaxHealRows

        public override string Validate()
        {
            string e = CommandCheck.Range(Code, "Code", HealCodes.Ok, HealCodes.NoPrint) ?? CommandCheck.Day(Day, "Day")
                ?? CommandCheck.Range(Area, "Area", 0, DriftAreas.All.Length - 1) ?? JoinCheck.Agency(Agency, "Agency", false)
                ?? CommandCheck.Range(Pieces, "Pieces", 1, ActionLimits.MaxHealPieces) ?? CommandCheck.Range(Piece, "Piece", 0, Pieces - 1);
            if (e != null) return e;
            if (Group != null && !ActionShapes.IsIdText(Group, ActionLimits.MaxGroupBytes)) return "Group is not a group name";
            if (Code != HealCodes.Ok)
                return Pieces != 1 || Groups != null || Rows != null ? "an answer that is not Ok carries no data" : null;
            if (Group == null)
            {
                // group prints
                if (Groups == null || Rows != null) return "a group-print answer carries Groups";
                if (AfterSeq != 0) return "group prints are not stream-ordered";
                if (Groups.Length > ActionLimits.MaxGroupsPerPiece) return "Groups has more than " + ActionLimits.MaxGroupsPerPiece + " entries in a piece";
                foreach (GroupPrint g in Groups)
                {
                    if (g == null) return "an empty group print";
                    e = g.Validate();
                    if (e != null) return e;
                    if (g.Area != Area) return "a group print of another area";
                }
                return null;
            }
            if (Rows == null || Groups != null) return "a heal carries Rows";
            if (DriftAreas.PerAgency(Area) != (Agency != null)) return "a group is named with an agency exactly in a per-agency area";
            if (Rows.Length > ActionLimits.MaxHealRows) return "Rows has more than " + ActionLimits.MaxHealRows + " rows";
            foreach (HealRow r in Rows)
            {
                if (r == null) return "an empty heal row";
                e = r.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    // Client -> host, during a resume's catch-up (DD-W12). Outside the sender's resume the host ignores it.
    public sealed class ResumeProgress : Message
    {
        public long Day; public uint LastApplied;   // the client's day and last applied seq (checked by the host)
        public bool Done;                           // the answer to ResumeCheck: Print is the client's fingerprint
        public Fingerprint Print;                   // M4's Fingerprint; null unless Done

        public override string Validate()
        {
            string e = CommandCheck.Day(Day, "Day");
            if (e != null) return e;
            if (Done != (Print != null)) return "a print rides exactly with Done";
            return Print == null ? null : Print.Validate();
        }
    }

    // Host -> a resuming client: send your fingerprint once every entry up to Seq is applied.
    public sealed class ResumeCheck : Message
    {
        public uint Seq; public long PausedTicks;

        public override string Validate()
        {
            return Seq < 1 ? "Seq is 0" : JoinCheck.Ticks(PausedTicks, "PausedTicks", false);
        }
    }
}
