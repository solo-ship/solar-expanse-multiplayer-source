using System;
using System.Reflection;
using HarmonyLib;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // The confirm popups of the build, cancel and queue clicks pause the game while they are open (Q8: in a session
    // that paused every player). All 32 calls of this overload are click handlers, so in a session none of them pauses:
    // the answer is a command, and the time keeps its owner, the clock vote. Outside a session the game's own pause
    // stays. The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class PopupNoPause
    {
        // The overload with the pause flag (the others have no such parameter), found by its whole parameter list.
        [HarmonyTargetMethod]
        static MethodBase Target()
        {
            return HookTargets.Method(typeof(UIManager), "ShowPopUP", typeof(string), typeof(Action), typeof(Action),
                typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(string), typeof(Action),
                typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string));
        }

        [HarmonyPrefix]
        static void Prefix(ref bool pauseTime)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return;
            pauseTime = false;
        }
    }
}
