using System;
using System.Collections.Generic;
using HarmonyLib;
using EnergyTransferWindow = Game.UI.Windows.Windows.EnergyTransferWindow;
using ESpecialAbilityFacilityNew = Data.ScriptableObject.ESpecialAbilityFacilityNew;
using Facility = Game.ObjectInfoDataScripts.Facility;
using IdForObjectInfo = Game.Info.IdForObjectInfo;
using IEnergyTransfer = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.IEnergyTransferFacilityOrModule;
using LaunchVehicle = CustomUpdate.LaunchVehicle;
using MirrorFacility = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.SpaceMirrorOrShadeFacility;
using MirrorTargetingWindow = Game.UI.Windows.Windows.MirrorTargetingWindow;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using QueueEntry = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SpaceModule = Game.ObjectInfoDataScripts.SpaceModule;
using SpaceModuleDescriptor = Data.ScriptableObject.SpaceModuleDescriptor;
using Spacecraft = CustomUpdate.Spacecraft;
using TransferTargetInfo =
    Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyTransferFacility.TransferTargetInfo;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // The building kinds' world calls (Task 1's list in docs/research/commands.md "K10"), the window's own calls made
    // on the ACTING agency's site: on the host after BuildingChecks decided the request again (the world may have
    // changed since Check), on a client with the host's normalised request, its names resolved the same way. Each
    // apply reports the counters it moved (an asteroid-pulling facility or module takes a ship id inside the apply),
    // the item ids it made and the objects the day's prints must cover.
    public static class BuildingApply
    {
        static readonly AccessTools.FieldRef<EnergyTransferWindow, Facility> EnergyWindowFacility =
            AccessTools.FieldRefAccess<EnergyTransferWindow, Facility>("facility");
        static readonly AccessTools.FieldRef<MirrorTargetingWindow, MirrorFacility> MirrorWindowFacility =
            AccessTools.FieldRefAccess<MirrorTargetingWindow, MirrorFacility>("facility");

        public static ApplyOutcome Apply(ActionRequest a, ApplyNumbers given)
        {
            bool host = given == null;
            BuildingTarget t;
            int code = BuildingChecks.Decide(a, host, out t);
            if (code != 0) return Refused(code);
            ActionRequest x = t.Applied;
            Counts before = Counts.Of(t.Company);
            switch (x.Kind)
            {
                case ActionKind.Build:
                    // the window's two steps: pay for every unit, then one construction of n units (D 136837-136839)
                    if (!t.Site.RemoveResource(t.Price * x.Count)) return ActionApply.PaymentShortfall(host);
                    t.Site.AddFacility(t.Descriptor, false, x.Count);
                    break;
                case ActionKind.BuildQueue:
                    t.Site.AddProductItemToQuery(new QueueEntry { MyIDScriptableObjectProductionItem = t.Descriptor,
                        countToBuild = x.Count });
                    break;
                case ActionKind.BuildCancel:
                    // the game's own cancel logs and changes nothing past what is left: on a client that is a split
                    if ((long)t.Facility.CountToBuild - t.Facility.CountBuild < x.Count)
                        return Refused(RefuseCodes.TargetChanged);
                    t.Facility.CancelBuild(x.Count);
                    break;
                case ActionKind.Scrap: t.Facility.Scrap(x.Count); break;
                case ActionKind.BuildReorder:
                    int moved;
                    t.Site.SetProductItemBefore(t.Facility, t.Before, false, true, out moved);
                    t.Site.PreUpdateBuilding();
                    break;
                case ActionKind.FacilityEnabled: t.Facility.Enabled = x.Value; break;
                case ActionKind.FacilityPriority: t.Site.SetPriority(t.Descriptor, x.Value); break;
                case ActionKind.SelectDeposit: Deposit(t.Site, t.Deposit, x.Value == 1); break;
                case ActionKind.MoveToOrbit:
                    if (x.Amount > 0)
                    {
                        if (!t.Site.RemoveResource(t.Resource, x.Amount)) return ActionApply.PaymentShortfall(host);
                        t.ToSite.AddResources(t.Resource, x.Amount);
                    }
                    else MoveModules((SpaceModule)t.Facility, t.ToSite, x.Count, t.ModulesInOnePopup);
                    break;
                case ActionKind.EnergyTargets: Energy(t.Facility, x.Targets); break;
                case ActionKind.MirrorTargets: Mirror((MirrorFacility)t.Facility, x.Targets); break;
                default: return Refused(RefuseCodes.NotAllowedHere);
            }
            Counts after = Counts.Of(t.Company);
            ApplyNumbers numbers = Numbers(x, before, after, host);
            if (numbers == null) return Refused(RefuseCodes.NotAllowedHere);
            return new ApplyOutcome { Code = 0, Applied = x, Numbers = numbers, Touched = Touched(x) };
        }

        static ApplyOutcome Refused(int code) { return new ApplyOutcome { Code = code }; }

        // ---- the world calls the windows make inline ----

        // UIExploredResourcesList.OnClickDepositToMine (D 163216-163227): one selected deposit per resource.
        static void Deposit(ObjectInfoData site, RowExploredResourcesData row, bool select)
        {
            if (!select) { row.SelectedToMine = false; return; }
            foreach (RowExploredResourcesData other in site.listExploredResourcesRows)
                if (other != null && other.ResourceType == row.ResourceType) other.SelectedToMine = false;
            row.SelectedToMine = true;
        }

        // ObjectInfo.OnDragAndDrop's two module paths (D 124414-124444 with the count popup, D 124463-124479 for one
        // module), with the agency's own data at the target where the handler reads the local player's.
        static void MoveModules(SpaceModule sm, ObjectInfoData to, int n, bool popup)
        {
            var smd = sm.facilityDescriptor as SpaceModuleDescriptor;
            bool installer = smd != null
                && smd.specialAbilityFacilityNew == ESpecialAbilityFacilityNew.InstallationModule
                && smd.facilityOrModuleToInstall != null;
            if (!popup)
            {
                bool install = installer && !(smd.facilityOrModuleToInstall is SpaceModuleDescriptor)
                    && to.CanAddFacility(smd.facilityOrModuleToInstall);
                sm.Scrap(1L, false);
                if (install) to.AddFacility(smd.facilityOrModuleToInstall, true);
                else to.AddSpaceModuleWithValidityCheck(smd);
                return;
            }
            sm.Scrap(n, false);
            for (int i = 0; i < n; i++)
            {
                SpaceModuleDescriptor arrives = smd;
                bool asModule = true;
                if (installer)
                {
                    var inner = smd.facilityOrModuleToInstall as SpaceModuleDescriptor;
                    if (inner != null) arrives = inner;
                    else if (to.CanAddFacility(smd.facilityOrModuleToInstall))
                    {
                        asModule = false;
                        to.AddFacility(smd.facilityOrModuleToInstall, true);
                    }
                }
                if (asModule) to.AddSpaceModuleWithValidityCheck(arrives);
            }
            sm.ObjectInfoData.TryChangeValidChangeIfNeedSpaceStation(sm.facilityDescriptor);
        }

        // The whole list, as fresh rows: the receiver link a row caches is unsaved, so every game resolves it anew on
        // the next transfer, as after a load (an edited row would otherwise keep a stale link on one game only).
        static void Energy(Facility f, TargetRow[] rows)
        {
            List<TransferTargetInfo> list = ((IEnergyTransfer)f).Targets;
            list.Clear();
            foreach (TargetRow r in rows)
                list.Add(new TransferTargetInfo { target = Target(r.Body), ratio = (float)r.Ratio });
            Redraw(() =>
            {
                EnergyTransferWindow w =
                    SerializedMonoBehaviourSingleton<UIManager>.Instance.GetWindow<EnergyTransferWindow>();
                if (w != null && w.Open && ReferenceEquals(EnergyWindowFacility(w), f)) w.SetData(f);
            });
        }

        // The whole list: the default rows first (the check held their targets), then the player's, then the
        // allocation a load makes (SpaceMirrorOrShadeFacility.OnLoadState), so every game allocates the units alike.
        static void Mirror(MirrorFacility f, TargetRow[] rows)
        {
            int defaults = BuildingChecks.DefaultTargets(f).Count;
            f.ResetMirrorsAllocation();
            f.Targets.Clear();
            for (int i = 0; i < rows.Length; i++)
                f.Targets.Add(new MirrorFacility.MirrorTargetInfo { target = Target(rows[i].Body),
                    mirrorsCount = rows[i].Count, isDefault = i < defaults });
            if (f.Enabled > 0) f.AllocateMirrorsAcrossTargets(true);
            Redraw(() =>
            {
                MirrorTargetingWindow w =
                    SerializedMonoBehaviourSingleton<UIManager>.Instance.GetWindow<MirrorTargetingWindow>();
                if (w != null && w.Open && ReferenceEquals(MirrorWindowFacility(w), f)) w.SetData(f);
            });
        }

        // A row's target: none for NoTarget, as the windows' add-row makes it.
        static IdForObjectInfo Target(int body)
        {
            ObjectInfo o = body == TargetListRules.NoTarget ? null : GameAgencies.Body(body);
            return o != null ? new IdForObjectInfo(o) : new IdForObjectInfo();
        }

        // A window that shows the list redraws from the new rows; one that fails to must not undo what the world took.
        static void Redraw(Action redraw)
        {
            try { redraw(); }
            catch (Exception e) { Log.Warn("command: a target list window did not redraw: " + e.GetType().Name); }
        }

        // ---- what the apply reports ----

        // The counters an apply can move: the agency's item counter and the game-wide craft counters.
        struct Counts
        {
            public long Items, Ships, Vehicles;
            public static Counts Of(Game.Company c)
            {
                return new Counts { Items = c.productionCountID, Ships = Spacecraft.count,
                    Vehicles = LaunchVehicle.count };
            }
        }

        // B1 and a module move are the building kinds that make items (and, for an asteroid-pulling facility or module,
        // a ship), so they always carry the three counters; any other kind carries a counter only if it moved one.
        static ApplyNumbers Numbers(ActionRequest x, Counts before, Counts after, bool host)
        {
            bool makes = x.Kind == ActionKind.Build || (x.Kind == ActionKind.MoveToOrbit && x.Count > 0);
            var rows = new List<CounterRow>();
            if (makes || after.Items != before.Items)
                rows.Add(new CounterRow { Which = CounterKind.ProductionCountId, Agency = x.Agency,
                    Before = before.Items, After = after.Items });
            if (makes || after.Ships != before.Ships)
                rows.Add(new CounterRow { Which = CounterKind.SpacecraftCount, Before = before.Ships,
                    After = after.Ships });
            if (makes || after.Vehicles != before.Vehicles)
                rows.Add(new CounterRow { Which = CounterKind.LaunchVehicleCount, Before = before.Vehicles,
                    After = after.Vehicles });
            int cap = x.Kind == ActionKind.MoveToOrbit ? ActionLimits.MaxMoveNewIds : ActionLimits.MaxNewIds;
            long[] ids = BuildingRules.NewItemIds(before.Items, after.Items, cap);
            if (ids == null)
            {
                // the world has changed already, so the host cannot refuse: a new stream, every client rejoins
                string why = ActionKind.Name(x.Kind) + " moved the item counter from " + before.Items + " to "
                    + after.Items;
                if (host) throw new InvalidOperationException(why);
                Log.Warn("command: " + why);
                return null;
            }
            return new ApplyNumbers { Counters = rows.Count > 0 ? rows.ToArray() : null,
                NewIds = ids.Length > 0 ? ids : null };
        }

        static PrintEntry Print(int kind, string agency, int body)
        {
            return new PrintEntry { Kind = kind, Agency = agency, Body = body };
        }

        // The objects whose day print shows the change: a build list (builds and the queue), a site's facilities
        // (units, units on, priorities, the deposits mined). Stock and the target lists are in no print.
        static IList<PrintEntry> Touched(ActionRequest x)
        {
            var list = new List<PrintEntry>();
            switch (x.Kind)
            {
                case ActionKind.Build:
                    list.Add(Print(ObjectKind.SiteBuilds, x.Agency, x.Body));
                    list.Add(Print(ObjectKind.SiteFacilities, x.Agency, x.Body));
                    break;
                case ActionKind.BuildQueue: case ActionKind.BuildCancel: case ActionKind.BuildReorder:
                    list.Add(Print(ObjectKind.SiteBuilds, x.Agency, x.Body));
                    break;
                case ActionKind.MoveToOrbit:
                    if (x.Count > 0)
                    {
                        list.Add(Print(ObjectKind.SiteFacilities, x.Agency, x.Body));
                        list.Add(Print(ObjectKind.SiteFacilities, x.Agency, x.ToBody));
                    }
                    break;
                default:
                    list.Add(Print(ObjectKind.SiteFacilities, x.Agency, x.Body));
                    break;
            }
            return list;
        }
    }
}
