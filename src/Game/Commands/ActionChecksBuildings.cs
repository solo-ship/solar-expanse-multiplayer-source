using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using AllScriptableObjectManager = Manager.AllScriptableObjectManager;
using CanBuildParameter = Game.CompanyScripts.CanBuildParameter;
using Company = Game.Company;
using EBonus = ScriptableObjectScripts.EBonus;
using EObjectTypes = Data.EObjectTypes;
using ESpecialAbilityFacilityNew = Data.ScriptableObject.ESpecialAbilityFacilityNew;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBaseDescriptor = Data.ScriptableObject.FacilityBaseDescriptor;
using GroundFacilityDescriptor = Data.ScriptableObject.GroundFacilityDescriptor;
using IEnergyReceiver = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.IEnergyReceiverFacilityOrModule;
using IEnergyTransfer = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.IEnergyTransferFacilityOrModule;
using MirrorFacility = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.SpaceMirrorOrShadeFacility;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using NotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using QueueEntry = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SpaceModule = Game.ObjectInfoDataScripts.SpaceModule;
using SpaceModuleDescriptor = Data.ScriptableObject.SpaceModuleDescriptor;
using Spacecraft = CustomUpdate.Spacecraft;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // What a building request names, found for the ACTING agency, and what the host decided for it. The host's Check
    // and Apply make it with every window check; a client's Apply makes it from the host's applied request with the
    // names checked only (the host has decided the rest, and a name that does not resolve means out of step).
    sealed class BuildingTarget
    {
        public ActionRequest Applied;       // the request as it is applied (counts and amounts normalised)
        public Company Company;
        public ObjectInfo Body, ToBody;
        public ObjectInfoData Site, ToSite;
        public FacilityBaseDescriptor Descriptor;
        public Facility Facility;           // the named facility (a queue entry's placeholder for its cancel)
        public Facility Before;             // B5: the facility the dragged one moves before
        public ResourcePrice Price;         // B1: one unit's price with the agency's own bonus
        public ResourceDefinition Resource; // B11 stock
        public RowExploredResourcesData Deposit;
        public bool ModulesInOnePopup;      // B11: more than one free, so the count popup's path (D 124414-124444)
    }

    // The building windows' checks (B1-B5, B7, B8, B10, B11 and the two target-list windows), repeated on the host for
    // the acting agency: Task 1's list in docs/research/commands.md "K10". Several window checks read the local player,
    // so they are rewritten here rather than called: ObjectInfoWindow.CheckCanBuildFacility, CanBuildParameter's
    // "one on a planet that is not the home" rule and ObjectInfo.OnDragAndDrop's pair rules. The decisions live in
    // Core (BuildingRules, MoveRules, TargetListRules) with real tests; this file only gathers the agency's facts.
    // The mission planner's blocks are the clicking player's own window state, not the world's: the capture sees them.
    public static class BuildingChecks
    {
        // The game members these checks read, each resolved on its own so a renamed one is a refusal with a log line
        // (Missing), never a TypeInitializationException that fails every building apply. The private method is found
        // through plain reflection and bound with the runtime's own CreateDelegate: Harmony's MethodDelegate has
        // another signature in HarmonyX (BepInEx), where the call made this whole class fail to load.
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            Ref<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<CanBuildParameter, GroundFacilityDescriptor[]> Tab =
            Ref<CanBuildParameter, GroundFacilityDescriptor[]>("tab");
        static readonly AccessTools.FieldRef<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>> Priorities =
            Ref<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>>("facilityBaseDescriptorToPriority");
        static readonly Func<ObjectInfo, Company, double> LiftCapacity = Lift();
        static readonly string Missing = MissingLookup();

        static AccessTools.FieldRef<T, F> Ref<T, F>(string field)
        {
            try { return AccessTools.FieldRefAccess<T, F>(field); }
            catch (Exception e)
            {
                Log.Error("command: building checks: " + typeof(T).Name + "." + field + " cannot be read: "
                    + e.GetType().Name);
                return null;
            }
        }

        static Func<ObjectInfo, Company, double> Lift()
        {
            try
            {
                MethodInfo m = typeof(ObjectInfo).GetMethod("HowMuchCanDragToOrbit", BindingFlags.Instance
                    | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(Company) }, null);
                if (m == null || m.ReturnType != typeof(double)) return null;
                return (Func<ObjectInfo, Company, double>)Delegate.CreateDelegate(
                    typeof(Func<ObjectInfo, Company, double>), m, false);
            }
            catch (Exception e)
            {
                Log.Error("command: building checks: ObjectInfo.HowMuchCanDragToOrbit cannot be bound: "
                    + e.GetType().Name);
                return null;
            }
        }

        // The first game member this game lacks, or null.
        static string MissingLookup()
        {
            string m = Built == null ? "ProductionItem.build" : Tab == null ? "CanBuildParameter.tab"
                : Priorities == null ? "ObjectInfoData.facilityBaseDescriptorToPriority"
                : LiftCapacity == null ? "ObjectInfo.HowMuchCanDragToOrbit(Company)" : null;
            if (m != null) Log.Error("command: building checks: the game has no " + m + "; every building action is "
                + "refused");
            return m;
        }

        public static bool Handles(int kind)
        {
            switch (kind)
            {
                case ActionKind.Build: case ActionKind.BuildQueue: case ActionKind.BuildCancel: case ActionKind.Scrap:
                case ActionKind.BuildReorder: case ActionKind.FacilityEnabled: case ActionKind.FacilityPriority:
                case ActionKind.SelectDeposit: case ActionKind.MoveToOrbit: case ActionKind.EnergyTargets:
                case ActionKind.MirrorTargets:
                    return true;
                default: return false;
            }
        }

        // 0, or the refusal.
        public static int Check(ActionRequest a)
        {
            BuildingTarget t;
            return Decide(a, true, out t);
        }

        // host: every window check, the request normalised into t.Applied. Not host: the names only, for a client
        // applying the host's entry.
        internal static int Decide(ActionRequest a, bool host, out BuildingTarget t)
        {
            t = null;
            if (a == null || !Handles(a.Kind) || Missing != null) return RefuseCodes.NotAllowedHere;
            Company c = GameAgencies.Find(a.Agency);
            ObjectInfo body = GameAgencies.Body(a.Body);
            ObjectInfoData site = GameAgencies.Site(body, c);
            if (c == null || site == null) return RefuseCodes.BadTarget;
            var r = new BuildingTarget { Applied = ActionCopy.Of(a), Company = c, Body = body, Site = site };
            int code;
            switch (a.Kind)
            {
                case ActionKind.Build: case ActionKind.BuildQueue: code = NewBuild(r, host); break;
                case ActionKind.BuildCancel: code = Cancel(r, host); break;
                case ActionKind.Scrap: code = Scrap(r, host); break;
                case ActionKind.BuildReorder: code = Reorder(r); break;
                case ActionKind.FacilityEnabled: code = Enabled(r, host); break;
                case ActionKind.FacilityPriority: code = Priority(r, host); break;
                case ActionKind.SelectDeposit: code = Deposit(r, host); break;
                case ActionKind.MoveToOrbit: code = Move(r, host); break;
                case ActionKind.EnergyTargets: code = Energy(r, host); break;
                default: code = Mirror(r, host); break;
            }
            if (code == 0) t = r;
            return code;
        }

        // ---- lookups ----

        // A facility descriptor by its exact id: GetByID also answers to a renamed old id, which is not this one.
        internal static FacilityBaseDescriptor DescriptorOf(string id)
        {
            if (id == null) return null;
            var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllFacility;
            FacilityBaseDescriptor d = all != null ? all.GetByID(id) : null;
            return d != null && d.ID == id ? d : null;
        }

        static ResourceDefinition ResourceOf(string id)
        {
            if (id == null) return null;
            var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllResourceDefinitions;
            ResourceDefinition d = all != null ? all.GetByID(id) : null;
            return d != null && d.ID == id ? d : null;
        }

        // A built or building facility of the site by its item id and type (never a queue placeholder, whose id is
        // -1). Ids repeat at a site, so SiteItems.Facility picks: a finished one of that type first; a facility of
        // another type with the id comes back only when none of the type has it, for the caller's TargetChanged.
        static Facility FacilityById(ObjectInfoData site, long id, string content)
        {
            if (site.ListFacility == null || id < 0) return null;
            var found = new List<Facility>();
            var seen = new List<SiteItem>();
            foreach (Facility f in site.ListFacility)
            {
                if (f == null || f is NotYetPay || f.ID != id) continue;
                found.Add(f);
                seen.Add(new SiteItem(f.ID, f.facilityDescriptor != null ? f.facilityDescriptor.ID : null,
                    f.FinishConstructionBool));
            }
            int pick = SiteItems.Facility(seen, id, content);
            return pick >= 0 ? found[pick] : null;
        }

        // The site's build list as a request names its rows (BuildListRow): the unfinished items, then the queue.
        internal static List<BuildListRow> Rows(ObjectInfoData site)
        {
            var rows = new List<BuildListRow>();
            if (site.ProductionItem != null)
                foreach (ProductionItem p in site.ProductionItem)
                {
                    if (p == null || Built(p)) continue;
                    var f = p as Facility;
                    rows.Add(new BuildListRow { ItemId = p.ID, Facility = f != null,
                        Item = f != null ? (f.facilityDescriptor != null ? f.facilityDescriptor.ID : "") : null });
                }
            if (site.ListProductionQuery != null)
                foreach (QueueEntry e in site.ListProductionQuery)
                {
                    if (e == null) continue;
                    var d = e.MyIDScriptableObjectProductionItem as FacilityBaseDescriptor;
                    rows.Add(new BuildListRow { ItemId = -1, Queue = true, Facility = d != null,
                        Item = e.MyIDScriptableObjectProductionItem != null ? e.MyIDScriptableObjectProductionItem.ID
                            : "" });
                }
            return rows;
        }

        // An unfinished build by its item id and its row's type (Rows: a facility's descriptor, nothing for a craft).
        static ProductionItem UnfinishedById(ObjectInfoData site, long id, string content)
        {
            if (site.ProductionItem == null) return null;
            var found = new List<ProductionItem>();
            var seen = new List<SiteItem>();
            foreach (ProductionItem p in site.ProductionItem)
            {
                if (p == null || p.ID != id) continue;
                var f = p as Facility;
                found.Add(p);
                seen.Add(new SiteItem(p.ID, f != null ? (f.facilityDescriptor != null ? f.facilityDescriptor.ID : "")
                    : null, Built(p)));
            }
            int pick = SiteItems.Unfinished(seen, id, content);
            return pick >= 0 ? found[pick] : null;
        }

        // ---- B1, B2: a new build ----

        static int NewBuild(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            FacilityBaseDescriptor fbd = DescriptorOf(a.Item);
            if (fbd == null) return RefuseCodes.BadTarget;
            t.Descriptor = fbd;
            t.Price = fbd.Price * t.Company.BonusController.GetBonus(EBonus.BuildCost, fbd, t.Body);
            if (!host) return 0;
            if (!t.Company.IsUnlockFacility(fbd)) return RefuseCodes.Locked;
            if (!Listed(fbd, t.Body, t.Site, t.Company)) return RefuseCodes.NotAllowedHere;
            if (!CanBuildFacility(fbd, t.Body, t.Site, t.Company)) return RefuseCodes.NotAllowedHere;
            int howMany;
            int canBuild = fbd.CanBuildParameter.HowManyCanBuild(t.Site, fbd, out howMany);
            bool station = fbd.FacilityItemClass != typeof(SpaceStationFacilityNextSegment) && fbd.SpaceStationBuilding;
            int stationMax = 0, stationHave = 0;
            if (station)
            {
                // the station owner's cap: the game's own reads the local player's data, which only the owner's
                // game has a view on (StationCapRule)
                stationMax = (int)Math.Min(int.MaxValue, StationCapRule.For(t.Site.ObjectInfo));
                fbd.CanBuildParameter.HowManyCanBuild(t.Site, null, out stationHave);
            }
            int count, code;
            if (a.Kind == ActionKind.Build)
            {
                ResourcePrice price = t.Price;
                ObjectInfoData site = t.Site;
                Company c = t.Company;
                code = BuildingRules.BuildNow(a.Count, canBuild, n => site.HaveResources(price * n),
                    n => site.HaveResources(price * n) && c.MoneyController.CurrentMoney >= (price * n).BuildCost,
                    station, stationMax, stationHave, out count);
            }
            else code = BuildingRules.QueueCount(a.Count, canBuild, station, stationMax, stationHave, out count);
            if (code == 0) a.Count = count;
            return code;
        }

        // Only what the build menu lists for this body (AllFacility.GetAllFacilityData D 96844-96874 and
        // ChoseFacilityWindow.FilterByObjectInfo D 133490-133526), for the acting agency.
        static bool Listed(FacilityBaseDescriptor fbd, ObjectInfo body, ObjectInfoData site, Company c)
        {
            var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllFacility;
            if (all == null || all.ListNotEmpty == null || !all.ListNotEmpty.Contains(fbd)) return false;
            if (fbd.FacilityDescriptorSegmentConstruction != null)
            {
                ObjectInfo parent = body.parentObjectInfo;
                ObjectInfo low = parent != null && parent.LowOrbitCustom != null ? parent.LowOrbitCustom.GetObjectInfo()
                    : null;
                ObjectInfoData lowSite = GameAgencies.Site(low, c);
                bool found = false;
                if (lowSite != null && lowSite.ListFacility != null)
                    foreach (Facility f in lowSite.ListFacility)
                        if (f != null && f.facilityDescriptor == fbd.FacilityDescriptorSegmentConstruction)
                            found = true;
                if (!found) return false;
            }
            Type cls = fbd.FacilityItemClass;
            EObjectTypes type = body.objectTypes;
            if (type != EObjectTypes.Orbit && type != EObjectTypes.SolarOrbit)
            {
                if (type == EObjectTypes.Asteroid || type == EObjectTypes.Comet)
                {
                    if (fbd.NoBuildOnAsteroid) return false;
                }
                else if (fbd.PossiblePlacement == FacilityBaseDescriptor.EPossiblePlacement.Orbit) return false;
                return cls != typeof(SpaceStationFacilityNextSegment)
                    && cls != typeof(SpaceStationConstructionFacility);
            }
            if (body.spaceStationConstructionFacility)
            {
                if (!fbd.PossiblePlacement.HasFlag(FacilityBaseDescriptor.EPossiblePlacement.SpaceStation)
                    && !(fbd is SpaceModuleDescriptor)) return false;
                FacilityBaseDescriptor seg = body.FacilityDescriptorSegmentConstruction;
                if (seg != null && seg.gfdSpaceStationData != null && cls == typeof(SpaceStationFacilityNextSegment)
                    && fbd != seg.gfdSpaceStationData.FbdSegments) return false;
                return true;
            }
            if (!fbd.PossiblePlacement.HasFlag(FacilityBaseDescriptor.EPossiblePlacement.Orbit)
                && fbd is GroundFacilityDescriptor) return false;
            var module = fbd as SpaceModuleDescriptor;
            if (module != null && !module.BuildOnOrbitSpaceModuleAllow) return false;
            if (type == EObjectTypes.Orbit && body.parentObjectInfo != null
                && body.parentObjectInfo.objectTypes == EObjectTypes.Moons
                && (cls == typeof(SpaceStationConstructionFacility) || cls == typeof(InterstellarConstructionFacility)))
                return false;
            return true;
        }

        // ObjectInfoWindow.CheckCanBuildFacility (D 137248-137283) for the agency's own site: construction equipment,
        // the descriptor's build rule and, for a mine, a deposit of what it mines explored there.
        static bool CanBuildFacility(FacilityBaseDescriptor fbd, ObjectInfo body, ObjectInfoData site, Company c)
        {
            if (site.ConstructionEquipmentCount <= 0 && fbd.ConstructionEquipmentCountIsRequired) return false;
            if (!CanBuild(fbd.CanBuildParameter, site, fbd, c, false)) return false;
            var ground = fbd as GroundFacilityDescriptor;
            if (ground == null) return true;
            HashSet<ResourceDefinition> mined = ground.GetResourcesToMine(c);
            if (mined == null || mined.Count == 0) return true;
            if (!body.CanMineResources || site.listExploredResourcesRows == null) return false;
            foreach (RowExploredResourcesData row in site.listExploredResourcesRows)
                if (row != null && mined.Contains(row.ResourceType) && row.ExploredFullyOrPreliminary) return true;
            return false;
        }

        // CanBuildParameter.CanBuild (D 129880-130057) with its two rules that do not stand alone for another agency
        // rewritten: "one on a planet that is not the home" compares with the local player's home, and "the other
        // ground facility blocks it" asks that rule of other descriptors. Their UI text flags are left alone.
        static bool CanBuild(CanBuildParameter p, ObjectInfoData site, FacilityBaseDescriptor fbd, Company c,
            bool recursion)
        {
            if (p.canBuildBy == CanBuildParameter.ECanBuild.oneOnPlanetNotEarth)
            {
                if (!Habitable(p, site, fbd)) return false;
                if (c.mainObjectInfo == null) return false;   // an agency with no home: the rule cannot be judged
                return CanBuildParameter.CustomOneOnPlanetNotEarth(site) || site.ObjectInfo.id != c.mainObjectInfo.id;
            }
            if (p.canBuildBy != CanBuildParameter.ECanBuild.otherGroundFacilityIsBlockThenEnable)
                return p.CanBuild(site, fbd, recursion);
            if (!Habitable(p, site, fbd)) return false;
            bool open = true;
            GroundFacilityDescriptor[] tab = Tab(p);
            if (!recursion && tab != null)
                foreach (GroundFacilityDescriptor g in tab)
                    if (g != null && g != fbd && CanBuild(g.CanBuildParameter, site, g, c, true)) open = false;
            if (open && site.ListFacility != null)
                foreach (Facility f in site.ListFacility)
                    if (f != null && f.facilityDescriptor == fbd) return false;
            return open;
        }

        // CanBuildCheckHabitabilityParametersAndPlanetCharacteristic (private, D 129875): the descriptor's terraform
        // limits on the body.
        static bool Habitable(CanBuildParameter p, ObjectInfoData site, FacilityBaseDescriptor fbd)
        {
            return p.terraformParameterCanBuild == null || p.terraformParameterCanBuild.CheckAll(site.ObjectInfo, fbd);
        }

        // ---- B3: cancel ----

        static int Cancel(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            List<BuildListRow> rows = Rows(t.Site);
            int code;
            if (a.ItemId >= 0)
            {
                int index;
                code = BuildingRules.NamedItem(rows, a.ItemId, a.Item, a.Position, out index);
                if (code != 0) return code;
                t.Facility = UnfinishedById(t.Site, a.ItemId, a.Item) as Facility;
                if (t.Facility == null || t.Facility is NotYetPay) return RefuseCodes.BadTarget;
            }
            else
            {
                int q;
                code = BuildingRules.NamedQueueEntry(rows, a.Item, a.Position, out q);
                if (code != 0) return code;
                QueueEntry e = t.Site.ListProductionQuery[q];
                NotYetPay placeholder;
                if (!t.Site.DictionaryFacilityNotYetPayCash.TryGetValue(e, out placeholder) || placeholder == null)
                    return RefuseCodes.BadTarget;
                t.Facility = placeholder;
            }
            if (!host) return 0;
            // a finished facility's button is its own action (B9), never a cancel
            if (t.Facility.FinishConstructionBool) return RefuseCodes.AlreadyDone;
            return BuildingRules.CancelProblem(a.Count, t.Facility.CountToBuild, t.Facility.CountBuild);
        }

        // ---- B4: scrap ----

        static int Scrap(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            FacilityBaseDescriptor fbd = DescriptorOf(a.Item);
            if (fbd == null) return RefuseCodes.BadTarget;
            Facility f = null;
            if (a.ItemId >= 0) f = FacilityById(t.Site, a.ItemId, a.Item);
            else
            {
                // by type alone only when one finished facility of it stands here
                if (t.Site.ListFacility != null)
                    foreach (Facility x in t.Site.ListFacility)
                        if (x != null && !(x is NotYetPay) && x.facilityDescriptor == fbd && x.FinishConstructionBool)
                        {
                            if (f != null) return RefuseCodes.BadTarget;
                            f = x;
                        }
            }
            if (f == null) return RefuseCodes.BadTarget;
            if (f.facilityDescriptor != fbd) return RefuseCodes.TargetChanged;
            t.Facility = f;
            if (!host) return 0;
            a.ItemId = f.ID;   // every game scraps the facility the host found, by its id
            if (!f.FinishConstructionBool || !fbd.CanBeScrapped) return RefuseCodes.NotAllowedHere;
            return BuildingRules.ScrapProblem(a.Count, f.Quantity, f.Enabled, f.MinEnabledQuantity,
                f.ValidCanAddFacility, EngineInFlight(f, t.Company));
        }

        // An asteroid engine whose pulling flight is still under way (D 134670-134688), for the agency itself.
        static bool EngineInFlight(Facility f, Company c)
        {
            if (f.facilityDescriptor.scTypeAsteroidPullingShip == null) return false;
            MissionInfoManager m = MonoBehaviourSingleton<MissionInfoManager>.Instance;
            TimeController time = MonoBehaviourSingleton<TimeController>.Instance;
            if (m == null || time == null) throw new InvalidOperationException("no missions or clock");
            foreach (MissionInfo mi in m.ListMissionInfo)
            {
                if (mi == null || mi.cancel || mi.start != f.ObjectInfoData.ObjectInfo
                    || !ReferenceEquals(f.ObjectInfoData.company, c) || mi.cargoAll == null
                    || !mi.cargoAll.entireAsteroid)
                    continue;
                var sc = mi.spacecraftInfo2 as Spacecraft;
                if (sc != null && sc.spacecraftType == f.facilityDescriptor.scTypeAsteroidPullingShip
                    && mi.DateArrive > time.CurrentTime) return true;
            }
            return false;
        }

        // ---- B5: reorder ----

        static int Reorder(BuildingTarget t)
        {
            ActionRequest a = t.Applied;
            List<BuildListRow> rows = Rows(t.Site);
            int code = BuildingRules.ReorderProblem(rows, a.ItemId, a.Item, a.Position, a.ToPosition);
            if (code != 0) return code;
            t.Facility = UnfinishedById(t.Site, a.ItemId, a.Item) as Facility;
            if (t.Facility == null) return RefuseCodes.BadTarget;
            // the row's drag carries a facility only while it has no finished unit (UIRowFacility.CanDrag,
            // GetDragAndDropTransactItem), and only such a target moves it (OnDragAndDrop D 165503-165516)
            if (t.Facility.Quantity != 0) return RefuseCodes.NotAllowedHere;
            // the front is a drop on the first row: a facility there is the target, as in the window's own list
            // (listFacility keeps its order then); only a craft at the head leaves no target
            BuildListRow to = rows[a.ToPosition];
            if (a.ToPosition == BuildingRules.FrontPosition && (to.Queue || !to.Facility)) return 0;
            t.Before = UnfinishedById(t.Site, to.ItemId, to.Item) as Facility;
            if (t.Before == null) return RefuseCodes.BadTarget;
            if (t.Before.Quantity != 0) return RefuseCodes.NotAllowedHere;
            return 0;
        }

        // ---- B7, B8: units on, priority ----

        static int Enabled(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            Facility f = FacilityById(t.Site, a.ItemId, a.Item);
            if (f == null) return RefuseCodes.BadTarget;
            if (f.facilityDescriptor == null || f.facilityDescriptor.ID != a.Item) return RefuseCodes.TargetChanged;
            t.Facility = f;
            if (!host) return 0;
            // the slider shows for a facility that can be turned off and moves only while it can work here
            if (!f.CanBeTurnedOff || !f.ValidCanAddFacility) return RefuseCodes.NotAllowedHere;
            long value;
            int code = BuildingRules.EnabledValue(a.Value, f.Quantity, f.MinEnabledQuantity, f.Enabled, out value);
            if (code == 0) a.Value = (int)value;
            return code;
        }

        static int Priority(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            FacilityBaseDescriptor fbd = DescriptorOf(a.Item);
            if (fbd == null) return RefuseCodes.BadTarget;
            t.Descriptor = fbd;
            if (!host) return 0;
            bool has = false;
            if (t.Site.ListFacility != null)
                foreach (Facility f in t.Site.ListFacility)
                    if (f != null && !(f is NotYetPay) && f.facilityDescriptor == fbd) has = true;
            if (!has) return RefuseCodes.BadTarget;
            // the window shows the priority only for a type that uses energy or workers (D 134984)
            if (!(fbd.EnergyConsumption > 0.0) && fbd.NeedWorkersToWork(t.Company) <= 0)
                return RefuseCodes.NotAllowedHere;
            return BuildingRules.PriorityProblem(a.Value, StoredPriority(t.Site, fbd));
        }

        // GetPriority without its write (it stores the default it returns): the host's check must not change the site.
        static int StoredPriority(ObjectInfoData site, FacilityBaseDescriptor fbd)
        {
            Dictionary<FacilityBaseDescriptor, int> stored = Priorities(site);
            int p;
            return stored != null && stored.TryGetValue(fbd, out p) ? p : fbd.Priority;
        }

        // ---- B10: the deposit to mine ----

        static int Deposit(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            List<RowExploredResourcesData> rows = t.Site.listExploredResourcesRows;
            if (rows == null || a.Position < 0 || a.Position >= rows.Count || rows[a.Position] == null)
                return RefuseCodes.TargetChanged;
            RowExploredResourcesData row = rows[a.Position];
            if (row.ResourceType == null || row.ResourceType.ID != a.Item) return RefuseCodes.TargetChanged;
            t.Deposit = row;
            if (!host) return 0;
            // the explored list shows for the agency's own data, not on an orbit (ResourcesListExploreShow D 137131)
            if (t.Body.objectTypes == EObjectTypes.Orbit || !t.Body.ShowListExplored) return RefuseCodes.NotAllowedHere;
            return BuildingRules.DepositProblem(row.SelectedToMine, a.Value == 1);
        }

        // ---- B11: stock or modules to another body ----

        static DragBody DragOf(ObjectInfo o)
        {
            return new DragBody { Id = o.id, Parent = o.parentObjectInfo != null ? o.parentObjectInfo.id : 0,
                Orbit = o.objectTypes == EObjectTypes.Orbit,
                PlanetOrMoon = o.objectTypes == EObjectTypes.Planet || o.objectTypes == EObjectTypes.Moons,
                Linked = o.AsteroidFromPush || o.interstellarConstructionFacility };
        }

        static int Move(BuildingTarget t, bool host)
        {
            ActionRequest a = t.Applied;
            t.ToBody = GameAgencies.Body(a.ToBody);
            t.ToSite = GameAgencies.Site(t.ToBody, t.Company);
            if (t.ToSite == null) return RefuseCodes.BadTarget;
            int pair = MoveRules.Pair(DragOf(t.Body), DragOf(t.ToBody));
            if (host && pair == DragPair.None) return RefuseCodes.NotAllowedHere;
            // CanDragToOrbit and HowMuchCanDragToOrbit ask GetObjectInfoData(company), which is the agency's own site
            // here: Site found it on this body, and the lookup returns a body's own data before any redirect
            bool lifted = pair != DragPair.Up || t.Body.CanDragToOrbit(t.Company);
            if (a.Amount > 0)
            {
                t.Resource = ResourceOf(a.Item);
                if (t.Resource == null) return RefuseCodes.BadTarget;
                if (!host) return 0;
                if (t.Resource.ResourceType != ResourceDefinition.EResourceType.Normal || !lifted)
                    return RefuseCodes.NotAllowedHere;
                double cap = pair == DragPair.Up ? LiftCapacity(t.Body, t.Company) : MoveRules.FreeCap;
                double amount;
                int code = MoveRules.StockAmount(a.Amount, t.Site.CheckResources(t.Resource), cap, out amount);
                if (code == 0) a.Amount = amount;
                return code;
            }
            var sm = FacilityById(t.Site, a.ItemId, a.Item) as SpaceModule;
            if (sm == null) return RefuseCodes.BadTarget;
            if (sm.facilityDescriptor == null || sm.facilityDescriptor.ID != a.Item) return RefuseCodes.TargetChanged;
            t.Facility = sm;
            // the path is the host's: the free count subtracts the units the local mission planner picked as cargo,
            // which no other game knows, so a client takes it from the request and never reads that count
            if (!host)
            {
                t.ModulesInOnePopup = a.Value == 1;
                return 0;
            }
            long available = t.Body.GetAvailableCountOffSpaceModule(sm);
            t.ModulesInOnePopup = MoveRules.PopupPath(available);
            a.Value = t.ModulesInOnePopup ? 1 : 0;
            if (!lifted) return RefuseCodes.NotAllowedHere;
            // UIRowFacility.CanDrag (D 165456-165480): not below the units in use, only a module that can be cargo
            if (sm.Enabled <= sm.MinEnabledQuantity && sm.MinEnabledQuantity != 0) return RefuseCodes.InUse;
            var smd = sm.facilityDescriptor as SpaceModuleDescriptor;
            if (smd == null || !smd.CanBeLoadAsCargo) return RefuseCodes.NotAllowedHere;
            FacilityBaseDescriptor added = Installed(smd, t.ToSite, t.ModulesInOnePopup) ?? smd;
            int count;
            int c2 = MoveRules.ModuleCount(a.Count, available, sm.Quantity, added.BlockStacking || smd.BlockStacking,
                out count);
            if (c2 == 0) a.Count = count;
            return c2;
        }

        // The facility an installation module turns into at the target, or null when it arrives as itself. The
        // count popup's path and the one-module path differ (D 124422-124436 and D 124463-124478): only the popup's
        // swaps in a module descriptor, and only the one-module path asks CanAddFacility before anything else.
        internal static FacilityBaseDescriptor Installed(SpaceModuleDescriptor smd, ObjectInfoData to, bool popup)
        {
            if (smd.specialAbilityFacilityNew != ESpecialAbilityFacilityNew.InstallationModule
                || smd.facilityOrModuleToInstall == null) return null;
            var module = smd.facilityOrModuleToInstall as SpaceModuleDescriptor;
            if (module != null) return popup ? module : null;
            return to.CanAddFacility(smd.facilityOrModuleToInstall) ? smd.facilityOrModuleToInstall : null;
        }

        // ---- the two target-list windows ----

        // The facility the list belongs to: by its item id, or by its type when only one of it stands here.
        static Facility ListOwner(BuildingTarget t, Func<Facility, bool> kind)
        {
            ActionRequest a = t.Applied;
            if (a.ItemId >= 0)
            {
                Facility f = FacilityById(t.Site, a.ItemId, a.Item);
                return f != null && kind(f) && f.facilityDescriptor != null && f.facilityDescriptor.ID == a.Item
                    ? f : null;
            }
            Facility found = null;
            if (t.Site.ListFacility != null)
                foreach (Facility f in t.Site.ListFacility)
                    if (f != null && !(f is NotYetPay) && kind(f) && f.facilityDescriptor != null
                        && f.facilityDescriptor.ID == a.Item)
                    {
                        if (found != null) return null;
                        found = f;
                    }
            return found;
        }

        // The action button opens the window for a finished facility whose action is enabled (D 134854-134858).
        // Whether it is working only gates opening the window, which then stays open for edits, so it is not asked.
        static bool WindowOpens(Facility f)
        {
            return f.FinishConstructionBool && f.HasCustomAction && f.IsCustomActionEnabled;
        }

        static int Energy(BuildingTarget t, bool host)
        {
            Facility f = ListOwner(t, x => x is IEnergyTransfer);
            if (f == null) return RefuseCodes.BadTarget;
            t.Facility = f;
            if (!host) return 0;
            t.Applied.ItemId = f.ID;   // every game edits the facility the host found, by its id
            if (!WindowOpens(f)) return RefuseCodes.NotAllowedHere;
            ObjectInfo body = f.ObjectInfoData.ObjectInfo;
            ObjectInfo parent = body.parentObjectInfo;
            // the game's own rows: an orbit's link to its body, a body's link to its orbit (D 133713-133733)
            var fixedTargets = new List<int>();
            foreach (var row in ((IEnergyTransfer)f).Targets)
            {
                ObjectInfo target = row != null && row.target != null ? row.target.Object : null;
                if (target == null) continue;
                if (TargetListRules.EnergyRowFixed(body.objectTypes == EObjectTypes.Orbit,
                    parent != null ? parent.id : 0, target.id, target.objectTypes == EObjectTypes.Orbit,
                    target.parentObjectInfo != null ? target.parentObjectInfo.id : 0, body.id))
                    fixedTargets.Add(target.id);
            }
            Company c = t.Company;
            return TargetListRules.EnergyProblem(t.Applied.Targets, fixedTargets, body.id,
                parent != null ? parent.id : 0, b => HasReceiver(GameAgencies.Site(GameAgencies.Body(b), c)));
        }

        static bool HasReceiver(ObjectInfoData site)
        {
            if (site == null || site.ListFacility == null) return false;
            foreach (Facility f in site.ListFacility) if (f is IEnergyReceiver) return true;
            return false;
        }

        static int Mirror(BuildingTarget t, bool host)
        {
            var f = ListOwner(t, x => x is MirrorFacility) as MirrorFacility;
            if (f == null) return RefuseCodes.BadTarget;
            t.Facility = f;
            if (!host) return 0;
            t.Applied.ItemId = f.ID;
            // a shade has no action (HasCustomAction is a positive parameter), so no window
            if (!f.FinishConstructionBool || !f.HasCustomAction) return RefuseCodes.NotAllowedHere;
            ObjectInfo body = f.ObjectInfoData.ObjectInfo;
            ObjectInfo parent = body.parentObjectInfo;
            Company c = t.Company;
            return TargetListRules.MirrorProblem(t.Applied.Targets, DefaultTargets(f), f.Quantity, body.id,
                parent != null ? parent.id : 0, b => MirrorTarget(GameAgencies.Body(b), c));
        }

        // The targets of the mirror's default rows (its own body's parent, made with the facility), in order; a row
        // whose body is gone is NoTarget.
        internal static List<int> DefaultTargets(MirrorFacility f)
        {
            var defaults = new List<int>();
            foreach (MirrorFacility.MirrorTargetInfo row in f.Targets)
            {
                if (row == null || !row.isDefault) continue;
                ObjectInfo target = row.target != null ? row.target.Object : null;
                defaults.Add(target != null ? target.id : TargetListRules.NoTarget);
            }
            return defaults;
        }

        // The mirror window's search filter (D 136287-136296) on a body the agency knows of.
        static bool MirrorTarget(ObjectInfo o, Company c)
        {
            if (o == null) return false;
            EObjectTypes k = o.objectTypes;
            if (k != EObjectTypes.Planet && k != EObjectTypes.Comet && k != EObjectTypes.Moons
                && k != EObjectTypes.Protoplanet && k != EObjectTypes.DwarfPlanet && k != EObjectTypes.Asteroid)
                return false;
            ObjectInfoData d = GameAgencies.Site(o, c);
            return d != null && d.IsDiscoveredOrNotAsteroid && !o.IsHideToDiscover;
        }
    }
}
