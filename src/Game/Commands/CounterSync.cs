using System;
using HarmonyLib;
using Company = Game.Company;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The counters of a client's apply (DD3, DD11; plan "Where clients apply") as this game holds them: the item
    // counter of an agency, the game-wide ship and launch-vehicle counters and the ship-name counters. CounterPass
    // (Core) decides what is set and checked; this reads and writes. Equal counters make equal item, ship and
    // launch-vehicle ids on every game. Owned by the client glue; main thread.
    public sealed class CounterSync : ICounterStore
    {
        static readonly AccessTools.FieldRef<ProductionItem, int> ItemId =
            AccessTools.FieldRefAccess<ProductionItem, int>("id");

        readonly CounterPass pass;

        public CounterSync() { pass = new CounterPass(this); }

        // The ship-name counters (N7) map a content key to the name text this game's language makes for the type
        // (NameCounters, wired by the session). Without it a name-counter row cannot be followed: out of step.
        public Func<Company, string, long, bool> SetName;
        public Func<Company, string, long> NameCount;

        public void SkipNext() { pass.SkipNext(); }
        public void SpoilNext() { pass.SpoilNext(); }
        public string Before(CounterRow[] rows) { return pass.Before(rows); }
        public string Before(CounterRow[] rows, long atTicks) { return pass.Before(rows, atTicks); }
        public ItemIdGaps Gaps { get { return pass.Gaps; } }
        public string After(CounterRow[] rows) { return pass.After(rows); }
        public string BeforeBuildsPrint(string agency) { return pass.BeforeBuildsPrint(agency); }
        public string Set(CounterRow[] rows) { return pass.Set(rows); }

        public string Read(CounterRow r, out long value)
        {
            value = 0;
            switch (r.Which)
            {
                case CounterKind.SpacecraftCount: value = Spacecraft.count; return null;
                case CounterKind.LaunchVehicleCount: value = LaunchVehicle.count; return null;
                case CounterKind.MissionId: value = FlightApply.LastMissionId; return null;
                case CounterKind.ProductionCountId:
                {
                    Company c = GameAgencies.Find(r.Agency);
                    if (c == null) return "no agency " + r.Agency + " for " + CounterRules.Name(r);
                    value = c.productionCountID;
                    return null;
                }
                case CounterKind.OfferCount: value = WorldCounters.Offers; return null;
                case CounterKind.NameCounter:
                {
                    Company c = GameAgencies.Find(r.Agency);
                    Func<Company, string, long> count = NameCount;
                    if (c == null || count == null) return "cannot read " + CounterRules.Name(r);
                    value = count(c, r.Key);
                    return null;
                }
                default: return "an unknown counter " + r.Which;
            }
        }

        // A landed ship's facility this game made ahead of an entry the host applied first (ItemIdGaps.Ahead), moved
        // before the entry is applied: the item holds its own id, and the site and the ship hold the object.
        public string Renumber(object item, long id)
        {
            var p = item as ProductionItem;
            if (p == null) return "an item to renumber is not one of the game's";
            if (id < 0 || id > int.MaxValue) return "an item id cannot be " + id;
            ItemId(p) = (int)id;
            return null;
        }

        public string Write(CounterRow r, long value)
        {
            if (value < 0 || value > int.MaxValue) return CounterRules.Name(r) + " cannot be " + value;
            switch (r.Which)
            {
                case CounterKind.SpacecraftCount: Spacecraft.count = (int)value; return null;
                case CounterKind.LaunchVehicleCount: LaunchVehicle.count = (int)value; return null;
                case CounterKind.MissionId: FlightApply.LastMissionId = (int)value; return null;
                case CounterKind.ProductionCountId:
                {
                    Company c = GameAgencies.Find(r.Agency);
                    if (c == null) return "no agency " + r.Agency + " for " + CounterRules.Name(r);
                    c.productionCountID = (int)value;
                    return null;
                }
                case CounterKind.OfferCount: WorldCounters.Offers = (int)value; return null;
                case CounterKind.NameCounter:
                {
                    Company c = GameAgencies.Find(r.Agency);
                    Func<Company, string, long, bool> set = SetName;
                    if (c == null || set == null || !set(c, r.Key, value)) return "cannot set " + CounterRules.Name(r);
                    return null;
                }
                default: return "an unknown counter " + r.Which;
            }
        }
    }
}
