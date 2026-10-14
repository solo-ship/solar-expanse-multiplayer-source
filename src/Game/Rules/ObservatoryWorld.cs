using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using IdForObjectInfo = Game.Info.IdForObjectInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoManager = Manager.ObjectInfoManager;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using Facility = Game.ObjectInfoDataScripts.Facility;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // The observatory as the rules see it, apart from the patches: how an entry names one, the state an entry
    // compares after the apply, the groups' index, and the calls a client's apply makes to run the game's own held
    // methods (ObservatoryRule holds them everywhere else). No state of its own.
    public static class ObservatoryWorld
    {
        static readonly AccessTools.FieldRef<ObservatoryFacility, ObservatoryFacility.EWorkMode> ModeOf =
            AccessTools.FieldRefAccess<ObservatoryFacility, ObservatoryFacility.EWorkMode>("workMode");
        static readonly AccessTools.FieldRef<ObservatoryFacility, IdForObjectInfo> TargetOf =
            AccessTools.FieldRefAccess<ObservatoryFacility, IdForObjectInfo>("target");
        static readonly AccessTools.FieldRef<ObservatoryFacility, IdForObjectInfoGroups> GroupOf =
            AccessTools.FieldRefAccess<ObservatoryFacility, IdForObjectInfoGroups>("targetGroup");
        static readonly AccessTools.FieldRef<ObservatoryFacility, double> DaysOf =
            AccessTools.FieldRefAccess<ObservatoryFacility, double>("workingDaysToFinishWork");
        static readonly AccessTools.FieldRef<Spacecraft, List<Facility>> OnShip =
            AccessTools.FieldRefAccess<Spacecraft, List<Facility>>("facilityOnShip");
        static readonly AccessTools.FieldRef<Company, double> SurveyCost =
            AccessTools.FieldRefAccess<Company, double>("exoplanetCurrentSurveyCost");

        static readonly AccessTools.FieldRef<ObjectInfoManager, ObjectInfoManager.InGameChangeSystemData> Created =
            AccessTools.FieldRefAccess<ObjectInfoManager, ObjectInfoManager.InGameChangeSystemData>(
                "inGameChangeSystemData");

        static readonly MethodInfo StepMethod = Method("OnScheduledTimeOfResourceExploration");
        static readonly MethodInfo FoundMethod = Method("OnObjectDiscovered");
        static readonly MethodInfo ReplanMethod = Method("ScheduleNextExplorationStep");
        static readonly MethodInfo DestroyMethod = Method("ObjectInfoOnvirtualDestroy");
        static readonly MethodInfo ListenerMethod = Method("AnyResourceExploredByOtherProbeOrObservatory");
        static readonly MethodInfo UnexploredList = Method("GetListOfUnexploredResource");

        static MethodInfo Method(string name)
        {
            MethodInfo m = AccessTools.Method(typeof(ObservatoryFacility), name);
            if (m == null) throw new InvalidOperationException("the game has no ObservatoryFacility." + name);
            return m;
        }

        // How many deposits this observatory still has to examine (the game's own list); int.MaxValue with no target.
        public static int UnexploredCount(ObservatoryFacility o)
        {
            var list = UnexploredList.Invoke(o, null) as List<RowExploredResourcesData>;
            return ObservatoryRules.Unexplored(list == null ? -1 : list.Count);
        }

        // A deposit's position among its target's rows for this observatory's agency (-1 if it is not one of them).
        public static int RowPosition(ObservatoryFacility o, RowExploredResourcesData row)
        {
            ObjectInfo target = TargetBody(o);
            if (target == null || o.Company == null) return -1;
            ObjectInfoData data = target.GetObjectInfoData(o.Company);
            return data == null ? -1 : ObservatoryRules.PositionIn(data.listExploredResourcesRows, row);
        }

        // Every body of a generated system under the host's system name (the game's `<name><letter>` rule).
        public static void RenameBodies(Game.SolarSystemLoader.SolarSystemData data, string from, string to)
        {
            if (data == null || data.tabObjectInfoData == null) return;
            foreach (var o in data.tabObjectInfoData)
            {
                o.customName = SystemNames.Rename(o.customName, from, to);
                if (o.addNBodyData != null) o.addNBodyData.name = SystemNames.Rename(o.addNBodyData.name, from, to);
            }
        }

        // The first body of a system whose name is not under the system's own, or null.
        public static string BodyNameProblem(Game.SolarSystemLoader.SolarSystemData data, string system)
        {
            if (data == null || data.tabObjectInfoData == null) return null;
            foreach (var o in data.tabObjectInfoData)
            {
                if (SystemNames.Differs(o.customName, system)) return "a body is named " + o.customName;
                if (o.addNBodyData != null && SystemNames.Differs(o.addNBodyData.name, system))
                    return "a body is named " + o.addNBodyData.name;
            }
            return null;
        }

        public static int Mode(ObservatoryFacility o) { return (int)ModeOf(o); }

        public static ObjectInfo TargetBody(ObservatoryFacility o)
        {
            IdForObjectInfo t = TargetOf(o);
            return t != null ? t.Object : null;
        }

        // The objects the game made in play (a new system, a spawned asteroid): its own record of them.
        public static ObjectInfoManager.InGameChangeSystemData CreatedObjects(ObjectInfoManager m)
        {
            return Created(m);
        }

        public static double SurveyCostOf(Company c) { return SurveyCost(c); }

        public static void SetSurveyCost(Company c, double v) { SurveyCost(c) = v; }

        // ---- the groups: an index in the game's list, never the id (NEAR-EARTH OBJECTS and COMETS are both 0) ----

        public static int GroupIndex(ObjectInfoGroups g)
        {
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            return g == null || m == null ? -1 : m.allObjectInfosGroups.IndexOf(g);
        }

        public static ObjectInfoGroups GroupAt(int index)
        {
            ObjectInfoManager m = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (m == null || index < 0 || index >= m.allObjectInfosGroups.Count) return null;
            return m.allObjectInfosGroups[index];
        }

        // ---- the state an entry compares ----

        public static ObsAfter AfterOf(ObservatoryFacility o)
        {
            int mode = Mode(o);
            ObjectInfo target = TargetBody(o);
            IdForObjectInfoGroups g = GroupOf(o);
            return new ObsAfter
            {
                Mode = mode,
                Target = target != null ? target.id : -1,
                Group = GroupIndex(g != null ? g.Object : null),
                Days = ObservatoryRules.DaysAfter(mode, DaysOf(o))
            };
        }

        // ---- naming ----

        // How an entry names this observatory: a site facility by its site, descriptor and item id; a probe
        // observatory by its descriptor and the ship it rides in, with Body its own target (its item id changes at
        // every load and is never used). Null when it cannot be named (the caller logs and raises nothing).
        public static ActionRequest NameOf(ObservatoryFacility o, int kind)
        {
            Company company = o.Company;
            if (company == null || o.facilityDescriptor == null || string.IsNullOrEmpty(company.ID)) return null;
            var a = new ActionRequest { Kind = kind, Agency = company.ID, Item = o.facilityDescriptor.ID };
            ObjectInfoData site = o.ObjectInfoData;
            ObjectInfo target = TargetBody(o);
            if (o.facilityDescriptor.ObservatoryFacilityFromSC)
            {
                int ship = ShipOf(o);
                if (ship < 0) return null;
                a.Ship = ship;
                int siteBody = site != null && site.ObjectInfo != null ? site.ObjectInfo.id : 0;
                a.Body = target != null ? target.id : siteBody;
            }
            else
            {
                if (site == null || site.ObjectInfo == null) return null;
                a.Body = site.ObjectInfo.id;
                a.ItemId = o.ID;
            }
            return a.Body > 0 && !string.IsNullOrEmpty(a.Item) ? a : null;
        }

        // The id of the ship a probe observatory rides in, or -1.
        static int ShipOf(ObservatoryFacility o)
        {
            ShipManager ships = MonoBehaviourSingleton<ShipManager>.Instance;
            if (ships == null) return -1;
            foreach (Spacecraft sc in ships.ListAllSpaceShip)
            {
                if (sc == null) continue;
                List<Facility> list = OnShip(sc);
                if (list != null && list.Contains(o)) return sc.ID;
            }
            return -1;
        }

        // The observatory an entry names, or null: the same lookup on every game. The agency is the entry's wire id
        // (or the game's own id, which has the same wire form), matched to its company, never by comparing id texts.
        public static ObservatoryFacility Find(ActionRequest a)
        {
            if (a == null) return null;
            Company company = GameAgencies.Find(AgencyWire.From(a.Agency));
            if (company == null) return null;
            if (a.Ship >= 0) return FindProbe(company, a.Item, a.Ship);
            ObjectInfoManager objects = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (objects == null) return null;
            // Ids repeat at a site: a new observatory under construction can carry the starting one's id and type
            // until it stacks into it. The game's exploration window offers only finished, working observatories
            // (FillObservatoryLists, D 134201-134208: BuildProgress >= 1 and Enabled > 0), so a finished one wins.
            var found = new List<ObservatoryFacility>();
            var seen = new List<SiteItem>();
            foreach (ObjectInfo o in objects.allObjectInfos)
            {
                if (o == null || o.id != a.Body) continue;
                foreach (ObjectInfoData d in o.ObjectsInfoData)
                {
                    if (d == null || !ReferenceEquals(d.company, company)) continue;
                    foreach (Facility f in d.ListFacility)
                    {
                        var obs = f as ObservatoryFacility;
                        if (obs == null || obs.ID != a.ItemId || obs.facilityDescriptor == null
                            || obs.facilityDescriptor.ID != a.Item) continue;
                        found.Add(obs);
                        seen.Add(new SiteItem(obs.ID, a.Item, obs.FinishConstructionBool));
                    }
                }
            }
            int pick = SiteItems.Facility(seen, a.ItemId, a.Item);
            return pick >= 0 ? found[pick] : null;
        }

        static ObservatoryFacility FindProbe(Company company, string item, int ship)
        {
            ShipManager ships = MonoBehaviourSingleton<ShipManager>.Instance;
            if (ships == null) return null;
            foreach (Spacecraft sc in ships.ListAllSpaceShip)
            {
                if (sc == null || sc.ID != ship) continue;
                List<Facility> list = OnShip(sc);
                if (list == null) continue;
                foreach (Facility f in list)
                {
                    var obs = f as ObservatoryFacility;
                    if (obs != null && ReferenceEquals(obs.Company, company) && obs.facilityDescriptor != null
                        && obs.facilityDescriptor.ID == item) return obs;
                }
            }
            return null;
        }

        // A survey crossing is inside some observatory's MyUpdate; when that is not known (a call from elsewhere),
        // the agency's first site observatory working on the survey is the one the entry names.
        public static ObservatoryFacility SurveyingObservatory(Company company)
        {
            ObjectInfoManager objects = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (objects == null || company == null) return null;
            foreach (ObjectInfo o in objects.allObjectInfos)
            {
                if (o == null) continue;
                foreach (ObjectInfoData d in o.ObjectsInfoData)
                {
                    if (d == null || d.company != company) continue;
                    foreach (Facility f in d.ListFacility)
                    {
                        var obs = f as ObservatoryFacility;
                        if (obs != null && Mode(obs) == ObsMode.ExoplanetSurvey) return obs;
                    }
                }
            }
            return null;
        }

        // ---- what a client's apply runs: the game's own held methods, for exactly this observatory ----

        // 54: the examine step the host finished. The step runs even if this game's own timer is not yet due.
        public static string ApplyStep(ObservatoryFacility o) { return Run(o, StepMethod, null); }

        // 55: the asteroid the host's observatory found.
        public static string ApplyFound(ObservatoryFacility o) { return Run(o, FoundMethod, null); }

        // 57: a re-plan. toBody is the object the host's observatory re-planned for: one that was destroyed (the
        // game's own handler) or the examined body whose last deposit another source explored (the silent re-plan).
        public static string ApplyReplan(ObservatoryFacility o, ObjectInfo toBody)
        {
            if (toBody == null) return "the re-plan names no object";
            if (toBody.IsInGameDestroy) return Run(o, DestroyMethod, new object[] { toBody, null });
            return Run(o, ReplanMethod, new object[] { true });
        }

        // 56: the system the host's survey found. The agency's survey line is crossed with the game's own call, the
        // system is generated from the host's seed (if it is new to this game) and named as the host named it, and
        // its world object is made with the host's id. Null = done; else why the entry did not apply.
        public static string ApplySystemFound(Company company, int index, int seed, string name, int objectId)
        {
            RuleState st = HumanAgencies.State;
            if (st == null) return "no session";
            if (company == null) return "no such agency";
            if (company.DiscoveredSystemsCount != index)
                return company.ID + " has found " + company.DiscoveredSystemsCount + " systems, the entry is for index "
                    + index;
            var pending = new PendingSystem { Index = index, Seed = seed, Name = name, ObjectId = objectId,
                Given = true };
            st.Pending = pending;
            try
            {
                double power = ObservatoryRules.CrossingPower(SurveyCostOf(company));
                bool crossed = company.DecreaseExoplanetSurveyCost(power);
                if (!crossed || company.DiscoveredSystemsCount != index + 1) return "the survey did not cross its line";
                if (pending.Problem != null) return pending.Problem;
                // the generated bodies must carry the host's names (the check that catches a name that differed)
                var systems = MonoBehaviourSingleton<ObjectInfoManager>.Instance.SystemsToDiscover;
                if (name != null && index < systems.Count)
                    return BodyNameProblem(systems[index].SolarSystemData, name);
                return null;
            }
            catch (Exception e) { return "the survey threw " + e.GetType().Name; }
            finally { st.Pending = null; }
        }

        // The host's own run of a noted re-plan or a deferred listener, under the caller's record.
        public static string RunNote(ObservatoryFacility o, ReplanNote note)
        {
            if (note.Row != null) return Run(o, ListenerMethod, new object[] { note.Row });
            return Run(o, DestroyMethod, new object[] { note.Destroyed, null });
        }

        static string Run(ObservatoryFacility o, MethodInfo method, object[] args)
        {
            RuleState st = HumanAgencies.State;
            if (st == null) return "no session";
            if (o == null) return "no such observatory";
            if (st.Replanning != null) return "an observatory call inside another";
            st.Replanning = o;
            try { method.Invoke(o, args); return null; }
            catch (Exception e) { return method.Name + " threw " + (e.InnerException ?? e).GetType().Name; }
            finally { st.Replanning = null; }
        }

        // ---- the held listener ----

        // The listener a client must still drop when the row is explored, which is all the game's handler does there.
        public static void Unsubscribe(ObservatoryFacility o, RowExploredResourcesData row)
        {
            var handler = (Action<RowExploredResourcesData>)Delegate.CreateDelegate(
                typeof(Action<RowExploredResourcesData>), o, ListenerMethod);
            row.OnValueChange -= handler;
        }
    }
}
