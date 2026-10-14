using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using EObjectTypes = Data.EObjectTypes;
using EUnlockUIElement = ScriptableObjectScripts.EUnlockUIElement;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoManager = Manager.ObjectInfoManager;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SubObjectInfo = Game.Info.SubObjectInfo;

namespace SolarExpanseMultiplayer
{
    // What one O1-O5 request resolved to: the observatory and, for O1 and O2, the body or group it is ordered to.
    sealed class ObservatoryOrder
    {
        public ObservatoryFacility Observatory;
        public ObjectInfo Body;
        public ObjectInfoGroups Group;
    }

    // The exploration window's world for the ACTING agency (O1-O5): its observatory found by the entry's own lookup
    // (ObservatoryWorld.Find, with the game's agency id: the request carries the wire id), and the bodies and groups
    // read from the agency's own data, never the local player's map. No state.
    static class ObservatoryOrders
    {
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            AccessTools.FieldRefAccess<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<Facility, long> Enabled =
            AccessTools.FieldRefAccess<Facility, long>("enabled");

        // The observatory the request names, looked up as every game looks it up; null when there is none.
        public static ObservatoryFacility Find(ActionRequest a, Company c)
        {
            ActionRequest named = c != null ? ObservatoryCommand.WithGameAgency(a, c.ID) : null;
            return named != null ? ObservatoryWorld.Find(named) : null;
        }

        public static ObservatoryFacts Facts(ActionRequest a, Company c, ObservatoryFacility o)
        {
            var f = new ObservatoryFacts { Probe = a.Ship >= 0 };
            if (o == null) return f;
            f.Found = true;
            f.Finished = Built(o);
            f.SwitchedOn = Enabled(o) > 0;
            f.Mode = ObservatoryWorld.Mode(o);
            f.SurveyUnlocked = c.IsUnlockUIElement(EUnlockUIElement.ExoplanetSurveyResearchTab);
            return f;
        }

        public static ExamineTarget Examine(ObjectInfo body, Company c)
        {
            var sub = body as SubObjectInfo;
            EObjectTypes type = body.GetObjectTypes();
            var t = new ExamineTarget
            {
                Listed = body.clickObjectInfo == null
                    && (sub == null || sub.TypeSubObjectInfo != SubObjectInfo.ETypeSubObjectInfo.Spacecraft),
                CanMine = body.CanMineResources,
                OrbitOrSystem = type == EObjectTypes.Orbit || type == EObjectTypes.SolarSystem
                    || type == EObjectTypes.SolarOrbit,
                Hidden = body.IsHideToDiscover
            };
            ObjectInfoData d = GameAgencies.Site(body, c);
            if (d == null) return t;
            t.HasData = true;
            t.Discovered = d.IsDiscoveredOrNotAsteroid;
            t.DataHidden = d.IsHideToDiscover;
            t.KnownUnexplored = KnownUnexplored(d);
            return t;
        }

        static bool KnownUnexplored(ObjectInfoData d)
        {
            List<RowExploredResourcesData> rows = d.listExploredResourcesRows;
            if (rows == null) return false;
            foreach (RowExploredResourcesData r in rows)
                if (r != null && r.ObservedData.Value > 0.0 && !r.ExploredFullyOrPreliminary) return true;
            return false;
        }

        // An asteroid the observatory is looking for now is free again once the window's "stop first" has run.
        public static DiscoverTarget Discover(ObjectInfo o, Company c, ObservatoryFacility own)
        {
            var t = new DiscoverTarget { Asteroid = o.objectTypes == EObjectTypes.Asteroid,
                Hidden = o.IsHideToDiscover };
            ObjectInfoData d = GameAgencies.Site(o, c);
            if (d == null) return t;
            t.HasData = true;
            t.Discovered = d.IsDiscoveredOrNotAsteroid;
            t.DataHidden = d.IsHideToDiscover;
            bool mine = own != null && ObservatoryWorld.Mode(own) == ObsMode.Discovery
                && ObservatoryWorld.TargetBody(own) == o;
            t.BeingFound = d.IsBeingCurrentlyDiscovered && !mine;
            return t;
        }

        // 0, or the refusal; order: what the apply runs. A client resolves the same names without the window's checks
        // (the host made them); any name that no longer resolves is out of step.
        public static int Decide(ActionRequest a, Company c, bool host, out ObservatoryOrder order)
        {
            order = null;
            if (c == null) return RefuseCodes.BadTarget;
            ObservatoryFacility o = Find(a, c);
            ObservatoryFacts f = Facts(a, c, o);
            order = new ObservatoryOrder { Observatory = o };
            if (a.ToBody > 0) order.Body = GameAgencies.Body(a.ToBody);
            bool groupNamed = a.Group >= 0;
            if (groupNamed) order.Group = ObservatoryWorld.GroupAt(a.Group);
            if (!host)
            {
                bool named = o != null && (a.ToBody < 1 || order.Body != null) && (!groupNamed || order.Group != null);
                return named ? 0 : RefuseCodes.BadTarget;
            }
            switch (a.Kind)
            {
                case ActionKind.ObservatoryExamine:
                    return ObservatoryCommand.CheckExamine(f, a.ToBody > 0,
                        order.Body != null ? Examine(order.Body, c) : null, groupNamed, ExamineGroup(order.Group, c));
                case ActionKind.ObservatoryDiscover:
                    return ObservatoryCommand.CheckDiscover(f, groupNamed, DiscoverList(order.Group, groupNamed, c, o));
                case ActionKind.ObservatorySurvey: return ObservatoryCommand.CheckSurvey(f);
                case ActionKind.ObservatoryStop: return ObservatoryCommand.CheckStop(f);
                default: return ObservatoryCommand.CheckNotify(f);
            }
        }

        static List<ExamineTarget> ExamineGroup(ObjectInfoGroups g, Company c)
        {
            if (g == null) return null;
            var list = new List<ExamineTarget>();
            foreach (ObjectInfo o in g.objectInGroup) if (o != null) list.Add(Examine(o, c));
            return list;
        }

        // The group's objects, or with no group every object in the system (RandomizeNextObjectToDiscover's two
        // lists); null for a group index the game does not have.
        static List<DiscoverTarget> DiscoverList(ObjectInfoGroups g, bool groupNamed, Company c,
            ObservatoryFacility own)
        {
            if (groupNamed && g == null) return null;
            IEnumerable<ObjectInfo> objects = g != null ? g.objectInGroup
                : MonoBehaviourSingleton<ObjectInfoManager>.Instance.allObjectInfos;
            var list = new List<DiscoverTarget>();
            foreach (ObjectInfo o in objects) if (o != null) list.Add(Discover(o, c, own));
            return list;
        }
    }
}
