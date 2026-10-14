using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LoadSaveManager = Manager.LoadSaveManager;

namespace SolarExpanseMultiplayer
{
    // DD8, the one prefix and postfix over all five callers of LoadSaveManager.SaveToFile. In a session the host's saves
    // are written into Saves\Multiplayer\ under the mod's names; a client's every save is refused; F8's "bugSave" stays
    // vanilla on the host; outside a session nothing changes. The postfix acts only on a save the game says it wrote.
    // The decisions are Core's (SaveRewrite, SaveLedger); the owner is SaveGlue. Patched deferred (HookPlan): SaveToFile
    // returns a bool, and HarmonyX would load a helper assembly the content scan counts if it were patched before the
    // menu's scan.
    [HarmonyPatch(typeof(LoadSaveManager), nameof(LoadSaveManager.SaveToFile))]
    public static class SaveNameRewrite
    {
        static readonly FieldInfo LastSaveName = AccessTools.Field(typeof(LoadSaveManager), "lastSaveName");

        public sealed class State
        {
            public SaveRewritePlan Plan;
            public string LastName;
            public string Original;
        }

        [HarmonyPrefix]
        static bool Prefix(LoadSaveManager __instance, ref string saveName, ref bool __result, out State __state)
        {
            __state = null;
            SaveGlue glue = Session.Early != null ? Session.Early.Saves : null;
            if (glue == null || !Session.Live) return true;
            try
            {
                SaveRewritePlan plan = glue.PlanSave(saveName);
                if (plan.Kind == SaveRewriteKind.Vanilla) return true;
                if (plan.Kind == SaveRewriteKind.Refuse) { glue.Refused(plan.Why); __result = false; return false; }
                __state = new State
                {
                    Plan = plan, Original = saveName,
                    LastName = LastSaveName != null ? LastSaveName.GetValue(__instance) as string : null
                };
                saveName = plan.Name;
                return true;
            }
            catch (Exception e)
            {
                // fail closed: a save in a session never lands in the player's own Saves by accident
                Log.Error("saves: the name rewrite failed, the save is refused: " + e.Message);
                __result = false;
                return false;
            }
        }

        [HarmonyPostfix]
        static void Postfix(LoadSaveManager __instance, bool __result, State __state)
        {
            if (__state == null || __state.Plan == null) return;
            try
            {
                if (LastSaveName != null) LastSaveName.SetValue(__instance, __state.LastName);
                SaveGlue glue = Session.Early != null ? Session.Early.Saves : null;
                if (glue != null) glue.Saved(__instance, __state.Plan, __result);
            }
            catch (Exception e) { Log.Error("saves: the save bookkeeping failed: " + e); }
        }
    }

    // A client's refused saves, logged with a count rather than once per call (the yearly autosave and a clicked Save both
    // land here), and one notice per few seconds.
    public sealed class ClientSaveBlock
    {
        int count;
        long lastNoticeMs = long.MinValue / 2;

        public int Count { get { return count; } }

        public void Refused(string why, NoticeQueue notices, long nowMs)
        {
            count++;
            if (count <= 3 || count % 100 == 0) Log.Info("saves: a save was refused (" + why + "), " + count + " so far");
            if (notices == null || nowMs - lastNoticeMs < 5000) return;
            lastNoticeMs = nowMs;
            notices.Push("notice.saveHostOnly", null, NoticeQueue.Routine, nowMs);
        }
    }
}
