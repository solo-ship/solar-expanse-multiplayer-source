using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using Company = Game.Company;
using EconomySystem = Manager.EconomySystem;
using GameManager = Manager.GameManager;
using MoneyController = Game.CompanyScripts.MoneyController;

namespace SolarExpanseMultiplayer
{
    // S7, S8 and EnableAI (docs/research/commands.md, section 2): a computer agency gets +Infinity money in
    // CompanyBehaviour.AfterLoadState and again in EnableAI, and Company.OnPlayerChange switches the AI of every
    // enabled rival on. For a human agency that would make its money infinite on every load and put a computer in
    // charge of it, so in a session none of the three runs for a human agency. The one exception is the player change
    // that switches the player's own AI off. The money a human agency has is the host's (SeatClaimed, DD8).
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class MoneyRules
    {
        // AfterLoadState and OnPlayerChange are explicit interface implementations: private methods named after the
        // interface, found through the interface map as M4's test fault patch does.
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return Required(Mapped(typeof(AiCompanyBehaviour), typeof(Manager.ISaveStateDataProvider),
                "AfterLoadState"), "CompanyBehaviour.AfterLoadState");
            yield return Required(AccessTools.Method(typeof(AiCompanyBehaviour), "EnableAI"),
                "CompanyBehaviour.EnableAI");
            yield return Required(Mapped(typeof(Company), typeof(Manager.IPlayerChangeCallbackReceiver),
                "OnPlayerChange"), "Company.OnPlayerChange");
        }

        [HarmonyPrefix]
        static bool SkipForHumans(object __instance)
        {
            if (!HumanAgencies.Active) return true;
            var behaviour = __instance as AiCompanyBehaviour;
            Company company = behaviour != null ? behaviour.Company : __instance as Company;
            Company player = Player();
            bool playerChangeOfThePlayer = __instance is Company && company != null && company == player;
            return !HumanRules.SkipsAiOrMoney(true, HumanAgencies.IsHuman(company), playerChangeOfThePlayer);
        }

        static Company Player()
        {
            GameManager m = MonoBehaviourSingleton<GameManager>.Instance;
            return m != null ? m.Player : null;
        }

        internal static MethodBase Mapped(Type type, Type contract, string name)
        {
            InterfaceMapping map = type.GetInterfaceMap(contract);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == name) return map.TargetMethods[i];
            return null;
        }

        // Harmony throws on a null target; saying which one makes the log line useful.
        internal static MethodBase Required(MethodBase m, string what)
        {
            if (m == null) throw new InvalidOperationException("the game has no " + what + " any more");
            return m;
        }
    }

    // S6, the bankruptcy branch of EconomySystem.OnEachDayChange: when RemoveMoney fails the local player's game ends
    // and any other agency is switched off, each game on its own money. In a session that decision is the host's (DD9):
    // for a human agency the day event treats the failed payment as made, so nobody's game ends and no game stops an
    // agency alone, and the host raises AgencyBankrupt into the stream, which stops the agency on every game together
    // (a tiny money difference cannot stop it on one side only). A rival stays with the game's branch.
    // One call in the day event is replaced, so no return value is patched.
    [HarmonyPatch(typeof(EconomySystem), "OnEachDayChange")]
    public static class BankruptcyRule
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            MethodInfo pay = AccessTools.Method(typeof(BankruptcyRule), "Pay");
            return Il.Replace(code, "EconomySystem.OnEachDayChange", 1,
                (c, i) => Il.Calls(c[i], typeof(MoneyController), "RemoveMoney", 1),
                () => new[] { new CodeInstruction(OpCodes.Call, pay) });
        }

        // The day event's `company.MoneyController.RemoveMoney(cost)`: the same call, and for a human agency whose
        // money ran out a success after the host has been told.
        public static bool Pay(MoneyController money, double cost)
        {
            if (!HumanAgencies.Active) return money.RemoveMoney(cost);
            bool paid = money.RemoveMoney(cost);
            if (paid) return true;
            Company company = money.GetComponent<Company>();
            if (!HumanRules.StopsOnBankruptcy(true, HumanAgencies.IsHuman(company))) return false;
            IHostDecisions host = HumanAgencies.Host;
            if (host != null) host.AgencyBankrupt(company.ID);
            return true;
        }
    }
}
