using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoWindow = Game.UI.Windows.Windows.ObjectInfoWindow;
using UIWindow = Game.UI.Windows.Windows.UIWindow;

namespace SolarExpanseMultiplayer
{
    // S2, ObjectInfoData.AddToProductionFromQuery (the daily payment of a queued build, also run when an item is queued
    // and the stock is there). It checks that the queue's OWNER can afford the head item, then prices it with the local
    // player's bonus, pays from the local player's stock on that body (ObjectInfo.RemoveResources(price, Player)) and
    // names a ship after the local player. In vanilla only the player has a queue, so the two were one agency; with two
    // human agencies one game paid for the other's building from its own stock, or its payment failed and its queue
    // stalled (K1). In a session the owner's bonus prices it, the owner pays and the owner names the ship: seven calls
    // of GameManager.Player (the build-cost bonus of a ship and of a facility, the three payments and the two ship
    // names) become the data's own company. The method's remaining uses of the local player are the window refresh
    // under "company == the local player" and are left. Two calls of the info window's SetData(this) refresh the window
    // the local player has open on that body; for another agency's queue they would turn it into the other agency's
    // view, so they are skipped for any data that is not the local player's.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch(typeof(ObjectInfoData), "AddToProductionFromQuery")]
    public static class QueuePaymentRule
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            const string where = "ObjectInfoData.AddToProductionFromQuery";
            var payer = AccessTools.Method(typeof(QueuePaymentRule), "QueueOwner");
            List<CodeInstruction> owner = Il.PlayerToOwner(code, where, 7, (c, i) => i + 1 < c.Count &&
                (Il.Calls(c[i + 1], typeof(Company), "get_BonusController", 0) ||
                 Il.Calls(c[i + 1], typeof(ObjectInfo), "RemoveResources", 2) || NamesAShip(c, i)),
                OpCodes.Ldarg_0, payer);
            var show = AccessTools.Method(typeof(QueuePaymentRule), "RefreshWindow");
            List<CodeInstruction> shown = Il.Replace(owner, where + " (window refresh)", 2,
                (c, i) => i > 0 && c[i - 1].opcode == OpCodes.Ldarg_0 &&
                          Il.Calls(c[i], typeof(UIWindow), "SetData", 1),
                () => new[] { new CodeInstruction(OpCodes.Call, show) });
            var ship = AccessTools.Method(typeof(QueuePaymentRule), "OpenShipSection");
            var vehicle = AccessTools.Method(typeof(QueuePaymentRule), "OpenVehicleSection");
            List<CodeInstruction> ships = Il.Replace(shown, where + " (ship section)", 1,
                (c, i) => Il.Calls(c[i], typeof(ObjectInfoWindow), "OnAddSC", 0),
                () => new[] { new CodeInstruction(OpCodes.Call, ship) });
            return Il.Replace(ships, where + " (vehicle section)", 1,
                (c, i) => Il.Calls(c[i], typeof(ObjectInfoWindow), "OnAddLV", 0),
                () => new[] { new CodeInstruction(OpCodes.Call, vehicle) });
        }

        // The refresh note must not outlive one payment: a payment that skipped a refresh but never reached the
        // section call would otherwise swallow the next, legitimate one.
        [HarmonyFinalizer]
        static void ClearRefreshNote()
        {
            if (!HumanAgencies.Active) return;
            HumanAgencies.TakeRefreshSkipped();
        }

        public static void OpenShipSection(ObjectInfoWindow window)
        {
            if (!HumanAgencies.Active) { window.OnAddSC(); return; }
            if (!HumanAgencies.TakeRefreshSkipped()) window.OnAddSC();
        }

        public static void OpenVehicleSection(ObjectInfoWindow window)
        {
            if (!HumanAgencies.Active) { window.OnAddLV(); return; }
            if (!HumanAgencies.TakeRefreshSkipped()) window.OnAddLV();
        }

        // The ship's name is asked for a few instructions after the Player call (the type's name is loaded between).
        static bool NamesAShip(List<CodeInstruction> c, int i)
        {
            for (int j = i + 1; j <= i + 4 && j < c.Count; j++)
                if (Il.Calls(c[j], typeof(Company), "GetUniqueRocketName", 1)) return true;
            return false;
        }

        // The company that prices, pays for and names the queue's next item: the data's own, for a human agency.
        public static Company QueueOwner(GameManager manager, ObjectInfoData data)
        {
            if (!HumanAgencies.Active) return manager.Player;
            return HumanRules.OwnerPays(true, HumanAgencies.IsHuman(data.company)) ? data.company : manager.Player;
        }

        // `window.SetData(data)` as the game calls it after a payment; another agency's data never reaches the player.
        public static void RefreshWindow(UIWindow window, object data)
        {
            if (!HumanAgencies.Active) { window.SetData(data); return; }
            var info = data as ObjectInfoData;
            if (info != null && info.company != MonoBehaviourSingleton<GameManager>.Instance.Player)
            {
                HumanAgencies.NoteRefreshSkipped();
                return;
            }
            window.SetData(data);
        }
    }
}
