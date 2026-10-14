namespace SolarExpanseMultiplayer
{
    // Protocol 4's eight command messages, kinds 23-30 in Envelope (plan "Interfaces, Protocol 4"). Every one is a
    // plain class with a fixed schema; Validate() refuses each out-of-range field and every list is checked for its
    // length before anything uses it. No message carries a name: ships are named on each game from the name counter, so
    // nothing from the other side reaches a player as text.

    static class CommandCheck
    {
        public static string Range(long v, string field, long min, long max)
        {
            return v < min || v > max ? field + " outside " + min + ".." + max : null;
        }

        public static string Day(long v, string field) { return Range(v, field, 0, ActionLimits.MaxDay); }

        // What a day-boundary print may name: the fetchable kinds, and one agency's missions (Body 0).
        public static string PrintBody(int kind, int body)
        {
            if (kind == ObjectKind.Missions) return body == 0 ? null : "the missions print has no body";
            return ObjectBody(kind, body);
        }

        // Research has no site; every other object is named by its site's ObjectInfo.id. Only fetchable kinds: the
        // missions print is compared, never asked for (ObjectKind.Fetchable).
        public static string ObjectBody(int kind, int body)
        {
            if (!ObjectKind.Fetchable(kind)) return "Kind is not an object kind";
            if (kind == ObjectKind.Research) return body == 0 ? null : "research has no body";
            return Range(body, "Body", 1, ActionLimits.MaxBodyId);
        }

        public static string Id(string id, string field)
        {
            return ActionShapes.IsContentId(id) ? null : field + " is not a content id";
        }

        // A list of content ids: null counts as empty, at most max entries, each a content id.
        public static string IdList(string[] ids, string field, int max)
        {
            if (ids == null) return null;
            if (ids.Length > max) return field + " has more than " + max + " entries";
            foreach (string id in ids)
            {
                string e = Id(id, field + " entry");
                if (e != null) return e;
            }
            return null;
        }
    }

    public sealed class TargetRow
    {
        public int Body;
        public double Ratio;   // EnergyTargets: the share of the facility's output; 0 for MirrorTargets
        public int Count;      // MirrorTargets: the mirrors aimed at the body; 0 for EnergyTargets
    }

    // Client -> host: one click of the player, as the window saw it. Absolute values only (a slider is its final value,
    // never a step), so a lost or repeated request cannot drift a game. ActionShapes says which fields each kind takes.
    public sealed class ActionRequest : Message
    {
        public uint Req;          // the sender's request number: rises by at least 1 per request within one link
        public int Kind;          // ActionKind; a host-only kind from a client is refused here
        public string Agency;     // the acting agency
        public int Body;          // ObjectInfo.id of the site; 0 for research
        public int ToBody;        // MoveToOrbit only: the body the stock or modules are dropped on; 0 otherwise
        // a content id: topic, facility descriptor, spacecraft, launch vehicle, resource, module
        public string Item;
        public int Count;         // units (1..MaxCount), or 0 where the kind has none
        public long ItemId = -1;  // an item id (the game's counter), -1 = none or a queued placeholder
        public int Position = -1; // the position in a list the player saw (0-based), -1 = none
        public int ToPosition = -1;   // reorders: the position it moves before, -1 = none
        public int Value;         // units on, priority, deposit select (absolute values)
        // MoveToOrbit stock moved (0..1e15); SeatClaimed's money (finite, -1e15..1e15: money goes negative)
        public double Amount;
        public string[] Ids;      // research: the whole queue (a set) or the host's expanded list; at most MaxQueue
        public TargetRow[] Targets;   // EnergyTargets, MirrorTargets: the facility's whole new list; at most MaxTargets
        // the observatory kinds: a group's INDEX in the game's group list (examine, discover; ids are not unique), -1 =
        // none (also when absent on the wire)
        public int Group = -1;
        // a probe observatory's ship (its game-wide id, 0 or more), -1 = a site facility (also when absent on the wire)
        public int Ship = -1;
        // M6 flights (protocol 5); null on every other kind (ActionShapes enforces it)
        public MissionData Mission;   // FlightSchedule and SupplyFlight (request form), FlightPlanned (applied form)
        public CyclicalData Route;    // CyclicalSet
        public PresetData Preset;     // PresetSave
        // M7 shared world (protocol 6); null on every other kind (ActionShapes enforces it)
        public MarketOp[] Market;     // 80-84: the request's ops (one Add; one Cancel; Cancel+Add; one Fill; one Instant)
        public ContractRef Contract;  // 86, 87, 88, 92, 93, 95
        public GrowthRow[] Growth;    // 91 (host-decided; a request never carries it)

        // As a client's request: the fields, the kind's shape, and nothing the host alone may decide.
        public override string Validate()
        {
            string e = Check(false);
            if (e != null) return e;
            if (ActionKind.HostOnly(Kind)) return ActionKind.Name(Kind) + " is decided by the host";
            if (Kind == ActionKind.ResearchAddWithPrereqs && Ids != null)
                return "the host expands the prerequisites: no list in a request";
            return null;
        }

        // As the host applied it (ActionResult.Action): host-only kinds and R3's expanded list are allowed.
        internal string ValidateApplied() { return Check(true); }

        // applied: the finished mission (a result's entry) rather than the client's request form
        string Check(bool applied)
        {
            if (!ActionKind.IsKnown(Kind)) return "Kind is not an action";
            string e = JoinCheck.Agency(Agency, "Agency", Kind != ActionKind.PopulationGrowth);
            e = e ?? CommandCheck.Range(Body, "Body", 0, ActionLimits.MaxBodyId);
            e = e ?? CommandCheck.Range(ToBody, "ToBody", 0, ActionLimits.MaxBodyId);
            e = e ?? CommandCheck.Range(Count, "Count", 0, ActionLimits.MaxCount);
            // the market maker's ItemId is a date (ticks)
            e = e ?? CommandCheck.Range(ItemId, "ItemId", -1, Kind == ActionKind.MarketMaker ? JoinCheck.MaxTicks : int.MaxValue);
            e = e ?? CommandCheck.Range(Position, "Position", -1, ActionLimits.MaxPosition);
            e = e ?? CommandCheck.Range(ToPosition, "ToPosition", -1, ActionLimits.MaxPosition);
            e = e ?? CommandCheck.Range(Group, "Group", -1, ActionLimits.MaxBodyId);
            e = e ?? CommandCheck.Range(Ship, "Ship", -1, int.MaxValue);
            e = e ?? CommandCheck.Range(Value, "Value", -ActionLimits.MaxUnits, ActionLimits.MaxUnits);
            e = e ?? Finite(Amount, "Amount", -ActionLimits.MaxMoney, ActionLimits.MaxMoney);
            if (e != null) return e;
            if (Item != null) { e = CommandCheck.Id(Item, "Item"); if (e != null) return e; }
            e = CommandCheck.IdList(Ids, "Ids", ActionLimits.MaxQueue);
            if (e != null) return e;
            if (Targets != null && Targets.Length > ActionLimits.MaxTargets)
                return "Targets has more than " + ActionLimits.MaxTargets + " rows";
            e = MarketOp.ValidateList(Market, true, "Market") ?? GrowthRow.ValidateList(Growth);
            if (e == null && Contract != null) e = Contract.Validate();
            if (e != null) return e;
            return ActionShapes.Check(this, applied);
        }
    }

    public static class CounterKind
    {
        public const int ProductionCountId = 1, NameCounter = 2, SpacecraftCount = 3, LaunchVehicleCount = 4;
        public const int MissionId = 5;   // M6: game-wide, no agency
        public const int OfferCount = 6;   // M7: MarketOfferManager.countOffer, game-wide, no agency
    }

    // One counter an apply moved: a client sets it to Before, applies, and checks After (and every new id).
    public sealed class CounterRow
    {
        public int Which;         // CounterKind
        public string Agency;     // for the per-agency counters; null for the game-wide ones
        // NameCounter only: the craft type's content id or the hull design's id, never a display name
        public string Key;
        public long Before, After;

        internal string Validate()
        {
            if (Which < CounterKind.ProductionCountId || Which > CounterKind.OfferCount)
                return "Counter.Which is not a counter";
            bool perAgency = Which == CounterKind.ProductionCountId || Which == CounterKind.NameCounter;
            if (perAgency != (Agency != null))
                return perAgency ? "Counter.Agency missing" : "a game-wide counter has no agency";
            if (Agency != null && !JoinCheck.IsAgencyId(Agency)) return "Counter.Agency is not an agency id";
            bool named = Which == CounterKind.NameCounter;
            if (named != (Key != null)) return named ? "Counter.Key missing" : "only a name counter has a key";
            if (Key != null && !ActionShapes.IsContentId(Key)) return "Counter.Key is not a content id";
            string e = CommandCheck.Range(Before, "Counter.Before", 0, int.MaxValue);
            return e ?? CommandCheck.Range(After, "Counter.After", 0, int.MaxValue);
        }

        internal static string ValidateList(CounterRow[] rows, int max, string field)
        {
            if (rows == null) return null;
            if (rows.Length > max) return field + " has more than " + max + " rows";
            foreach (CounterRow r in rows)
            {
                if (r == null) return "an empty row in " + field;
                string e = r.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    // One observatory die: which of the five call sites drew it, the value, and the id it was checked against (the
    // deposit's resource id hashed, or the body's id; 0 where the site has none), so a client notices a draw it would
    // not have made.
    public sealed class ObsDraw
    {
        public int Site;
        public int Value;
        public int Ref;

        // Value is whatever the game's call drew (any int); the site and the checked id are held to their ranges
        internal string Validate()
        {
            return CommandCheck.Range(Site, "Draw.Site", 0, ActionLimits.MaxDrawSite)
                ?? CommandCheck.Range(Ref, "Draw.Ref", 0, int.MaxValue);
        }

        internal static string ValidateList(ObsDraw[] draws)
        {
            if (draws == null) return null;
            if (draws.Length > ActionLimits.MaxDraws)
                return "Draws has more than " + ActionLimits.MaxDraws + " entries";
            foreach (ObsDraw d in draws)
            {
                if (d == null) return "an empty draw";
                string e = d.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    // The observatory's work modes, as the game's EWorkMode numbers them.
    public static class ObsMode
    {
        public const int Idle = 0, Exploration = 1, Discovery = 2, ExoplanetSurvey = 3;
    }

    // An observatory as it stands after an apply: the client compares its own after applying the same entry.
    public sealed class ObsAfter
    {
        public int Mode;     // ObsMode
        public int Target;   // the body being examined, -1 = none
        public int Group;    // the group being examined or discovered, as its index in the game's group list, -1 = none
        // Whole days of work left, defined so both games make the same number: -1 when the mode is Idle or
        // ExoplanetSurvey (the game leaves a stale, game-specific leftover there), else the game's figure rounded down
        // and clamped to 0 or more.
        public int Days;

        internal string Validate()
        {
            string e = CommandCheck.Range(Mode, "After.Mode", ObsMode.Idle, ObsMode.ExoplanetSurvey)
                ?? CommandCheck.Range(Target, "After.Target", -1, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Group, "After.Group", -1, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Days, "After.Days", -1, int.MaxValue);
            if (e != null) return e;
            bool noWork = Mode == ObsMode.Idle || Mode == ObsMode.ExoplanetSurvey;
            if (noWork && Days != -1) return "After.Days is -1 when the observatory has no target to work on";
            return !noWork && Days < 0 ? "After.Days is 0 or more while the observatory works" : null;
        }
    }

    // One asteroid a removal made the game spawn: every value the game's own creation takes, so every game creates the
    // same one.
    public sealed class AsteroidSpawn
    {
        public int Group;    // the group's index in the game's group list
        public string SubType;
        public int Id;
        public int Mass;
        public float A, Ecc, P, OmegaUc, OmegaLc, Incl;
        public string Name;   // the asteroid's designation, in the game's own form (ActionShapes.IsAsteroidName)
        // The game's own UnityEngine.Random is seeded with this for the creation, on every game: the new asteroid's
        // deposits (ObjectInfo.ResourcesRandomization, inside CreateDynamicAsteroid) come from those draws, and the
        // entry carries no deposits of its own.
        public int Seed;

        internal string Validate()
        {
            string e = CommandCheck.Range(Group, "Spawn.Group", 0, ActionLimits.MaxBodyId)
                ?? CommandCheck.Id(SubType, "Spawn.SubType")
                // Mass is the game's exponent of ten: negative, so the whole int range is allowed
                ?? CommandCheck.Range(Id, "Spawn.Id", 1, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Seed, "Spawn.Seed", 0, ActionLimits.MaxSeed);
            if (e != null) return e;
            foreach (float v in new[] { A, Ecc, P, OmegaUc, OmegaLc, Incl })
            {
                e = Message.Finite(v, "Spawn orbit value", -ActionLimits.MaxOrbitValue, ActionLimits.MaxOrbitValue);
                if (e != null) return e;
            }
            return ActionShapes.IsAsteroidName(Name) ? null : "Spawn.Name is not an asteroid designation";
        }

        internal static string ValidateList(AsteroidSpawn[] spawns)
        {
            if (spawns == null) return null;
            if (spawns.Length > ActionLimits.MaxSpawns)
                return "Spawns has more than " + ActionLimits.MaxSpawns + " entries";
            foreach (AsteroidSpawn s in spawns)
            {
                if (s == null) return "an empty spawn";
                string e = s.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    // Host -> clients in the stream: one applied action, with the host's own numbers, in order (Seq) and stamped with
    // the host's game day. Every game applies it with those numbers.
    public sealed class ActionResult : Message
    {
        public uint Seq;          // the stream's entry number: 1, 2, 3 ... per stream, the same on every client
        public long Day;          // DayKey.Current at the host's apply (entries raised in a day event: the next day)
        public long AtTicks;      // the host's CurrentTime when it applied (logs)
        public int Player;        // who acted (0 = the host; also 0 for host-decided kinds)
        public uint Req;          // that player's request number (0 for host-decided kinds)
        public ActionRequest Action;   // as the host applied it (normalised: R3 expanded, finished topics left out)
        public CounterRow[] Counters;  // every counter the apply moved; at most MaxCounters
        public long[] NewIds;     // item, ship or launch-vehicle ids the apply created, in order; at most MaxNewIds
        public ObsDraw[] Draws;   // the observatory dice the apply drew, in order; at most MaxDraws
        public ObsAfter After;    // the observatory's state after the host's apply
        public string SystemName; // SystemFound: the generated system's name (a place name)
        public AsteroidSpawn[] Spawns;   // the asteroids the apply spawned, in order; at most MaxSpawns
        public AsteroidPull Pull;        // AsteroidPulled only (M6)
        // M7 shared world (protocol 6)
        public int Origin;               // EntryOrigin: Ai only on AiAllowed kinds, Player never on a HostOnly kind
        public MarketOp[] Ops;           // 80-84, 90, 15/34 fills, 94: what the host made, in order; at most MaxMarketOps
        public AiStep[] Steps;           // 94 only; at most MaxAiSteps
        public ChainRow[] Chain;         // 92 and 93: what the completion set off, in order; at most MaxChainRows

        public override string Validate()
        {
            if (Seq < 1) return "Seq is 0";
            string e = CommandCheck.Day(Day, "Day") ?? JoinCheck.Ticks(AtTicks, "AtTicks", false)
                ?? CommandCheck.Range(Player, "Player", 0, HostGate.MaxPlayerIds);
            if (e != null) return e;
            if (Action == null) return "Action missing";
            e = Action.ValidateApplied();
            if (e != null) return e;
            if (ActionKind.HostOnly(Action.Kind) && (Player != 0 || Req != 0))
                return "a host-decided entry has no player or request number";
            e = CounterRow.ValidateList(Counters, ActionLimits.MaxCounters, "Counters");
            if (e != null) return e;
            e = ValidateOrigin() ?? ValidateWorld();
            if (e != null) return e;
            e = ValidateNewIds();
            return e ?? ValidateObservatory();
        }

        // Who may have made the entry (the client checks the origin against its HumanSet again when it applies).
        string ValidateOrigin()
        {
            string e = CommandCheck.Range(Origin, "Origin", EntryOrigin.Player, EntryOrigin.Ai);
            if (e != null) return e;
            int kind = Action.Kind;
            string name = ActionKind.Name(kind);
            if (Origin != EntryOrigin.Player && (Player != 0 || Req != 0)) return "a host or AI entry has no player or request number";
            if (Origin == EntryOrigin.Player && ActionKind.HostOnly(kind)) return name + " is decided by the host, not a player";
            if (Origin == EntryOrigin.Host && (!ActionKind.HostOnly(kind) || kind == ActionKind.AiAction))
                return name + " is not a host-origin kind";
            if (Origin == EntryOrigin.Ai && !ActionKind.AiAllowed(kind)) return name + " is not an AI-origin kind";
            if (Origin != EntryOrigin.Ai && kind == ActionKind.AiAction) return "an AiAction comes from a rival";
            return null;
        }

        // The market, AI and contract lists ride only on the kinds that make them, each in its own shape.
        string ValidateWorld()
        {
            int kind = Action.Kind;
            string name = ActionKind.Name(kind);
            bool carriesOps = ActionKind.IsMarket(kind) || kind == ActionKind.MarketMaker || kind == ActionKind.BuyAndBuild
                || kind == ActionKind.CraftBuyAndBuild || kind == ActionKind.AiAction;
            if (Ops != null && !carriesOps) return name + " carries no market ops";
            if (Steps != null && kind != ActionKind.AiAction) return name + " carries no AI steps";
            bool chain = kind == ActionKind.ObjectiveDone || kind == ActionKind.ContractDone;
            if (Chain != null && !chain) return name + " carries no chain";
            if (chain && (Chain == null || Chain.Length < 1)) return name + " needs its chain";
            string e = MarketOp.ValidateList(Ops, false, "Ops") ?? AiStep.ValidateList(Steps) ?? ChainRow.ValidateList(Chain);
            if (e != null) return e;
            int n = Ops == null ? 0 : Ops.Length;
            switch (kind)
            {
                case ActionKind.OfferCreate: return Sequence(MarketOps.Add);
                case ActionKind.OfferCancel: return Sequence(MarketOps.Cancel);
                case ActionKind.OfferEdit: return Sequence(MarketOps.Cancel, MarketOps.Add);
                case ActionKind.OfferTake: return Sequence(MarketOps.Fill);
                case ActionKind.MarketInstant: return Sequence(MarketOps.Instant);
                case ActionKind.MarketMaker:
                    foreach (MarketOp o in Ops ?? new MarketOp[0])
                        if (o.Op != MarketOps.Add && o.Op != MarketOps.Cancel && o.Op != MarketOps.PriceDiff)
                            return "the market maker only adds, cancels and re-quotes";
                    return null;
                case ActionKind.BuyAndBuild: case ActionKind.CraftBuyAndBuild:
                    foreach (MarketOp o in Ops ?? new MarketOp[0])
                        if (o.Op != MarketOps.Fill) return "buying what is missing only fills offers";
                    if (n > ActionLimits.MaxBuyFills) return "buying what is missing fills at most " + ActionLimits.MaxBuyFills + " offers";
                    return null;
                case ActionKind.AiAction:
                    if ((Steps == null ? 0 : Steps.Length) + n < 1) return "an AiAction needs a step or an op";
                    if ((Steps == null ? 0 : Steps.Length) + n > ActionLimits.MaxAiSteps)
                        return "an AiAction is one task call: at most " + ActionLimits.MaxAiSteps + " steps and ops together";
                    if (Steps != null && Steps.Length > 0 && Steps[0].Op == AiOps.Complete && n > 0)
                        return "a Complete step is never mixed with market ops";
                    foreach (MarketOp o in Ops ?? new MarketOp[0])
                        if (o.Op == MarketOps.PriceDiff) return "an AI does not re-quote";
                    return null;
                default: return null;
            }
        }

        string Sequence(params int[] ops)
        {
            if (Ops == null || Ops.Length != ops.Length) return ActionKind.Name(Action.Kind) + " carries " + ops.Length + " op(s)";
            for (int i = 0; i < ops.Length; i++)
                if (Ops[i].Op != ops[i]) return ActionKind.Name(Action.Kind) + " carries the wrong op";
            return null;
        }

        string ValidateNewIds()
        {
            if (NewIds == null) return null;
            int max = Action.Kind == ActionKind.MoveToOrbit ? ActionLimits.MaxMoveNewIds
                : Action.Kind == ActionKind.AiAction ? ActionLimits.MaxAiSteps : ActionLimits.MaxNewIds;
            if (NewIds.Length > max) return "NewIds has more than " + max + " entries";
            foreach (long id in NewIds)
            {
                string e = CommandCheck.Range(id, "NewIds entry", 0, int.MaxValue);
                if (e != null) return e;
            }
            return null;
        }

        // The observatory and asteroid numbers ride only on the kinds that make them (so the worst case of an entry is
        // the worst case of its kind), and are held to their own ranges. Draws and spawns never meet in one entry.
        string ValidateObservatory()
        {
            int kind = Action.Kind;
            string name = ActionKind.Name(kind);
            if (Draws != null && !ActionKind.CarriesDraws(kind)) return name + " carries no draws";
            if (After != null && !ActionKind.CarriesAfter(kind)) return name + " has no observatory state";
            if (Spawns != null && !ActionKind.CarriesSpawns(kind)) return name + " carries no spawns";
            if (SystemName != null && kind != ActionKind.SystemFound) return name + " has no system name";
            if (Pull != null && kind != ActionKind.AsteroidPulled) return name + " carries no pull";
            if (Pull == null && kind == ActionKind.AsteroidPulled) return name + " needs its pull";
            // the host's own observatory steps always say what state they left the observatory in (a found system
            // may not: the observatory that found it can be gone with its target)
            if (kind != ActionKind.SystemFound && kind >= ActionKind.ObservatoryStep
                && kind <= ActionKind.ObservatoryReplan && After == null)
                return name + " needs the observatory's state";
            if (SystemName != null && !ActionShapes.IsSystemName(SystemName))
                return "SystemName is not a catalogue name of at most " + ActionLimits.MaxSystemNameBytes + " bytes";
            string e = After == null ? null : After.Validate();
            return e ?? ObsDraw.ValidateList(Draws) ?? AsteroidSpawn.ValidateList(Spawns)
                ?? (Pull == null ? null : Pull.Validate());
        }
    }

    // Host -> the actor only: the request did not apply, and why. The player reads CommandText.Refused(Code).
    public sealed class ActionRefused : Message
    {
        public uint Req;
        public int Code;   // RefuseCodes
        // CannotSend is local only: the host never sends it
        public override string Validate()
        {
            return RefuseCodes.IsKnown(Code) && Code != RefuseCodes.CannotSend ? null : "Code is not a refusal";
        }
    }

    // What one object looked like at the host's day boundary: kind, agency, body (0 for research) and a 16-hex print.
    public sealed class PrintEntry
    {
        public int Kind;          // ObjectKind
        public string Agency;
        public int Body;
        public string Print;      // 16 lower-case hex digits

        internal string Validate()
        {
            return CommandCheck.PrintBody(Kind, Body) ?? JoinCheck.Agency(Agency, "Print.Agency", true)
                ?? Message.Hex(Print, "Print.Print", 16, true);
        }
    }

    // Host -> clients in the stream: the host has finished day Day. Every entry stamped with a day up to it has a Seq
    // up to LastSeq; a client may end that day once it has applied them all and compared the prints.
    public sealed class DayClosed : Message
    {
        public long Day; public uint LastSeq;
        // the host's prints, before its day event, of every object an entry of this day touched
        public PrintEntry[] Prints;
        // M7 (protocol 6, I9): the world print is required, one AreaPrint per DriftAreas index, in order
        public AreaPrint[] World;
        public string WorldDay;       // the day's whole Discrete hash (16 hex)
        public AreaValue[] Values;    // value days only, else null; at most MaxAreaValues

        public override string Validate()
        {
            string e = CommandCheck.Day(Day, "Day") ?? ValidateWorld();
            if (e != null) return e;
            if (Prints == null) return null;
            if (Prints.Length > ActionLimits.MaxPrintsPerDay) return "more than " + ActionLimits.MaxPrintsPerDay
                + " prints";
            foreach (PrintEntry p in Prints)
            {
                if (p == null) return "an empty print entry";
                e = p.Validate();
                if (e != null) return e;
            }
            return null;
        }

        // A valid world print of nothing, for a host with no world side attached (M5 and M6 behaviour, tests): all zeros, never
        // a real hash. M7 Task 6 replaces it with the world read at the day-event point.
        public static DayClosed WithBlankWorld(DayClosed d)
        {
            d.World = new AreaPrint[DriftAreas.All.Length];
            for (int i = 0; i < d.World.Length; i++) d.World[i] = new AreaPrint { Area = i, Discrete = "0000000000000000" };
            d.WorldDay = "0000000000000000";
            return d;
        }

        string ValidateWorld()
        {
            if (World == null || WorldDay == null) return "the world print is required";
            if (World.Length != DriftAreas.All.Length) return "World has one print per area (" + DriftAreas.All.Length + ")";
            for (int i = 0; i < World.Length; i++)
            {
                if (World[i] == null) return "an empty area print";
                string e = World[i].Validate(i);
                if (e != null) return e;
            }
            string d = Message.Hex(WorldDay, "WorldDay", 16, true);
            if (d != null || Values == null) return d;
            if (Values.Length > ActionLimits.MaxAreaValues) return "Values has more than " + ActionLimits.MaxAreaValues + " entries";
            foreach (AreaValue v in Values)
            {
                if (v == null) return "an empty value";
                d = v.Validate();
                if (d != null) return d;
            }
            return null;
        }
    }

    // Client -> host: the print of one object differed; send the host's copy from day Day.
    public sealed class ObjectRequest : Message
    {
        public long Day; public int Kind; public string Agency; public int Body;

        public override string Validate()
        {
            string e = CommandCheck.Day(Day, "Day") ?? CommandCheck.ObjectBody(Kind, Body);
            return e ?? JoinCheck.Agency(Agency, "Agency", true);
        }
    }

    public sealed class ResearchState
    {
        public string Running; public double Progress;
        public string[] Queue;                                   // at most MaxQueue
        // topics started and not finished; at most MaxPaused, equal lengths
        public string[] Paused; public double[] PausedProgress;

        internal string Validate()
        {
            string e = null;
            if (Running != null) e = CommandCheck.Id(Running, "Running");
            e = e ?? Finite(Progress, "Progress", 0, ActionLimits.MaxProgress) ?? CommandCheck.IdList(Queue, "Queue",
                ActionLimits.MaxQueue)
                ?? CommandCheck.IdList(Paused, "Paused", ActionLimits.MaxPaused);
            if (e != null) return e;
            int np = Paused == null ? 0 : Paused.Length, nq = PausedProgress == null ? 0 : PausedProgress.Length;
            if (np != nq) return "Paused and PausedProgress differ in length";
            if (nq > ActionLimits.MaxPaused) return "PausedProgress has more than " + ActionLimits.MaxPaused
                + " entries";
            for (int i = 0; i < nq; i++)
            {
                e = Finite(PausedProgress[i], "PausedProgress entry", 0, ActionLimits.MaxProgress);
                if (e != null) return e;
            }
            return null;
        }

        static string Finite(double v, string f, double min, double max) { return Message.Finite(v, f, min, max); }
    }

    public sealed class BuildRow
    {
        public long ItemId; public string Item; public bool Craft; public string NameKey;
        public long NameNumber; public int Count; public double Progress;
        // NameKey and NameNumber: a craft's language-independent CountSN key and the count its name was made from; each
        // game composes the display name itself. A facility has neither.
        // Count is the units to build (0 in old saves, which the game reads as one unit); CountBuild the units done,
        // so a copy heals a build whose units finished differently.
        public int CountBuild;

        internal string Validate()
        {
            string e = CommandCheck.Range(ItemId, "Build.ItemId", 0, int.MaxValue) ?? CommandCheck.Id(Item,
                "Build.Item")
                ?? CommandCheck.Range(Count, "Build.Count", 0, ActionLimits.MaxCount)
                ?? CommandCheck.Range(CountBuild, "Build.CountBuild", 0, ActionLimits.MaxCount)
                ?? Message.Finite(Progress, "Build.Progress", 0, ActionLimits.MaxProgress);
            if (e != null) return e;
            if (!Craft) return NameKey != null || NameNumber != 0 ? "a facility has no name key" : null;
            return CommandCheck.Id(NameKey, "Build.NameKey") ?? CommandCheck.Range(NameNumber, "Build.NameNumber", 0,
                int.MaxValue);
        }
    }

    public sealed class QueueRow
    {
        public string Item; public int Count;
        internal string Validate()
        {
            string e = CommandCheck.Id(Item, "Queue.Item");
            return e ?? CommandCheck.Range(Count, "Queue.Count", 1, ActionLimits.MaxCount);
        }
    }

    public sealed class FacilityRow
    {
        public string Item; public long ItemId; public int Count; public int Enabled; public int Priority;

        internal string Validate()
        {
            string e = CommandCheck.Id(Item, "Facility.Item");
            e = e ?? CommandCheck.Range(ItemId, "Facility.ItemId", 0, int.MaxValue);
            e = e ?? CommandCheck.Range(Count, "Facility.Count", 0, ActionLimits.MaxUnits);
            e = e ?? CommandCheck.Range(Enabled, "Facility.Enabled", 0, ActionLimits.MaxUnits);
            return e ?? CommandCheck.Range(Priority, "Facility.Priority", ActionLimits.MinPriority,
                ActionLimits.MaxPriority);
        }
    }

    public sealed class SiteBuildState
    {
        // productionCountID and the name counters; the first piece only
        public CounterRow[] Counters;
        public BuildRow[] Building;                              // unfinished facilities and crafts, in order
        public QueueRow[] Queue;                                 // not-yet-paid entries, in order

        internal string Validate(int piece)
        {
            if (piece > 0 && Counters != null) return "counters belong in the first piece";
            string e = CounterRow.ValidateList(Counters, ActionLimits.MaxCounters, "Counters");
            if (e != null) return e;
            int nb = Building == null ? 0 : Building.Length, nq = Queue == null ? 0 : Queue.Length;
            if (nb + nq > ActionLimits.MaxRowsPerPiece) return "more than " + ActionLimits.MaxRowsPerPiece
                + " rows in one piece";
            for (int i = 0; i < nb; i++)
            {
                if (Building[i] == null) return "an empty build row";
                e = Building[i].Validate();
                if (e != null) return e;
            }
            for (int i = 0; i < nq; i++)
            {
                if (Queue[i] == null) return "an empty queue row";
                e = Queue[i].Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    public sealed class SiteFacilityState
    {
        public FacilityRow[] Built; public string[] Mining;

        internal string Validate()
        {
            if (Built != null)
            {
                if (Built.Length > ActionLimits.MaxRowsPerPiece) return "more than " + ActionLimits.MaxRowsPerPiece
                    + " built rows in one piece";
                foreach (FacilityRow r in Built)
                {
                    if (r == null) return "an empty facility row";
                    string e = r.Validate();
                    if (e != null) return e;
                }
            }
            return CommandCheck.IdList(Mining, "Mining", ActionLimits.MaxMiningKeys);
        }
    }

    // Host -> a client out of agreement with the host's print: the host's copy of one object, in pieces when it is
    // large. The client restores it when every piece has arrived. Code != Ok carries no data and puts the client out of
    // step.
    public sealed class ObjectData : Message
    {
        public int Code;                           // ObjectCodes
        // applied when the client's day is Day and its last applied seq is AfterSeq
        public long Day; public uint AfterSeq;
        public int Piece, Pieces;                  // 0..Pieces-1 of 1..MaxPieces
        public int Kind; public string Agency; public int Body;
        public ResearchState Research;             // Kind Research
        public SiteBuildState Builds;              // Kind SiteBuilds
        public SiteFacilityState Facilities;       // Kind SiteFacilities

        public override string Validate()
        {
            string e = CommandCheck.Range(Code, "Code", ObjectCodes.Ok, ObjectCodes.NoSuchObject);
            e = e ?? CommandCheck.Day(Day, "Day");
            e = e ?? CommandCheck.ObjectBody(Kind, Body);
            e = e ?? JoinCheck.Agency(Agency, "Agency", true);
            e = e ?? CommandCheck.Range(Pieces, "Pieces", 1, ActionLimits.MaxPieces);
            e = e ?? CommandCheck.Range(Piece, "Piece", 0, Pieces - 1);
            if (e != null) return e;
            bool research = Kind == ObjectKind.Research, builds = Kind == ObjectKind.SiteBuilds;
            if (Code != ObjectCodes.Ok)
            {
                bool carries = Research != null || Builds != null || Facilities != null;
                return Pieces != 1 || carries ? "an answer that is not Ok carries no data" : null;
            }
            if (research && Pieces != 1) return "research comes in one piece";
            bool facilities = !research && !builds;
            if ((Research != null) != research || (Builds != null) != builds || (Facilities != null) != facilities)
                return "the state does not match Kind";
            if (research) return Research.Validate();
            return builds ? Builds.Validate(Piece) : Facilities.Validate();
        }
    }

    public sealed class AgencyMoneyRow { public string Agency; public double Money; public bool Away; }

    // One site's crew-death timer (ObjectInfoData.lastNotificationLifeSupportFinish): when that agency's crew there
    // ran out of supplies. The game does not save it, so a joiner's fresh copy would start the count again and kill
    // the crew days after the host does; AgencyState carries every human agency's running timers.
    public sealed class LifeSupportRow
    {
        public string Agency; public int Body; public long Ticks;

        internal string Validate()
        {
            return JoinCheck.Agency(Agency, "LifeSupport.Agency", true)
                ?? CommandCheck.Range(Body, "LifeSupport.Body", 1, ActionLimits.MaxBodyId)
                ?? JoinCheck.Ticks(Ticks, "LifeSupport.Ticks", false);
        }
    }

    // Host -> one client, at a batch's end and after a quick rejoin: every human agency's money and counters, and where
    // the stream continues.
    public sealed class AgencyState : Message
    {
        public long Day; public uint NextSeq;                    // the stream continues with NextSeq, on this day
        public AgencyMoneyRow[] Agencies;                             // every human agency; at most JoinLimits.MaxPlayers
        // per-agency and game-wide counters; at most MaxStateCounters
        public CounterRow[] Counters;
        // the running crew-death timers of the human agencies' sites, one per (agency, site); at most
        // MaxLifeSupportRows; a site with none is not listed
        public LifeSupportRow[] LifeSupport;
        // the number of the host's stream this state starts (0 for the first; each stream reset adds one), so the
        // comparison tools can match a client's entries to the host's after a host load
        public int Stream;

        public override string Validate()
        {
            string e = CommandCheck.Day(Day, "Day");
            if (e != null) return e;
            if (NextSeq < 1) return "NextSeq is 0";
            if (Stream < 0) return "Stream is negative";
            e = ValidateLifeSupport();
            if (e != null) return e;
            if (Agencies == null || Agencies.Length < 1 || Agencies.Length > JoinLimits.MaxPlayers)
                return "Agencies outside 1.." + JoinLimits.MaxPlayers;
            for (int i = 0; i < Agencies.Length; i++)
            {
                AgencyMoneyRow r = Agencies[i];
                if (r == null) return "an empty agency row";
                e = JoinCheck.Agency(r.Agency, "Agency", true)
                    ?? Finite(r.Money, "Money", -ActionLimits.MaxMoney, ActionLimits.MaxMoney);
                if (e != null) return e;
                for (int j = 0; j < i; j++) if (Agencies[j].Agency == r.Agency) return "an agency twice";
            }
            return CounterRow.ValidateList(Counters, ActionLimits.MaxStateCounters, "Counters");
        }

        // Each row's own form, at most MaxLifeSupportRows, one per (agency, site), and only for an agency this state
        // lists: a timer for anyone else would be a rival's, which every game runs alike already.
        string ValidateLifeSupport()
        {
            if (LifeSupport == null) return null;
            if (LifeSupport.Length > ActionLimits.MaxLifeSupportRows)
                return "LifeSupport has more than " + ActionLimits.MaxLifeSupportRows + " rows";
            for (int i = 0; i < LifeSupport.Length; i++)
            {
                LifeSupportRow r = LifeSupport[i];
                if (r == null) return "an empty life-support row";
                string e = r.Validate();
                if (e != null) return e;
                bool listed = false;
                if (Agencies != null)
                    foreach (AgencyMoneyRow a in Agencies) if (a != null && a.Agency == r.Agency) listed = true;
                if (!listed) return "a life-support timer for an agency the state does not list";
                for (int j = 0; j < i; j++)
                    if (LifeSupport[j].Agency == r.Agency && LifeSupport[j].Body == r.Body)
                        return "a site's life-support timer twice";
            }
            return null;
        }
    }

    // Host -> every client: a new stream starts (the host loaded a save, or hit a fault); each client rejoins fully.
    public sealed class StreamReset : Message
    {
        public int Code;   // StreamCodes
        public override string Validate()
        {
            return CommandCheck.Range(Code, "Code", StreamCodes.HostLoaded, StreamCodes.HostFault);
        }
    }
}
