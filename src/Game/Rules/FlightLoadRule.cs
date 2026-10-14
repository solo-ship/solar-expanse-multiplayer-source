using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using GameManager = Manager.GameManager;
using MissionInfo = Game.Info.MissionInfo;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using PMTabSchedule = Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule;

namespace SolarExpanseMultiplayer
{
    // FS2: a saved flight's leftover fuel at a load. The game's PlanFlyCode re-plans every flying mission when a save
    // loads and puts the saved fuel back only on the local player's branch (the one that uses the player's planning
    // window); every other agency's flight is planned through the AI window and keeps the recomputed fuel, which it
    // delivers on arrival. Two games would then deliver different fuel: the host (the flight's agency is not its
    // player) and the joiner of its snapshot, and real joiners meet flights in the air. So in a session every
    // agency's saved flight keeps its saved leftover fuel: the AI branch reads it before it plans and puts it back
    // after, as the player branch does.
    [HarmonyPatch]
    public static class FlightLoadRule
    {
        // PlanFlyCode's work is a compiler-made lambda: its name is not stable, so it is found by shape, and exactly
        // one must exist.
        [HarmonyTargetMethod]
        static MethodBase Target()
        {
            MethodBase found = null;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Type t in typeof(GameManager).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (MethodInfo m in t.GetMethods(all))
                    if (m.Name.StartsWith("<PlanFlyCode>b__", StringComparison.Ordinal))
                    {
                        if (found != null)
                            throw new InvalidOperationException("GameManager.PlanFlyCode has two lambdas");
                        found = m;
                    }
            if (found == null) throw new InvalidOperationException("GameManager.PlanFlyCode's lambda was not found");
            return found;
        }

        static bool IsAiScheduleLoad(List<CodeInstruction> c, int i)
        {
            var f = c[i].operand as FieldInfo;
            if (c[i].opcode != OpCodes.Ldfld || f == null || f.Name != "pmwForAI") return false;
            for (int j = i + 1; j < c.Count && j <= i + 4; j++)
                if (Il.Calls(c[j], typeof(PMTabSchedule), "OnClickScheduleButtonForCode", 0)) return true;
            return false;
        }

        static bool FollowsNote(List<CodeInstruction> c, int i)
        {
            for (int back = 1; back <= 3 && i - back >= 0; back++)
                if (Il.Calls(c[i - back], typeof(FlightLoadRule), "NoteWindow", 1)) return true;
            return false;
        }

        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> KeepFuelAtLoad(IEnumerable<CodeInstruction> code)
        {
            MethodInfo note = AccessTools.Method(typeof(FlightLoadRule), nameof(NoteWindow));
            MethodInfo keep = AccessTools.Method(typeof(FlightLoadRule), nameof(KeepFuel));
            CodeInstruction field = null;
            List<CodeInstruction> first = Il.Replace(code, "GameManager.PlanFlyCode (the AI window)", 1, (c, i) =>
            {
                bool hit = IsAiScheduleLoad(c, i);
                if (hit) field = c[i];
                return hit;
            }, () => new[] { new CodeInstruction(field), new CodeInstruction(OpCodes.Call, note) });
            CodeInstruction schedule = null;
            return Il.Replace(first, "GameManager.PlanFlyCode (the AI schedule call)", 1, (c, i) =>
            {
                bool hit = Il.Calls(c[i], typeof(PMTabSchedule), "OnClickScheduleButtonForCode", 0)
                    && FollowsNote(c, i);
                if (hit) schedule = c[i];
                return hit;
            }, () => new[] { new CodeInstruction(schedule), new CodeInstruction(OpCodes.Call, keep) });
        }

        // Before the AI window plans: remember the leftover fuel the saved flight was read with.
        public static PlanMissionWindow NoteWindow(PlanMissionWindow window)
        {
            if (!HumanAgencies.Active) return window;
            try
            {
                RuleState st = HumanAgencies.State;
                var p = window.PMMissionParameter;
                st.FuelArmed = p != null && p.CargoAll != null && FuelKeep.Keeps(true, p.LoadingFromSave);
                if (st.FuelArmed) st.SavedFuel = p.CargoAll.cargoFuel.cargoMass;
            }
            catch (Exception e) { Log.Error("flights: could not read a saved flight's fuel: " + e.GetType().Name); }
            return window;
        }

        // After it planned: put the saved leftover fuel back, as the player's branch does.
        public static MissionInfo KeepFuel(MissionInfo mission)
        {
            if (!HumanAgencies.Active) return mission;
            try
            {
                RuleState st = HumanAgencies.State;
                if (!st.FuelArmed) return mission;
                st.FuelArmed = false;
                if (mission != null) mission.cargoAll.cargoFuel.cargoMass = st.SavedFuel;
            }
            catch (Exception e) { Log.Error("flights: could not keep a saved flight's fuel: " + e.GetType().Name); }
            return mission;
        }
    }
}
