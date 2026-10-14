using System;
using System.Collections.Generic;
using Company = Game.Company;
using EBonus = ScriptableObjectScripts.EBonus;
using EObjectTypes = Data.EObjectTypes;
using FacilityToConstructNotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using LaunchVehicleToConstructNotYetPay = Data.LaunchVehicleToConstructNotYetPay;
using MissionInfo = Game.Info.MissionInfo;
using MyIDScriptableObjectProductionItemCountToBuild = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using ResourcePriceOne = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePriceOne;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using SpacecraftToConstructNotYetPay = Data.SpacecraftToConstructNotYetPay;

namespace SolarExpanseMultiplayer
{
    // "Send supplies to orbit" (F10, kind 65) as the games see the production row it was clicked on. A construct that is
    // not yet paid for has no id (the game builds its row object from the site's production queue with id -1), so a
    // request names the row by the site, the queue entry's content id and the entry's place in the site's queue, and
    // every game finds the same row object from that. The row keeps the flight in MissionInfoToBuild, which greys its
    // send button: the clicking game's callback used to set it, so the apply sets it on every game.
    internal static class FlightSupply
    {
        // The row object (a facility, a ship or a launch vehicle not yet paid for) the queue place stands for, or null.
        public static object Row(ObjectInfoData site, int index, string contentId)
        {
            if (site == null || contentId == null) return null;
            List<MyIDScriptableObjectProductionItemCountToBuild> queue = site.ListProductionQuery;
            if (queue == null || index < 0 || index >= queue.Count) return null;
            MyIDScriptableObjectProductionItemCountToBuild key = queue[index];
            if (key == null || key.MyIDScriptableObjectProductionItem == null
                || key.MyIDScriptableObjectProductionItem.ID != contentId) return null;
            FacilityToConstructNotYetPay f;
            if (site.DictionaryFacilityNotYetPayCash.TryGetValue(key, out f)) return f;
            SpacecraftToConstructNotYetPay s;
            if (site.DictionarySTCNotYetPayCash.TryGetValue(key, out s)) return s;
            LaunchVehicleToConstructNotYetPay l;
            if (site.DictionaryLTCNotYetPayCash.TryGetValue(key, out l)) return l;
            return null;
        }

        // The queue place of a row object at its site, or -1.
        public static int IndexOf(ObjectInfoData site, object row)
        {
            if (site == null || row == null) return -1;
            List<MyIDScriptableObjectProductionItemCountToBuild> queue = site.ListProductionQuery;
            for (int i = 0; queue != null && i < queue.Count; i++)
                if (ReferenceEquals(Row(site, i, queue[i].MyIDScriptableObjectProductionItem != null
                    ? queue[i].MyIDScriptableObjectProductionItem.ID : null), row)) return i;
            return -1;
        }

        public static MissionInfo LinkOf(object row)
        {
            var f = row as FacilityToConstructNotYetPay;
            if (f != null) return f.MissionInfoToBuild;
            var s = row as SpacecraftConstructData;
            return s != null ? s.MissionInfoToBuild : null;
        }

        static void SetLink(object row, MissionInfo mission)
        {
            var f = row as FacilityToConstructNotYetPay;
            if (f != null) { f.MissionInfoToBuild = mission; return; }
            var s = row as SpacecraftConstructData;
            if (s != null) s.MissionInfoToBuild = mission;
        }

        // What the row's construct still needs, as the row prices it (D 165246-165284, 167450-167540), without what the
        // site already holds: the most a supply flight may carry of each resource.
        static ResourcePrice Need(object row)
        {
            var f = row as FacilityToConstructNotYetPay;
            if (f != null)
                return f.facilityDescriptor.Price * (f.CountToBuild - f.CountBuild)
                    * f.Company.BonusController.GetBonus(EBonus.BuildCost, f.facilityDescriptor);
            var l = row as LaunchVehicleToConstructNotYetPay;
            if (l != null)
                return l.LaunchVehicleType.PriceBase * (l.CountToBuild - l.CountBuild)
                    * l.Company.BonusController.GetBonus(EBonus.BuildCost, l.LaunchVehicleType);
            var s = row as SpacecraftToConstructNotYetPay;
            if (s != null)
                return s.SpacecraftType.Price * (s.CountToBuild - s.CountBuild)
                    * s.Company.BonusController.GetBonus(EBonus.BuildCost, s.SpacecraftType);
            return null;
        }

        // The host's own checks for F10 (plan Y-5), on top of FlightChecks' craft and body checks: the construct is the
        // acting agency's and has no supply flight yet, the direction fits the body, the flight joins the body to its
        // other end, and it carries resources the construct needs, no more of each than it needs.
        public static int Check(ActionRequest a, Company c)
        {
            ObjectInfo body = GameAgencies.Body(a.Body);
            if (body == null || GameAgencies.Body(a.ToBody) == null) return RefuseCodes.BadTarget;
            ObjectInfoData site = GameAgencies.Site(body, c);
            object row = Row(site, (int)a.ItemId, a.Item);
            if (row == null) return RefuseCodes.BadTarget;
            MissionInfo held = LinkOf(row);
            if (held != null && !held.cancel && !held.complete) return RefuseCodes.CraftBusy;
            bool orbit = body.objectTypes == EObjectTypes.Orbit || body.AsteroidFromPush;
            bool planet = (body.objectTypes == EObjectTypes.Planet || body.objectTypes == EObjectTypes.Moons)
                && body.LowOrbitCustom != null;
            if (a.Value == 0 ? !orbit : !planet) return RefuseCodes.BadTarget;
            MissionData m = a.Mission;
            if (!(m.Target == a.Body && m.Start == a.ToBody)) return RefuseCodes.BadTarget;
            ResourcePrice need = Need(row);
            if (need == null) return RefuseCodes.BadTarget;
            foreach (CargoRow r in m.Cargo ?? new CargoRow[0])
            {
                if (r.Kind != CargoKinds.Resource) return RefuseCodes.BadTarget;
                double most = -1;
                foreach (ResourcePriceOne one in need.ListResources)
                    if (one.ResourceDefinition != null && one.ResourceDefinition.ID == r.Item)
                        most = Math.Ceiling(one.Price);
                if (most < 0 || r.Amount > most + 1e-6) return RefuseCodes.BadTarget;
            }
            return 0;
        }

        // The flight is made: the row it was made for keeps it. Same on every game, so the send button greys the same.
        public static void Link(ActionRequest a, MissionInfo mission)
        {
            Company c = GameAgencies.Find(a.Agency);
            object row = Row(GameAgencies.Site(GameAgencies.Body(a.Body), c), (int)a.ItemId, a.Item);
            if (row == null)
            {
                Log.Warn("flight: supply flight " + mission.id + " found no row to link at " + a.Body);
                return;
            }
            SetLink(row, mission);
            Log.Info("flight: supply flight " + mission.id + " linked to the row at " + a.Body + " #" + a.ItemId);
        }
    }
}
