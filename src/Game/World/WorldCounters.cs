using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MarketOfferManager = Manager.MarketOfferManager;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The counters an apply can move (M7 DD-W3, DD-W7): the offer counter, the game-wide ship and launch-vehicle counters
    // and the acting agency's item counter. The host takes them before and after an apply and sends the rows that moved,
    // with the ids that follow from them; a client sets them first and checks them after (CounterSync). Main thread.
    public sealed class WorldCounters
    {
        static readonly AccessTools.FieldRef<MarketOfferManager, int> CountOffer =
            AccessTools.FieldRefAccess<MarketOfferManager, int>("countOffer");

        // MarketOfferManager.countOffer: the id the next offer takes is this plus one.
        public static int Offers
        {
            get { return CountOffer(MarketOfferManager.Instance); }
            set { CountOffer(MarketOfferManager.Instance) = value; }
        }

        public long Ships, Vehicles, Offer, Item;

        public static WorldCounters Take(Company agency)
        {
            return new WorldCounters { Ships = Spacecraft.count, Vehicles = LaunchVehicle.count, Offer = Offers,
                Item = agency != null ? agency.productionCountID : 0 };
        }

        // The rows of the counters that moved since this was taken, in a fixed order; null when none did.
        public CounterRow[] RowsSince(WorldCounters now, string agency)
        {
            var rows = new List<CounterRow>();
            if (now.Offer != Offer)
                rows.Add(new CounterRow { Which = CounterKind.OfferCount, Before = Offer, After = now.Offer });
            if (now.Ships != Ships)
                rows.Add(new CounterRow { Which = CounterKind.SpacecraftCount, Before = Ships, After = now.Ships });
            if (now.Vehicles != Vehicles)
                rows.Add(new CounterRow { Which = CounterKind.LaunchVehicleCount, Before = Vehicles, After = now.Vehicles });
            if (now.Item != Item)
                rows.Add(new CounterRow { Which = CounterKind.ProductionCountId, Agency = agency, Before = Item,
                    After = now.Item });
            return rows.Count == 0 ? null : rows.ToArray();
        }

        // The ids an apply took, in the order of the rows: each counter's step up. The offer counter's ids are in the
        // ops, not here.
        public long[] NewIdsSince(WorldCounters now)
        {
            var ids = new List<long>();
            for (long i = Ships + 1; i <= now.Ships; i++) ids.Add(i);
            for (long i = Vehicles + 1; i <= now.Vehicles; i++) ids.Add(i);
            for (long i = Item + 1; i <= now.Item; i++) ids.Add(i);
            return ids.Count == 0 ? null : ids.ToArray();
        }
    }
}
