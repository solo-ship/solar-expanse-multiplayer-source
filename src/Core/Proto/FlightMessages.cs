using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Protocol 5's flight types (plan "Protocol 5"). No new message kind: they ride on ActionRequest (23) and
    // ActionResult (24). Every list is length-checked before any use, every double is finite and in range, every id is
    // a content id, and no type name ever comes from the network. Validate returns null, or what is wrong.

    public static class MissionFlags
    {
        public const int Cyclical = 1, NoBurst = 2, OrbitalContainer = 4, BuyFuel = 8, Fast = 16;
        public const int All = Cyclical | NoBurst | OrbitalContainer | BuyFuel | Fast;
        // what a client's request may set; Cyclical, OrbitalContainer and Fast are the host's alone
        public const int Request = NoBurst | BuyFuel;
    }

    public static class CraftKinds
    {
        public const int Ship = 1, Rocket = 2, Construct = 3, Facility = 4, Container = 5;
    }

    public static class CargoKinds { public const int Resource = 1, Module = 2, Crew = 3, Fuel = 4, Ship = 5; }

    public static class RemovalKinds { public const int Resource = 1, Money = 2; }

    // EMissionCreator (S1): Manual, DragAndDrop, Cyclical, Other, ResourcesToOrbitToBuildSC.
    public static class MissionCreators
    {
        public const int Manual = 0, DragAndDrop = 1, Cyclical = 2, Other = 3, ResourcesToOrbitToBuildSC = 4;
    }

    public sealed class CraftRef
    {
        public int Kind;          // CraftKinds
        public long Id = -1;      // Ship/Rocket: Spacecraft.ID or LaunchVehicle.ID (game-wide); Construct: the item id;
                                  // Facility and Container: -1
        public int Index = -1;    // Facility: the quantity index; else -1
        public string Type;       // the craft type's content id (Facility: scTypeIDFacility); null for a Container

        internal string Validate()
        {
            string e = CommandCheck.Range(Kind, "Craft.Kind", CraftKinds.Ship, CraftKinds.Container);
            if (e != null) return e;
            switch (Kind)
            {
                case CraftKinds.Facility:
                    e = CommandCheck.Range(Id, "Craft.Id", -1, -1)
                        ?? CommandCheck.Range(Index, "Craft.Index", 0, ActionLimits.MaxCount);
                    break;
                case CraftKinds.Container:
                    e = CommandCheck.Range(Id, "Craft.Id", -1, -1) ?? CommandCheck.Range(Index, "Craft.Index", -1, -1);
                    if (e == null && Type != null) e = "a container has no type";
                    return e;
                default:
                    e = CommandCheck.Range(Id, "Craft.Id", 0, int.MaxValue)
                        ?? CommandCheck.Range(Index, "Craft.Index", -1, -1);
                    break;
            }
            return e ?? CommandCheck.Id(Type, "Craft.Type");
        }

        internal string Key() { return Kind + "|" + Id + "|" + Index + "|" + Type; }

        internal CraftRef Copy() { return (CraftRef)MemberwiseClone(); }

        // 1..max (required) or 0..max crafts, each valid, none twice in the list.
        internal static string ValidateList(CraftRef[] crafts, int min, string field)
        {
            int n = crafts == null ? 0 : crafts.Length;
            if (n < min || n > ActionLimits.MaxMissionCrafts)
                return field + " outside " + min + ".." + ActionLimits.MaxMissionCrafts;
            for (int i = 0; i < n; i++)
            {
                if (crafts[i] == null) return "an empty craft in " + field;
                string e = crafts[i].Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    public sealed class CargoRow
    {
        public int Kind;          // CargoKinds
        public string Item;       // a content id (null only for crew and fuel rows)
        public double Amount;     // 0..MaxMoney
        public int Count;         // 0..MaxUnits

        internal string Validate()
        {
            string e = CommandCheck.Range(Kind, "Cargo.Kind", CargoKinds.Resource, CargoKinds.Ship);
            if (e != null) return e;
            if (Item != null || (Kind != CargoKinds.Crew && Kind != CargoKinds.Fuel))
            {
                e = CommandCheck.Id(Item, "Cargo.Item");
                if (e != null) return e;
            }
            return Message.Finite(Amount, "Cargo.Amount", 0, ActionLimits.MaxMoney)
                ?? CommandCheck.Range(Count, "Cargo.Count", 0, ActionLimits.MaxUnits);
        }

        internal CargoRow Copy() { return (CargoRow)MemberwiseClone(); }

        internal static string ValidateList(CargoRow[] rows)
        {
            if (rows == null) return null;
            if (rows.Length > ActionLimits.MaxCargoRows)
                return "Cargo has more than " + ActionLimits.MaxCargoRows + " rows";
            foreach (CargoRow r in rows)
            {
                if (r == null) return "an empty cargo row";
                string e = r.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    public sealed class GridPoint
    {
        public double V1X, V1Y, V1Z, DepX, DepY, DepZ, ArrX, ArrY, ArrZ;

        internal GridPoint Copy() { return (GridPoint)MemberwiseClone(); }

        internal string Validate()
        {
            foreach (double v in new[] { V1X, V1Y, V1Z, DepX, DepY, DepZ, ArrX, ArrY, ArrZ })
            {
                string e = Message.Finite(v, "Grid value", -ActionLimits.MaxMoney, ActionLimits.MaxMoney);
                if (e != null) return e;
            }
            return null;
        }
    }

    // The values the host's schedule step reads. Wide ranges here (finite, a sane size); FlightSanity holds the tight
    // bounds.
    public sealed class FlightFigures
    {
        public double DV11, DV22;                                    // DeltaV = DV11 + DV22
        public double FuelNeed, MinFuelCost, LeftOverFuel, FlightCost, FuelToOrbit, CostStartLV;
        public double AllFuelNeed, OptimalFuelNeed;
        public double LifeSupport;
        public double CargoMassPotencjal;
        public int EarthMoonCase;
        public bool GravityAssistClick;   // revision 15b: the slingshot flag as the actor's planner set it

        public const int MaxEarthMoonCase = 255;

        internal FlightFigures Copy() { return (FlightFigures)MemberwiseClone(); }

        internal string Validate()
        {
            foreach (double v in new[] { DV11, DV22, FuelNeed, MinFuelCost, LeftOverFuel, FlightCost, FuelToOrbit,
                CostStartLV, AllFuelNeed, OptimalFuelNeed, LifeSupport, CargoMassPotencjal })
            {
                string e = Message.Finite(v, "Figure", -ActionLimits.MaxMoney, ActionLimits.MaxMoney);
                if (e != null) return e;
            }
            return CommandCheck.Range(EarthMoonCase, "EarthMoonCase", 0, MaxEarthMoonCase);
        }
    }

    public sealed class RemovalRow
    {
        public int What;          // RemovalKinds
        public string Agency;     // the agency charged (the acting one)
        public int Body;          // the site; 0 for money
        public string Resource;   // the resource's content id; null for money
        public double Amount;     // > 0, at most MaxMoney

        internal RemovalRow Copy() { return (RemovalRow)MemberwiseClone(); }

        internal string Validate()
        {
            string e = CommandCheck.Range(What, "Removal.What", RemovalKinds.Resource, RemovalKinds.Money)
                ?? JoinCheck.Agency(Agency, "Removal.Agency", true);
            if (e != null) return e;
            if (What == RemovalKinds.Money)
            {
                if (Body != 0) return "a money removal names no body";
                if (Resource != null) return "a money removal names no resource";
            }
            else
            {
                e = CommandCheck.Range(Body, "Removal.Body", 1, ActionLimits.MaxBodyId)
                    ?? CommandCheck.Id(Resource, "Removal.Resource");
                if (e != null) return e;
            }
            return Amount > 0 && Amount <= ActionLimits.MaxMoney ? null
                : "Removal.Amount outside (0, MaxMoney]: "
                    + Amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // A mission as it travels: in a request (the client's trajectory, nothing the host alone decides) or applied (the
    // host's real figures, removals and name).
    public sealed class MissionData
    {
        public long MissionId = -1;       // the host's; -1 in a request
        public int Start, Target;         // ObjectInfo ids, 1..MaxBodyId, Start != Target
        public long DepartTicks, ArriveTicks;
        public CraftRef[] Ships;          // first = the mission's spacecraft; 1..MaxMissionCrafts
        public CraftRef[] Rockets;        // first = its launch vehicle; 0..MaxMissionCrafts
        public CargoRow[] Cargo;          // 0..MaxCargoRows
        public string Name;               // MissionNames.Problem must be null; cleaned by Validate
        public int Creator;               // EMissionCreator
        public int FuelSource;            // the planner's CheckFuelTakeFrom choice: advice in a request
        public int Flags;                 // MissionFlags
        public double MaxFuelPrice;       // 0..MaxMoney
        public GridPoint Grid;
        public FlightFigures Figures;
        public RemovalRow[] Removals;     // applied form: what the host's schedule step took, in order
        public int Container = -1;        // applied form: 1 when the apply used an orbital supply container, else 0

        public const int MaxFuelSource = 3;

        // A deep copy: no list or row is shared with the original.
        public MissionData Copy()
        {
            var c = (MissionData)MemberwiseClone();
            c.Ships = Copies(Ships, x => x.Copy());
            c.Rockets = Copies(Rockets, x => x.Copy());
            c.Cargo = Copies(Cargo, x => x.Copy());
            c.Removals = Copies(Removals, x => x.Copy());
            c.Grid = Grid == null ? null : Grid.Copy();
            c.Figures = Figures == null ? null : Figures.Copy();
            return c;
        }

        internal static T[] Copies<T>(T[] list, Func<T, T> copy) where T : class
        {
            if (list == null) return null;
            var c = new T[list.Length];
            for (int i = 0; i < list.Length; i++) c[i] = list[i] == null ? null : copy(list[i]);
            return c;
        }

        // request: the client's form (Z1); applied: the host's finished mission
        public string Validate(bool applied)
        {
            string e = applied ? CommandCheck.Range(MissionId, "MissionId", 0, int.MaxValue)
                               : CommandCheck.Range(MissionId, "MissionId", -1, -1);
            e = e ?? CommandCheck.Range(Start, "Start", 1, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Target, "Target", 1, ActionLimits.MaxBodyId);
            if (e != null) return e;
            if (Start == Target) return "Start is Target";
            e = JoinCheck.Ticks(DepartTicks, "DepartTicks", false) ?? JoinCheck.Ticks(ArriveTicks, "ArriveTicks", false);
            if (e != null) return e;
            if (DepartTicks >= ArriveTicks) return "the flight does not arrive after it departs";
            e = CraftRef.ValidateList(Ships, 1, "Ships") ?? CraftRef.ValidateList(Rockets, 0, "Rockets");
            if (e != null) return e;
            e = NoRepeats();
            e = e ?? CargoRow.ValidateList(Cargo);
            if (e != null) return e;
            e = ValidateName(applied);
            if (e != null) return e;
            e = CommandCheck.Range(Creator, "Creator", MissionCreators.Manual,
                MissionCreators.ResourcesToOrbitToBuildSC);
            if (e == null && !applied && Creator != MissionCreators.Manual && Creator != MissionCreators.DragAndDrop
                && Creator != MissionCreators.ResourcesToOrbitToBuildSC)
                e = "a request's Creator is Manual or DragAndDrop (or the supply planner's)";
            e = e ?? CommandCheck.Range(FuelSource, "FuelSource", 0, MaxFuelSource);
            if (e == null && (Flags < 0 || Flags > MissionFlags.All || (!applied && (Flags & ~MissionFlags.Request) != 0)))
                e = applied ? "Flags has an unknown bit" : "a request sets only NoBurst and BuyFuel";
            e = e ?? Message.Finite(MaxFuelPrice, "MaxFuelPrice", 0, ActionLimits.MaxMoney);
            if (e != null) return e;
            if (Grid == null) return "Grid missing";
            if (Figures == null) return "Figures missing";
            e = Grid.Validate() ?? Figures.Validate();
            if (e != null) return e;
            if (!applied) return Removals != null ? "a request carries no removals"
                : Container != -1 ? "a request has no container" : null;
            if (Removals == null) return "Removals missing";
            if (Removals.Length > ActionLimits.MaxRemovals)
                return "Removals has more than " + ActionLimits.MaxRemovals + " rows";
            foreach (RemovalRow r in Removals)
            {
                if (r == null) return "an empty removal row";
                e = r.Validate();
                if (e != null) return e;
            }
            return CommandCheck.Range(Container, "Container", 0, 1);
        }

        string ValidateName(bool applied)
        {
            string e = MissionNames.Problem(Name);
            if (e != null) return e;
            if (Name != null) Name = MissionNames.Clean(Name);
            return applied && string.IsNullOrEmpty(Name) ? "the finished mission has no name" : null;
        }

        string NoRepeats()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CraftRef c in Ships) if (!seen.Add(c.Key())) return "a craft twice in one mission";
            if (Rockets != null) foreach (CraftRef c in Rockets) if (!seen.Add(c.Key())) return "a craft twice in one mission";
            return null;
        }
    }

    // A route: both legs in request form (the host plans them).
    public sealed class CyclicalData
    {
        public MissionData Out;
        public MissionData Back;
        public int Flags;     // RouteFlags: the route record's toggles (S1)
        public int EndsCount;     // the trips of a route that ends after N times; else 0
        public long EndsUntil;    // the date (ticks) of a route that ends on one; else 0

        public CyclicalData Copy()
        {
            var c = (CyclicalData)MemberwiseClone();
            c.Out = Out == null ? null : Out.Copy();
            c.Back = Back == null ? null : Back.Copy();
            return c;
        }

        internal string Validate()
        {
            if (Out == null) return "Route.Out missing";
            if (Back == null) return "Route.Back missing";
            string e = Out.Validate(false) ?? Back.Validate(false);
            if (e != null) return e;
            if (Out.Creator > MissionCreators.DragAndDrop || Back.Creator > MissionCreators.DragAndDrop)
                return "a route's legs have Creator Manual or DragAndDrop";
            e = CommandCheck.Range(Flags, "Route.Flags", 0, ActionLimits.MaxCyclicalFlags);
            return e ?? RouteRules.Problem(this);
        }
    }

    // MissionPresetData's fields as plain values (DD-F11).
    public sealed class PresetData
    {
        public string Name;               // MissionNames.Problem must be null; never empty after cleaning
        public int Target;                // 1..MaxBodyId
        public string ScType, ScTypeFacility, LvType;   // content ids or null
        public int ScCount, LvCount;      // 1..MaxMissionCrafts (LvCount 0 when there is no launch vehicle type)
        public bool ScIsLowOrbitContainer, ScIsFromFacility, ForCyclical;
        public CargoRow[] Cargo;          // 0..MaxCargoRows
        public int CostType;              // ECostType 0..2

        public const int MaxCostType = 2;

        public PresetData Copy()
        {
            var c = (PresetData)MemberwiseClone();
            c.Cargo = MissionData.Copies(Cargo, x => x.Copy());
            return c;
        }

        public string Validate()
        {
            string e = MissionNames.Problem(Name);
            if (e != null) return e;
            Name = MissionNames.Clean(Name);
            if (Name.Length == 0) return "the preset has no name";
            e = CommandCheck.Range(Target, "Preset.Target", 1, ActionLimits.MaxBodyId);
            foreach (string id in new[] { ScType, ScTypeFacility, LvType })
                if (e == null && id != null) e = CommandCheck.Id(id, "Preset type");
            e = e ?? CommandCheck.Range(ScCount, "Preset.ScCount", 1, ActionLimits.MaxMissionCrafts);
            if (e != null) return e;
            if (LvType == null) { if (LvCount != 0) return "a preset without a launch vehicle has LvCount 0"; }
            else
            {
                e = CommandCheck.Range(LvCount, "Preset.LvCount", 1, ActionLimits.MaxMissionCrafts);
                if (e != null) return e;
            }
            return CargoRow.ValidateList(Cargo) ?? CommandCheck.Range(CostType, "Preset.CostType", 0, MaxCostType);
        }
    }

    // The low orbit and spin the game gave a pulled asteroid.
    public sealed class AsteroidPull
    {
        public int Asteroid, Parent;   // ObjectInfo ids
        public int OrbitId = -1;       // the low orbit CreateOrbit made, or -1 when the asteroid already had one
        public float DistanceFake;     // finite
        public int Rotation;           // 0..359

        internal string Validate()
        {
            return CommandCheck.Range(Asteroid, "Pull.Asteroid", 1, ActionLimits.MaxBodyId)
                ?? CommandCheck.Range(Parent, "Pull.Parent", 1, ActionLimits.MaxBodyId)
                ?? (OrbitId == -1 ? null : CommandCheck.Range(OrbitId, "Pull.OrbitId", 1, ActionLimits.MaxBodyId))
                ?? Message.Finite(DistanceFake, "Pull.DistanceFake", -ActionLimits.MaxOrbitValue,
                    ActionLimits.MaxOrbitValue)
                ?? CommandCheck.Range(Rotation, "Pull.Rotation", 0, 359);
        }
    }

    // The mission-name path (Q2): the player-name rule and cleaning.
    public static class MissionNames
    {
        // null = ok: M2's Text rule (Hello.Name's) at MaxMissionNameChars. A null or empty name passes.
        public static string Problem(string name)
        {
            return Message.Text(name, "Name", ActionLimits.MaxMissionNameChars, false);
        }

        // Never null; Problem(Clean(x)) == null for every x.
        public static string Clean(string name)
        {
            return TextSanitiser.Clean(name, ActionLimits.MaxMissionNameChars, "");
        }

        // The name a receiver gives the game. Vanilla's AI names a rival flight "<ship> <id>\n[AI: <Company.ID>]";
        // Clean makes the newline a space. For a rival agency only, when the cleaned name ends with
        // " [AI: " + agency + "]" and holds no other "[AI: ", that one space becomes "\n" again. Anything else is
        // returned unchanged. A suffix comparison, never a parse. The id is matched ignoring case: the entry carries
        // the wire id (AgencyWire.From lowers the game's) while the tag keeps the game's capitals, which stay as
        // they are, since the name must be the host's to the character or the missions print differs.
        public static string ForGame(string cleaned, string agency, bool rival)
        {
            if (cleaned == null) return "";
            if (!rival || agency == null) return cleaned;
            string suffix = " [AI: " + agency + "]";
            if (!cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return cleaned;
            string head = cleaned.Substring(0, cleaned.Length - suffix.Length);
            if (head.IndexOf("[AI: ", StringComparison.OrdinalIgnoreCase) >= 0) return cleaned;
            return head + "\n" + cleaned.Substring(head.Length + 1);
        }
    }
}
