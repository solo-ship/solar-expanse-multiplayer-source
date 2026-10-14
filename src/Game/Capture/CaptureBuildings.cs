using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Company = Game.Company;
using EBonus = ScriptableObjectScripts.EBonus;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBaseDescriptor = Data.ScriptableObject.FacilityBaseDescriptor;
using FacilityInfoWindow = Game.UI.Windows.Windows.FacilityInfoWindow;
using NotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoWindow = Game.UI.Windows.Windows.ObjectInfoWindow;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SpaceModule = Game.ObjectInfoDataScripts.SpaceModule;
using SpaceModuleDescriptor = Data.ScriptableObject.SpaceModuleDescriptor;
using UIExploredResourcesList = Game.UI.Windows.Elements.ObjectInfoElements.UIExploredResourcesList;

namespace SolarExpanseMultiplayer
{
    // B1-B11, the building clicks (docs/research/commands.md, K10), each captured at the entry or popup delegate that
    // ends the click. A click that would only open a popup runs as the game wrote it; its OK delegate is the capture.
    //   B1 build now: Part1, when the player can pay; B2 queue: the popup's OK (the body from the delegate's window);
    //   B3 cancel and B4 scrap: the confirm popups' OK delegates; B7 units on and B8 priority: the slider and the
    //   +/- buttons; B10 deposit: the row click; B11 move to another body: the stock and module popups' OK;
    //   B9's energy and mirror edits are in the other part. The drops (B5, P4 and B11's one-module drop) are
    //   CaptureDrops. Every capture on an object another agency owns is refused with the note. The class needs a
    //   [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static partial class CaptureBuildings
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "ObjectInfoWindow.FacilityListOnOnClickCreateFacilityPart1", Build },
            { "ObjectInfoWindow/<>c__DisplayClass30_0.<FacilityListOnOnClickCreateFacilityPart1>b__2", BuildQueue },
            { "FacilityInfoWindow/<>c__DisplayClass74_0.<OnActionButtonClick>b__1", CancelOne },
            { "FacilityInfoWindow/<>c__DisplayClass74_0.<OnActionButtonClick>b__3", CancelMany },
            { "FacilityInfoWindow/<>c__DisplayClass72_0.<OnScrapButtonClick>b__2", ScrapOne },
            { "FacilityInfoWindow/<>c__DisplayClass72_0.<OnScrapButtonClick>b__4", ScrapMany },
            { "FacilityInfoWindow.OnEnabledFacilitiesSliderChange", UnitsOn },
            { "FacilityInfoWindow.ChangePriority", Priority },
            { "UIExploredResourcesList.OnClickDepositToMine", Deposit },
            { "ObjectInfo/<>c__DisplayClass452_1.<OnDragAndDrop>b__0", MoveStock },
            { "ObjectInfo/<>c__DisplayClass452_2.<OnDragAndDrop>b__2", MoveModules },
            { "EnergyTransferWindow.OnAddRowButtonClick", EnergyAdd },
            { "EnergyTransferWindow.OnTargetSelected", EnergyTarget },
            { "EnergyTransferWindow.RatioInputFieldEndEdit", EnergyShare },
            { "EnergyTransferWindow.OnRowTrashClick", EnergyRemove },
            { "MirrorTargetingWindow.OnAddRowButtonClick", MirrorAdd },
            { "MirrorTargetingWindow.OnTargetSelected", MirrorTarget },
            { "MirrorTargetingWindow.MirrorsInputFieldEndEdit", MirrorCount },
            { "MirrorTargetingWindow.OnRowTrashClick", MirrorRemove },
            { "MirrorTargetingWindow.OnCloseButtonClick", MirrorClose }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(ObjectInfoWindow), "FacilityListOnOnClickCreateFacilityPart1");
            yield return HookTargets.Lambda(typeof(ObjectInfoWindow), "<>c__DisplayClass30_0",
                "<FacilityListOnOnClickCreateFacilityPart1>b__2");
            yield return HookTargets.Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass74_0",
                "<OnActionButtonClick>b__1");
            yield return HookTargets.Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass74_0",
                "<OnActionButtonClick>b__3");
            yield return HookTargets.Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass72_0",
                "<OnScrapButtonClick>b__2");
            yield return HookTargets.Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass72_0",
                "<OnScrapButtonClick>b__4");
            yield return HookTargets.Method(typeof(FacilityInfoWindow), "OnEnabledFacilitiesSliderChange");
            yield return HookTargets.Method(typeof(FacilityInfoWindow), "ChangePriority");
            yield return HookTargets.Method(typeof(UIExploredResourcesList), "OnClickDepositToMine");
            yield return HookTargets.Lambda(typeof(ObjectInfo), "<>c__DisplayClass452_1", "<OnDragAndDrop>b__0");
            yield return HookTargets.Lambda(typeof(ObjectInfo), "<>c__DisplayClass452_2", "<OnDragAndDrop>b__2");
            foreach (MethodBase m in ListTargets()) yield return m;
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        static readonly AccessTools.FieldRef<ObjectInfoWindow, ObjectInfo> WindowBody =
            AccessTools.FieldRefAccess<ObjectInfoWindow, ObjectInfo>("objectInfoCurrent");
        static readonly AccessTools.FieldRef<FacilityInfoWindow, Facility> WindowFacility =
            AccessTools.FieldRefAccess<FacilityInfoWindow, Facility>("currentFacility");
        static readonly AccessTools.FieldRef<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>> Priorities =
            AccessTools.FieldRefAccess<ObjectInfoData, Dictionary<FacilityBaseDescriptor, int>>(
                "facilityBaseDescriptorToPriority");

        // ---- B1, B2: a new build or a queued one ----

        static bool Build(ICaptureSeam seam, object self, object[] args)
        {
            var window = (ObjectInfoWindow)self;
            var fbd = (FacilityBaseDescriptor)args[0];
            int count = (int)args[3];
            if (count <= 0) return true;   // the game logs it and does nothing
            ObjectInfo body = WindowBody(window);
            Company player = WindowCapture.Player();
            ObjectInfoData site = window.ObjectInfoDataCurrent ?? GameAgencies.Site(body, player);
            if (body == null || site == null || fbd == null) { WindowCapture.CannotSend(seam); return false; }
            var draft = new ActionRequest { Kind = ActionKind.Build, Agency = WindowCapture.Local(), Body = body.id,
                Item = fbd.ID, Count = count };
            // the window offers the queue (or shows a limit) when it cannot build now, and that popup is the game's;
            // another agency's window is refused by the gate
            if (ReferenceEquals(site.company, player) && !CanBuildNow(fbd, body, site, player, count)) return true;
            WindowCapture.Send(seam, draft, site.company);
            return false;
        }

        // What Part1 decides before it pays, with the same rules the host applies for the agency (BuildingRules): the
        // count capped by HowManyCanBuild, the largest affordable one, the station's cap. True = the window builds.
        static bool CanBuildNow(FacilityBaseDescriptor fbd, ObjectInfo body, ObjectInfoData site, Company player,
            int count)
        {
            ResourcePrice price = fbd.Price * player.BonusController.GetBonus(EBonus.BuildCost, fbd, body);
            int howMany;
            int canBuild = fbd.CanBuildParameter.HowManyCanBuild(site, fbd, out howMany);
            bool station = fbd.FacilityItemClass != typeof(SpaceStationFacilityNextSegment) && fbd.SpaceStationBuilding;
            int stationMax = 0, stationHave = 0;
            if (station)
            {
                stationMax = (int)Math.Min(int.MaxValue, StationCapRule.For(body));
                fbd.CanBuildParameter.HowManyCanBuild(site, null, out stationHave);
            }
            int built;
            // the game pays through RemoveResource, which also wants the money: short of it, the queue popup opens
            return BuildingRules.BuildNow(count, canBuild, n => site.HaveResources(price * n),
                n => site.HaveResources(price * n) && player.MoneyController.CurrentMoney >= (price * n).BuildCost,
                station, stationMax, stationHave, out built) == 0;
        }

        // The queue popup's OK. The body is the window the delegate was made in: it is read when the player answers,
        // not when it was asked (a popup that blocks the window cannot have moved it).
        static bool BuildQueue(ICaptureSeam seam, object self, object[] args)
        {
            var window = HookTargets.Field<ObjectInfoWindow>(self, "<>4__this");
            var fbd = HookTargets.Field<FacilityBaseDescriptor>(self, "obj");
            int count = HookTargets.Field<int>(self, "count");
            ObjectInfo body = WindowBody(window);
            if (body == null || fbd == null || count < 1) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.BuildQueue, Agency = WindowCapture.Local(), Body = body.id,
                Item = fbd.ID, Count = count };
            WindowCapture.Send(seam, a, null);   // the delegate queues on the player's own data
            return false;
        }

        // ---- the rows the host names (BuildingChecks.Rows: the unfinished builds, then the queue) ----

        // The row of an unfinished item, or -1.
        static int ItemPosition(ObjectInfoData site, long itemId)
        {
            List<BuildListRow> rows = BuildingChecks.Rows(site);
            for (int i = 0; i < rows.Count; i++) if (!rows[i].Queue && rows[i].ItemId == itemId) return i;
            return -1;
        }

        // The row of a queued entry's stand-in, or -1.
        static int QueuePosition(ObjectInfoData site, NotYetPay stand)
        {
            List<BuildListRow> rows = BuildingChecks.Rows(site);
            int first = 0;
            foreach (BuildListRow r in rows) if (!r.Queue) first++;
            int queued = 0;
            if (site.ListProductionQuery != null)
                foreach (var e in site.ListProductionQuery)
                {
                    if (e == null) continue;
                    NotYetPay found;
                    if (site.DictionaryFacilityNotYetPayCash.TryGetValue(e, out found) && ReferenceEquals(found, stand))
                        return first + queued;
                    queued++;
                }
            return -1;
        }

        internal static int RowOf(ObjectInfoData site, Facility f)
        {
            var stand = f as NotYetPay;
            return stand != null ? QueuePosition(site, stand) : ItemPosition(site, f.ID);
        }

        // ---- B3: cancel a build ----

        static bool CancelOne(ICaptureSeam seam, object self, object[] args)
        {
            return SendCancel(seam, HookTargets.Field<Facility>(self, "currentFacility"), 1);
        }

        static bool CancelMany(ICaptureSeam seam, object self, object[] args)
        {
            Facility f = HookTargets.Field<Facility>(self, "currentFacility");
            long typed;
            if (f == null || !long.TryParse((string)args[0], out typed)) return true;   // the game does nothing
            long left = f.CountToBuild - f.CountBuild;
            return SendCancel(seam, f, (int)Math.Max(1L, Math.Min(typed, left)));
        }

        static bool SendCancel(ICaptureSeam seam, Facility f, int count)
        {
            ObjectInfoData site = f != null ? f.ObjectInfoData : null;
            int row = site != null ? RowOf(site, f) : -1;
            if (site == null || site.ObjectInfo == null || f.facilityDescriptor == null || row < 0 || count < 1)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            var a = new ActionRequest { Kind = ActionKind.BuildCancel, Agency = WindowCapture.Local(),
                Body = site.ObjectInfo.id, Item = f.facilityDescriptor.ID, Count = count,
                ItemId = f is NotYetPay ? -1 : f.ID, Position = row };
            WindowCapture.Send(seam, a, f.Company);
            return false;
        }

        // ---- B4: scrap ----

        static bool ScrapOne(ICaptureSeam seam, object self, object[] args)
        {
            return SendScrap(seam, WindowFacility(HookTargets.Field<FacilityInfoWindow>(self, "<>4__this")), 1);
        }

        static bool ScrapMany(ICaptureSeam seam, object self, object[] args)
        {
            Facility f = WindowFacility(HookTargets.Field<FacilityInfoWindow>(self, "<>4__this"));
            long typed;
            if (f == null || !long.TryParse((string)args[0], out typed)) return true;   // the game does nothing
            // too many for the units that must stay on: the game's popup says so
            if (f.Quantity - typed < f.MinEnabledQuantity) return true;
            if (typed < 1 || typed > ActionLimits.MaxCount)
            {
                ClickGate.Refuse(seam, typed < 1 ? RefuseCodes.CannotSend : RefuseCodes.TooManyUnits,
                    "scrap count " + typed);
                return false;
            }
            return SendScrap(seam, f, (int)typed);
        }

        static bool SendScrap(ICaptureSeam seam, Facility f, int count)
        {
            if (f == null || f.ObjectInfoData == null || f.ObjectInfoData.ObjectInfo == null
                || f.facilityDescriptor == null)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            var a = new ActionRequest { Kind = ActionKind.Scrap, Agency = WindowCapture.Local(),
                Body = f.ObjectInfoData.ObjectInfo.id, Item = f.facilityDescriptor.ID, Count = count, ItemId = f.ID };
            WindowCapture.Send(seam, a, f.Company);
            return false;
        }

        // ---- B7, B8: units on, priority (set actions) ----

        static bool UnitsOn(ICaptureSeam seam, object self, object[] args)
        {
            if (HookTargets.Field<bool>(self, "notChangeSlider")) return true;   // the window moved its own slider
            var window = (FacilityInfoWindow)self;
            Facility f = WindowFacility(window);
            var slider = HookTargets.Field<UnityEngine.UI.Slider>(self, "enabledFacilitiesSlider");
            if (f == null || f.facilityDescriptor == null || f.ObjectInfoData == null || slider == null)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            long units = (long)Mathf.Floor(slider.value / slider.maxValue * (float)f.Quantity);
            var a = new ActionRequest { Kind = ActionKind.FacilityEnabled, Agency = WindowCapture.Local(),
                Body = f.ObjectInfoData.ObjectInfo.id, Item = f.facilityDescriptor.ID, ItemId = f.ID };
            // the number still on its way is the one this slider move is measured against
            int current = ClickGate.PendingOr(seam, a, (int)Math.Min(f.Enabled, (long)ActionLimits.MaxUnits));
            if (units == current) return false;
            a.Value = (int)Math.Max(0L, Math.Min(units, (long)ActionLimits.MaxUnits));
            WindowCapture.Send(seam, a, f.Company);
            return false;
        }

        // +/- on the priority: the new stored value is the pending one (or the stored one) plus the step, held to the
        // window's range; the window shows the stored value plus 5.
        static bool Priority(ICaptureSeam seam, object self, object[] args)
        {
            var window = (FacilityInfoWindow)self;
            Facility f = WindowFacility(window);
            if (f == null || f.facilityDescriptor == null || f.ObjectInfoData == null)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            Vector2Int range = HookTargets.Field<Vector2Int>(self, "minMaxPriority");
            var a = new ActionRequest { Kind = ActionKind.FacilityPriority, Agency = WindowCapture.Local(),
                Body = f.ObjectInfoData.ObjectInfo.id, Item = f.facilityDescriptor.ID };
            int stored = ClickGate.PendingOr(seam, a, StoredPriority(f.ObjectInfoData, f.facilityDescriptor));
            int shown = Mathf.Clamp(stored + 5 + (int)args[0], range.x, range.y);
            if (shown - 5 == stored) return false;
            a.Value = shown - 5;
            WindowCapture.Send(seam, a, f.Company);
            return false;
        }

        // GetPriority without its write: it stores the default it returns, which would change this game's site alone.
        internal static int StoredPriority(ObjectInfoData site, FacilityBaseDescriptor fbd)
        {
            Dictionary<FacilityBaseDescriptor, int> stored = Priorities(site);
            int p;
            return stored != null && stored.TryGetValue(fbd, out p) ? p : fbd.Priority;
        }

        // ---- B10: the deposit to mine ----

        static bool Deposit(ICaptureSeam seam, object self, object[] args)
        {
            var row = args[0];
            var data = HookTargets.Property<RowExploredResourcesData>(row, "ExploredResourcesData");
            // the window the click was made in (the list's parent), not the first object window
            object parent = AccessTools.Field(self.GetType(), "parentWindow") != null
                ? AccessTools.Field(self.GetType(), "parentWindow").GetValue(self)
                : AccessTools.Property(self.GetType(), "parentWindow") != null
                    ? AccessTools.Property(self.GetType(), "parentWindow").GetValue(self, null) : null;
            ObjectInfoWindow window = parent as ObjectInfoWindow
                ?? SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance.GetWindow<ObjectInfoWindow>();
            ObjectInfo body = window != null ? window.ObjectInfoCurrent : null;
            Company player = WindowCapture.Player();
            ObjectInfoData site = GameAgencies.Site(body, player);
            int position = site != null && site.listExploredResourcesRows != null
                ? site.listExploredResourcesRows.IndexOf(data) : -1;
            if (data == null || data.ResourceType == null || position < 0)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            var a = new ActionRequest { Kind = ActionKind.SelectDeposit, Agency = WindowCapture.Local(),
                Body = body.id, Item = data.ResourceType.ID, Position = position };
            // the desired state, not a toggle: a click while an earlier one is on its way flips that one
            bool selected = ClickGate.IsPending(seam, a) ? ClickGate.PendingOr(seam, a, 0) == 1 : data.SelectedToMine;
            a.Value = selected ? 0 : 1;
            WindowCapture.Send(seam, a, player);
            return false;
        }

        // ---- B11: stock or modules to another body ----

        static bool MoveStock(ICaptureSeam seam, object self, object[] args)
        {
            var outer = HookTargets.Field<object>(self, "CS$<>8__locals1");
            var to = HookTargets.Field<ObjectInfo>(outer, "<>4__this");
            var resource = HookTargets.Field<ResourceDefinition>(outer, "rd2");
            var data = HookTargets.Field<ObjectInfoData>(self, "oiDataFrom");
            double max = HookTargets.Field<double>(self, "howMuchToLeaveMax");
            double all = HookTargets.Field<double>(self, "howMuchToLeaveMax1");
            double result;
            double amount = double.TryParse((string)args[0], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out result)
                ? Math.Max(Math.Min(1.0, max), Math.Min(result, Math.Max(1.0, max))) : all;
            if (to == null || resource == null || data == null || data.ObjectInfo == null || !(amount > 0)
                || double.IsInfinity(amount))
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            var a = new ActionRequest { Kind = ActionKind.MoveToOrbit, Agency = WindowCapture.Local(),
                Body = data.ObjectInfo.id, ToBody = to.id, Item = resource.ID, Amount = amount };
            WindowCapture.Send(seam, a, data.company);
            return false;
        }

        // Several modules: the typed count, cut to the units there, as the delegate cuts it; a drop that makes more new
        // ids than a request may is sent as several.
        static bool MoveModules(ICaptureSeam seam, object self, object[] args)
        {
            var outer = HookTargets.Field<object>(self, "CS$<>8__locals2");
            var to = HookTargets.Field<ObjectInfo>(outer, "<>4__this");
            var sm = HookTargets.Field<SpaceModule>(self, "sm");
            int max = HookTargets.Field<int>(self, "howMuchToLeaveMax");
            int result;
            int typed = int.TryParse((string)args[0], NumberStyles.None, CultureInfo.InvariantCulture, out result)
                ? Math.Max(Math.Min(1, max), Math.Min(result, Math.Max(1, max))) : max;
            if (to == null || sm == null || sm.facilityDescriptor == null || sm.ObjectInfoData == null
                || sm.ObjectInfoData.ObjectInfo == null)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            int count = (int)Math.Min((long)typed, sm.Quantity);
            var smd = sm.facilityDescriptor as SpaceModuleDescriptor;
            ObjectInfoData target = GameAgencies.Site(to, WindowCapture.Player());
            FacilityBaseDescriptor added = smd != null && target != null
                ? BuildingChecks.Installed(smd, target, true) : null;
            bool block = (added != null && added.BlockStacking) || (smd != null && smd.BlockStacking);
            var list = new List<ActionRequest>();
            foreach (int part in MoveSplit.Chunks(count, block))
                list.Add(new ActionRequest { Kind = ActionKind.MoveToOrbit, Agency = WindowCapture.Local(),
                    Body = sm.ObjectInfoData.ObjectInfo.id, ToBody = to.id, Item = sm.facilityDescriptor.ID,
                    Count = part, ItemId = sm.ID });
            WindowCapture.SendAll(seam, list, sm.Company);
            return false;
        }
    }
}
