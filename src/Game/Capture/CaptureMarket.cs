using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using FacilityToConstructNotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using MarketOfferMakeOfferWindow = Game.UI.Windows.Windows.MarketOfferMakeOfferWindow;
using ObjectInfo = Game.Info.ObjectInfo;
using Offer = Game.ObjectInfoDataScripts.Offer;
using OfferData = Game.ObjectInfoDataScripts.OfferData;
using OfferUIRow = Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using TakeOfferDialog = Game.UI.Windows.Windows.TakeOfferDialog;
using UIRowFacility = Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;
using UIWindow = Game.UI.Windows.Windows.UIWindow;

namespace SolarExpanseMultiplayer
{
    // The offer board's clicks (M7 DD-W4; the board is MarketOfferWindow, from the toolbar and from a planet's
    // "Marketplace" button) and "buy what is missing" (B6, P5), each captured where the click ends:
    //   80 make and 82 edit: the make-offer window's button (an edit has the offer it came from);
    //   81 cancel: that window's delete button, and the row's own button on an offer of the player's;
    //   83 take: the take dialog's accept, after its bankruptcy popup (the offer's key, the count, the price and the
    //      units left that the dialog saw);
    //   84 "Immediately": the confirm popup's OK, with the typed price as the limit;
    //   15 and 34: the two rows' buy-and-build button, once for the facility rows and once for the craft rows.
    // The windows' own tests (the button is enabled only when valid) are repeated on the host, so a request that gets
    // here is only the click. A fractional price is rounded to a whole one and a count to a whole number: the protocol's
    // offers are whole. MarketWindow.BuyResources and SellResources are unused in the game and stay refused.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureMarket
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "MarketOfferMakeOfferWindow.MakeOfferButtonOnClick", MakeOrEdit },
            { "MarketOfferMakeOfferWindow.OnClickDeleteOffer", DeleteOffer },
            { "MarketOfferMakeOfferWindow.<OnClickButtonImmediatelyTransaction>b__39_0", Immediately },
            { "OfferUIRow.TakeOfferButton", RowButton },
            { "TakeOfferDialog.OnClickAcceptPart2", TakeOffer },
            { "UIRowFacility.BuyResourcesOnMarket", BuyFacility },
            { "UIRowRocket.BuyResourcesOnMarket", BuyCraft }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(MarketOfferMakeOfferWindow), "MakeOfferButtonOnClick");
            yield return HookTargets.Method(typeof(MarketOfferMakeOfferWindow), "OnClickDeleteOffer");
            yield return HookTargets.Method(typeof(MarketOfferMakeOfferWindow),
                "<OnClickButtonImmediatelyTransaction>b__39_0");
            yield return HookTargets.Method(typeof(OfferUIRow), "TakeOfferButton");
            yield return HookTargets.Method(typeof(TakeOfferDialog), "OnClickAcceptPart2");
            yield return HookTargets.Method(typeof(UIRowFacility), "BuyResourcesOnMarket", typeof(ObjectInfo),
                typeof(ResourcePrice));
            yield return HookTargets.Method(typeof(UIRowRocket), "BuyResourcesOnMarket", typeof(ObjectInfo),
                typeof(ResourcePrice));
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        // ---- the window's own private calls and its closing ----

        static object Call(object target, string method)
        {
            MethodInfo m = AccessTools.Method(target.GetType(), method);
            if (m == null) throw new InvalidOperationException(target.GetType().Name + "." + method + " was not found");
            return m.Invoke(target, null);
        }

        static void Close(object window, bool immediately)
        {
            var w = window as UIWindow;
            if (w == null) return;
            if (immediately) w.HideImmediately(); else w.HideNoImmediately();
        }

        // ---- the offers (the requests themselves are MarketRequests', so the tests build them the same way) ----

        // The whole price and count the protocol carries; null when the typed numbers leave nothing to offer.
        static MarketOp NewOffer(OfferData data, string agency)
        {
            if (data == null || data.whereOffer == null || data.rd == null) return null;
            return MarketRequests.NewOffer(agency, data.rd.ID, data.buySell, data.whereOffer.id, data.countToBuySell,
                data.pricePerUnit);
        }

        // 80, 82: the make-offer button. The game's popup about a price far from the market's, when it shows, is the
        // click: nothing is sent until the player clicks again.
        static bool MakeOrEdit(ICaptureSeam seam, object self, object[] args)
        {
            var data = HookTargets.Field<OfferData>(self, "offerDataCurrent");
            var from = HookTargets.Field<Offer>(self, "fromOffer");
            string agency = WindowCapture.Local();
            if (!(bool)Call(self, "ValidCreatedOffer")) { WindowCapture.CannotSend(seam); return false; }
            if ((bool)Call(self, "ShowPopUpPricePerUnit")) return false;
            MarketOp add = NewOffer(data, agency);
            if (add == null) { WindowCapture.CannotSend(seam); return false; }
            ActionRequest a = MarketRequests.Make(agency, add, from != null ? WorldMarket.KeyOf(from) : null);
            if (WindowCapture.Send(seam, a, data.company)) Close(self, false);
            return false;
        }

        // 81: the delete button of the window an offer was opened in.
        static bool DeleteOffer(ICaptureSeam seam, object self, object[] args)
        {
            var from = HookTargets.Field<Offer>(self, "fromOffer");
            if (from == null) { WindowCapture.CannotSend(seam); return false; }
            ActionRequest a = MarketRequests.CancelOffer(WindowCapture.Local(), WorldMarket.KeyOf(from));
            if (WindowCapture.Send(seam, a, from.Company)) Close(self, false);
            return false;
        }

        // 81: the board row's button is a cancel on an offer of the player's own, else it opens the take dialog.
        static bool RowButton(ICaptureSeam seam, object self, object[] args)
        {
            var offer = HookTargets.Field<Offer>(self, "offer");
            if (offer == null || !ReferenceEquals(offer.Company, WindowCapture.Player())) return true;
            WindowCapture.Send(seam, MarketRequests.CancelOffer(WindowCapture.Local(), WorldMarket.KeyOf(offer)), null);
            return false;
        }

        // 83: the dialog's accept (after the bankruptcy popup, which stays the game's).
        static bool TakeOffer(ICaptureSeam seam, object self, object[] args)
        {
            var offer = HookTargets.Field<Offer>(self, "offer");
            int count = HookTargets.Field<int>(self, "count");
            ActionRequest a = offer == null ? null
                : MarketRequests.Take(WindowCapture.Local(), WorldMarket.KeyOf(offer), count, offer.PricePerUnit,
                    offer.CountLeft);
            if (a == null) { WindowCapture.CannotSend(seam); return false; }
            if (WindowCapture.Send(seam, a, null)) Close(self, false);
            return false;
        }

        // 84: the confirm popup's OK of "Immediately". Source 1 (the make-offer window); the typed price is the limit.
        static bool Immediately(ICaptureSeam seam, object self, object[] args)
        {
            var data = HookTargets.Field<OfferData>(self, "offerDataCurrent");
            if (data == null || data.whereOffer == null || data.rd == null || !(data.countToBuySell > 0))
            { WindowCapture.CannotSend(seam); return false; }
            ActionRequest a = MarketRequests.Instant(WindowCapture.Local(), data.whereOffer.id, data.rd.ID,
                data.buySell, data.countToBuySell, data.pricePerUnit);
            if (WindowCapture.Send(seam, a, data.company)) Close(self, true);
            return false;
        }

        // ---- B6 and P5: buy what is missing and build now ----

        static bool BuyFacility(ICaptureSeam seam, object self, object[] args)
        {
            var where = args[0] as ObjectInfo;
            var queued = HookTargets.Field<object>(self, "facility") as FacilityToConstructNotYetPay;
            if (where == null || queued == null || queued.facilityDescriptor == null)
            { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.BuyAndBuild, Agency = WindowCapture.Local(), Body = where.id,
                Item = queued.facilityDescriptor.ID, Count = queued.CountToBuild };
            WindowCapture.Send(seam, a, queued.Company);
            return false;
        }

        static bool BuyCraft(ICaptureSeam seam, object self, object[] args)
        {
            var where = args[0] as ObjectInfo;
            object row = HookTargets.Property<object>(self, "CurrentRowRocketData");
            var queued = row != null ? HookTargets.Field<object>(row, "rConstruct") as SpacecraftConstructData : null;
            string type = queued == null ? null : queued.SpacecraftType != null ? queued.SpacecraftType.ID
                : queued.LaunchVehicleType != null ? queued.LaunchVehicleType.ID : null;
            if (where == null || type == null) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.CraftBuyAndBuild, Agency = WindowCapture.Local(),
                Body = where.id, Item = type, Count = queued.CountToBuild };
            WindowCapture.Send(seam, a, null);
            return false;
        }
    }
}
