using System;
using HarmonyLib;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityInfoWindow = Game.UI.Windows.Windows.FacilityInfoWindow;
using MissionPresetController = Game.UI.Windows.Elements.PlanMissionElements.MissionPresetController;
using NotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoWindow = Game.UI.Windows.Windows.ObjectInfoWindow;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // After an entry has applied on this game (the host's OnPublished, a client's OnEntryApplied): the windows that
    // show what it changed are redrawn or closed with the calls the game's own click handler makes after its world
    // call (WindowRefreshPlan has them by kind, with the decompile lines). Only a window that shows the acting
    // agency's data is touched, so another agency's entry never changes what this player sees. A window that fails
    // to redraw must not stop the stream: the world has changed already. No state.
    static class WindowRefresh
    {
        static readonly AccessTools.FieldRef<FacilityInfoWindow, Facility> CardFacility =
            AccessTools.FieldRefAccess<FacilityInfoWindow, Facility>("currentFacility");

        static object PresetDropdown(MissionPresetController c)
        {
            return Traverse.Create(c).Field("presetDropdown").GetValue();
        }

        public static void After(ActionRequest a)
        {
            if (a == null) return;
            int plan = WindowRefreshPlan.For(a.Kind);
            if (plan == WindowRefreshPlan.None) return;
            try
            {
                UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
                if (ui == null) return;
                if (plan == WindowRefreshPlan.PresetList) Presets(ui, a);
                else if (plan == WindowRefreshPlan.CardCancel || plan == WindowRefreshPlan.CardScrap
                    || plan == WindowRefreshPlan.CardRedraw) Card(ui, plan, a);
                else
                {
                    ObjectInfoWindow main = ui.GetWindow<ObjectInfoWindow>();
                    ObjectInfoWindow second = ui.GetSecondWindow<ObjectInfoWindow>();
                    Site(main, plan, a);
                    if (!ReferenceEquals(second, main)) Site(second, plan, a);
                }
            }
            catch (Exception e)
            {
                Log.Warn("command: a window did not redraw after " + ActionKind.Name(a.Kind) + ": "
                    + e.GetType().Name);
            }
        }

        static bool Acting(Game.Company c, ActionRequest a) { return c != null && GameAgencies.Wire(c) == a.Agency; }

        // An object window open on the acting agency's data. A buy-and-build redraws it wherever it is open: the row
        // it was pressed in can stand for a low orbit's queue in its planet's window.
        static void Site(ObjectInfoWindow w, int plan, ActionRequest a)
        {
            if (w == null || !w.Open) return;
            ObjectInfoData data = w.ObjectInfoDataCurrent;
            if (data == null || data.ObjectInfo == null || !Acting(data.company, a)) return;
            if (plan != WindowRefreshPlan.SiteRedraw && data.ObjectInfo.id != a.Body) return;
            switch (plan)
            {
                case WindowRefreshPlan.FacilityAdded: w.OnAddFacilityModule(); break;
                case WindowRefreshPlan.FacilityQueued:
                    // RefreshFacilityList reads the queue of the local player's data: for any other it would mix two
                    if (ReferenceEquals(data.company, WindowCapture.Player())) w.RefreshFacilityList();
                    else w.SetData(data);
                    break;
                default: w.SetData(data); break;
            }
        }

        // The open planner's preset list, when the preset is this player's (the list reads the local player's own):
        // rebuilt from the agency's presets and the new one selected, without loading it, as OnSaveClicked leaves it.
        static void Presets(UIManager ui, ActionRequest a)
        {
            if (a.Agency != WindowCapture.Local()) return;
            PlanMissionWindow w = ui.GetWindow<PlanMissionWindow>();
            if (w == null || !w.Open) return;
            MissionPresetController presets = w.GetComponentInChildren<MissionPresetController>(true);
            Game.Company player = WindowCapture.Player();
            if (presets == null || player == null) return;
            presets.RefreshPresets();
            object list = PresetDropdown(presets);
            if (list == null) return;
            Traverse.Create(list).Method("SetValueWithoutNotify", player.MissionPresets.Count).GetValue();
            Traverse.Create(list).Method("RefreshShownValue").GetValue();
        }

        // The facility card open on the facility the entry named.
        static void Card(UIManager ui, int plan, ActionRequest a)
        {
            FacilityInfoWindow card = ui.GetWindow<FacilityInfoWindow>();
            if (card == null || !card.Open) return;
            Facility f = CardFacility(card);
            if (f == null || f.facilityDescriptor == null || f.facilityDescriptor.ID != a.Item) return;
            if (f.ObjectInfoData == null || f.ObjectInfoData.ObjectInfo == null) return;
            if (f.ObjectInfoData.ObjectInfo.id != a.Body || !Acting(f.Company, a)) return;
            switch (plan)
            {
                case WindowRefreshPlan.CardScrap:
                    if (f.ID != a.ItemId) return;
                    if (WindowRefreshPlan.CloseCard(f.Quantity))
                    {
                        CardFacility(card) = null;
                        card.HideNoImmediately();
                    }
                    else card.SetData(f);
                    break;
                case WindowRefreshPlan.CardCancel:
                    // a queued entry's stand-in has no item id of its own (-1 in the request)
                    if (a.ItemId == -1 ? !(f is NotYetPay) : f.ID != a.ItemId) return;
                    if (WindowRefreshPlan.CloseCard((long)f.CountToBuild - f.CountBuild)) card.HideNoImmediately();
                    else card.SetData(f);
                    break;
                default: card.SetData(f); break;
            }
        }
    }
}
