using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;
using IdForObjectInfo = Game.Info.IdForObjectInfo;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using Offer = Game.ObjectInfoDataScripts.Offer;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using CompanyContractData = Game.ContractsObjectives.CompanyContractData;
using CompanyObjectiveData = Game.ContractsObjectives.CompanyObjectiveData;
using Contract = Game.ContractsObjectives.Contract;
using ResearchManager = Manager.ResearchManager;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchDataProgress = ScriptableObjectScripts.ResearchDataProgress;
using ResourceDefinitionIDSave = ScriptableObjectScripts.ResourceDefinitionIDSave;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using Spacecraft = CustomUpdate.Spacecraft;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using EObjectTypes = Data.EObjectTypes;
using HabitabilityParams = Data.ScriptableObject.Terraformation.TerraformationConfig.HabitabilityParametersNew;
using HabitabilityClass = Data.ScriptableObject.Terraformation.TerraformationConfig.HabitabilityClass;

namespace SolarExpanseMultiplayer
{
    // How many private fields were asked for and how many could not be found (this build's names; docs/research/state-accessors.md).
    internal static class StateFieldStats { public static int Total, Missing; }

    // A field read through a cached AccessTools.FieldRefAccess (no reflection per read, no allocation). A field that is not found
    // (or has another type) logs ONE error here and then reads as the type's default, so one renamed member cannot stop the whole
    // reader; the count of such fields is in the day-0 log line. M3 Task 6 (it replaces the spike's Rf).
    internal sealed class StateField<T, F> where T : class
    {
        readonly AccessTools.FieldRef<T, F> r;

        public StateField(string name)
        {
            StateFieldStats.Total++;
            try { r = AccessTools.FieldRefAccess<T, F>(name); }
            catch (Exception e)
            {
                StateFieldStats.Missing++;
                Log.Error("drift: field " + typeof(T).Name + "." + name + " not found or of another type: " + e.Message);
            }
        }

        public bool Ok { get { return r != null; } }
        public F Get(T o) { return r == null ? default(F) : r(o); }
        public void Set(T o, F v) { if (r != null) r(o) = v; }
    }

    // M3's state reader (Task 6): visits every area through one IItemSink, by fields. It reads private fields through cached
    // FieldRefAccess and never calls a getter with a side effect (state-accessors.md "Fields, not getters"), and it allocates
    // NOTHING per visit after Prepare (docs/research/gc.md):
    //   * every string handed to the sink is a string the game already holds (ids, names) or a literal; body names are cached in Prepare
    //     (TrueObjectName and Object.name build a string on every call);
    //   * rocket constructs come from the private List<SpacecraftConstructData> (GetListRocketConstruct() boxes its enumerator);
    //   * foreach only over concrete List<> / Dictionary<,> / Queue<>; no LINQ, no closures, no params, no enum keys, no logging;
    //   * game getters that allocate are not called: IsDiscoveredOrNotAsteroid (boxes an enum in Equals) and HabitabilityParametersNew.Result
    //     (builds an array) are done here on the same fields, with the same arithmetic.
    // Main thread only. Never throws: an area that fails logs ONE error per area per run and the visit goes on.
    public static partial class StateReader
    {
        // The private fields (built once, in Prepare).
        static partial class Fx
        {
            public static readonly StateField<Company, double> Survey = new StateField<Company, double>("exoplanetCurrentSurveyCost");
            public static readonly StateField<ResearchManager, List<ResearchDefinition>> Completed = new StateField<ResearchManager, List<ResearchDefinition>>("completedResearch");
            public static readonly StateField<ResearchManager, List<ResearchDataProgress>> Partial = new StateField<ResearchManager, List<ResearchDataProgress>>("startNotFinish");
            public static readonly StateField<ObjectInfoData, List<RowResourcesData>> Rows = new StateField<ObjectInfoData, List<RowResourcesData>>("listRowResourcesData");
            public static readonly StateField<ObjectInfoData, List<RowResourcesData>> RowsBefore = new StateField<ObjectInfoData, List<RowResourcesData>>("listRowResourceDataBeforeReduce");
            public static readonly StateField<ObjectInfoData, bool> WasReduce = new StateField<ObjectInfoData, bool>("wasReduceToSave");
            public static readonly StateField<ObjectInfoData, bool> WasUndo = new StateField<ObjectInfoData, bool>("wasUndoReduceToSave");
            public static readonly StateField<ObjectInfoData, bool> DiscoveredAsteroid = new StateField<ObjectInfoData, bool>("isDiscoveredAsteroid");
            public static readonly StateField<ObjectInfoData, List<ProductionQuery>> Query = new StateField<ObjectInfoData, List<ProductionQuery>>("listProductionQuery");
            public static readonly StateField<ObjectInfoData, List<SpacecraftConstructData>> Rocket = new StateField<ObjectInfoData, List<SpacecraftConstructData>>("listSpacecraftConstruct");
            public static readonly StateField<ObjectInfoData, long> CrewInWork = new StateField<ObjectInfoData, long>("crewValueInWork");
            public static readonly StateField<ProductionItem, float> BuildProgress = new StateField<ProductionItem, float>("buildProgress");
            public static readonly StateField<ProductionItem, List<float>> ProgressList = new StateField<ProductionItem, List<float>>("listBuildProgress");
            public static readonly StateField<ProductionItem, bool> Build = new StateField<ProductionItem, bool>("build");
            public static readonly StateField<ProductionItem, bool> StartBuild = new StateField<ProductionItem, bool>("startBuild");
            public static readonly StateField<ProductionItem, string> TypeId = new StateField<ProductionItem, string>("idProductionItemType");
            public static readonly StateField<Facility, long> Quantity = new StateField<Facility, long>("quantity");
            public static readonly StateField<Facility, long> Enabled = new StateField<Facility, long>("enabled");
            public static readonly StateField<ObservatoryFacility, ObservatoryFacility.EWorkMode> Mode = new StateField<ObservatoryFacility, ObservatoryFacility.EWorkMode>("workMode");
            public static readonly StateField<ObservatoryFacility, IdForObjectInfo> Target = new StateField<ObservatoryFacility, IdForObjectInfo>("target");
            public static readonly StateField<ObservatoryFacility, double> DaysToFinish = new StateField<ObservatoryFacility, double>("workingDaysToFinishWork");
            public static readonly StateField<ObjectInfo, HabitabilityParams> FinalHab = new StateField<ObjectInfo, HabitabilityParams>("finalHabitabilityParameters");
            public static readonly StateField<Spacecraft, DateTime> LaunchTime = new StateField<Spacecraft, DateTime>("launchTime");
            public static readonly StateField<Spacecraft, DateTime?> LandingTime = new StateField<Spacecraft, DateTime?>("landingTime");
            public static readonly StateField<Offer, string> OfferCompany = new StateField<Offer, string>("companyID");
            public static readonly StateField<Offer, ResourceDefinitionIDSave> OfferRd = new StateField<Offer, ResourceDefinitionIDSave>("rdTypeIDSave");
            // The private field behind Offer.WhereOfferID. The property exists only in the 0.26.10 beta (its getter returns this field);
            // the field is in both builds, so the field read lets one DLL compile and run on either (docs/research/stable-vs-beta.md).
            public static readonly StateField<Offer, int> OfferWhere = new StateField<Offer, int>("whereOfferID");
            public static readonly StateField<Manager.MarketOfferManager, Manager.MarketOfferManager.ResourceMarketPriceData> PriceData =
                new StateField<Manager.MarketOfferManager, Manager.MarketOfferManager.ResourceMarketPriceData>("resourceMarketPriceData");
            public static readonly StateField<CycleMissionManager, Dictionary<Spacecraft, CycleMissionsData>> Cyclical =
                new StateField<CycleMissionManager, Dictionary<Spacecraft, CycleMissionsData>>("SCtoCycleMissionsData");

            public static void Touch() { }   // forces the initialisers above to run now (in Prepare), not on the first read
        }

        // A body's two names, read once (see Prepare).
        sealed class BodyText { public string Name = "", Sub = ""; }

        static Company[] companies = new Company[0];       // active agencies (enabled or idle), sorted by Company.ID, ordinal
        static string[] ids = new string[0];
        static string skipped = "";
        static List<ObjectInfoData>[] sites = new List<ObjectInfoData>[0];   // per agency index: the sites (ObjectInfoData.company), rebuilt each visit
        static double[] stockSum = new double[0], crewSum = new double[0];
        static int habUncomputed, bodyCount, siteCount, areaFailures;
        static readonly bool[] failed = new bool[DriftAreas.All.Length];
        static readonly bool[] failedPseudo = new bool[2];   // the visit itself (-1) and the scalars (-2)
        static readonly Dictionary<int, BodyText> bodyText = new Dictionary<int, BodyText>(256);
        static HabitabilityClass habitability;              // the game's habitability curves (for Result), cached in Prepare
        static bool prepared;

        // Company.ID per agency index, sorted ordinal (active = enabled or idle). The array is the reader's own: do not change it.
        public static string[] Agencies { get { return ids; } }

        // The inactive agencies (neither enabled nor idle), comma separated; "" when none. For the record header.
        public static string Skipped { get { return skipped; } }

        public static bool IsPrepared { get { return prepared; } }
        // successful Prepare calls (who prepared when)
        public static int Prepares { get; private set; }
        public static int BodyCount { get { return bodyCount; } }          // as of the last Visit
        public static int SiteCount { get { return siteCount; } }
        public static int AreaFailures { get { return areaFailures; } }    // areas that threw at least once since Prepare
        public static int FieldsTotal { get { return StateFieldStats.Total; } }
        public static int FieldsMissing { get { return StateFieldStats.Missing; } }

        // At save loaded. Caches the field refs, the sorted agency list and each body's two names. May allocate.
        public static void Prepare()
        {
            prepared = false;
            try
            {
                Fx.Touch();
                List<Company> all = MonoBehaviourSingleton<Manager.GameManager>.Instance.Companies;
                var active = new List<Company>(16);
                var sk = new StringBuilder();
                for (int i = 0; i < all.Count; i++)
                {
                    Company c = all[i];
                    if (c == null || c.ID == null) continue;
                    // Idle agencies are active GameObjects (SetupPlayerAndRivalAIs): hashed. Only inactive ones are skipped and named.
                    if (!c.gameObject.activeSelf) { if (sk.Length > 0) sk.Append(','); sk.Append(c.ID); continue; }
                    active.Add(c);
                }
                active.Sort(CompareIds);
                companies = active.ToArray();
                ids = new string[companies.Length];
                sites = new List<ObjectInfoData>[companies.Length];
                for (int i = 0; i < companies.Length; i++) { ids[i] = companies[i].ID; sites[i] = new List<ObjectInfoData>(1024); }
                stockSum = new double[companies.Length];
                crewSum = new double[companies.Length];
                skipped = sk.ToString();

                bodyText.Clear();
                List<ObjectInfo> bodies = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos;
                for (int i = 0; i < bodies.Count; i++) if (bodies[i] != null) NamesOf(bodies[i]);
                habitability = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance.TerraformationConfig.Habitability;
                for (int i = 0; i < failed.Length; i++) failed[i] = false;
                failedPseudo[0] = false; failedPseudo[1] = false;
                areaFailures = 0;
                RebuildSites();
                prepared = true;
                Prepares++;
            }
            catch (Exception e) { Log.Error("drift: state reader could not prepare: " + e); }
        }

        static int CompareIds(Company a, Company b) { return string.CompareOrdinal(a.ID, b.ID); }

        // One line for the log (load time only).
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(companies.Length).Append(" active agencies (sorted by ID): ");
            for (int i = 0; i < companies.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ids[i]);
                if (companies[i].IsPlayer) sb.Append("[player]");
                if (companies[i].Definition != null && companies[i].Definition.IsWorldGovernment) sb.Append("[wg]");
                if (!companies[i].enabled) sb.Append("[idle or disabled]");
            }
            sb.Append("; skipped inactive: ").Append(skipped.Length == 0 ? "none" : skipped);
            sb.Append("; bodies ").Append(bodyCount).Append(", sites ").Append(siteCount);
            sb.Append("; private fields ").Append(StateFieldStats.Total - StateFieldStats.Missing).Append(" of ").Append(StateFieldStats.Total).Append(" found");
            return sb.ToString();
        }

        // Everything one day's read visits: the per-agency areas for each agency index, then the world areas.
        public static void Visit(IItemSink sink)
        {
            if (!prepared || sink == null) return;
            try
            {
                RebuildSites();
                habUncomputed = 0;
                for (int i = 0; i < companies.Length; i++) { stockSum[i] = 0.0; crewSum[i] = 0.0; }
                for (int i = 0; i < companies.Length; i++)
                    for (int a = 0; a < DriftAreas.PerAgencyCount; a++) Guarded(sink, a, i);
                for (int a = DriftAreas.PerAgencyCount; a < DriftAreas.All.Length; a++) Guarded(sink, a, DriftAreas.WorldAgency);
            }
            catch (Exception e) { Fail(-1, DriftAreas.WorldAgency, e); }
        }

        // The ScalarValue entries of the visit just made (per-agency: money, profit, research, crew, stock; world: offers, bodies,
        // habitability-uncomputed). Replaces whatever the target held.
        public static void Scalars(DaySnapshot target)
        {
            if (target == null) return;
            target.ScalarCount = 0;
            if (!prepared) return;
            try
            {
                for (int i = 0; i < companies.Length; i++)
                {
                    Company c = companies[i];
                    Game.CompanyScripts.MoneyController mc = c.MoneyController;
                    ResearchManager rm = c.ResearchManager;
                    ResearchDataProgress slot = rm != null ? rm.Slot1 : null;
                    AddScalar(target, i, DriftFields.ScalarMoney, mc != null ? mc.CurrentMoney : 0.0);
                    AddScalar(target, i, DriftFields.ScalarProfit, mc != null ? mc.TotalProfit : 0.0);
                    AddScalar(target, i, DriftFields.ScalarResearch, slot != null ? (double)slot.Progress : 0.0);
                    AddScalar(target, i, DriftFields.ScalarCrew, crewSum[i]);
                    AddScalar(target, i, DriftFields.ScalarStock, stockSum[i]);
                }
                Manager.MarketOfferManager mm = Manager.MarketOfferManager.Instance;
                AddScalar(target, DriftAreas.WorldAgency, DriftFields.ScalarOffers, mm != null && mm.Offerts != null ? mm.Offerts.Count : 0);
                AddScalar(target, DriftAreas.WorldAgency, DriftFields.ScalarBodies, bodyCount);
                AddScalar(target, DriftAreas.WorldAgency, DriftFields.ScalarHabitabilityUncomputed, habUncomputed);
            }
            catch (Exception e) { Fail(-2, DriftAreas.WorldAgency, e); }
        }

        // The checked append (AreaHasher.AddScalar, cloud review D-4): false for a bad agency or name index, or a full snapshot. That is one
        // ERROR per run through Fail (the exception exists only on that failure path), and the day is not trusted.
        static void AddScalar(DaySnapshot t, int agency, int name, double value)
        {
            if (AreaHasher.AddScalar(t, agency, companies.Length, name, value)) return;
            Fail(-2, agency, new InvalidOperationException("scalar " + name + " for agency index " + agency + " refused (bad index or the snapshot is full)"));
        }

        // ------------------------------------------------------------------ plumbing

        static void Guarded(IItemSink s, int area, int agency)
        {
            try
            {
                s.BeginBlock(area, agency);
                switch (area)
                {
                    case 0: Money(s, agency); break;
                    case 1: Research(s, agency); break;
                    case 2: Facilities(s, agency); break;
                    case 3: Production(s, agency); break;
                    case 4: Stock(s, agency); break;
                    case 5: Population(s, agency); break;
                    case 6: Exploration(s, agency); break;
                    case 7: Fleet(s, agency); break;
                    case 8: Missions(s, agency); break;
                    case 9: Contracts(s, agency); break;
                    case 10: Market(s); break;
                    case 11: Bodies(s); break;
                    case 12: Deposits(s); break;
                    case 13: Habitability(s); break;
                }
                s.EndBlock();
            }
            catch (Exception e)
            {
                try { s.EndBlock(); } catch (Exception) { }   // closes the half-built block so the visit can go on
                Fail(area, agency, e);
            }
        }

        // One ERROR per area per run (an allowed allocation); -1 = the visit itself, -2 = the scalars (each also once per run, reset in Prepare).
        static void Fail(int area, int agency, Exception e)
        {
            if (area >= 0)
            {
                if (failed[area]) return;
                failed[area] = true;
            }
            else
            {
                int p = -area - 1;   // -1 -> 0, -2 -> 1
                if (p < 0 || p >= failedPseudo.Length || failedPseudo[p]) return;
                failedPseudo[p] = true;
            }
            areaFailures++;
            string what = area >= 0 ? "area " + DriftAreas.All[area] : (area == -1 ? "visit" : "scalars");
            string who = agency >= 0 && agency < ids.Length ? ids[agency] : "-";
            Log.Error("drift: " + what + " (" + who + ") failed: " + e);
        }

        // The sites of each agency (ObjectInfoData.company), in the order of allObjectInfos: the lists are cleared and refilled
        // without allocating (their capacity is set in Prepare); agencies are matched by reference.
        static void RebuildSites()
        {
            for (int i = 0; i < sites.Length; i++) sites[i].Clear();
            int bodies = 0, count = 0;
            List<ObjectInfo> all = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos;
            for (int i = 0; i < all.Count; i++)
            {
                ObjectInfo oi = all[i];
                if (oi == null) continue;
                bodies++;
                List<ObjectInfoData> od = oi.ObjectsInfoData;
                for (int j = 0; j < od.Count; j++)
                {
                    ObjectInfoData d = od[j];
                    if (d == null) continue;
                    Company dc = d.company;
                    for (int k = 0; k < companies.Length; k++)
                        if (ReferenceEquals(companies[k], dc)) { sites[k].Add(d); count++; break; }
                }
            }
            bodyCount = bodies; siteCount = count;
        }

        // A body's cached names; a body first seen after Prepare (a new asteroid) is read once, here (an allowed allocation).
        static BodyText NamesOf(ObjectInfo oi)
        {
            BodyText t;
            if (!bodyText.TryGetValue(oi.id, out t))
            {
                t = new BodyText();
                try { t.Name = oi.TrueObjectName ?? ""; } catch (Exception) { }
                try
                {
                    var sub = oi.objectSubType;
                    var fallback = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                        .DefaultObjectSubType;
                    t.Sub = sub != null ? BodySubtype.Text(sub.name, ReferenceEquals(sub, fallback)) : "";
                }
                catch (Exception) { }
                bodyText[oi.id] = t;
            }
            return t;
        }

        // ProductionItem.BuildProgress is a getter that writes (it fills listBuildProgress); this is its logic without the writes.
        static float Progress(ProductionItem p)
        {
            float bp = Fx.BuildProgress.Get(p);
            List<float> list = Fx.ProgressList.Get(p);
            if (p.CountToBuild == 0) return list != null && list.Count == 1 ? list[0] : bp;
            if (Fx.Build.Get(p)) return 1f;
            // The getter first pads its list (an empty or null list gets buildProgress, then zeros up to CountBuild) and then scans it.
            // Mirrored on a virtual list of the same length, with no write: entries past the real ones are 0f (the first is bp when empty).
            int cb = p.CountBuild;
            int n = list == null ? 0 : list.Count;
            int total = n == 0 ? 1 : n;
            if (total < cb) total = cb;
            for (int i = 0; i < total; i++)
            {
                float x = i < n ? list[i] : (i == 0 ? bp : 0f);
                if (!(x >= 1f)) return x;
            }
            // Every entry is complete: the getter returns the entry at countBuild when there is one (only a real one can get here), else bp.
            return cb < n ? list[cb] : bp;
        }

        // ProductionItem.ProductionItemType writes a cache and, for a Facility, fills idProductionItemType lazily: use the descriptor.
        static string TypeOf(ProductionItem p)
        {
            Facility f = p as Facility;
            if (f != null) return f.facilityDescriptor != null ? f.facilityDescriptor.ID : "";
            return Fx.TypeId.Get(p) ?? "";
        }

        // ObjectInfoData.ListRowResourcesData writes on first use after a save: read the list the getter would return, without running it.
        static List<RowResourcesData> StockList(ObjectInfoData d)
        {
            if (Fx.WasReduce.Get(d) && !Fx.WasUndo.Get(d))
            {
                List<RowResourcesData> before = Fx.RowsBefore.Get(d);
                if (before != null) return before;
            }
            return Fx.Rows.Get(d);
        }

        static long Ticks(DateTime? d) { return d.HasValue ? d.Value.Ticks : long.MinValue; }

        // ------------------------------------------------------------------ the areas (per agency)

        static void Money(IItemSink s, int ai)
        {
            Company c = companies[ai];
            Game.CompanyScripts.MoneyController mc = c.MoneyController;
            if (mc != null)
            {
                s.BeginItem(); s.Key("money");
                s.Val(DriftFields.CurrentMoney, mc.CurrentMoney); s.Val(DriftFields.TotalProfit, mc.TotalProfit);
                s.Val(DriftFields.ExoplanetCurrentSurveyCost, Fx.Survey.Get(c));
                s.Val(DriftFields.DiscoveredSystemsCount, (long)c.DiscoveredSystemsCount); s.Val(DriftFields.ProductionCountID, (long)c.productionCountID);
                s.EndItem();
            }
            Dictionary<string, int> cs = c.CountSN;
            if (cs != null)
                foreach (KeyValuePair<string, int> kv in cs) { s.BeginItem(); s.Key(kv.Key); s.Val(DriftFields.Count, (long)kv.Value); s.EndItem(); }
        }

        static void Research(IItemSink s, int ai)
        {
            Company c = companies[ai];
            ResearchManager rm = c.ResearchManager;
            if (rm == null) return;
            List<ResearchDefinition> done = Fx.Completed.Get(rm);
            if (done != null)
                for (int i = 0; i < done.Count; i++) { s.BeginItem(); s.Key("done"); s.Key(done[i] != null ? done[i].ID : ""); s.EndItem(); }
            ResearchDataProgress sl = rm.Slot1;
            s.BeginItem(); s.Key("slot");
            s.Val(DriftFields.Id, sl != null && sl.ResearchDefinition != null ? sl.ResearchDefinition.ID : "");
            s.Val(DriftFields.Progress, sl != null ? (double)sl.Progress : 0.0); s.Val(DriftFields.Complete, sl != null && sl.Complete);
            s.EndItem();
            long pos = 0;
            foreach (ResearchDefinition q in rm.queueRD) { s.BeginItem(); s.Key("queue"); s.Key(pos++); s.Val(DriftFields.Id, q != null ? q.ID : ""); s.EndItem(); }
            List<ResearchDataProgress> part = Fx.Partial.Get(rm);
            if (part != null)
                for (int i = 0; i < part.Count; i++)
                {
                    ResearchDataProgress p = part[i];
                    if (p == null) continue;
                    s.BeginItem(); s.Key("partial"); s.Key(p.ResearchDefinition != null ? p.ResearchDefinition.ID : ""); s.Val(DriftFields.Progress, (double)p.Progress); s.EndItem();
                }
            s.BeginItem(); s.Key("observatory-bonus"); s.Val(DriftFields.BonusFromObservatory, (double)rm.BonusFromObservatory); s.EndItem();
        }

        static void Facilities(IItemSink s, int ai)
        {
            List<ObjectInfoData> st = sites[ai];
            for (int i = 0; i < st.Count; i++)
            {
                ObjectInfoData d = st[i];
                List<Facility> lf = d.ListFacility;
                if (lf == null) continue;
                for (int k = 0; k < lf.Count; k++)
                {
                    Facility f = lf[k];
                    if (f == null) continue;
                    s.BeginItem(); s.Key((long)d.id); s.Key((long)f.ID);
                    s.Val(DriftFields.Descriptor, f.facilityDescriptor != null ? f.facilityDescriptor.ID : "");
                    s.Val(DriftFields.Quantity, Fx.Quantity.Get(f)); s.Val(DriftFields.Enabled, Fx.Enabled.Get(f));
                    s.Val(DriftFields.BuildProgress, (double)Progress(f));
                    s.Val(DriftFields.CountToBuild, (long)f.CountToBuild); s.Val(DriftFields.CountBuild, (long)f.CountBuild); s.Val(DriftFields.StartBuild, Fx.StartBuild.Get(f));
                    ObservatoryFacility ob = f as ObservatoryFacility;
                    if (ob != null)
                    {
                        IdForObjectInfo tg = Fx.Target.Get(ob);
                        s.Val(DriftFields.Mode, (long)(int)Fx.Mode.Get(ob)); s.Val(DriftFields.DaysToFinish, Fx.DaysToFinish.Get(ob));
                        s.Val(DriftFields.Target, tg != null ? (long)tg.ID : -1L);
                    }
                    s.EndItem();
                }
            }
        }

        static void Production(IItemSink s, int ai)
        {
            List<ObjectInfoData> st = sites[ai];
            for (int i = 0; i < st.Count; i++)
            {
                ObjectInfoData d = st[i];
                List<ProductionItem> pis = d.ProductionItem;
                if (pis != null)
                    for (int k = 0; k < pis.Count; k++)
                    {
                        ProductionItem p = pis[k];
                        if (p == null) continue;
                        Facility f = p as Facility;
                        var r = new ProductionRow
                        {
                            Body = d.id, Id = p.ID, Type = TypeOf(p), Progress = Progress(p),
                            CountToBuild = p.CountToBuild, CountBuild = p.CountBuild, StartBuild = Fx.StartBuild.Get(p),
                            IsFacility = f != null, InFacilityList = f != null && Listed(d.ListFacility, f)
                        };
                        ProductionPrint.Write(s, ref r);
                    }
                List<ProductionQuery> q = Fx.Query.Get(d);
                if (q != null)
                    for (int k = 0; k < q.Count; k++)
                    {
                        ProductionQuery e = q[k];
                        if (e == null) continue;
                        s.BeginItem(); s.Key((long)d.id); s.Key("q"); s.Key((long)k);
                        s.Val(DriftFields.Item, e.MyIDScriptableObjectProductionItem != null ? e.MyIDScriptableObjectProductionItem.ID : ""); s.Val(DriftFields.Count, (long)e.countToBuild);
                        s.EndItem();
                    }
                List<SpacecraftConstructData> rc = Fx.Rocket.Get(d);   // the private list: GetListRocketConstruct() returns it as IEnumerable
                if (rc != null)
                    for (int k = 0; k < rc.Count; k++)
                    {
                        SpacecraftConstructData r = rc[k];
                        if (r == null) continue;
                        s.BeginItem(); s.Key((long)d.id); s.Key("rc"); s.Key((long)r.ID);
                        s.Val(DriftFields.Name, r.SpaceCraftName ?? r.RocketGiveName ?? ""); s.Val(DriftFields.BuildProgress, (double)Progress(r));
                        s.Val(DriftFields.CountToBuild, (long)r.CountToBuild); s.Val(DriftFields.CountBuild, (long)r.CountBuild);
                        s.EndItem();
                    }
            }
        }

        // Every row of the list in use, zero rows included, no value filter.
        static void Stock(IItemSink s, int ai)
        {
            List<ObjectInfoData> st = sites[ai];
            double sum = 0.0;
            for (int i = 0; i < st.Count; i++)
            {
                ObjectInfoData d = st[i];
                List<RowResourcesData> rows = StockList(d);
                if (rows == null) continue;
                for (int k = 0; k < rows.Count; k++)
                {
                    RowResourcesData r = rows[k];
                    if (r == null) continue;
                    s.BeginItem(); s.Key((long)d.id); s.Key(r.resourceTypeIDSave != null ? r.resourceTypeIDSave.id : ""); s.Key((long)(int)r.ResourceState);
                    double v = r.Value;
                    s.Val(DriftFields.Value, v); s.Val(DriftFields.Balance, r.Balance);
                    s.EndItem();
                    if (!double.IsNaN(v) && !double.IsInfinity(v)) sum += v;
                }
            }
            stockSum[ai] = sum;
        }

        static void Population(IItemSink s, int ai)
        {
            List<ObjectInfoData> st = sites[ai];
            double crewTotal = 0.0;
            for (int i = 0; i < st.Count; i++)
            {
                ObjectInfoData d = st[i];
                RowResourcesData cr = d.CrewResource;
                long crew = cr != null ? (long)cr.Value : 0L;
                long inWork = Fx.CrewInWork.Get(d);
                crewTotal += crew;
                if (crew == 0 && inWork == 0) continue;
                s.BeginItem(); s.Key((long)d.id); s.Val(DriftFields.Crew, crew); s.Val(DriftFields.CrewInWork, inWork); s.EndItem();
            }
            crewSum[ai] = crewTotal;
        }

        static void Exploration(IItemSink s, int ai)
        {
            List<ObjectInfoData> st = sites[ai];
            for (int i = 0; i < st.Count; i++)
            {
                ObjectInfoData d = st[i];
                // IsDiscoveredOrNotAsteroid is "isDiscoveredAsteroid || !objectTypes.Equals(Asteroid)": Enum.Equals boxes, so the same logic on the field.
                ObjectInfo oi = d.ObjectInfo;
                bool discovered = Fx.DiscoveredAsteroid.Get(d) || (oi != null && oi.objectTypes != EObjectTypes.Asteroid);
                s.BeginItem(); s.Key((long)d.id); s.Val(DriftFields.Discovered, discovered); s.Val(DriftFields.BeingDiscovered, d.IsBeingCurrentlyDiscovered); s.EndItem();
                List<RowExploredResourcesData> rows = d.listExploredResourcesRows;
                if (rows == null) continue;
                for (int k = 0; k < rows.Count; k++)
                {
                    RowExploredResourcesData r = rows[k];
                    if (r == null) continue;
                    s.BeginItem(); s.Key((long)d.id); s.Key(r.ResourceType != null ? r.ResourceType.ID : "");
                    s.Val(DriftFields.Value, r.Value); s.Val(DriftFields.Preliminary, r.PreliminaryExplored); s.Val(DriftFields.Full, r.ExploredFullyOrPreliminary); s.Val(DriftFields.Selected, r.SelectedToMine);
                    s.EndItem();
                }
            }
        }

        static void Fleet(IItemSink s, int ai)
        {
            Company c = companies[ai];
            List<Spacecraft> ships = ShipManager.Instance.ListAllSpaceShip;
            for (int i = 0; i < ships.Count; i++)
            {
                Spacecraft sc = ships[i];
                if (sc == null || !ReferenceEquals(sc.GetCompany(), c)) continue;
                var r = new ShipRow
                {
                    Agency = ids[ai], Id = sc.ID, Type = sc.spacecraftType != null ? sc.spacecraftType.ID : "",
                    Name = sc.spacecraftName ?? "", Phase = (long)(int)sc.CurrentPhase,
                    OnObject = IdOf(sc.CurrentlyOnThisObject),
                    TrajStart = -1L, TrajEnd = -1L, TrajStartDate = long.MinValue, TrajEndDate = long.MinValue,
                    LaunchTicks = Fx.LaunchTime.Get(sc).Ticks, LandingTicks = Ticks(Fx.LandingTime.Get(sc))
                };
                Landing(sc, ref r);
                Game.VisualizationScripts.TrajectoryObject tr = sc.TrajectoryObject;
                if (tr != null)
                {
                    r.TrajStart = IdOf(tr.StartObjectInfo); r.TrajEnd = IdOf(tr.EndObjectInfo);
                    r.TrajStartDate = tr.StartDate.Ticks; r.TrajEndDate = tr.EndDate.Ticks;
                }
                long cargoCount = 0, crew = 0; double mass = 0.0;
                Game.ObjectInfoDataScripts.CargoAll ca = sc.CargoAll;
                if (ca != null && ca.listCargo != null)
                    for (int k = 0; k < ca.listCargo.Count; k++)
                    {
                        Game.ObjectInfoDataScripts.Cargo cg = ca.listCargo[k];
                        if (cg == null) continue;
                        cargoCount++; mass += cg.cargoMass; if (cg.crew) crew += cg.crewValue;
                    }
                r.CargoCount = cargoCount; r.CargoMass = mass; r.Crew = crew;
                FleetPrint.Write(s, ref r);
            }
            List<ObjectInfoData> st = sites[ai];
            for (int i = 0; i < st.Count; i++)
            {
                ObjectInfo oi = st[i].ObjectInfo;
                if (oi == null) continue;
                List<LaunchVehicle> lvs = oi.ListLaunchVehicle;
                if (lvs == null) continue;
                for (int k = 0; k < lvs.Count; k++)
                {
                    LaunchVehicle lv = lvs[k];
                    if (lv == null || !ReferenceEquals(lv.company, c)) continue;
                    s.BeginItem(); s.Key("lv"); s.Key((long)lv.ID);
                    s.Val(DriftFields.Type, lv.launchVehicleType != null ? lv.launchVehicleType.ID : ""); s.Val(DriftFields.Body, lv.objectInfo != null ? (long)lv.objectInfo.id : -1L);
                    s.Date(DriftFields.LaunchTime, lv.launchTime.HasValue ? lv.launchTime.Value.Ticks : long.MinValue);
                    s.EndItem();
                }
            }
        }

        static void Missions(IItemSink s, int ai)
        {
            Company c = companies[ai];
            List<MissionInfo> list = Manager.MissionInfoManager.Instance.ListMissionInfo;
            for (int i = 0; i < list.Count; i++)
            {
                MissionInfo m = list[i];
                if (m == null || !ReferenceEquals(m.company, c)) continue;
                // Only what a save keeps the same, since a joiner's world is the host's save: a finished mission is
                // left out (the save drops all but each agency's newest 50, and a landed one's trajectory ends lose
                // their bodies on the game that flew it but not on one that loaded it), and so is deltaV (never saved:
                // 0 after a load for a planned flight, recomputed for a rival's).
                if (m.complete || m.cancel) continue;
                s.BeginItem(); s.Key((long)m.id);
                s.Val(DriftFields.Name, m.missionName ?? "");
                s.Date(DriftFields.Launch, m.DateLaunch.Ticks); s.Date(DriftFields.Arrive, m.DateArrive.Ticks);
                Game.VisualizationScripts.TrajectoryObject tr = m.trajectoryObject;
                ObjectInfo a = tr != null ? tr.StartObjectInfo : null, b = tr != null ? tr.EndObjectInfo : null;
                s.Val(DriftFields.Start, a != null ? (long)a.id : -1L); s.Val(DriftFields.Target, b != null ? (long)b.id : -1L);
                s.Val(DriftFields.Complete, m.complete); s.Val(DriftFields.Cancel, m.cancel); s.Val(DriftFields.Landed, m.wasLand);
                s.Val(DriftFields.CostFuel, m.costFuel);
                s.EndItem();
            }
            // Cyclical missions, from the manager's private dictionary (the Spacecraft.CycleMissionsData getter resets state). DriftFields has no
            // a / b / pause names, so the entry uses start, target and state (0 or 1 for pause). Its trip count is left
            // out: a load never sets it back (CycleMissionsData's load restores only a "this many times" route's
            // limit), so a joiner's is 0 where the host's has counted.
            CycleMissionManager cm = CycleMissionManager.Instance;
            if (cm == null) return;
            Dictionary<Spacecraft, CycleMissionsData> cyc = Fx.Cyclical.Get(cm);
            if (cyc != null)
                foreach (KeyValuePair<Spacecraft, CycleMissionsData> kv in cyc)
                {
                    CycleMissionsData cd = kv.Value;
                    if (cd == null || !ReferenceEquals(cd.Company, c) || kv.Key == null) continue;
                    s.BeginItem(); s.Key("cyc"); s.Key((long)kv.Key.ID);
                    s.Val(DriftFields.Start, cd.A != null ? (long)cd.A.id : -1L); s.Val(DriftFields.Target, cd.B != null ? (long)cd.B.id : -1L);
                    s.Val(DriftFields.State, cd.Pause ? 1L : 0L);
                    s.EndItem();
                }
        }

        static void Contracts(IItemSink s, int ai)
        {
            Company c = companies[ai];
            List<Contract> all = Manager.ContractManager.Instance.allContracts;
            for (int i = 0; i < all.Count; i++)
            {
                Contract ct = all[i];
                if (ct == null || ct.ContractDefinition == null) continue;
                CompanyContractData cd;
                if (!ct.PerCompanyContractData.TryGetValue(c, out cd) || cd == null) continue;
                string id = ct.ContractDefinition.ID;
                s.BeginItem(); s.Key(id);
                s.Val(DriftFields.State, (long)(int)cd.currentState); s.Val(DriftFields.PerCompanyState, (long)(int)ct.ContractStateForCompany(c)); s.Date(DriftFields.Start, cd.startContract.Ticks);
                s.EndItem();
                List<CompanyObjectiveData> ol = cd.ObjectivesDataList;
                if (ol == null) continue;
                for (int k = 0; k < ol.Count; k++)
                {
                    CompanyObjectiveData od = ol[k];
                    if (od == null) continue;
                    s.BeginItem(); s.Key(id); s.Key((long)k); s.Val(DriftFields.Complete, od.IsCompleteUI); s.Val(DriftFields.HowMuchCurrent, od.howMuchCurrent); s.EndItem();
                }
            }
        }

        // ------------------------------------------------------------------ the world areas

        static void Market(IItemSink s)
        {
            List<Offer> offers = Manager.MarketOfferManager.Instance.Offerts;
            for (int i = 0; i < offers.Count; i++)
            {
                Offer o = offers[i];
                // a done offer leaves the list one real second after its full fill, on each game's own clock: not printed
                if (o == null || o.OfferDone) continue;
                ResourceDefinitionIDSave rd = Fx.OfferRd.Get(o);
                s.BeginItem(); s.Key((long)o.ID);
                s.Val(DriftFields.Company, Fx.OfferCompany.Get(o) ?? ""); s.Val(DriftFields.Resource, rd != null ? rd.id : ""); s.Val(DriftFields.BuySell, o.BuySell);
                s.Val(DriftFields.Price, o.PricePerUnit); s.Val(DriftFields.Amount, o.CountToBuySell); s.Val(DriftFields.Completed, o.CountCompleted);
                s.Date(DriftFields.Start, o.OfferStartDate.Ticks); s.Val(DriftFields.Where, (long)Fx.OfferWhere.Get(o)); s.Val(DriftFields.Done, o.OfferDone);
                s.EndItem();
            }
            // GetMarketClearingPrice adds an entry when one is missing: read the list.
            Manager.MarketOfferManager.ResourceMarketPriceData pd = Fx.PriceData.Get(Manager.MarketOfferManager.Instance);
            if (pd != null && pd.resourceMarketPriceDataList != null)
                for (int i = 0; i < pd.resourceMarketPriceDataList.Count; i++)
                {
                    Manager.MarketOfferManager.ResourceMarketPriceDataPerObjectPerRD e = pd.resourceMarketPriceDataList[i];
                    if (e == null || (IsLazyDefault(e) && e.summedPreviousTradesQuantity != null && e.summedPreviousTradesQuantity.Count == 0)) continue;
                    s.BeginItem(); s.Key("price"); s.Key((long)e.oiID); s.Key(e.rd != null ? e.rd.ID : ""); s.Val(DriftFields.Value, e.marketClearingPrice); s.EndItem();
                }
        }

        // GetMarketClearingPrice makes an entry with the default price when it reads a price nobody has traded at (opening
        // the offer board does), on that game alone. An entry still holding that default with no trade is not state, so it
        // is not printed (M7 DD-W9, C2). Same arithmetic as GetResourceMarketPriceDataPerObjectPerRD (D 83258-83286).
        static bool IsLazyDefault(Manager.MarketOfferManager.ResourceMarketPriceDataPerObjectPerRD e)
        {
            if (e.rd == null) return false;
            Company player = MonoBehaviourSingleton<Manager.GameManager>.Instance.Player;
            if (player == null || player.mainObjectInfo == null) return false;
            double expected = e.rd.MarketClearingPriceBase;
            if (e.oiID != player.mainObjectInfo.id)
            {
                ObjectInfo other = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.GetByID(e.oiID);
                if (other == null) return false;
                double dist = other.DistanceToSunInAU;
                expected = expected * (1.0 + Math.Abs(player.mainObjectInfo.DistanceToSunInAU - dist) * dist)
                    * (double)MonoBehaviourSingleton<Manager.MarketOfferManager>.Instance.marketConfig.MarketBaseFromSunDistanceParameter
                    * other.MarketBaseFromSunDistanceParameter;
            }
            return e.marketClearingPrice == expected;
        }

        static void Bodies(IItemSink s)
        {
            List<ObjectInfo> all = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos;
            for (int i = 0; i < all.Count; i++)
            {
                ObjectInfo oi = all[i];
                if (oi == null) continue;
                BodyText t = NamesOf(oi);
                s.BeginItem(); s.Key((long)oi.id);
                s.Val(DriftFields.Type, (long)(int)oi.objectTypes); s.Val(DriftFields.Subtype, t.Sub); s.Val(DriftFields.TrueName, t.Name);
                s.Val(DriftFields.Destroyed, oi.IsInGameDestroy); s.Val(DriftFields.NbodyDeleted, oi.NBodyDelete); s.Val(DriftFields.PushedAsteroid, oi.AsteroidFromPush);
                s.EndItem();
            }
        }

        static void Deposits(IItemSink s)
        {
            List<ObjectInfo> all = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos;
            for (int i = 0; i < all.Count; i++)
            {
                ObjectInfo oi = all[i];
                if (oi == null) continue;
                List<RowResourcesData> rows = oi.ListRowResourcesData;   // a body's own rows: a plain field getter
                if (rows == null) continue;
                for (int k = 0; k < rows.Count; k++)
                {
                    RowResourcesData r = rows[k];
                    if (r == null) continue;
                    float? mf = r.MiningFactor;
                    s.BeginItem(); s.Key((long)oi.id); s.Key(r.resourceTypeIDSave != null ? r.resourceTypeIDSave.id : ""); s.Key((long)(int)r.ResourceState);
                    s.Val(DriftFields.Value, r.Value); s.Val(DriftFields.MiningFactor, mf.HasValue ? (double)mf.Value : -1.0);
                    s.EndItem();
                }
            }
        }

        // Every body is an item and its key feeds Discrete; the six values only when finalHabitabilityParameters is set (null = "not
        // computed yet": the getter would compute and cache it, and when that happens depends on whatever last read it).
        static void Habitability(IItemSink s)
        {
            List<ObjectInfo> all = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos;
            for (int i = 0; i < all.Count; i++)
            {
                ObjectInfo oi = all[i];
                if (oi == null) continue;
                HabitabilityParams h = Fx.FinalHab.Get(oi);
                s.BeginItem(); s.Key((long)oi.id);
                if (h != null)
                {
                    s.Val(DriftFields.Temperature, h.temperature); s.Val(DriftFields.Pressure, h.pressure); s.Val(DriftFields.Composition, h.composition);
                    s.Val(DriftFields.Gravity, h.gravity); s.Val(DriftFields.Radiation, h.radiation);
                    s.Val(DriftFields.Result, HabitabilityResult(h));
                }
                else habUncomputed++;
                s.EndItem();
            }
        }

        // HabitabilityParametersNew.Result, line for line (build acs-d7cc4b107d686b46), without its two allocations (a 6-element
        // array of structs per call). The curves are the game's own, cached in Prepare. NaN when the curves are missing or throw.
        static double HabitabilityResult(HabitabilityParams h)
        {
            HabitabilityClass hc = habitability;
            if (hc == null) return double.NaN;
            try
            {
                double sum = 0.0, weights = 0.0, w;
                w = hc.temperature.weight; sum += (double)hc.temperature.curve.Evaluate((float)h.temperature) * w; weights += w;
                w = hc.composition.weight; sum += (double)hc.composition.curve.Evaluate((float)h.composition) * w; weights += w;
                w = hc.pressure.weight; sum += (double)hc.pressure.curve.Evaluate((float)h.pressure) * w; weights += w;
                w = hc.gravity.weight; sum += (double)hc.gravity.curve.Evaluate((float)h.gravity) * w; weights += w;
                w = hc.water.weight; sum += (double)hc.water.curve.Evaluate((float)h.water) * w; weights += w;
                w = hc.magnetosphere.weight; sum += (double)hc.magnetosphere.curve.Evaluate((float)h.radiation) * w; weights += w;
                double result = sum / weights;
                double pcSum = 0.0;
                int pcCount = 0;
                if (!NearOne(h.extremeVolcanism)) { pcSum += h.extremeVolcanism; pcCount++; }
                if (!NearOne(h.environmentalToxicity)) { pcSum += h.environmentalToxicity; pcCount++; }
                if (!NearOne(h.cryoVolcanism)) { pcSum += h.cryoVolcanism; pcCount++; }
                if (!NearOne(h.hydroCarbonLakes)) { pcSum += h.hydroCarbonLakes; pcCount++; }
                if (pcCount > 0) result += (pcSum / pcCount - 1.0) * 200.0;
                if (result < -100.0) result = -100.0; else if (result > 100.0) result = 100.0;   // Mathd.Clamp(v, -100, 100): NaN stays NaN
                return result;
            }
            catch (Exception) { return double.NaN; }
        }

        static bool NearOne(double v) { return Math.Abs(1.0 - v) < 1.0000000116860974E-07; }
    }
}
