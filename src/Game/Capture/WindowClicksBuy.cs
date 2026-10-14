using System.Reflection;
using HarmonyLib;
using NotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using ObjectInfo = Game.Info.ObjectInfo;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using UIRowFacility = Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;

namespace SolarExpanseMultiplayer
{
    // "Buy what is missing and build now" (B6, P5) driven through the row that shows the queued entry: the row says
    // where it would buy and what, and its own button method is what the player's Ctrl-click or button calls, so the
    // real capture (CaptureMarket) makes the request.
    public static partial class WindowClicks
    {
        static string BuyMissing(ActionRequest a, bool craft)
        {
            ObjectInfo body = Body(a.Body);
            if (!craft)
            {
                foreach (UIRowFacility row in Live<UIRowFacility>())
                {
                    var queued = HookTargets.Field<object>(row, "facility") as NotYetPay;
                    if (queued == null || queued.facilityDescriptor == null || queued.facilityDescriptor.ID != a.Item
                        || queued.CountToBuild != a.Count || !ReferenceEquals(queued.ObjectInfoData.ObjectInfo, body))
                        continue;
                    return Press(row, "UIRowFacility");
                }
                throw Refuse("no build list row shows a queued " + a.Item + " x" + a.Count + " at " + a.Body);
            }
            foreach (UIRowRocket row in Live<UIRowRocket>())
            {
                object data = HookTargets.Property<object>(row, "CurrentRowRocketData");
                var queued = data != null ? HookTargets.Field<object>(data, "rConstruct") as SpacecraftConstructData : null;
                string type = queued == null ? null : queued.SpacecraftType != null ? queued.SpacecraftType.ID
                    : queued.LaunchVehicleType != null ? queued.LaunchVehicleType.ID : null;
                if (type != a.Item || queued.CountToBuild != a.Count
                    || !ReferenceEquals(queued.ObjectInfoData.ObjectInfo, body)) continue;
                return Press(row, "UIRowRocket");
            }
            throw Refuse("no craft row shows a queued " + a.Item + " x" + a.Count + " at " + a.Body);
        }

        // The row's own pair: where it buys and the price, then the button method.
        static string Press(object row, string type)
        {
            MethodInfo enable = AccessTools.Method(row.GetType(), "BuyResourcesOnMarketEnable",
                new[] { typeof(ObjectInfo).MakeByRefType(), typeof(ResourcePrice).MakeByRefType() });
            MethodInfo press = AccessTools.Method(row.GetType(), "BuyResourcesOnMarket",
                new[] { typeof(ObjectInfo), typeof(ResourcePrice) });
            if (enable == null || press == null) throw Refuse(type + " has no buy-what-is-missing button");
            var args = new object[2];
            if (!(bool)enable.Invoke(row, args)) throw Refuse("the row cannot buy what is missing");
            press.Invoke(row, args);
            return type + ".BuyResourcesOnMarket";
        }
    }
}
