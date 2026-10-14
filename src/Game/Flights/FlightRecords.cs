using System;
using MissionInfo = Game.Info.MissionInfo;
using MissionInfoManager = Manager.MissionInfoManager;
using PdsType = Game.PMMissionParameterDataSave;

namespace SolarExpanseMultiplayer
{
    // A save record (the PMMissionParameterDataSave the game writes for a mission) is taken from the plan when the
    // mission is made, so after a load it holds the figures the load's re-plan computed, while the mission itself was
    // given back the save's: the leftover fuel (the player's branch, or FlightLoadRule's KeepFuel) and the cost
    // figures (FlightRules.KeepSavedFigures). A joiner's load of the host's snapshot gives its missions the record's
    // figures, so before each snapshot the host sets every record to its mission's own. The records are read only
    // when the game saves (vanilla never reads them back while running), so nothing the host plays with changes.
    internal static class FlightRecords
    {
        // How many records were changed; -1 when the missions could not be read (the snapshot goes ahead).
        public static int MatchMissions()
        {
            try
            {
                MissionInfoManager m = MonoBehaviourSingleton<MissionInfoManager>.Instance;
                if (m == null || m.ListMissionInfo == null) return 0;
                int changed = 0;
                foreach (MissionInfo mi in m.ListMissionInfo)
                {
                    if (mi == null) continue;
                    PdsType pds = m.GetMissionInfoSaveFromMissionInfo(mi);
                    if (pds != null && Match(pds, mi)) changed++;
                }
                return changed;
            }
            catch (Exception e)
            {
                Log.Error("flight: could not match the save records to their missions: " + e.GetType().Name);
                return -1;
            }
        }

        static bool Match(PdsType pds, MissionInfo mi)
        {
            bool changed = false;
            var record = pds.cargoAllData != null ? pds.cargoAllData.cargoFuel : null;
            var own = mi.cargoAll != null ? mi.cargoAll.cargoFuel : null;
            if (record != null && own != null && !Same(record.cargoMass, own.cargoMass))
            {
                record.cargoMass = own.cargoMass;
                changed = true;
            }
            if (!Same(pds.allFuelNeed, mi.costFuel)) { pds.allFuelNeed = mi.costFuel; changed = true; }
            if (!Same(pds.optimalFuelNeed, mi.optimalCostFuel))
            {
                pds.optimalFuelNeed = mi.optimalCostFuel;
                changed = true;
            }
            if (pds.costType != mi.costType) { pds.costType = mi.costType; changed = true; }
            return changed;
        }

        static bool Same(double a, double b)
        {
            return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        }
    }
}
