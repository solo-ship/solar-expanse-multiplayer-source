using System;
using HarmonyLib;
using HabitabilityParametersNew = Data.ScriptableObject.Terraformation.TerraformationConfig.HabitabilityParametersNew;
using ObjectInfo = Game.Info.ObjectInfo;

namespace SolarExpanseMultiplayer
{
    // Skips the habitability warm-up of a joiner's snapshot load (Core's PrewarmSkip says why). Inside
    // ExtractAllFromSaveData, ObjectInfo.AfterLoadState runs 15 rounds of UpdateAndSumUpHabitabilityParameters +
    // UpdateDepositStates and one UpdateHabitabilityAndOrVisualization per body. Its only model step is
    // HabitabilityParametersNew.Update (UpdateAndSumUpHabitabilityParameters is its one caller), so skipping that and
    // UpdateDepositStates keeps the saved base values and deposit rows as the host saved them, while the game still
    // builds the final values (base plus the facilities' bonuses), the facilities' multipliers and the visuals.
    // The gate is the joiner's current load's (Session.PrewarmSkip): open only during that load's extract, so the
    // host, a solo game, a normal load and every monthly step run the game's own code. Patched on joiners only
    // (HookPlan.HabitabilityPrewarm). A prefix that throws lets the game's method run (logged).
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a class without one.
    [HarmonyPatch(typeof(HabitabilityParametersNew))]
    public static class HabitabilityPrewarm
    {
        [HarmonyPatch(nameof(HabitabilityParametersNew.Update))]
        [HarmonyPrefix]
        static bool StepPrefix(ObjectInfo objectInfo, ref bool __result)
        {
            try
            {
                PrewarmSkip g = Session.PrewarmSkip;
                if (g == null || !g.Step(Id(objectInfo))) return true;
                __result = false;
                return false;
            }
            catch (Exception e) { Threw(e); return true; }
        }

        [HarmonyPatch(nameof(HabitabilityParametersNew.UpdateDepositStates))]
        [HarmonyPrefix]
        static bool DepositPrefix(ObjectInfo objectInfo, ref bool __result)
        {
            try
            {
                PrewarmSkip g = Session.PrewarmSkip;
                if (g == null || !g.Deposit(Id(objectInfo))) return true;
                __result = false;
                return false;
            }
            catch (Exception e) { Threw(e); return true; }
        }

        static int Id(ObjectInfo o) { return o != null ? o.GetInstanceID() : 0; }

        // Every time, not once: the class keeps no state of its own, and a prefix that throws is a broken build.
        static void Threw(Exception e)
        {
            Log.Error("join: the habitability prewarm prefix threw (the game's step ran): " + e.GetType().Name);
        }
    }
}
