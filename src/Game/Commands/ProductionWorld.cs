using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using EBonus = ScriptableObjectScripts.EBonus;
using EObjectTypes = Data.EObjectTypes;
using Facility = Game.ObjectInfoDataScripts.Facility;
using LaunchVehicleType = Data.ScriptableObject.LaunchVehicleType;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ProductionType = Data.ScriptableObject.MyIDScriptableObjectProductionItem;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using SpaceCraftConstructData = Game.UI.Windows.Elements.SpaceCraftConstructElements.SpaceCraftConstructData;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using SpacecraftType = Data.ScriptableObject.SpacecraftType;

namespace SolarExpanseMultiplayer
{
    // A ship or launch-vehicle type as the construction window offers it. The unlock is asked of the listed type
    // (GetUnlockRocketType); everything else the window reads from the row's data (its type, price and name text),
    // so the apply reads it there too.
    sealed class CraftType
    {
        public SpacecraftType ListedShip;
        public LaunchVehicleType ListedVehicle;
        public SpaceCraftConstructData Data;
        public ProductionType Item { get { return Data.ProductionItem; } }
        public SpacecraftType Ship { get { return Data.SpacecraftType; } }
        public LaunchVehicleType Vehicle { get { return Data.LaunchVehicleType; } }
        // the queue popup's entry prefers the ship type (D 139275-139282)
        public ProductionType QueueItem { get { return Ship != null ? (ProductionType)Ship : Vehicle; } }
    }

    // What one P1-P4 request resolved to in this game: the build site, the price, the build list's game objects.
    sealed class CraftPlan
    {
        public CraftType Type;
        public ObjectInfo BuildBody;
        public ObjectInfoData Site;
        public int Count;
        public object Target;            // P3, P4: the build or queued entry the request names
        public ProductionItem Before;    // P4: the build it is dropped on, null for the front
    }

    // The construction window's world for the ACTING agency (P1-P4): its types, its sites found by their company, its
    // prices with its own bonuses, and the site's build list in the order requests name it. No state.
    static class ProductionWorld
    {
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            AccessTools.FieldRefAccess<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<ProductionItem, string> TypeId =
            AccessTools.FieldRefAccess<ProductionItem, string>("idProductionItemType");
        static readonly AccessTools.FieldRef<ObjectInfoData, List<ProductionQuery>> Query =
            AccessTools.FieldRefAccess<ObjectInfoData, List<ProductionQuery>>("listProductionQuery");

        // A type with exactly this id among the ones the window lists, or null.
        public static CraftType TypeOf(string id)
        {
            if (id == null) return null;
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            foreach (SpacecraftType t in all.AllSpacecraftType.ListNotEmpty)
                if (t != null && t.ID == id) return Usable(new CraftType { ListedShip = t,
                    Data = t.spaceCraftConstructDefault });
            foreach (LaunchVehicleType t in all.AllLaunchVehicleType.ListNotEmpty)
                if (t != null && t.ID == id) return Usable(new CraftType { ListedVehicle = t,
                    Data = t.spaceCraftConstructDefault });
            return null;
        }

        // A row whose data names no type cannot be built the window's way.
        static CraftType Usable(CraftType t)
        {
            return t.Data != null && t.Item != null && (t.Ship != null || t.Vehicle != null) ? t : null;
        }

        static ObjectInfo LowOrbit(ObjectInfo body)
        {
            return body.LowOrbitCustom != null ? body.LowOrbitCustom.GetObjectInfo() : null;
        }

        // Where P1 and P2 put the build: an orbit-only ship ordered at a planet goes to its low orbit (D 139223-139230,
        // 139606).
        public static ObjectInfo BuildBody(CraftType t, ObjectInfo body)
        {
            ObjectInfo low = LowOrbit(body);
            bool toOrbit = ProductionCommand.BuildsInLowOrbit(t.Ship != null && t.Ship.OrbitSC,
                body.objectTypes == EObjectTypes.Orbit, body.AsteroidFromPush, low != null,
                low != null && low.objectTypes == EObjectTypes.Orbit);
            return toOrbit ? low : body;
        }

        // The assembly an orbit-only ship's row asks for (CheckCanBuildSpacecraftsAndLVs, D 137174-137182): note it
        // does not ask whether the low orbit is an orbit, unlike BuildBody; both are the game's own.
        static ObjectInfo AssemblyOrbit(ObjectInfo body)
        {
            ObjectInfo low = LowOrbit(body);
            return body.objectTypes != EObjectTypes.Orbit && low != null && !body.AsteroidFromPush ? low : body;
        }

        public static CraftFacts Facts(Company c, ObjectInfo body, CraftType t)
        {
            var f = new CraftFacts();
            if (t == null) return f;
            f.Known = true;
            f.LaunchVehicle = t.Vehicle != null;
            f.Unlocked = t.ListedVehicle != null ? c.IsUnlockRocketType(t.ListedVehicle)
                : c.IsUnlockSpacecraftType(t.ListedShip);
            ObjectInfoData site = GameAgencies.Site(body, c);
            f.HasSite = site != null;
            if (site == null) return f;
            f.Assembly = site.VehicleAssemblyCountEnable;
            if (t.Vehicle != null)
            {
                f.LvWithoutAssembly = site.CanBeBuildLVWhenNoVehicleAssembly(t.Vehicle);
                f.LvOnThisBody = t.Vehicle.CanBuildOnThisPlanet(site);
                f.LvParameter = t.Vehicle.CanBuildParameter.CanBuild(site);
                return f;
            }
            f.OrbitOnly = t.Ship.OrbitSC;
            if (f.OrbitOnly)
            {
                ObjectInfoData orbit = GameAgencies.Site(AssemblyOrbit(body), c);
                f.HasOrbitSite = orbit != null;
                f.OrbitAssembly = orbit != null ? orbit.VehicleAssemblyCountEnable : 0;
            }
            return f;
        }

        // The window's price for one unit: the type's price times the agency's own build-cost bonus, rounded up
        // (D 139238-139239).
        public static ResourcePrice Price(Company c, CraftType t)
        {
            ResourcePrice p = t.Data.Price * c.BonusController.GetBonus(EBonus.BuildCost, t.Item);
            p.CeilToInt();
            return p;
        }

        // The site's build list in the print's order (ObjectRead): the unfinished builds, then the queued entries;
        // `things` gets the game object of each row.
        public static List<SiteRow> Rows(ObjectInfoData site, List<object> things)
        {
            var rows = new List<SiteRow>();
            if (site.ProductionItem != null)
                foreach (ProductionItem p in site.ProductionItem)
                {
                    if (p == null || Built(p)) continue;
                    var craft = p as SpacecraftConstructData;
                    rows.Add(new SiteRow { ItemId = p.ID, Item = ItemOf(p), Craft = craft != null,
                        Remaining = p.CountToBuild == 0 ? 1 : p.CountToBuild - p.CountBuild });
                    things.Add(p);
                }
            List<ProductionQuery> q = Query(site);
            if (q != null)
                foreach (ProductionQuery e in q)
                {
                    if (e == null) continue;
                    ProductionType item = e.MyIDScriptableObjectProductionItem;
                    rows.Add(new SiteRow { Queued = true, Item = item != null ? item.ID : "",
                        Craft = item is SpacecraftType || item is LaunchVehicleType, Remaining = e.countToBuild });
                    things.Add(e);
                }
            return rows;
        }

        static string ItemOf(ProductionItem p)
        {
            var f = p as Facility;
            if (f != null) return f.facilityDescriptor != null ? f.facilityDescriptor.ID : "";
            return TypeId(p) ?? "";
        }

        // The stand-in the game shows for a queued ship or launch vehicle; cancelling it is how the game takes the
        // entry out of the queue. Null when the game has none.
        public static SpacecraftConstructData Placeholder(ObjectInfoData site, ProductionQuery e)
        {
            Data.SpacecraftToConstructNotYetPay ship;
            if (site.DictionarySTCNotYetPayCash.TryGetValue(e, out ship) && ship != null) return ship;
            Data.LaunchVehicleToConstructNotYetPay lv;
            if (site.DictionaryLTCNotYetPayCash.TryGetValue(e, out lv) && lv != null) return lv;
            return null;
        }

        // ---- the decision every kind makes on the host, and the plan both sides apply ----

        // 0, or the refusal. host: the window's checks for the acting agency, with P1's count cut to what it can pay
        // for; a client takes the host's count and only resolves the same objects (a name that no longer resolves is
        // out of step).
        public static int Decide(ActionRequest a, Company c, ObjectInfo body, bool host, out CraftPlan plan)
        {
            plan = null;
            if (c == null || body == null) return RefuseCodes.BadTarget;
            if (a.Kind == ActionKind.CraftBuild || a.Kind == ActionKind.CraftQueue) return Order(a, c, body, host,
                out plan);
            ObjectInfoData site = GameAgencies.Site(body, c);
            if (site == null) return RefuseCodes.BadTarget;
            var things = new List<object>();
            List<SiteRow> rows = Rows(site, things);
            plan = new CraftPlan { Site = site, BuildBody = body };
            if (a.Kind == ActionKind.CraftCancel)
            {
                int index;
                int code = ProductionCommand.CancelTarget(rows, a, out index);
                if (code != 0) return code;
                plan.Target = things[index];
                var queued = plan.Target as ProductionQuery;
                // the game cancels a queued entry through its stand-in; with none it cannot be cancelled
                if (queued != null && Placeholder(site, queued) == null) return RefuseCodes.NotAllowedHere;
                return 0;
            }
            if (a.Kind != ActionKind.CraftReorder) return RefuseCodes.NotAllowedHere;
            int at, before;
            int r = ProductionCommand.ReorderTarget(rows, a, out at, out before);
            if (r != 0) return r;
            plan.Target = things[at];
            plan.Before = before >= 0 ? (ProductionItem)things[before] : null;
            return 0;
        }

        static int Order(ActionRequest a, Company c, ObjectInfo body, bool host, out CraftPlan plan)
        {
            plan = null;
            CraftType t = TypeOf(a.Item);
            if (host)
            {
                int code = ProductionCommand.CheckCraft(Facts(c, body, t), a.Count);
                if (code != 0) return code;
            }
            else if (t == null) return RefuseCodes.BadTarget;
            ObjectInfo buildBody = BuildBody(t, body);
            ObjectInfoData site = GameAgencies.Site(buildBody, c);
            if (site == null) return RefuseCodes.BadTarget;
            plan = new CraftPlan { Type = t, BuildBody = buildBody, Site = site, Count = a.Count };
            if (a.Kind != ActionKind.CraftBuild || !host) return 0;
            ResourcePrice price = Price(c, t);
            double money = c.MoneyController.CurrentMoney;
            plan.Count = ProductionCommand.Affordable(a.Count, n => site.HaveResources(price * n),
                n => money >= (price * n).BuildCost);
            return plan.Count > 0 ? 0 : RefuseCodes.CannotAfford;
        }

        // The local player's object window shows the site: redrawn after a change to it, as the window's own handlers
        // do. Another agency's builds never touch it; a window that fails to redraw must not undo the world's change.
        public static void Redraw(Company c, ObjectInfo body)
        {
            if (c == null || !c.IsPlayer || body == null) return;
            try
            {
                var ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance;
                if (ui == null) return;
                var main = ui.GetWindow<Game.UI.Windows.Windows.ObjectInfoWindow>();
                if (main != null && main.Open && main.ObjectInfoCurrent == body) main.SetData(body);
                var second = ui.GetSecondWindow<Game.UI.Windows.Windows.ObjectInfoWindow>();
                if (second != null && second.Open && second.ObjectInfoCurrent == body) second.SetData(body);
            }
            catch (Exception e) { Log.Warn("command: the object window did not redraw: " + e.GetType().Name); }
        }
    }
}
