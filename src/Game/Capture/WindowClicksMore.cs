using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using DragItem = Game.UI.DragAndDropSystem.DragAndDropScriptableObjectTransactItem;
using EnergyTransferWindow = Game.UI.Windows.Windows.EnergyTransferWindow;
using ExplorationWindow = Game.UI.Windows.Windows.ExplorationWindow;
using Facility = Game.ObjectInfoDataScripts.Facility;
using IEnergyTransfer = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.IEnergyTransferFacilityOrModule;
using MirrorFacility = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.SpaceMirrorOrShadeFacility;
using MirrorTargetingWindow = Game.UI.Windows.Windows.MirrorTargetingWindow;
using MyDropdown = Game.UI.CommonControls.MyDropdown;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using SpaceCraftConstructData = Game.UI.Windows.Elements.SpaceCraftConstructElements.SpaceCraftConstructData;
using SpaceCraftConstructionWindow = Game.UI.Windows.Windows.SpaceCraftConstructionWindow;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using StackedRowRocketData = Game.UI.Windows.Elements.ObjectInfoElements.StackedRowRocketData;
using UIObjectName = Game.UI.Windows.Elements.ObjectInfoElements.UIObjectName;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;

namespace SolarExpanseMultiplayer
{
    // The click handlers for the two target-list windows, the production windows and the observatory (WindowClicks).
    public static partial class WindowClicks
    {
        // ---- B9: the list windows. One click makes the first difference between the game's list and the script's.

        static string EnergyList(ActionRequest a)
        {
            Facility f = FacilityAt(Site(Body(a.Body)), a);
            var list = f as IEnergyTransfer;
            if (list == null) throw Refuse("the item is not an energy transfer facility");
            EnergyTransferWindow w = Window(Ui.GetWindow<EnergyTransferWindow>(), "the energy transfer window");
            if (!ReferenceEquals(HookTargets.Field<Facility>(w, "facility"), f))
                throw Refuse("the energy window is not on that facility");
            return ListClick(w, "energyTransferRows", CaptureBuildings.EnergyRows(list), a.Targets, false,
                (op, row, i, wanted) =>
                {
                    switch (op)
                    {
                        case TargetOp.Add: Call(w, "OnAddRowButtonClick"); return "OnAddRowButtonClick";
                        case TargetOp.Remove: Call(w, "OnRowTrashClick", row); return "OnRowTrashClick";
                        case TargetOp.SetTarget:
                            Call(w, "OnTargetSelected", row, Body(wanted[i].Body));
                            return "OnTargetSelected";
                        default:
                            Call(w, "RatioInputFieldEndEdit", row,
                                (wanted[i].Ratio * 100).ToString("R", CultureInfo.CurrentCulture));
                            return "RatioInputFieldEndEdit";
                    }
                }, "EnergyTransferWindow");
        }

        static string MirrorList(ActionRequest a)
        {
            Facility f = FacilityAt(Site(Body(a.Body)), a);
            var mirror = f as MirrorFacility;
            if (mirror == null) throw Refuse("the item is not a space mirror");
            MirrorTargetingWindow w = Window(Ui.GetWindow<MirrorTargetingWindow>(), "the mirror targeting window");
            if (!ReferenceEquals(HookTargets.Field<MirrorFacility>(w, "facility"), f))
                throw Refuse("the mirror window is not on that facility");
            return ListClick(w, "mirrorTargetRows", CaptureBuildings.MirrorRows(mirror), a.Targets, true,
                (op, row, i, wanted) =>
                {
                    switch (op)
                    {
                        case TargetOp.Add: Call(w, "OnAddRowButtonClick"); return "OnAddRowButtonClick";
                        case TargetOp.Remove: Call(w, "OnRowTrashClick", row); return "OnRowTrashClick";
                        case TargetOp.SetTarget:
                            Call(w, "OnTargetSelected", row, Body(wanted[i].Body));
                            return "OnTargetSelected";
                        default:
                            Call(w, "MirrorsInputFieldEndEdit", row,
                                wanted[i].Count.ToString(CultureInfo.CurrentCulture));
                            return "MirrorsInputFieldEndEdit";
                    }
                }, "MirrorTargetingWindow");
        }

        static string ListClick(object window, string rowsField, TargetRow[] current, TargetRow[] wanted, bool mirror,
            Func<TargetOp, object, int, TargetRow[], string> click, string type)
        {
            int index;
            TargetOp op = TargetDiff.Between(current, wanted ?? new TargetRow[0], mirror, out index);
            if (op == TargetOp.None) throw Refuse("the list is already so");
            object row = null;
            if (index >= 0)
            {
                var rows = HookTargets.Field<IList>(window, rowsField);
                if (rows == null || index >= rows.Count) throw Refuse("the window shows no row " + index);
                row = rows[index];
            }
            return type + "." + click(op, row, index, wanted);
        }

        // ---- production ----

        static SpaceCraftConstructionWindow ConstructionWindow(ObjectInfo body, ActionRequest a)
        {
            SpaceCraftConstructionWindow w = Window(Ui.GetWindow<SpaceCraftConstructionWindow>(),
                "the construction window");
            if (HookTargets.Field<ObjectInfo>(w, "objectInfoCurrent") != body)
                throw Refuse("the construction window is not on body " + body.id);
            return w;
        }

        static SpaceCraftConstructData SelectedCraft(SpaceCraftConstructionWindow w, string item)
        {
            SpaceCraftConstructData data = w.CurrentRowSpacecraftOrLaunchVehicle != null
                ? w.CurrentRowSpacecraftOrLaunchVehicle.SpacecraftConstructRowData : null;
            if (data == null || CaptureProduction.TypeId(data) != item)
                throw Refuse("select " + item + " in the construction window first");
            return data;
        }

        static string CraftBuild(ActionRequest a)
        {
            ObjectInfo body = Body(a.Body);
            SpaceCraftConstructionWindow w = ConstructionWindow(body, a);
            SelectedCraft(w, a.Item);
            Call(w, "OnClickAcceptPart1", false, a.Count);
            return "SpaceCraftConstructionWindow.OnClickAcceptPart1";
        }

        static string CraftQueue(ActionRequest a)
        {
            ObjectInfo body = Body(a.Body);
            SpaceCraftConstructData data = SelectedCraft(ConstructionWindow(body, a), a.Item);
            return Lambda(typeof(SpaceCraftConstructionWindow), "<>c__DisplayClass25_0", "<OnClickAcceptPart1>b__0",
                new object[] { "count", a.Count, "data", data, "objectInfoCurrent", body });
        }

        // The build at the request's row of the site's build list.
        static SpacecraftConstructData CraftAt(ObjectInfoData site, int row)
        {
            var things = new List<object>();
            ProductionWorld.Rows(site, things);
            if (row < 0 || row >= things.Count) throw Refuse("no build list row " + row);
            var craft = things[row] as SpacecraftConstructData;
            if (craft == null)
            {
                var entry = things[row] as Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
                craft = entry != null ? ProductionWorld.Placeholder(site, entry) : null;
            }
            if (craft == null) throw Refuse("row " + row + " is not a ship or launch vehicle");
            return craft;
        }

        static UIRowRocket RocketRow(SpacecraftConstructData craft)
        {
            foreach (UIRowRocket row in Live<UIRowRocket>())
            {
                var stack = HookTargets.Field<StackedRowRocketData>(row, "currentStackedRowRocketData");
                if (stack != null && stack.Count > 0 && ReferenceEquals(stack[0].rConstruct, craft)) return row;
            }
            throw Refuse("no build row shows that ship");
        }

        static string CraftCancel(ActionRequest a)
        {
            ObjectInfoData site = Site(Body(a.Body));
            Call(RocketRow(CraftAt(site, a.Position)), "CancelBuild");
            return "UIRowRocket.CancelBuild";
        }

        static string CraftReorder(ActionRequest a)
        {
            ObjectInfoData site = Site(Body(a.Body));
            SpacecraftConstructData dragged = CraftAt(site, a.Position);
            Call(RocketRow(CraftAt(site, a.ToPosition)), "OnDragAndDrop", new DragItem { helpObj2 = dragged });
            return "UIRowRocket.OnDragAndDrop";
        }

        // ---- the observatory ----

        static ObservatoryFacility Observatory(ActionRequest a)
        {
            ObservatoryFacility o = ObservatoryWorld.Find(ObservatoryCommand.WithGameAgency(a, Me().ID));
            if (o == null) throw Refuse("no such observatory");
            return o;
        }

        static ExplorationWindow ExplorationOn(ObservatoryFacility o, out int index)
        {
            ExplorationWindow w = Window(Ui.GetWindow<ExplorationWindow>(), "the exploration window");
            var all = HookTargets.Field<List<ObservatoryFacility>>(w, "allObservatories");
            index = all != null ? all.IndexOf(o) : -1;
            if (index < 0) throw Refuse("the exploration window does not list that observatory");
            return w;
        }

        static string Examine(ActionRequest a)
        {
            ObservatoryFacility o = Observatory(a);
            object target = a.ToBody > 0 ? (object)Body(a.ToBody) : ObservatoryWorld.GroupAt(a.Group);
            if (target == null) throw Refuse("no target body or group");
            return Lambda(typeof(ExplorationWindow), "<>c__DisplayClass77_0", "<OnAcceptExamineButtonClick>b__0",
                new object[] { "observatory", o, "targetObject", target });
        }

        static string Discover(ActionRequest a)
        {
            ObservatoryFacility o = Observatory(a);
            return Lambda(typeof(ExplorationWindow), "<>c__DisplayClass78_0", "<OnAcceptDiscoverButtonClick>b__0",
                new object[] { "observatory", o, "targetGroup", ObservatoryWorld.GroupAt(a.Group) });
        }

        static string Survey(ActionRequest a)
        {
            int index;
            ExplorationWindow w = ExplorationOn(Observatory(a), out index);
            HookTargets.Field<MyDropdown>(w, "observatoriesDropDown3").value = index;
            Call(w, "<OnAssignExoplanetDiscoveryButtonClick>b__66_0");
            return "ExplorationWindow.OnAssignExoplanetDiscoveryButtonClick";
        }

        static string StopObservatory(ActionRequest a)
        {
            int index;
            ExplorationWindow w = ExplorationOn(Observatory(a), out index);
            Call(w, "OnStopButtonClick", index);
            return "ExplorationWindow.OnStopButtonClick";
        }

        static string NotifyObservatory(ActionRequest a)
        {
            int index;
            ExplorationWindow w = ExplorationOn(Observatory(a), out index);
            var dropdown = HookTargets.Field<MyDropdown>(w, "observatoriesDropDown");
            dropdown.value = index;
            Call(w, "HandleNotificationToggleChange", dropdown, a.Value == 1);
            return "ExplorationWindow.HandleNotificationToggleChange";
        }

        static string Forget(ActionRequest a)
        {
            foreach (UIObjectName name in Live<UIObjectName>())
            {
                var shown = HookTargets.Field<ObjectInfo>(name, "objectInfoCurrent");
                if (shown == null || shown.id != a.Body) continue;
                Call(name, "<OnClickTrashObjectInfo>b__18_0");
                return "the forget button's confirm";
            }
            throw Refuse("no object name bar shows body " + a.Body);
        }
    }
}
