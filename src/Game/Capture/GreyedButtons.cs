using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ObjectInfo = Game.Info.ObjectInfo;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using SpaceCraftInfoWindow = Game.UI.Windows.Windows.SpaceCraftInfoWindow;
using SpaceCraftProjectWindow = Game.UI.Windows.Windows.SpaceCraftProjectWindow;
using UIRowFacility = Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;

namespace SolarExpanseMultiplayer
{
    // The click paths a session does not carry yet (DD13): in a session each handler answers with the note and does
    // nothing, so no game changes alone. Each is a TripwireTable entry too, which the dev trigger "trip" can reach
    // without the window. What each waits for: the space elevator's transfer and saving a ship design: later (M6
    // leftovers, M8). Captured elsewhere: the market, the nuke, leaving stock and buying what is missing (M7:
    // CaptureMarket, CaptureWorld); the planner's Schedule, the preset save, the abort and scrap popups, a route's
    // Schedule, edit, pause and delete and "send supplies to orbit" (M6: CaptureFlights, CaptureSupply).
    // MarketWindow.BuyResources and SellResources are unreachable.
    // Not here: "Manage observatories", which throws in the game itself.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class GreyedButtons
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            foreach (System.Type row in new[] { typeof(UIRowFacility), typeof(UIRowRocket) })
            {
                yield return HookTargets.Method(row, "SpaceElevatorInstanceDragAndDrop");
            }
            yield return HookTargets.Method(typeof(SpaceCraftProjectWindow), "CreatedNewProject");
            yield return HookTargets.Method(typeof(SpaceCraftProjectWindow), "OnClickBtnSave");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            Reach(seam, __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            return false;
        }

        // What a use of a greyed control does, by the name the tripwire table gives it. The dev trigger "trip" calls
        // this for a path without the window; false for a name that is not a greyed path.
        public static bool Reach(ICaptureSeam seam, string path)
        {
            TripwireEntry entry = TripwireTable.Resolve(path);
            if (seam == null || entry == null || entry.Hooked) return false;
            WindowCapture.Greyed(seam, entry.Name);
            return true;
        }
    }
}
