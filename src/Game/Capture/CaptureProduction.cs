using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using EBonus = ScriptableObjectScripts.EBonus;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using RowRocketData = Game.UI.Windows.Elements.ObjectInfoElements.RowRocketData;
using SpaceCraftConstructData = Game.UI.Windows.Elements.SpaceCraftConstructElements.SpaceCraftConstructData;
using SpaceCraftConstructionWindow = Game.UI.Windows.Windows.SpaceCraftConstructionWindow;
using SpaceCraftInfoWindow = Game.UI.Windows.Windows.SpaceCraftInfoWindow;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;

namespace SolarExpanseMultiplayer
{
    // P1-P4, the ship and launch-vehicle clicks (docs/research/commands.md, K10):
    //   P1 build: OnClickAcceptPart1, when the player can pay; P2 queue: the popup's OK;
    //   P3 cancel: the two CancelBuild methods the confirm popups call; P4 reorder is a drop (CaptureDrops).
    // A request names the window's body: the host moves an orbit-only ship to the low orbit itself, as the window
    // does. P5 (buy what is missing) is greyed. The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx)
    // silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureProduction
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "SpaceCraftConstructionWindow.OnClickAcceptPart1", Build },
            { "SpaceCraftConstructionWindow/<>c__DisplayClass25_0.<OnClickAcceptPart1>b__0", Queue },
            { "UIRowRocket.CancelBuild", CancelFromRow },
            { "SpaceCraftInfoWindow.CancelBuild", CancelFromInfo }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(SpaceCraftConstructionWindow), "OnClickAcceptPart1");
            yield return HookTargets.Lambda(typeof(SpaceCraftConstructionWindow), "<>c__DisplayClass25_0",
                "<OnClickAcceptPart1>b__0");
            yield return HookTargets.Method(typeof(UIRowRocket), "CancelBuild");
            yield return HookTargets.Method(typeof(SpaceCraftInfoWindow), "CancelBuild");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        static readonly AccessTools.FieldRef<SpaceCraftConstructionWindow, ObjectInfo> WindowBody =
            AccessTools.FieldRefAccess<SpaceCraftConstructionWindow, ObjectInfo>("objectInfoCurrent");

        // The type id the host looks up: the ship's type, else the launch vehicle's (the queue popup's own order).
        internal static string TypeId(SpaceCraftConstructData data)
        {
            if (data.SpacecraftType != null) return data.SpacecraftType.ID;
            return data.LaunchVehicleType != null ? data.LaunchVehicleType.ID : null;
        }

        // ---- P1, P2 ----

        static bool Build(ICaptureSeam seam, object self, object[] args)
        {
            var window = (SpaceCraftConstructionWindow)self;
            int count = (int)args[1];
            if (count <= 0) return true;   // the game logs it and does nothing
            SpaceCraftConstructData data = window.CurrentRowSpacecraftOrLaunchVehicle != null
                ? window.CurrentRowSpacecraftOrLaunchVehicle.SpacecraftConstructRowData : null;
            if (data == null) return true;   // "no spacecraft or vehicle selected": the game logs it
            ObjectInfo body = WindowBody(window);
            Company player = WindowCapture.Player();
            string type = TypeId(data);
            if (body == null || player == null || type == null) { WindowCapture.CannotSend(seam); return false; }
            // the window pays for the largest count it can afford and otherwise offers the queue (its OK is P2)
            ObjectInfo site = ProductionWorld.BuildBody(new CraftType { Data = data }, body);
            ResourcePrice price = data.Price * player.BonusController.GetBonus(EBonus.BuildCost, data.ProductionItem);
            price.CeilToInt();
            if (!CraftBuyRules.BuildsNow(count, n => site.HaveResources(price * n, player),
                n => player.MoneyController.CurrentMoney >= (price * n).BuildCost)) return true;
            var a = new ActionRequest { Kind = ActionKind.CraftBuild, Agency = WindowCapture.Local(), Body = body.id,
                Item = type, Count = count };
            WindowCapture.Send(seam, a, null);   // the window builds on the player's own data
            return false;
        }

        static bool Queue(ICaptureSeam seam, object self, object[] args)
        {
            var data = HookTargets.Field<SpaceCraftConstructData>(self, "data");
            var body = HookTargets.Field<ObjectInfo>(self, "objectInfoCurrent");
            int count = HookTargets.Field<int>(self, "count");
            string type = data != null ? TypeId(data) : null;
            if (body == null || type == null || count < 1) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.CraftQueue, Agency = WindowCapture.Local(), Body = body.id,
                Item = type, Count = count };
            WindowCapture.Send(seam, a, null);
            return false;
        }

        // ---- P3 ----

        static bool CancelFromRow(ICaptureSeam seam, object self, object[] args)
        {
            RowRocketData row = HookTargets.Property<RowRocketData>(self, "CurrentRowRocketData");
            return SendCancel(seam, row != null ? row.rConstruct : null);
        }

        static bool CancelFromInfo(ICaptureSeam seam, object self, object[] args)
        {
            RowRocketData row = HookTargets.Field<RowRocketData>(self, "rowRocketData");
            return SendCancel(seam, row != null ? row.rConstruct : null);
        }

        // The build is named by its row in the site's build list; a queued entry's stand-in has no item id (-1).
        static bool SendCancel(ICaptureSeam seam, SpacecraftConstructData construct)
        {
            ObjectInfoData site = construct != null ? construct.ObjectInfoData : null;
            int at = -1;
            List<SiteRow> rows = null;
            if (site != null) at = RowOf(site, construct, out rows);
            if (site == null || site.ObjectInfo == null || at < 0)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            bool queued = rows[at].Queued;
            var a = new ActionRequest { Kind = ActionKind.CraftCancel, Agency = WindowCapture.Local(),
                Body = site.ObjectInfo.id, Item = rows[at].Item, ItemId = queued ? -1 : construct.ID, Position = at };
            WindowCapture.Send(seam, a, site.company);
            return false;
        }

        // The row of a build, or of the queued entry it stands in for; -1 when the list does not hold it.
        internal static int RowOf(ObjectInfoData site, SpacecraftConstructData construct, out List<SiteRow> rows)
        {
            var things = new List<object>();
            rows = ProductionWorld.Rows(site, things);
            for (int i = 0; i < things.Count; i++)
            {
                if (ReferenceEquals(things[i], construct)) return i;
                var entry = things[i] as ProductionQuery;
                if (entry != null && ReferenceEquals(ProductionWorld.Placeholder(site, entry), construct)) return i;
            }
            return -1;
        }
    }
}
