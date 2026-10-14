using System;
using System.Collections.Generic;
using HarmonyLib;
using Company = Game.Company;
using EObjectTypes = Data.EObjectTypes;
using Facility = Game.ObjectInfoDataScripts.Facility;
using LabelsManager = Manager.LabelsManager;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;
using Spacecraft = CustomUpdate.Spacecraft;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The object a ForgetObject names, read as CanPlayerForgotObject reads it (D 124643-124713) but with no local
    // player in it: its own flags through their fields (two of the game's getters add conditions the check does not
    // have), every agency's use of it, and the site a station's stock moves to. No state.
    static class ForgetWorld
    {
        static readonly AccessTools.FieldRef<ObjectInfo, bool> PushOnlyToOrbit =
            AccessTools.FieldRefAccess<ObjectInfo, bool>("pushOnlyToOrbitAsteroid");
        static readonly AccessTools.FieldRef<ObjectInfo, int> PushTarget =
            AccessTools.FieldRefAccess<ObjectInfo, int>("pushOnlyToOrbitAsteroidToTargetID");
        static readonly AccessTools.FieldRef<ObjectInfo, bool> NeverForget =
            AccessTools.FieldRefAccess<ObjectInfo, bool>("objectToNotForotObject");
        static readonly AccessTools.FieldRef<ObjectInfo, ObjectLabel2Element> Label =
            AccessTools.FieldRefAccess<ObjectInfo, ObjectLabel2Element>("label");
        static readonly AccessTools.FieldRef<ProductionItem, bool> Built =
            AccessTools.FieldRefAccess<ProductionItem, bool>("build");
        static readonly AccessTools.FieldRef<ObjectInfoData, List<ProductionQuery>> Query =
            AccessTools.FieldRefAccess<ObjectInfoData, List<ProductionQuery>>("listProductionQuery");

        public static bool IsConstructionSite(ObjectInfo o)
        {
            return o.SpaceStationConstructionFacility
                || (o.interstellarConstructionFacility && !o.interstellarConstructionFacilityFinish);
        }

        // The builder of a construction site: its agency owns the site.
        public static Company Builder(ObjectInfo o)
        {
            Facility f = o.CreateByFacility;
            return f != null ? f.Company : null;
        }

        // A station site's stock goes to its builder's data on the parent's low orbit (the trash popup's OK,
        // D 163699-163702). Null when either side is missing.
        public static ObjectInfoData StockFrom(ObjectInfo o, Company builder) { return GameAgencies.Site(o, builder); }

        public static ObjectInfoData StockTo(ObjectInfo o, Company builder)
        {
            ObjectInfo parent = o.parentObjectInfo;
            ObjectInfo low = parent != null && parent.LowOrbitCustom != null ? parent.LowOrbitCustom.GetObjectInfo()
                : null;
            return GameAgencies.Site(low, builder);
        }

        public static ForgetFacts Facts(ObjectInfo o, string actor)
        {
            var f = new ForgetFacts();
            if (o == null || o.IsInGameDestroy) return f;
            f.Exists = true;
            f.StationSite = o.SpaceStationConstructionFacility;
            f.InterstellarSite = !f.StationSite && o.interstellarConstructionFacility
                && !o.interstellarConstructionFacilityFinish;
            Company builder = Builder(o);
            f.SiteOwner = builder != null ? GameAgencies.Wire(builder) : null;
            f.TransferPossible = builder != null && StockFrom(o, builder) != null && StockTo(o, builder) != null;
            f.Asteroid = o.objectTypes == EObjectTypes.Asteroid;
            f.ElevatorConnected = o.SpaceElevatorConnection != null;
            f.UsedInElevator = o.IsUseInSpaceElevator;
            f.Pushable = o.PushableAsteroid2;
            f.PushOnlyToOrbit = PushOnlyToOrbit(o);
            ObjectLabel2Element label = Label(o);
            f.ImpactLabel = label != null && label.LayerLabel == LabelsManager.ELayerLabel.AsteroidImpact;
            f.PushTargetSet = PushTarget(o) != -1;
            f.NeverForget = NeverForget(o);
            f.OnShip = o.gameObject.GetComponentInParent<Spacecraft>() != null;
            Missions(o, f);
            f.OthersUse = OthersUse(o, actor);
            return f;
        }

        // The game's own test (an impact or pull mission taking it from here), and any live mission of any agency to
        // or from it. A mission that cannot be read counts as one under way.
        static void Missions(ObjectInfo o, ForgetFacts f)
        {
            DateTime now = MonoBehaviourSingleton<TimeController>.Instance.CurrentTime;
            foreach (MissionInfo m in MonoBehaviourSingleton<MissionInfoManager>.Instance.ListMissionInfo)
            {
                if (m == null || m.cancel || !(m.DateArrive > now)) continue;
                try
                {
                    bool from = m.start == o;
                    if (from && m.cargoAll != null && m.cargoAll.entireAsteroid) f.CarriedByMission = true;
                    if (from || m.target == o) f.MissionUnderWay = true;
                }
                catch (Exception) { f.MissionUnderWay = true; }
            }
        }

        // Another active agency's facilities, stock, unfinished builds or queue at the object.
        static bool OthersUse(ObjectInfo o, string actor)
        {
            if (o.ObjectsInfoData == null) return false;
            foreach (ObjectInfoData d in o.ObjectsInfoData)
            {
                if (d == null || d.company == null) continue;
                List<ProductionQuery> q = Query(d);
                if (ForgetCommand.OtherUses(GameAgencies.Wire(d.company) == actor, d.company.gameObject.activeSelf,
                    d.ListFacility != null && d.ListFacility.Count > 0, HasStock(d), Building(d),
                    q != null && q.Count > 0)) return true;
            }
            return false;
        }

        static bool HasStock(ObjectInfoData d)
        {
            if (d.ListRowResourcesData != null)
                foreach (RowResourcesData r in d.ListRowResourcesData)
                    if (r != null && r.Value > 0.0) return true;
            return false;
        }

        static bool Building(ObjectInfoData d)
        {
            if (d.ProductionItem != null)
                foreach (ProductionItem p in d.ProductionItem)
                    if (p != null && !Built(p)) return true;
            return false;
        }
    }
}
