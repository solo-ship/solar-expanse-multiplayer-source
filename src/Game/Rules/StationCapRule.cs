using System;
using HarmonyLib;
using AllScriptableObjectManager = Manager.AllScriptableObjectManager;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;

namespace SolarExpanseMultiplayer
{
    // A space station's facility cap (ObjectInfo.GetCountMaxFacilitySpaceStation, D 125261) walks the station's
    // segments through the view on the LOCAL player's data at the station, which only the station's owner has (the
    // view is set on the owner's data, D 38819). Every other game finds no view and answers "no cap", so a host that is
    // not the owner never caps a client's station, and the games disagree on which units are valid as they finish
    // (CanAddFacility asks the cap, D 112779), which splits the facility prints. In a session the walk is the owner's
    // on every game: the agency of the facility that made the station (CreateByFacility), which every game agrees on.
    // A no-op outside a session, and for a station no facility made (the game's own answer).
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch(typeof(ObjectInfo), nameof(ObjectInfo.GetCountMaxFacilitySpaceStation))]
    public static class StationCapRule
    {
        [HarmonyPrefix]
        static bool OwnersCap(ObjectInfo __instance, ref long __result)
        {
            if (!HumanAgencies.Active) return true;
            try
            {
                Company owner = Owner(__instance);
                if (owner == null) return true;
                __result = Of(__instance, owner);
                return false;
            }
            catch (Exception e)
            {
                // the game's own answer is the local player's, as it would be outside a session
                Log.Error("station: the owner's cap failed (the game's own ran): " + e.GetType().Name);
                if (HumanAgencies.Host == null)
                    HumanAgencies.ClientOutOfStep("the station's cap threw " + e.GetType().Name);
                return true;
            }
        }

        static Company Owner(ObjectInfo station)
        {
            Facility by = station != null ? station.CreateByFacility : null;
            return by != null ? by.Company : null;
        }

        // The cap as the owner's own game counts it: its segment views walked from the first.
        public static long Of(ObjectInfo station, Company owner)
        {
            ObjectInfoData d = GameAgencies.Site(station, owner);
            if (d == null || d.CustomViewObjectInfo == null || d.CustomViewObjectInfo.SpaceStationBuilder == null)
                return StationCap.None;
            int capacity = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.SpaceStationConfig
                .OrbitStationCapacity;
            int segments = 0;
            for (ISpaceStationBuilder b = d.CustomViewObjectInfo.SpaceStationBuilder; b != null && segments <= capacity;
                b = b.GetNext())
                segments++;
            return StationCap.Of(segments, capacity, station.CreateByFacilityDescriptor.gfdSpaceStationData
                .MaxFacilityAmount);
        }

        // The host's check: the owner's cap whether or not the rule is patched (-semp-no-human-rules), the game's own
        // for a station no facility made.
        public static long For(ObjectInfo station)
        {
            Company owner = Owner(station);
            return owner != null ? Of(station, owner) : station.GetCountMaxFacilitySpaceStation();
        }
    }
}
