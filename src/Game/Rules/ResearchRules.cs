using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchTree = Game.UI.Windows.Windows.ResearchTree.ResearchTree;
using UIManager = Game.UI.UIManager;
using ObjectInfoManager = Manager.ObjectInfoManager;
using ResearchManager = Manager.ResearchManager;

namespace SolarExpanseMultiplayer
{
    // S3 and S4 (the 2026-10-11 decisions: each agency its own research queue and cost).
    // S3, ResearchManager.AutoPickResearch: the next queued topic starts after a completion only for the local player's
    // agency, so a remote human's queue stops after its current topic on the other player's game. In a session every
    // human agency's queue moves on, on every game.
    // S4, ObjectInfoManager.CalculateMaintenanceCostsAndIncomePerDay: every agency's daily research cost depends on
    // whether the local player has a topic running. In a session each agency's own slot decides its own cost.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class ResearchRules
    {
        static readonly AccessTools.FieldRef<ResearchManager, Company> OwnerOf =
            AccessTools.FieldRefAccess<ResearchManager, Company>("company");

        // What the tree had selected, kept while another agency's next topic starts.
        public sealed class Selection { public ResearchDefinition Was; }

        // AutoPickResearch points the local research window at the topic it started. For a human agency that is not the
        // local player that would move the player's tree to someone else's topic, so the selection is put back.
        [HarmonyPatch(typeof(ResearchManager), "AutoPickResearch")]
        [HarmonyPrefix]
        static void KeepSelection(ResearchManager __instance, ref Selection __state)
        {
            if (!HumanAgencies.Active) return;
            Company owner = OwnerOf(__instance);
            if (!HumanAgencies.IsHuman(owner) || owner == MonoBehaviourSingleton<Manager.GameManager>.Instance.Player)
                return;
            ResearchTree tree = Tree();
            if (tree != null) __state = new Selection { Was = tree.selectOnShow };
        }

        [HarmonyPatch(typeof(ResearchManager), "AutoPickResearch")]
        [HarmonyFinalizer]
        static void RestoreSelection(Selection __state)
        {
            if (!HumanAgencies.Active) return;
            if (__state == null) return;
            ResearchTree tree = Tree();
            if (tree != null) tree.selectOnShow = __state.Was;
        }

        static ResearchTree Tree()
        {
            UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
            return ui != null ? ui.GetWindow<ResearchTree>() : null;
        }

        [HarmonyPatch(typeof(ResearchManager), "AutoPickResearch")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileAutoPick(IEnumerable<CodeInstruction> code)
        {
            return Il.SkipTestToHumans(code, "ResearchManager.AutoPickResearch");
        }

        // The cost method is an instance method (days, company, ...): its company parameter is argument 2.
        [HarmonyPatch(typeof(ObjectInfoManager), "CalculateMaintenanceCostsAndIncomePerDay")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TranspileCost(IEnumerable<CodeInstruction> code)
        {
            return Il.PlayerToOwner(code, "ObjectInfoManager.CalculateMaintenanceCostsAndIncomePerDay", 1,
                (c, i) => i + 1 < c.Count && Il.Calls(c[i + 1], typeof(Company), "get_ResearchManager", 0),
                OpCodes.Ldarg_2, AccessTools.Method(typeof(HumanAgencies), "ResearchOwner"));
        }
    }
}
