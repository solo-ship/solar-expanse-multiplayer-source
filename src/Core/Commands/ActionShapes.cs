using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // What each kind of request may carry. A kind lists the fields it requires and the further fields it may carry;
    // every other field must be at its default, so a request can carry only its own lists and the worst case of a
    // message is the worst case of its largest kind (plan "Caps and their derivation"). The table is written from the
    // per-action inventory of docs/research/commands.md (section "K10").
    public static class ActionShapes
    {
        const int Body = 1, ToBody = 2, Item = 4, Count = 8, ItemId = 16, Position = 32, ToPosition = 64, Value = 128,
            Amount = 256, Ids = 512, Targets = 1024, Group = 2048, Ship = 4096, Mission = 8192, Route = 16384,
            Preset = 32768, Market = 65536, Contract = 131072, Growth = 262144;

        // required, optional
        static void Fields(int kind, out int required, out int optional)
        {
            optional = 0;
            switch (kind)
            {
                case ActionKind.ResearchStart: case ActionKind.ResearchAdd: required = Item; break;
                // Ids: the host's expansion
                case ActionKind.ResearchAddWithPrereqs: required = Item; optional = Ids; break;
                case ActionKind.ResearchSetQueue: required = Ids; break;
                case ActionKind.Build: case ActionKind.BuildQueue: case ActionKind.BuyAndBuild:
                case ActionKind.CraftBuild: case ActionKind.CraftQueue: case ActionKind.CraftBuyAndBuild:
                    required = Body | Item | Count; break;
                case ActionKind.BuildCancel: required = Body | Item | Count | ItemId | Position; break;
                case ActionKind.Scrap: required = Body | Item | Count; optional = ItemId; break;
                case ActionKind.BuildReorder: case ActionKind.CraftReorder:
                    required = Body | Item | ItemId | Position | ToPosition; break;
                case ActionKind.FacilityEnabled: required = Body | Item | ItemId | Value; break;
                case ActionKind.FacilityPriority: required = Body | Item | Value; break;
                case ActionKind.SelectDeposit: required = Body | Item | Position | Value; break;
                // Value: the module drop's path the host decided (1 = the count popup's), so no game reads the UI state
                // it depends on
                case ActionKind.MoveToOrbit:
                    required = Body | ToBody | Item; optional = Amount | Count | ItemId | Value; break;
                // the facility is named by its descriptor (Item) and, when the window knows it, its item id: a body may
                // hold several target-list facilities, even of one type
                case ActionKind.EnergyTargets: case ActionKind.MirrorTargets:
                    required = Body | Item | Targets; optional = ItemId; break;
                case ActionKind.CraftCancel: required = Body | Item | ItemId | Position; break;
                case ActionKind.AgencyBankrupt: case ActionKind.SeatReleased: required = 0; break;
                case ActionKind.SeatClaimed: required = Amount; break;
                case ActionKind.CraftFinished: required = Body | Item | ItemId; break;
                // an observatory is named by its item id (a site facility) or its ship (a probe), checked in Check; a
                // new system's ItemId is the new object's id, so SystemFound names its observatory by Body and Item
                case ActionKind.ObservatoryExamine:
                    required = Body | Item; optional = ToBody | Group | ItemId | Ship; break;
                case ActionKind.ObservatoryDiscover: required = Body | Item; optional = Group | ItemId | Ship; break;
                case ActionKind.ObservatorySurvey: case ActionKind.ObservatoryStop:
                    required = Body | Item; optional = ItemId | Ship; break;
                case ActionKind.ObservatoryNotify: required = Body | Item | Value; optional = ItemId | Ship; break;
                case ActionKind.ForgetObject: required = Body; break;
                case ActionKind.ObservatoryStep: case ActionKind.ObservatoryFound:
                    required = Body | Item; optional = ToBody | ItemId | Ship; break;
                case ActionKind.SystemFound: required = Body | Item | Value; optional = Count | ItemId; break;
                case ActionKind.ObservatoryReplan: required = Body | Item | ToBody; optional = ItemId | Ship; break;
                case ActionKind.AsteroidSpawned: required = Body; optional = ItemId; break;
                case ActionKind.ObjectRemoved: required = Body; optional = ItemId | Count; break;
                // M6 flights. FlightCancel's Ship is its first ship, or -1 for a facility craft.
                case ActionKind.FlightSchedule: required = Body | Mission; break;
                case ActionKind.FlightCancel: required = ItemId | Value; optional = Ship; break;
                case ActionKind.CyclicalSet: required = Ship | Route | Value; break;
                case ActionKind.CyclicalPause: required = Ship | Value; break;
                case ActionKind.CyclicalDelete: required = Ship; break;
                case ActionKind.SupplyFlight:
                    required = Body | ToBody | Item | ItemId | Value | Mission; break;
                case ActionKind.CraftScrap: required = Body; optional = Ship | ItemId; break;
                case ActionKind.PresetSave: required = Preset; break;
                case ActionKind.FlightPlanned: required = Body | Mission; break;
                case ActionKind.MissionCancelled: required = ItemId | Value; break;
                case ActionKind.AsteroidPulled: required = Body | ToBody | ItemId; optional = Count; break;
                case ActionKind.LandingCraft: required = Body | Item | ItemId; break;
                case ActionKind.MissionLaunched: required = ItemId; break;
                // M7 shared world. The request forms of 80-84 are MarketShapes'; Amount is the instant trade's units.
                case ActionKind.OfferCreate: case ActionKind.OfferCancel: case ActionKind.OfferEdit:
                case ActionKind.OfferTake: required = Market; break;
                // ItemId names the source (0 = the unused MarketWindow, which the host refuses; 1 = the make-offer window)
                case ActionKind.MarketInstant: required = Body | Item | Amount | ItemId | Value; optional = Market; break;
                case ActionKind.NukeAsteroid: case ActionKind.EndGameShip: required = Body | Item | ItemId; break;
                case ActionKind.ContractAccept: case ActionKind.ContractCancel: case ActionKind.ObjectiveUi:
                case ActionKind.ObjectiveDone: case ActionKind.ContractDone: case ActionKind.ContractActivated:
                    required = Contract; break;
                case ActionKind.LeaveStock: required = Body | Item | Amount; break;
                // ItemId: the date the maker's yearly run left lastOfferPricesUpdateTime at (ticks), -1 for a re-quote
                case ActionKind.MarketMaker: required = Value; optional = ItemId; break;
                case ActionKind.PopulationGrowth: required = Growth | Value; break;
                case ActionKind.AiAction: required = 0; break;
                default: required = 0; optional = 0; break;
            }
        }

        // null = the shape is fine. The field forms (ranges, lengths, content ids) are the message's own Validate; this
        // is which fields the kind takes, and the rules that tie fields together.
        public static string Check(ActionRequest a) { return Check(a, false); }

        // applied: the entry as the host applied it (a finished mission), not a client's request
        public static string Check(ActionRequest a, bool applied)
        {
            if (a == null) return "no request";
            if (!ActionKind.IsKnown(a.Kind)) return "Kind " + a.Kind + " is not an action";
            int required, optional;
            Fields(a.Kind, out required, out optional);
            int allowed = required | optional;
            string name = ActionKind.Name(a.Kind);

            // forbidden: a field the kind does not take must be at its default
            if ((allowed & Body) == 0 && a.Body != 0) return name + " takes no Body";
            if ((allowed & ToBody) == 0 && a.ToBody != 0) return name + " takes no ToBody";
            if ((allowed & Item) == 0 && a.Item != null) return name + " takes no Item";
            if ((allowed & Count) == 0 && a.Count != 0) return name + " takes no Count";
            if ((allowed & ItemId) == 0 && a.ItemId != -1) return name + " takes no ItemId";
            if ((allowed & Position) == 0 && a.Position != -1) return name + " takes no Position";
            if ((allowed & ToPosition) == 0 && a.ToPosition != -1) return name + " takes no ToPosition";
            if ((allowed & Value) == 0 && a.Value != 0) return name + " takes no Value";
            if ((allowed & Amount) == 0 && a.Amount != 0) return name + " takes no Amount";
            if ((allowed & Ids) == 0 && a.Ids != null) return name + " takes no Ids";
            if ((allowed & Targets) == 0 && a.Targets != null) return name + " takes no Targets";
            if ((allowed & Group) == 0 && a.Group != -1) return name + " takes no Group";
            if ((allowed & Ship) == 0 && a.Ship != -1) return name + " takes no Ship";
            if ((allowed & Mission) == 0 && a.Mission != null) return name + " takes no Mission";
            if ((allowed & Route) == 0 && a.Route != null) return name + " takes no Route";
            if ((allowed & Preset) == 0 && a.Preset != null) return name + " takes no Preset";
            if ((allowed & Market) == 0 && a.Market != null) return name + " takes no Market";
            if ((allowed & Contract) == 0 && a.Contract != null) return name + " takes no Contract";
            if ((allowed & Growth) == 0 && a.Growth != null) return name + " takes no Growth";
            if (a.Kind == ActionKind.PopulationGrowth && a.Agency != null) return name + " takes no Agency";

            // required: present, in the kind's own range
            if ((required & Body) != 0 && a.Body < 1) return name + " needs a Body";
            if ((required & ToBody) != 0 && a.ToBody < 1) return name + " needs a ToBody";
            if ((required & Item) != 0 && a.Item == null) return name + " needs an Item";
            if ((required & Count) != 0 && a.Count < 1) return name + " needs a Count of at least 1";
            if ((required & ItemId) != 0 && a.ItemId < (NamesAPlaceholder(a.Kind) ? -1 : 0))
                return name + " needs an ItemId";
            if ((required & Position) != 0 && a.Position < 0) return name + " needs a Position";
            if ((required & ToPosition) != 0 && a.ToPosition < 0) return name + " needs a ToPosition";
            if ((required & Ids) != 0 && a.Ids == null) return name + " needs Ids";
            if ((required & Targets) != 0 && a.Targets == null) return name + " needs Targets";
            if ((required & Ship) != 0 && a.Ship < 0) return name + " needs a Ship";
            if ((required & Mission) != 0 && a.Mission == null) return name + " needs a Mission";
            if ((required & Route) != 0 && a.Route == null) return name + " needs a Route";
            if ((required & Preset) != 0 && a.Preset == null) return name + " needs a Preset";
            if ((required & Market) != 0 && (a.Market == null || a.Market.Length < 1)) return name + " needs its Market ops";
            if ((required & Contract) != 0 && a.Contract == null) return name + " needs a Contract";
            if ((required & Growth) != 0 && a.Growth == null) return name + " needs Growth";

            switch (a.Kind)
            {
                case ActionKind.FacilityEnabled:
                    return a.Value < 0 || a.Value > ActionLimits.MaxUnits ? "Value is not a number of units" : null;
                case ActionKind.FacilityPriority:
                    return a.Value < ActionLimits.MinPriority || a.Value > ActionLimits.MaxPriority
                        ? "Value is not a priority (" + ActionLimits.MinPriority + ".." + ActionLimits.MaxPriority
                            + ")" : null;
                case ActionKind.SelectDeposit:
                    return a.Value != 0 && a.Value != 1 ? "Value is not 0 or 1" : null;
                case ActionKind.MoveToOrbit: return MoveProblem(a);
                case ActionKind.EnergyTargets: return TargetsProblem(a.Targets, false);
                case ActionKind.MirrorTargets: return TargetsProblem(a.Targets, true);
                case ActionKind.SeatClaimed:
                    // money may be negative (a bankrupt agency)
                    return Message.Finite(a.Amount, "Amount", -ActionLimits.MaxMoney, ActionLimits.MaxMoney);
                case ActionKind.ObservatoryExamine:
                    if (a.ToBody < 1 && a.Group < 0) return name + " needs a target body or a group";
                    return Named(a);
                case ActionKind.ObservatoryDiscover: case ActionKind.ObservatorySurvey:
                case ActionKind.ObservatoryStop: case ActionKind.ObservatoryStep: case ActionKind.ObservatoryFound:
                case ActionKind.ObservatoryReplan:
                    return Named(a);
                case ActionKind.ObjectRemoved:
                    // an impact (it names its mission) announces the re-plans that follow it; a plain removal has none
                    if (a.Count < 0 || a.Count > ActionLimits.MaxImpactGroup)
                        return "ObjectRemoved announces more than " + ActionLimits.MaxImpactGroup + " trailing entries";
                    return a.Count > 0 && a.ItemId < 0 ? "ObjectRemoved without a mission has no trailing entries"
                        : null;
                case ActionKind.ObservatoryNotify:
                    return a.Value != 0 && a.Value != 1 ? "Value is not 0 or 1" : Named(a);
                case ActionKind.FlightSchedule: case ActionKind.FlightPlanned: case ActionKind.SupplyFlight:
                    return MissionProblem(a, applied);
                case ActionKind.FlightCancel:
                    return a.Value < 1 || a.Value > 5 ? "Value is not an abort branch (1..5)" : null;
                case ActionKind.CyclicalSet:
                    return a.Value != 0 && a.Value != 1 ? "Value is not 0 or 1" : a.Route.Validate();
                case ActionKind.CyclicalPause:
                    return a.Value != 0 && a.Value != 1 ? "Value is not 0 or 1" : null;
                case ActionKind.CraftScrap:
                    return (a.Ship >= 0) == (a.ItemId >= 0) ? "CraftScrap names a spacecraft or a launch vehicle, one"
                        : null;
                case ActionKind.PresetSave: return a.Preset.Validate();
                case ActionKind.MissionCancelled:
                    return a.Value < 1 || a.Value > 5 ? "Value is not a cancel reason (1..5)" : null;
                case ActionKind.AsteroidPulled:
                    return a.Count < 0 || a.Count > ActionLimits.MaxImpactGroup
                        ? "AsteroidPulled announces more than " + ActionLimits.MaxImpactGroup + " trailing entries"
                        : null;
                case ActionKind.OfferCreate: case ActionKind.OfferCancel: case ActionKind.OfferEdit:
                case ActionKind.OfferTake: case ActionKind.MarketInstant: return MarketShapes.Check(a);
                case ActionKind.NukeAsteroid: case ActionKind.EndGameShip: return null;
                case ActionKind.LeaveStock: return PositiveAmount(a, name);
                case ActionKind.ContractAccept: case ActionKind.ContractCancel: case ActionKind.ContractDone:
                case ActionKind.ContractActivated:
                    return a.Contract.Objective != -1 ? name + " names a whole contract" : null;
                case ActionKind.ObjectiveUi: case ActionKind.ObjectiveDone:
                    return a.Contract.Objective < 0 ? name + " names an objective" : null;
                case ActionKind.MarketMaker:
                    if (a.Value != 0 && a.Value != 1) return "Value is not 0 or 1";
                    return (a.Value == 0) == (a.ItemId < 1) ? "a yearly run names its date, a re-quote none" : null;
                case ActionKind.PopulationGrowth:
                    return a.Value != 0 && a.Value != 1 ? "Value is not 0 or 1" : null;
                case ActionKind.SystemFound:
                    return a.Value < 0 || a.Value > ActionLimits.MaxSeed
                        ? "Value is not a seed (0.." + ActionLimits.MaxSeed + ")" : null;
                default: return null;
            }
        }

        static string PositiveAmount(ActionRequest a, string name)
        {
            return Message.Finite(a.Amount, "Amount", 0, ActionLimits.MaxMoney) ?? (a.Amount > 0 ? null : name + " needs an Amount above 0");
        }

        // The mission a flight kind carries, in the form the message has (a request or the host's finished mission),
        // and the rules that tie it to the other fields.
        static string MissionProblem(ActionRequest a, bool applied)
        {
            bool supply = a.Kind == ActionKind.SupplyFlight;
            // the host's own kind always carries the finished form
            if (a.Kind == ActionKind.FlightPlanned) applied = true;
            string e = a.Mission.Validate(applied);
            if (e != null) return e;
            if (!supply && a.Body != a.Mission.Start)
                return ActionKind.Name(a.Kind) + " Body is not the mission's start";
            if (supply)
            {
                if (a.Value != 0 && a.Value != 1) return "Value is not 0 or 1";
                if (a.Mission.Creator != MissionCreators.ResourcesToOrbitToBuildSC)
                    return "a supply flight has the supply planner as its creator";
            }
            else if (!applied && a.Mission.Creator != MissionCreators.Manual
                && a.Mission.Creator != MissionCreators.DragAndDrop)
                return "a request's Creator is Manual or DragAndDrop";
            if (applied && a.Mission.Removals != null)
                foreach (RemovalRow r in a.Mission.Removals)
                    if (r.Agency != a.Agency) return "a removal charges another agency";
            return null;
        }

        // An observatory is a site facility (named by its item id) or a probe (named by its ship, whose own item id
        // changes at every load and is never used): exactly one of the two.
        static string Named(ActionRequest a)
        {
            bool facility = a.ItemId >= 0, probe = a.Ship >= 0;
            if (facility && probe)
                return ActionKind.Name(a.Kind) + " names its observatory by an item id and by a ship";
            if (!facility && !probe) return ActionKind.Name(a.Kind) + " names no observatory (an item id or a ship)";
            return null;
        }

        // A cancel names a queued placeholder (item id -1) by its position; a reorder, a craft cancel and the rest name
        // a real item.
        static bool NamesAPlaceholder(int kind)
        {
            return kind == ActionKind.BuildCancel || kind == ActionKind.CraftCancel;
        }

        // Stock (an Amount of a resource) or modules (a Count, with the source item id), exactly one of them.
        static string MoveProblem(ActionRequest a)
        {
            if (a.Body == a.ToBody) return "MoveToOrbit onto the same body";
            string e = Message.Finite(a.Amount, "Amount", 0, ActionLimits.MaxMoney);   // stock is never negative
            if (e != null) return e;
            bool stock = a.Amount > 0, modules = a.Count > 0;
            if (stock == modules) return "MoveToOrbit moves stock (an Amount) or modules (a Count), one of them";
            if (stock && a.ItemId != -1) return "A stock move names no source item";
            if (a.Value != 0 && (a.Value != 1 || !modules)) return "Value is a module drop's path (0 or 1)";
            return null;
        }

        static string TargetsProblem(TargetRow[] rows, bool mirror)
        {
            if (rows.Length > ActionLimits.MaxTargets) return "more than " + ActionLimits.MaxTargets + " target rows";
            foreach (TargetRow r in rows)
            {
                if (r == null) return "an empty target row";
                // Body 0: a row with no target yet, as the windows' add-row makes (the game skips it)
                if (r.Body < 0 || r.Body > ActionLimits.MaxBodyId) return "a target row's body is out of range";
                string e = Message.Finite(r.Ratio, "Target ratio", 0, ActionLimits.MaxRatio);
                if (e != null) return e;
                if (r.Count < 0 || r.Count > ActionLimits.MaxUnits) return "Target count out of range";
                if (mirror && r.Ratio != 0) return "a mirror row has no ratio";
                if (!mirror && r.Count != 0) return "an energy row has no mirror count";
            }
            return null;
        }

        // A content id as the game names things: valid UTF-8 text (no lone surrogate) of 1 to MaxIdBytes bytes, with no
        // control, format or separator character and no '"' or '\'. Spaces are allowed (three vanilla ids have them).
        // The JSON writer writes every character that passes as its raw UTF-8 bytes, so a message's size is its ids'
        // bytes plus quotes: the line and paragraph separators are refused because it writes them as six-byte escapes.
        public static bool IsContentId(string s) { return IsIdText(s, ActionLimits.MaxIdBytes); }

        // The same form with another byte limit (a heal row's key is up to 128 bytes, a group name 64).
        public static bool IsIdText(string s, int maxBytes)
        {
            if (s == null || s.Length == 0 || s.Length > maxBytes) return false;
            int bytes = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') return false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                    bytes += 4;
                }
                else if (char.IsLowSurrogate(c)) return false;
                else bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                if (bytes > maxBytes) return false;
                UnicodeCategory cat = char.GetUnicodeCategory(s, i);
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format
                    || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator) return false;
                if (char.IsHighSurrogate(c)) i++;
            }
            return true;
        }

        // The names the game itself makes, and nothing else: a name from the host reaches the player's screen, so
        // rich text and anything the game's own generators never write are refused. StarSystemNameGenerator writes
        // "<HD|HIP|TOI|K2|Kepler|GJ> <number>"; CreateDynamicAsteroid writes two capital letters, a digit, "-" and
        // four digits.
        static readonly Regex SystemNameRe =
            new Regex(@"^(HD|HIP|TOI|K2|Kepler|GJ) [0-9]+\z", RegexOptions.CultureInvariant);
        static readonly Regex AsteroidNameRe =
            new Regex(@"^[A-Z]{2}[0-9]-[0-9]{4}\z", RegexOptions.CultureInvariant);

        public static bool IsSystemName(string s)
        {
            return s != null && s.Length <= ActionLimits.MaxSystemNameBytes && SystemNameRe.IsMatch(s);
        }

        public static bool IsAsteroidName(string s) { return s != null && AsteroidNameRe.IsMatch(s); }

        // The key a set action is coalesced under: the same target, whatever the new value. Null for a do action.
        public static string Key(ActionRequest a)
        {
            if (a == null || !ActionKind.IsSet(a.Kind)) return null;
            var sb = new StringBuilder();
            sb.Append(a.Kind).Append('|').Append(a.Agency).Append('|').Append(a.Body).Append('|');
            // a site can hold two facilities of one type, so the item id (where the kind has one) is part of the key
            sb.Append(a.Item);
            if (a.ItemId >= 0) sb.Append('#').Append(a.ItemId);
            if (a.Ship >= 0) sb.Append("#ship").Append(a.Ship);   // two probes can be at one body
            if (a.Kind == ActionKind.SelectDeposit) sb.Append('|').Append(a.Position);   // one key per deposit row
            return sb.ToString();
        }
    }
}
