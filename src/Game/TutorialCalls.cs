using HarmonyLib;
using ContractManager = Manager.ContractManager;

namespace SolarExpanseMultiplayer
{
    // Two tutorial contracts move this player's camera when an objective completes: the telescope delivered to orbit
    // (contract_tutorial_firstorbit, CenterCameraOnTelescopeIfNeed) and the first flight to the Moon a quarter of the
    // way there (contract_tutorial_moonorbit, CenterCameraFirstFlyIfNeed, which also sets speed x1 with the click flag,
    // so it read as this player's own choice). In a session a host's command completes the objective while the player
    // is busy elsewhere, so neither runs: no camera jump, no speed vote. Both only touch the camera, the speed and a
    // flag of their own that is not saved, so skipping them changes nothing in the world. (The tutorial's pauses are
    // GamePauses'.)
    // Patched only in a test session (HookPlan.TutorialCalls), like ClockSync; main thread only.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a class without one.
    [HarmonyPatch(typeof(ContractManager))]
    public static class TutorialCalls
    {
        [HarmonyPatch(typeof(ContractManager), "CenterCameraOnTelescopeIfNeed")]
        [HarmonyPrefix]
        static bool Telescope() { return !ClockSync.Hooks.InSession; }

        [HarmonyPatch(typeof(ContractManager), "CenterCameraFirstFlyIfNeed")]
        [HarmonyPrefix]
        static bool FirstFly() { return !ClockSync.Hooks.InSession; }
    }
}
