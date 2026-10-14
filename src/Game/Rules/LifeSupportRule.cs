using System.Collections.Generic;
using HarmonyLib;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;

namespace SolarExpanseMultiplayer
{
    // S1, ObjectInfoData.UpdateLifeSupport: the daily supply use, the crew's death and the stalled-production flag run
    // only for the local player's agency, so on another player's game a remote human's supplies and crew stay frozen
    // (M4 Task 14: Earth supply). In a session every human agency is served on every game. The method's first test,
    // "company != the local player", is the one thing changed; the pause and the advisor window stay for the local
    // player, as they test the player separately. Private method, found by name.
    [HarmonyPatch(typeof(ObjectInfoData), "UpdateLifeSupport")]
    public static class LifeSupportRule
    {
        static readonly AccessTools.FieldRef<ObjectInfoData, bool> SkipsFirstDay =
            AccessTools.FieldRefAccess<ObjectInfoData, bool>("hackOneFrameLeesLifesupportBadtimeNotyfication");

        // A fresh data object (a joiner's load, a host reload) skips one day's consumption through an unsaved flag the
        // game sets after the first call, so a game that loaded later would part from one that played on. For a
        // human agency the flag is set before every call, so the first day is never skipped, on any game.
        [HarmonyPrefix]
        static void PrimeFirstDay(ObjectInfoData __instance)
        {
            if (!HumanAgencies.Active) return;
            if (HumanAgencies.IsHuman(__instance.company)) SkipsFirstDay(__instance) = true;
        }

        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            return Il.SkipTestToHumans(code, "ObjectInfoData.UpdateLifeSupport");
        }
    }
}
