using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityInfoWindow = Game.UI.Windows.Windows.FacilityInfoWindow;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;
using UIResorcesList = Game.UI.Windows.Elements.ObjectInfoElements.UIResorcesList;

namespace SolarExpanseMultiplayer
{
    // The two world clicks that are not the market's or the contracts' (M7 DD-W8, DD-W13):
    //   85 the nuke: the "destroy asteroid" button of a nuclear bomb's facility window, as vanilla shows it (no popup,
    //      no cost); the explosion, the camera and the windows closing are the apply's;
    //   89 leave stock on a body: the amount dialog's OK (Alt-click on a stock row, or a drag onto the explored list;
    //      both end in the same dialog). The dialog and its amount stay the game's.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureWorld
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "FacilityInfoWindow.OnClickDestroyAsteroid", Nuke },
            { "UIResorcesList/<>c__DisplayClass8_0.<ShowLeaveResourceDialog>b__0", LeaveStock }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(FacilityInfoWindow), "OnClickDestroyAsteroid");
            yield return HookTargets.Lambda(typeof(UIResorcesList), "<>c__DisplayClass8_0",
                "<ShowLeaveResourceDialog>b__0");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        // 85: only a nuclear bomb's window does anything in the game; any other facility is the game's own no-op.
        static bool Nuke(ICaptureSeam seam, object self, object[] args)
        {
            var facility = HookTargets.Field<Facility>(self, "currentFacility");
            if (!(facility is NuclearBomb) || facility.ObjectInfoData == null || facility.facilityDescriptor == null)
                return true;
            ObjectInfo asteroid = facility.ObjectInfoData.ObjectInfo;
            var a = new ActionRequest { Kind = ActionKind.NukeAsteroid, Agency = WindowCapture.Local(),
                Body = WindowCapture.Id(asteroid), Item = facility.facilityDescriptor.ID, ItemId = facility.ID };
            WindowCapture.Send(seam, a, facility.Company);
            return false;
        }

        // 89: the dialog's OK delegate. A text that is not a positive number is the game's own "fail to parse".
        static bool LeaveStock(ICaptureSeam seam, object self, object[] args)
        {
            double typed;
            if (!double.TryParse(args[0] as string, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out typed) || !(typed > 0.0)) return true;
            var row = HookTargets.Field<RowResourcesData>(self, "rrd");
            var list = HookTargets.Field<UIResorcesList>(self, "<>4__this");
            var site = list != null ? HookTargets.Field<ObjectInfoData>(list, "objectInfoDataCurrent") : null;
            if (row == null || row.ResourcesType == null || site == null || site.ObjectInfo == null)
            { WindowCapture.CannotSend(seam); return false; }
            double amount = Math.Min(typed, row.Value);
            if (!(amount > 0.0)) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.LeaveStock, Agency = WindowCapture.Local(),
                Body = site.ObjectInfo.id, Item = row.ResourcesType.ID, Amount = amount };
            WindowCapture.Send(seam, a, site.company);
            return false;
        }
    }
}
