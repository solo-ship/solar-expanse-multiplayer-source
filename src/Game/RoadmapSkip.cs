using System;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    // Test profiles only (GameHooks never patches this for a normal player): the Early Access roadmap popup
    // opens once per game start and gets in the way of automated runs. Marking it as already shown skips it.
    [HarmonyPatch(typeof(MenuSceneUI), "Start")]
    public static class RoadmapSkip
    {
        static bool logged;

        static void Prefix()
        {
            try
            {
                AccessTools.Field(typeof(MenuSceneUI), "roadmapWasDisplayed").SetValue(null, true);
                if (!logged) { logged = true; Log.Info("roadmap popup skipped (test profile)"); }
            }
            catch (Exception e) { Log.Error("roadmap skip failed: " + e.Message); }
        }
    }
}
