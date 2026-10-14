using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // DD-W5: population growth is rolled on the host and applied inside each client's year event. The year handlers
    // are the last of three inline blocks of TimeController.DoThePeriodicEvents, so the rule changes that one load of
    // `onEachYearChange` to go through YearHandlers, which returns a wrapper that runs begin, the game's own list, and
    // end in a finally, whether or not the list is empty. The transpiler throws unless exactly one load matches (the
    // session is then refused). On the host begin and end open and close a record of every body's roll and the end
    // raises it (kind 91, in parts); on a client begin applies the 91 ahead of the handlers and each body's roll is
    // replaced by the host's row. A no-op outside a session.
    // The class needs a [HarmonyPatch] of its own: HarmonyX silently skips one without.
    [HarmonyPatch]
    public static class GrowthRule
    {
        static readonly Action<double> Wrapper = Run;

        [HarmonyPatch(typeof(TimeController), "DoThePeriodicEvents")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> YearTranspiler(IEnumerable<CodeInstruction> code)
        {
            var field = AccessTools.Field(typeof(TimeController), "onEachYearChange");
            if (field == null) throw new InvalidOperationException("TimeController.onEachYearChange was not found");
            return Il.Replace(code, "TimeController.DoThePeriodicEvents", 1,
                (c, i) => c[i].opcode == OpCodes.Ldfld && ReferenceEquals(c[i].operand, field),
                () => new[]
                {
                    new CodeInstruction(OpCodes.Ldfld, field),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GrowthRule), "YearHandlers"))
                });
        }

        // Called with the game's list; the result takes its place.
        public static Action<double> YearHandlers(Action<double> list)
        {
            if (!HumanAgencies.Active) return list;
            if (!HumanAgencies.State.World.Wired) return list;
            HumanAgencies.State.World.YearList = list;
            return Wrapper;
        }

        static void Run(double days)
        {
            WorldRuleState st = HumanAgencies.State != null ? HumanAgencies.State.World : null;
            if (st == null) return;
            Action<double> list = st.YearList;
            st.YearList = null;
            Begin(st);
            try { if (list != null) list(days); }
            finally { End(st); }
        }

        static void Begin(WorldRuleState st)
        {
            st.YearOpen = true;
            if (HumanAgencies.Host != null)
            {
                st.GrowthRows = new List<GrowthRow>();
                return;
            }
            IWorldClientSeam client = st.Client;
            st.Book = null;
            if (client == null) return;
            string problem = client.ApplyInEventNow(ActionKind.PopulationGrowth, client.LocalDay);
            if (problem != null) client.OutOfStep(problem);
            else st.Book = client.Growth;
        }

        static void End(WorldRuleState st)
        {
            st.YearOpen = false;
            List<GrowthRow> rows = st.GrowthRows;
            st.GrowthRows = null;
            st.Book = null;
            try
            {
                if (HumanAgencies.Host != null)
                {
                    if (st.Host == null || rows == null) return;
                    List<GrowthRow[]> parts = GrowthParts.Split(rows, ActionLimits.MaxGrowthRows);
                    for (int i = 0; i < parts.Count; i++) st.Host.PopulationGrowth(parts[i], i == parts.Count - 1);
                }
                else if (st.Client != null)
                {
                    string problem = st.Client.CloseInEvent(ActionKind.PopulationGrowth);
                    if (problem != null) st.Client.OutOfStep(problem);
                }
            }
            catch (Exception e)
            {
                Log.Error("growth: closing the year record failed: " + e.GetType().Name);
                if (st.Host != null && HumanAgencies.Host != null) st.Host.HostFault("growth: the year record failed");
            }
        }

        // ---- each body's roll ----

        [HarmonyPatch(typeof(ObjectInfoData), "OnEachYearPopulationGrowth")]
        [HarmonyPrefix]
        static bool RollPrefix(ObjectInfoData __instance, ref double __state)
        {
            __state = 0;
            if (!HumanAgencies.Active) return true;
            WorldRuleState st = HumanAgencies.State.World;
            if (HumanAgencies.Host != null)
            {
                if (st.GrowthRows != null) __state = Math.Max(__instance.CrewResource.Value, 0.0);
                return true;
            }
            if (!st.Wired) return true;
            // a client never rolls: the host's row, or nothing (and it says so)
            string agency = GameAgencies.Wire(__instance.company);
            int body = __instance.ObjectInfo.id;
            int delta;
            if (st.Book == null || !st.Book.TryTake(agency, body, out delta))
            {
                if (st.Client != null) st.Client.OutOfStep("growth: no row from the host for " + agency + " on body " + body);
                return false;
            }
            __instance.UpdateFacilityRelatedSummaries(false, true);
            __instance.CrewResource.Value += delta;
            if (__instance.CrewResource.Value < 0.0) __instance.CrewResource.Value = 0.0;
            return false;
        }

        [HarmonyPatch(typeof(ObjectInfoData), "OnEachYearPopulationGrowth")]
        [HarmonyPostfix]
        static void RollPostfix(ObjectInfoData __instance, double __state)
        {
            if (!HumanAgencies.Active || HumanAgencies.Host == null) return;
            List<GrowthRow> rows = HumanAgencies.State.World.GrowthRows;
            if (rows == null) return;
            double delta = __instance.CrewResource.Value - __state;
            double max = ActionLimits.MaxGrowth;
            rows.Add(new GrowthRow
            {
                Agency = GameAgencies.Wire(__instance.company), Body = __instance.ObjectInfo.id,
                Delta = (int)Math.Max(-max, Math.Min(max, Math.Round(delta)))
            });
        }
    }
}
