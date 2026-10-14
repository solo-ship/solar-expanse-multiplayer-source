using System;
using System.Collections.Generic;
using HarmonyLib;
using CycleMissionManager = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionManager;
using CycleMissionsData = Game.UI.Windows.Elements.PlanMissionElements.CycleMissionsData;
using ObjectInfo = Game.Info.ObjectInfo;
using Spacecraft = CustomUpdate.Spacecraft;

namespace SolarExpanseMultiplayer
{
    // A client's copy of a host-planned route leg adds the route's trip as the host's controller did (RouteLegCount).
    public static class FlightRouteCount
    {
        static readonly AccessTools.FieldRef<CycleMissionManager, Dictionary<Spacecraft, CycleMissionsData>> Routes =
            AccessTools.FieldRefAccess<CycleMissionManager, Dictionary<Spacecraft, CycleMissionsData>>(
                "SCtoCycleMissionsData");

        public static void AfterClientLeg(ActionRequest entry)
        {
            try
            {
                MissionData m = entry != null ? entry.Mission : null;
                if (m == null || !RouteLegCount.IsRouteLeg(entry.Kind, m.Creator)) return;
                if (m.Ships == null || m.Ships.Length == 0 || m.Ships[0] == null || m.Ships[0].Kind != CraftKinds.Ship)
                    return;
                CycleMissionManager cm = CycleMissionManager.Instance;
                Dictionary<Spacecraft, CycleMissionsData> all = cm != null ? Routes(cm) : null;
                if (all == null) return;
                foreach (KeyValuePair<Spacecraft, CycleMissionsData> kv in all)
                {
                    if (kv.Key == null || kv.Value == null || kv.Key.ID != m.Ships[0].Id) continue;
                    CycleMissionsData r = kv.Value;
                    ObjectInfo a = r.A, b = r.B;
                    if (a == null || b == null) return;
                    bool aLowIsB = a.LowOrbitCustom != null && a.LowOrbitCustom.GetObjectInfo() == b;
                    bool bUnderA = b.parentObjectInfo != null && b.parentObjectInfo.LowOrbitCustom == a.LowOrbitCustom
                        && b.interstellarConstructionFacility;
                    if (!RouteLegCount.Counts(m.Start == b.id, aLowIsB, bUnderA)) return;
                    r.IncreasesCount();
                    Log.Info("flight: route of ship " + kv.Key.ID + " counts the leg of mission " + m.MissionId
                        + " (" + r.CountMission + ")");
                    return;
                }
            }
            catch (Exception e) { Log.Warn("flight: the route count could not follow the leg: " + e.GetType().Name); }
        }
    }
}
