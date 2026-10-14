using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    // Dev-only planted load faults, so each of the joiner's load-failure signals can be seen to stop a load in the real
    // game. Armed ONLY in a test profile with -semp-join-fault extract|success|unityerror|apply (DevFlags allows the
    // flag on a joiner only); without it every member returns at once and nothing is patched.
    //   - extract: on the first load, MarketOfferManager's ExtractFromSaveGameData throws inside
    //     ExtractAllFromSaveData, which then returns false, and the save-loaded flag is false;
    //   - success: on the first load, the save-loaded flag the joiner sees is forced false (the extract returns true);
    //   - unityerror: a Debug.LogError during the extract, on the main thread in the first load and from a worker
    //     thread in the second;
    //   - apply: on the first load, the apply step's boundary copy throws after a clean extract (the load must fail);
    //   - prewarm-patch: the deferred habitability patch fails to install (GameHooks.EnsureDeferred), so every load is
    //     refused with LoadsDifferently before anything loads;
    //   - prewarm-count: on the first load the prewarm gate is never opened, so the prefixes skip nothing and the
    //     verdict after the extract refuses the load with LoadsDifferently (what a game update that moved the warm-up
    //     would do).
    // Every later load in the process is clean, so the same process then joins again. The patch has its OWN Harmony
    // instance, applied on demand, and is not in HookPlan or GameHooks: a normal player's hook counts never change. Its
    // state is static because a Harmony prefix is a static method; Arm resets it for each load.
    public static class JoinTestFaults
    {
        const string HarmonyId = "com.solo-ship.solarexpansemultiplayer.join-test-faults";
        static Harmony harmony;
        static int loads;                    // armed loads so far in this process
        static volatile int planted;         // 0 none, 1 throw, 2 error on the main thread, 3 from a worker thread
        static bool flipSuccess, failApply;

        // The fault in force: only in a test profile with the flag (null otherwise).
        public static string Fault
        {
            get
            {
                if (Boot.Profile == null || Boot.Dev == null) return null;
                string f = Boot.Dev.JoinFault;
                return f == "extract" || f == "success" || f == "unityerror" || f == "apply" ||
                       f == "prewarm-patch" || f == "prewarm-count" ? f : null;
            }
        }

        // At the start of each joiner load.
        public static void Arm()
        {
            string f = Fault;
            if (f == null) return;
            planted = 0;
            flipSuccess = false;
            failApply = false;
            loads++;
            if (f == "extract" && loads == 1) planted = 1;
            else if (f == "unityerror" && loads == 1) planted = 2;
            else if (f == "unityerror" && loads == 2) planted = 3;
            else if (f == "success" && loads == 1) flipSuccess = true;
            else if (f == "apply" && loads == 1) failApply = true;
            if (planted != 0 && !Install()) planted = 0;
            Log.Info("join: test fault " + f + " for load " + loads + ": " + What());
        }

        static string What()
        {
            switch (planted)
            {
                case 1: return "a provider throws";
                case 2: return "an error on the main thread";
                case 3: return "an error from a worker thread";
                default: return flipSuccess ? "success forced false" : failApply ? "the apply step fails"
                    : "none (a clean load)";
            }
        }

        // The save-loaded flag as the joiner should see it.
        public static bool FilterSuccess(bool success)
        {
            if (!flipSuccess) return success;
            flipSuccess = false;
            Log.Info("join: test fault: the save-loaded success flag forced false");
            return false;
        }

        // True once, in the load the apply fault is armed for: the apply step then fails.
        public static bool FailApplyStep()
        {
            if (!failApply) return false;
            failApply = false;
            Log.Info("join: test fault: the apply step fails");
            return true;
        }

        // True when the deferred habitability patch must fail (prewarm-patch).
        public static bool FailDeferredPatch()
        {
            if (Fault != "prewarm-patch") return false;
            Log.Info("join: test fault: the deferred habitability patch fails");
            return true;
        }

        // True in the first load when the prewarm gate must stay shut (prewarm-count).
        public static bool HoldPrewarmGate()
        {
            if (Fault != "prewarm-count" || loads != 1) return false;
            Log.Info("join: test fault: the habitability prewarm gate stays shut");
            return true;
        }

        static bool Install()
        {
            if (harmony != null) return true;
            try
            {
                var h = new Harmony(HarmonyId);
                List<MethodInfo> patched = h.CreateClassProcessor(typeof(ProviderFault)).Patch();
                int n = patched == null ? 0 : patched.Count;
                if (n != 1)
                {
                    Log.Error("join: the test fault patch applied to " + n + " methods, expected 1");
                    return false;
                }
                harmony = h;
                Log.Info("join: test fault patch installed (its own Harmony instance)");
                return true;
            }
            catch (Exception e) { Log.Error("join: the test fault patch failed: " + e.Message); return false; }
        }

        // Runs only while a fault is planted for the load in progress, then disarms itself.
        [HarmonyPatch]
        static class ProviderFault
        {
            static MethodBase TargetMethod()
            {
                InterfaceMapping map = typeof(Manager.MarketOfferManager)
                    .GetInterfaceMap(typeof(Manager.ISaveStateDataProvider));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i].Name == "ExtractFromSaveGameData") return map.TargetMethods[i];
                return null;
            }

            static void Prefix()
            {
                int p = planted;
                if (p == 0) return;
                planted = 0;
                if (p == 1) throw new InvalidOperationException("planted test fault (join-fault extract)");
                if (p == 2)
                {
                    UnityEngine.Debug.LogError("planted test fault (join-fault unityerror, main thread)");
                    return;
                }
                const string text = "planted test fault (join-fault unityerror, worker thread)";
                var t = new Thread(() => UnityEngine.Debug.LogError(text));
                t.IsBackground = true;
                t.Name = "semp-test-fault";
                t.Start();
                t.Join(5000);
            }
        }
    }
}
