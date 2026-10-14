using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ExplorationWindow = Game.UI.Windows.Windows.ExplorationWindow;
using MyCameraController = CameraControl.MyCameraController;
using FacilityInfoWindow = Game.UI.Windows.Windows.FacilityInfoWindow;
using MyDropdown = Game.UI.CommonControls.MyDropdown;
using ObjectInfo = Game.Info.ObjectInfo;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using UIObjectName = Game.UI.Windows.Elements.ObjectInfoElements.UIObjectName;

namespace SolarExpanseMultiplayer
{
    // O1-O5, the observatory's window (docs/research/observatories.md), and the asteroid "forget" button. The orders
    // are captured at the delegates that run them, so the game's own "stop it first?" question still asks the player;
    // its OK delegate sends the order alone, because the host stops a busy observatory before it orders. The examine
    // target and the discover group come from the window's search fields (a group by its index in the game's list,
    // never its id). The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class
    // without one.
    [HarmonyPatch]
    public static class CaptureObservatory
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "ExplorationWindow/<>c__DisplayClass77_0.<OnAcceptExamineButtonClick>b__0", Examine },
            { "ExplorationWindow/<>c__DisplayClass78_0.<OnAcceptDiscoverButtonClick>b__0", Discover },
            { "ExplorationWindow.<OnAssignExoplanetDiscoveryButtonClick>b__66_0", Survey },
            { "ExplorationWindow/<>c__DisplayClass76_0.<AskAndStopIfBusyThenRun>b__0", StopFirst },
            { "ExplorationWindow.OnStopButtonClick", StopFromWindow },
            { "FacilityInfoWindow.OnStopButtonClick", StopFromFacility },
            { "ExplorationWindow.HandleNotificationToggleChange", Notify },
            { "UIObjectName.<OnClickTrashObjectInfo>b__18_0", Forget }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Lambda(typeof(ExplorationWindow), "<>c__DisplayClass77_0",
                "<OnAcceptExamineButtonClick>b__0");
            yield return HookTargets.Lambda(typeof(ExplorationWindow), "<>c__DisplayClass78_0",
                "<OnAcceptDiscoverButtonClick>b__0");
            yield return HookTargets.Method(typeof(ExplorationWindow),
                "<OnAssignExoplanetDiscoveryButtonClick>b__66_0");
            yield return HookTargets.Lambda(typeof(ExplorationWindow), "<>c__DisplayClass76_0",
                "<AskAndStopIfBusyThenRun>b__0");
            yield return HookTargets.Method(typeof(ExplorationWindow), "OnStopButtonClick", typeof(int));
            yield return HookTargets.Method(typeof(FacilityInfoWindow), "OnStopButtonClick");
            yield return HookTargets.Method(typeof(ExplorationWindow), "HandleNotificationToggleChange",
                typeof(MyDropdown), typeof(bool));
            yield return HookTargets.Method(typeof(UIObjectName), "<OnClickTrashObjectInfo>b__18_0");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        // The request that names the observatory (a site facility by its body, type and item id, a probe by its ship),
        // or null when it cannot be named.
        static ActionRequest Order(int kind, ObservatoryFacility o)
        {
            ActionRequest a = o != null ? ObservatoryWorld.NameOf(o, kind) : null;
            if (a != null) a.Agency = WindowCapture.Local();
            return a;
        }

        static bool Send(ICaptureSeam seam, ActionRequest a, ObservatoryFacility o)
        {
            if (a == null) { WindowCapture.CannotSend(seam); return false; }
            WindowCapture.Send(seam, a, o.Company);
            return false;
        }

        // ---- O1, O2, O3 ----

        static bool Examine(ICaptureSeam seam, object self, object[] args)
        {
            var o = HookTargets.Field<ObservatoryFacility>(self, "observatory");
            object target = HookTargets.Field<object>(self, "targetObject");
            ActionRequest a = Order(ActionKind.ObservatoryExamine, o);
            if (a == null) return Send(seam, null, o);
            var body = target as ObjectInfo;
            var group = target as ObjectInfoGroups;
            if (body != null) a.ToBody = body.id;
            else if (group != null) a.Group = ObservatoryWorld.GroupIndex(group);
            else return Send(seam, null, o);
            if (WindowCapture.Send(seam, a, o.Company)) CloseAfterOrder(self, false);
            return false;
        }

        static bool Discover(ICaptureSeam seam, object self, object[] args)
        {
            var o = HookTargets.Field<ObservatoryFacility>(self, "observatory");
            var group = HookTargets.Field<ObjectInfoGroups>(self, "targetGroup");
            ActionRequest a = Order(ActionKind.ObservatoryDiscover, o);
            if (a != null && group != null) a.Group = ObservatoryWorld.GroupIndex(group);
            if (a == null) return Send(seam, null, o);
            if (WindowCapture.Send(seam, a, o.Company)) CloseAfterOrder(self, true);
            return false;
        }

        static readonly AccessTools.FieldRef<ExplorationWindow, UnityEngine.Transform> CameraTargetOnOpen =
            AccessTools.FieldRefAccess<ExplorationWindow, UnityEngine.Transform>("cameraTargetOnOpen");

        // The window closes once the order is sent, as the game closes it once the order is made (OnCloseClick and the
        // camera back on what it looked at when the window opened, D 134362-134363 and 134380-134381). A refusal
        // comes back in the game's dialog.
        static void CloseAfterOrder(object self, bool discover)
        {
            var w = HookTargets.Field<ExplorationWindow>(self, "<>4__this");
            if (w == null) return;
            try
            {
                MethodInfo close = AccessTools.Method(typeof(ExplorationWindow), "OnCloseClick");
                if (close == null) { Log.Warn("command: the observatory window has no OnCloseClick"); return; }
                close.Invoke(w, null);
                MyCameraController camera = MonoBehaviourSingleton<MyCameraController>.Instance;
                if (camera == null) return;
                if (discover) camera.ChangeTarget(CameraTargetOnOpen(w));
                else camera.Target = CameraTargetOnOpen(w);
            }
            catch (Exception e) { Log.Warn("command: the observatory window did not close: " + e.GetType().Name); }
        }

        static bool Survey(ICaptureSeam seam, object self, object[] args)
        {
            var o = HookTargets.Property<ObservatoryFacility>(self, "SelectedObservatory3");
            return Send(seam, Order(ActionKind.ObservatorySurvey, o), o);
        }

        // The "stop it first?" popup's OK: the order that follows is the capture, and the host does the stopping.
        static bool StopFirst(ICaptureSeam seam, object self, object[] args)
        {
            var action = HookTargets.Field<Action>(self, "action");
            if (action != null) { action(); return false; }
            var o = HookTargets.Field<ObservatoryFacility>(self, "observatory");
            return Send(seam, Order(ActionKind.ObservatoryStop, o), o);
        }

        // ---- O4, O5 ----

        static bool StopFromWindow(ICaptureSeam seam, object self, object[] args)
        {
            var all = HookTargets.Field<List<ObservatoryFacility>>(self, "allObservatories");
            int index = (int)args[0];
            if (all == null || index < 0 || index >= all.Count || all[index] == null) return true;   // the game's own
            return Send(seam, Order(ActionKind.ObservatoryStop, all[index]), all[index]);
        }

        // The facility window's stop button: only an observatory's stop is a command (the others have no stop).
        static bool StopFromFacility(ICaptureSeam seam, object self, object[] args)
        {
            var o = HookTargets.Field<object>(self, "currentFacility") as ObservatoryFacility;
            if (o == null) return true;
            return Send(seam, Order(ActionKind.ObservatoryStop, o), o);
        }

        static bool Notify(ICaptureSeam seam, object self, object[] args)
        {
            var all = HookTargets.Field<List<ObservatoryFacility>>(self, "allObservatories");
            var dropdown = args[0] as MyDropdown;
            if (all == null || dropdown == null || dropdown.value < 0 || dropdown.value >= all.Count) return true;
            ObservatoryFacility o = all[dropdown.value];
            ActionRequest a = Order(ActionKind.ObservatoryNotify, o);
            if (a != null) a.Value = (bool)args[1] ? 1 : 0;
            return Send(seam, a, o);
        }

        // ---- the asteroid "forget" button: its confirm popup's OK ----

        static bool Forget(ICaptureSeam seam, object self, object[] args)
        {
            var body = HookTargets.Field<ObjectInfo>(self, "objectInfoCurrent");
            if (body == null) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.ForgetObject, Agency = WindowCapture.Local(),
                Body = body.id };
            WindowCapture.Send(seam, a, null);
            return false;
        }
    }
}
