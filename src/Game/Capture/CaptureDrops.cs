using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using DragItem = Game.UI.DragAndDropSystem.DragAndDropScriptableObjectTransactItem;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using SpaceModule = Game.ObjectInfoDataScripts.SpaceModule;
using SpacecraftConstructData = Data.SpacecraftConstructData;
using StackedRowRocketData = Game.UI.Windows.Elements.ObjectInfoElements.StackedRowRocketData;
using UIRowFacility = Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility;
using UIRowRocket = Game.UI.Windows.Elements.ObjectInfoElements.UIRowRocket;

namespace SolarExpanseMultiplayer
{
    // The drops: B5 (reorder a facility build, on a row), P4 (reorder a ship or launch-vehicle build) and B11's drop of
    // one module on another body. They are the game's IDragAndDropTarget.OnDragAndDrop, whose answer matters: when a
    // target says "not handled" the drag manager offers the same item to the next component under the pointer, so a
    // captured drop answers "handled" (true) after it was sent or refused, and the game never moves it a second time.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureDrops
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "UIRowFacility.OnDragAndDrop", ReorderBuild },
            { "UIRowRocket.OnDragAndDrop", ReorderCraft },
            { "ObjectInfo.OnDragAndDrop", MoveOneModule }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(UIRowFacility), "OnDragAndDrop");
            yield return HookTargets.Method(typeof(UIRowRocket), "OnDragAndDrop");
            yield return HookTargets.Method(typeof(ObjectInfo), "OnDragAndDrop");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args, ref bool __result)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            if (CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args)) return true;
            __result = true;
            return false;
        }

        // ---- B5 ----

        // A drop on an unfinished facility's row. Another agency's unfinished build can be dragged (the game checks the
        // owner after the unfinished test), so the owner is the dragged item's, and the capture refuses it.
        static bool ReorderBuild(ICaptureSeam seam, object self, object[] args)
        {
            var item = args[0] as DragItem;
            var dragged = item != null ? item.helpObj2 as Facility : null;
            Facility target = HookTargets.Property<Facility>(self, "Facility");
            if (dragged == null || target == null) return true;   // not a build being dragged: the game's own
            if (target.Quantity != 0) return true;                // a finished row: the game changes nothing
            if (ReferenceEquals(dragged, target)) return false;
            ObjectInfoData site = dragged.ObjectInfoData;
            if (site == null || site.ObjectInfo == null || !ReferenceEquals(site, target.ObjectInfoData)
                || dragged.facilityDescriptor == null)
            {
                WindowCapture.CannotSend(seam);
                return false;
            }
            int from = CaptureBuildings.RowOf(site, dragged), to = CaptureBuildings.RowOf(site, target);
            if (from < 0 || to < 0) { WindowCapture.CannotSend(seam); return false; }
            var a = new ActionRequest { Kind = ActionKind.BuildReorder, Agency = WindowCapture.Local(),
                Body = site.ObjectInfo.id, Item = dragged.facilityDescriptor.ID, ItemId = dragged.ID, Position = from,
                ToPosition = to };
            WindowCapture.Send(seam, a, dragged.Company);
            return false;
        }

        // ---- P4 ----

        // A drop of one of the player's builds on another row, or on the front. Dropping on a queued entry's stand-in
        // moves nothing in the game, so nothing is sent.
        static bool ReorderCraft(ICaptureSeam seam, object self, object[] args)
        {
            var item = args[0] as DragItem;
            var dragged = item != null ? item.helpObj2 as SpacecraftConstructData : null;
            if (dragged == null) return true;   // not a build being dragged: the game's own
            ObjectInfoData site = dragged.ObjectInfoData;
            var stack = HookTargets.Field<StackedRowRocketData>(self, "currentStackedRowRocketData");
            SpacecraftConstructData target = stack != null && stack.Count > 0 ? stack[0].rConstruct : null;
            if (site == null || site.ObjectInfo == null) { WindowCapture.CannotSend(seam); return false; }
            List<SiteRow> rows;
            int from = CaptureProduction.RowOf(site, dragged, out rows);
            int to = 0;
            if (target != null)
            {
                to = CaptureProduction.RowOf(site, target, out rows);
                if (to >= 0 && rows[to].Queued) return false;
            }
            if (from < 0 || to < 0 || rows[from].Queued) { WindowCapture.CannotSend(seam); return false; }
            if (from == to) return false;   // already there: nothing to do
            var a = new ActionRequest { Kind = ActionKind.CraftReorder, Agency = WindowCapture.Local(),
                Body = site.ObjectInfo.id, Item = rows[from].Item, ItemId = dragged.ID, Position = from,
                ToPosition = to };
            WindowCapture.Send(seam, a, site.company);
            return false;
        }

        // ---- B11 ----

        static DragBody DragOf(ObjectInfo o)
        {
            return new DragBody { Id = o.id, Parent = o.parentObjectInfo != null ? o.parentObjectInfo.id : 0,
                Orbit = o.objectTypes == Data.EObjectTypes.Orbit,
                PlanetOrMoon = o.objectTypes == Data.EObjectTypes.Planet || o.objectTypes == Data.EObjectTypes.Moons,
                Linked = o.AsteroidFromPush || o.interstellarConstructionFacility };
        }

        // The drop of one module (nothing to ask): the game moves it at once. Stock and several modules ask the
        // player how many first, and their popups' OK delegates are the captures (CaptureBuildings). Any other drop
        // opens the mission planner (M6) and runs as the game wrote it.
        static bool MoveOneModule(ICaptureSeam seam, object self, object[] args)
        {
            var item = args[0] as DragItem;
            var to = (ObjectInfo)self;
            if (item == null || item.objectInfoData == null || item.objectInfoData.ObjectInfo == null) return true;
            ObjectInfo from = item.objectInfoData.ObjectInfo;
            int pair = MoveRules.Pair(DragOf(from), DragOf(to));
            if (pair == DragPair.None) return true;
            Company player = WindowCapture.Player();
            bool lifted = pair != DragPair.Up || from.CanDragToOrbit(player);
            if (!lifted) return true;
            var stock = item.item as ResourceDefinition;
            if (stock != null && stock.ResourceType == ResourceDefinition.EResourceType.Normal) return true;
            var sm = item.helpObj as SpaceModule;
            if (sm == null || sm.facilityDescriptor == null) return true;
            if (from.GetAvailableCountOffSpaceModule(sm) > 1) return true;   // the count popup
            var a = new ActionRequest { Kind = ActionKind.MoveToOrbit, Agency = WindowCapture.Local(), Body = from.id,
                ToBody = to.id, Item = sm.facilityDescriptor.ID, Count = 1, ItemId = sm.ID };
            WindowCapture.Send(seam, a, sm.Company);
            return false;
        }
    }
}
