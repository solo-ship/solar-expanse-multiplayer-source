using System;
using System.Collections.Generic;
using Company = Game.Company;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using AiChildBehavior = AI.CompanyChildBehavior;

namespace SolarExpanseMultiplayer
{
    // M3's world switches (docs/research/state-accessors.md "AI (rivals)"; measured in the M3 Task 2 spike), in one
    // place for the drift tests (DriftWorld), the joiner (SnapshotLoad's apply step) and the host (JoinHost).
    // Stateless: every call reads the game afresh. Main thread only.
    public static class WorldSwitches
    {
        // One agency's AI off: EnableDisableAI(false), then DisableBehavior(false) on its tree and every child tree
        // (the plain DisableBehavior() would pause a tree instead of destroying it). Never touches
        // Company.BlockEnableAI: on an AI-on host that flag would stop every other rival's AI at its next EnableAI, so
        // the host switches off only the agencies humans play, one by one, and again after each of its loads.
        public static void AiOffOne(Company c)
        {
            if (c == null) throw new ArgumentNullException("c");
            c.EnableDisableAI(false);
            AiCompanyBehaviour cb = c.GetComponent<AiCompanyBehaviour>();
            if (cb != null) cb.DisableBehavior(false);
            AiChildBehavior[] kids = c.GetComponents<AiChildBehavior>();
            for (int k = 0; k < kids.Length; k++) kids[k].DisableBehavior(false);
        }

        // The given agencies' AI off, as M3's -semp-ai-off does it: BlockEnableAI first, so no later EnableAI turns one
        // back on, then the recipe for each. A load restores the saved child behaviours, which start themselves, so it
        // runs after every load.
        public static void AiOff(IList<Company> agencies)
        {
            Company.BlockEnableAI = true;
            for (int i = 0; i < agencies.Count; i++) AiOffOne(agencies[i]);
        }

        // The joiner's step: every active agency other than `except` (its player), the player and the World Government.
        // Returns how many were switched off. A client never decides for any other agency.
        public static int AiOff(Company except)
        {
            List<Company> all = MonoBehaviourSingleton<Manager.GameManager>.Instance.Companies;
            if (all == null) throw new InvalidOperationException("GameManager.Companies is null");
            var others = new List<Company>();
            foreach (Company c in new List<Company>(all))
            {
                if (c == null || c.ID == null || c.IsPlayer || ReferenceEquals(c, except)) continue;
                if (c.Definition != null && c.Definition.IsWorldGovernment) continue;
                if (!c.gameObject.activeSelf) continue;
                others.Add(c);
            }
            AiOff(others);
            return others.Count;
        }

        // The yearly autosave off: LoadSaveManager.OnEachYearCheckAutoSave starts with `if
        // (!autoSaveEveryNYears.HasValue) yield break;` and the coroutine checks the field at each year event, so
        // setting it after a load works (M3 Task 2, runs 04 and 05). Each load starts another coroutine; the field is
        // on the manager, so it is set again after every load. True when it reads null afterwards; problem says why not
        // (no manager, no field, or the value stuck). was: the value before.
        public static bool AutosaveOff(out float? was, out string problem)
        {
            was = null;
            problem = null;
            var lsm = SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance;
            var field = new StateField<Manager.LoadSaveManager, float?>("autoSaveEveryNYears");
            if (lsm == null || !field.Ok)
            {
                problem = "the LoadSaveManager or its autoSaveEveryNYears field is not there";
                return false;
            }
            was = field.Get(lsm);
            field.Set(lsm, null);
            float? now = field.Get(lsm);
            if (now.HasValue)
            {
                problem = "autoSaveEveryNYears is still " + now.Value.ToString("R",
                    System.Globalization.CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }
    }
}
