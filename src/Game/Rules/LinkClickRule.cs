using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using LabelLinksHandler = Game.UI.Windows.Elements.LabelLinksHandler;

namespace SolarExpanseMultiplayer
{
    // S18, LabelLinksHandler.OnPointerClick: a click on a spacecraft or launch-vehicle link in any text builds a
    // throwaway construct to open its info window and takes the local player's next item id for it
    // (productionCountID++). Nothing is kept, but every later item id of that agency shifts, so the two games'
    // counters part. In a session the counter is put back after the window has opened. The method is an explicit
    // interface implementation, found through the interface map.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class LinkClickRule
    {
        [HarmonyTargetMethod]
        static MethodBase Target()
        {
            return MoneyRules.Required(MoneyRules.Mapped(typeof(LabelLinksHandler),
                typeof(UnityEngine.EventSystems.IPointerClickHandler), "OnPointerClick"),
                "LabelLinksHandler.OnPointerClick");
        }

        // The state is the counter plus one, so 0 means "not saved".
        [HarmonyPrefix]
        static void Save(ref int __state)
        {
            if (!HumanAgencies.Active) return;
            Company player = Player();
            if (player != null) __state = player.productionCountID + 1;
        }

        // A finalizer, so a click that throws still gives the id back.
        [HarmonyFinalizer]
        static void Restore(int __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state == 0) return;
            Company player = Player();
            if (player != null) player.productionCountID = __state - 1;
        }

        static Company Player()
        {
            GameManager m = MonoBehaviourSingleton<GameManager>.Instance;
            return m != null ? m.Player : null;
        }
    }
}
