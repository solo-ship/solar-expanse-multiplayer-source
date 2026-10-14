using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Company = Game.Company;
using DragItem = Game.UI.DragAndDropSystem.DragAndDropScriptableObjectTransactItem;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBaseDescriptor = Data.ScriptableObject.FacilityBaseDescriptor;
using FacilityInfoWindow = Game.UI.Windows.Windows.FacilityInfoWindow;
using NotYetPay = Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObjectInfoWindow = Game.UI.Windows.Windows.ObjectInfoWindow;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchTree = Game.UI.Windows.Windows.ResearchTree.ResearchTree;
using ResearchTreeElement = Game.UI.Windows.Windows.ResearchTree.ResearchTreeElement;
using ResearchTreeRightPanel = Game.UI.Windows.Windows.ResearchTree.ResearchTreeRightPanel;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;
using SpaceModule = Game.ObjectInfoDataScripts.SpaceModule;
using UIExploredResourcesList = Game.UI.Windows.Elements.ObjectInfoElements.UIExploredResourcesList;
using UIManager = Game.UI.UIManager;
using UIRowFacility = Game.UI.Windows.Elements.ObjectInfoElements.UIRowFacility;

namespace SolarExpanseMultiplayer
{
    // The dev trigger "click <n>" (docs/research/commands.md, "Dev flags, triggers and faults"): line n of the action
    // script is driven through the game's own window, so the real capture prefix runs and the request that reaches the
    // session is the one a player's click would make. Each handler finds the window or row the click belongs to, and
    // calls the captured method the way the game's button does (a delegate's closure is made by hand where the click
    // goes through a popup's OK). A window that is not open, or a target that is not there, is a logged refusal, never
    // an exception. Test sessions only (the session registers it on ClickCapture); stateless.
    public static partial class WindowClicks
    {
        // The reason a click cannot be driven from where the game is now.
        sealed class ClickRefused : Exception { public ClickRefused(string why) : base(why) { } }

        static ClickRefused Refuse(string why) { return new ClickRefused(why); }

        // True when the kind has a handler (whatever it then did); false for a kind nobody clicks.
        public static bool Drive(ActionRequest a)
        {
            if (a == null || Array.IndexOf(ClickKinds.Player(), a.Kind) < 0) return false;
            string name = ActionKind.Name(a.Kind);
            try
            {
                string via = Run(a);
                Log.Info("command: click drove " + name + " " + via);
            }
            catch (ClickRefused r)
            {
                Log.Warn("command: click " + name + " refused: " + TextSanitiser.OneLine(r.Message));
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                Log.Warn("command: click " + name + " failed: " + inner.GetType().Name + ": "
                    + TextSanitiser.OneLine(inner.Message));
            }
            return true;
        }

        // Drives the click and says how: the window method, or the delegate.
        static string Run(ActionRequest a)
        {
            switch (a.Kind)
            {
                case ActionKind.ResearchStart: return ResearchStart(a);
                case ActionKind.ResearchAdd: return ResearchAdd(a);
                case ActionKind.ResearchAddWithPrereqs: return ResearchPrereqs(a);
                case ActionKind.ResearchSetQueue: return ResearchQueue(a);
                case ActionKind.Build: return Build(a);
                case ActionKind.BuildQueue: return BuildQueue(a);
                case ActionKind.BuildCancel: return Cancel(a);
                case ActionKind.Scrap: return Scrap(a);
                case ActionKind.BuildReorder: return ReorderBuild(a);
                case ActionKind.BuyAndBuild: return BuyMissing(a, false);
                case ActionKind.FacilityEnabled: return UnitsOn(a);
                case ActionKind.FacilityPriority: return Priority(a);
                case ActionKind.SelectDeposit: return Deposit(a);
                case ActionKind.MoveToOrbit: return Move(a);
                case ActionKind.EnergyTargets: return EnergyList(a);
                case ActionKind.MirrorTargets: return MirrorList(a);
                case ActionKind.CraftBuild: return CraftBuild(a);
                case ActionKind.CraftQueue: return CraftQueue(a);
                case ActionKind.CraftCancel: return CraftCancel(a);
                case ActionKind.CraftReorder: return CraftReorder(a);
                case ActionKind.CraftBuyAndBuild: return BuyMissing(a, true);
                case ActionKind.ObservatoryExamine: return Examine(a);
                case ActionKind.ObservatoryDiscover: return Discover(a);
                case ActionKind.ObservatorySurvey: return Survey(a);
                case ActionKind.ObservatoryStop: return StopObservatory(a);
                case ActionKind.ObservatoryNotify: return NotifyObservatory(a);
                case ActionKind.ForgetObject: return Forget(a);
                default: throw Refuse("no window handler");
            }
        }

        // ---- finding things ----

        static UIManager Ui { get { return SerializedMonoBehaviourSingleton<UIManager>.Instance; } }

        static Company Me()
        {
            Company c = WindowCapture.Player();
            if (c == null) throw Refuse("no player");
            return c;
        }

        static ObjectInfo Body(int id)
        {
            ObjectInfo o = GameAgencies.Body(id);
            if (o == null) throw Refuse("no body " + id);
            return o;
        }

        static ObjectInfoData Site(ObjectInfo body)
        {
            ObjectInfoData d = GameAgencies.Site(body, Me());
            if (d == null) throw Refuse("this agency has no data at " + body.id);
            return d;
        }

        static FacilityBaseDescriptor Descriptor(string id)
        {
            FacilityBaseDescriptor d = BuildingChecks.DescriptorOf(id);
            if (d == null) throw Refuse("no facility type " + id);
            return d;
        }

        // A facility of the site by item id: an unfinished build first, then a built one; a queue entry's stand-in
        // (item id -1) by its row.
        static Facility FacilityAt(ObjectInfoData site, ActionRequest a)
        {
            if (a.ItemId < 0)
            {
                int first = 0;
                foreach (BuildListRow r in BuildingChecks.Rows(site)) if (!r.Queue) first++;
                int q = a.Position - first, i = 0;
                if (q >= 0 && site.ListProductionQuery != null)
                    foreach (var e in site.ListProductionQuery)
                    {
                        if (e == null) continue;
                        NotYetPay stand;
                        if (i++ == q && site.DictionaryFacilityNotYetPayCash.TryGetValue(e, out stand)) return stand;
                    }
                throw Refuse("no queued entry at row " + a.Position);
            }
            if (site.ProductionItem != null)
                foreach (var p in site.ProductionItem)
                {
                    var f = p as Facility;
                    if (f != null && f.ID == a.ItemId) return f;
                }
            if (site.ListFacility != null)
                foreach (Facility f in site.ListFacility)
                    if (f != null && !(f is NotYetPay) && f.ID == a.ItemId) return f;
            throw Refuse("no facility with item id " + a.ItemId);
        }

        static T Window<T>(T w, string what) where T : Game.UI.Windows.Windows.UIWindow
        {
            if (w == null || !w.Open) throw Refuse(what + " is not open");
            return w;
        }

        static ObjectInfoWindow ObjectWindow(ObjectInfo body)
        {
            ObjectInfoWindow w = Window(Ui.GetWindow<ObjectInfoWindow>(), "the object window");
            if (w.ObjectInfoCurrent != body) throw Refuse("the object window is not on body " + body.id);
            return w;
        }

        static IEnumerable<T> Live<T>() where T : MonoBehaviour
        {
            foreach (T t in Resources.FindObjectsOfTypeAll<T>()) if (t != null && t.isActiveAndEnabled) yield return t;
        }

        // ---- calling ----

        static object Call(object target, string method, params object[] args)
        {
            MethodInfo m = AccessTools.Method(target.GetType(), method);
            if (m == null) throw Refuse(target.GetType().Name + "." + method + " was not found");
            return m.Invoke(target, args);
        }

        // A compiler-made closure with its captured fields set, and one of its lambdas called.
        static string Lambda(Type outer, string closure, string method, object[] fields, params object[] args)
        {
            object instance = Closure(outer, closure, fields);
            HookTargets.Lambda(outer, closure, method).Invoke(instance, args);
            return outer.Name + "/" + closure + "." + method;
        }

        static object Closure(Type outer, string closure, params object[] fields)
        {
            Type t = AccessTools.Inner(outer, closure);
            if (t == null) throw Refuse(outer.Name + "/" + closure + " was not found");
            object instance = Activator.CreateInstance(t, true);
            for (int i = 0; i + 1 < fields.Length; i += 2)
            {
                FieldInfo f = AccessTools.Field(t, (string)fields[i]);
                if (f == null) throw Refuse(t.Name + "." + fields[i] + " was not found");
                f.SetValue(instance, fields[i + 1]);
            }
            return instance;
        }

        static string Greyed(string path)
        {
            ICaptureSeam seam = Seam();
            if (!GreyedButtons.Reach(seam, path)) throw Refuse("no greyed path " + path);
            return "the greyed " + path;
        }

        // The session's seam for a click the dev trigger makes (an apply in progress would not capture).
        static ICaptureSeam Seam()
        {
            var owner = (object)Session.Commands as ICaptureOwner;
            if (owner == null || owner.Capture == null) throw Refuse("no session with commands");
            return owner.Capture;
        }

        // ---- research ----

        static ResearchDefinition Topic(string id)
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance
                .AllResearchDefinition;
            ResearchDefinition rd = all != null ? all.GetByID(id) : null;
            if (rd == null || rd.ID != id) throw Refuse("no research topic " + id);
            return rd;
        }

        static ResearchTree Tree() { return Window(Ui.GetWindow<ResearchTree>(), "the research tree"); }

        static string ResearchStart(ActionRequest a)
        {
            Tree().StartResearch(Topic(a.Item));
            return "ResearchTree.StartResearch " + a.Item;
        }

        static ResearchTreeRightPanel Panel()
        {
            return Window(Ui.GetWindow<ResearchTreeRightPanel>(), "the research panel");
        }

        static string ResearchAdd(ActionRequest a)
        {
            ResearchTree tree = Tree();
            ResearchDefinition rd = Topic(a.Item);
            ResearchTreeRightPanel panel = Panel();
            tree.SelectResearchDefinition(rd);
            tree.SelectResearchDefinition2(rd);
            Call(panel, "AddToQueryOnClick");
            return "the right panel's queue button for " + a.Item;
        }

        static string ResearchPrereqs(ActionRequest a)
        {
            ResearchTreeRightPanel panel = Panel();
            return Lambda(typeof(ResearchTreeRightPanel), "<>c__DisplayClass36_0", "<UpgradeOnClick>b__0",
                new object[] { "rd", Topic(a.Item), "<>4__this", panel });
        }

        static ResearchTreeElement ElementOf(string id)
        {
            foreach (ResearchTreeElement e in Live<ResearchTreeElement>())
            {
                var rd = HookTargets.Field<ResearchDefinition>(e, "researchDefinition");
                if (rd != null && rd.ID == id) return e;
            }
            throw Refuse("no tree element shows topic " + id);
        }

        // The whole queue the script names is one remove or one step from the game's: that button is clicked.
        static string ResearchQueue(ActionRequest a)
        {
            Tree();
            string[] current = Me().ResearchManager.queueRD.Select(d => d.ID).ToArray();
            QueueChange change = QueueDiff.Between(current, a.Ids ?? new string[0]);
            if (change == null) throw Refuse("the queue is not one remove or one step from the game's");
            ResearchTreeElement element = ElementOf(change.Id);
            if (change.Remove) Call(element, "DeleteFromQueue");
            else Call(element, "AddRemoveQueue", change.Up);
            return "the tree element of " + change.Id + (change.Remove ? " remove" : change.Up ? " up" : " down");
        }

        // ---- buildings ----

        static string Build(ActionRequest a)
        {
            ObjectInfo body = Body(a.Body);
            ObjectInfoWindow w = ObjectWindow(body);
            Call(w, "FacilityListOnOnClickCreateFacilityPart1", Descriptor(a.Item), body, true, a.Count);
            return "ObjectInfoWindow.FacilityListOnOnClickCreateFacilityPart1";
        }

        static string BuildQueue(ActionRequest a)
        {
            ObjectInfoWindow w = ObjectWindow(Body(a.Body));
            return Lambda(typeof(ObjectInfoWindow), "<>c__DisplayClass30_0",
                "<FacilityListOnOnClickCreateFacilityPart1>b__2",
                new object[] { "obj", Descriptor(a.Item), "count", a.Count, "<>4__this", w });
        }

        static string Cancel(ActionRequest a)
        {
            Facility f = FacilityAt(Site(Body(a.Body)), a);
            object[] fields = { "currentFacility", f, "facilityInfoWindow", null };
            return a.Count == 1
                ? Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass74_0", "<OnActionButtonClick>b__1", fields)
                : Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass74_0", "<OnActionButtonClick>b__3", fields,
                    a.Count.ToString(CultureInfo.InvariantCulture));
        }

        // Runs `act` with the facility window showing the facility, and puts the window back as it was.
        static string WithFacilityWindow(Facility f, Func<FacilityInfoWindow, string> act)
        {
            FacilityInfoWindow w = Window(Ui.GetWindow<FacilityInfoWindow>(), "the facility window");
            FieldInfo field = AccessTools.Field(typeof(FacilityInfoWindow), "currentFacility");
            object was = field.GetValue(w);
            field.SetValue(w, f);
            try { return act(w); }
            finally { field.SetValue(w, was); }
        }

        static string Scrap(ActionRequest a)
        {
            Facility f = FacilityAt(Site(Body(a.Body)), a);
            return WithFacilityWindow(f, w =>
            {
                object[] fields = { "<>4__this", w };
                return a.Count == 1
                    ? Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass72_0", "<OnScrapButtonClick>b__2", fields)
                    : Lambda(typeof(FacilityInfoWindow), "<>c__DisplayClass72_0", "<OnScrapButtonClick>b__4", fields,
                        a.Count.ToString(CultureInfo.InvariantCulture));
            });
        }

        static string ReorderBuild(ActionRequest a)
        {
            ObjectInfoData site = Site(Body(a.Body));
            Facility dragged = FacilityAt(site, a);
            List<BuildListRow> rows = BuildingChecks.Rows(site);
            if (a.ToPosition < 0 || a.ToPosition >= rows.Count || rows[a.ToPosition].Queue)
                throw Refuse("no unfinished build at row " + a.ToPosition);
            long targetId = rows[a.ToPosition].ItemId;
            foreach (UIRowFacility row in Live<UIRowFacility>())
            {
                Facility shown = HookTargets.Property<Facility>(row, "Facility");
                if (shown == null || shown.ID != targetId || !ReferenceEquals(shown.ObjectInfoData, site)) continue;
                Call(row, "OnDragAndDrop", new DragItem { helpObj2 = dragged });
                return "UIRowFacility.OnDragAndDrop";
            }
            throw Refuse("no build list row shows the build at row " + a.ToPosition);
        }

        static string UnitsOn(ActionRequest a)
        {
            Facility f = FacilityAt(Site(Body(a.Body)), a);
            if (f.Quantity < 1) throw Refuse("the facility has no finished units");
            return WithFacilityWindow(f, w =>
            {
                var slider = HookTargets.Field<UnityEngine.UI.Slider>(w, "enabledFacilitiesSlider");
                slider.SetValueWithoutNotify(slider.maxValue * (a.Value + 0.5f) / f.Quantity);
                Call(w, "OnEnabledFacilitiesSliderChange", slider.value);
                return "FacilityInfoWindow.OnEnabledFacilitiesSliderChange";
            });
        }

        static string Priority(ActionRequest a)
        {
            ObjectInfoData site = Site(Body(a.Body));
            FacilityBaseDescriptor fbd = Descriptor(a.Item);
            Facility f = null;
            if (site.ListFacility != null)
                foreach (Facility x in site.ListFacility)
                    if (x != null && !(x is NotYetPay) && x.facilityDescriptor == fbd) { f = x; break; }
            if (f == null) throw Refuse("no " + a.Item + " at " + a.Body);
            int diff = a.Value - CaptureBuildings.StoredPriority(site, fbd);
            if (diff == 0) throw Refuse("the priority is already " + a.Value);
            return WithFacilityWindow(f, w =>
            {
                for (int i = 0; i < Math.Abs(diff); i++) Call(w, "ChangePriority", Math.Sign(diff));
                return "FacilityInfoWindow.ChangePriority x" + Math.Abs(diff);
            });
        }

        static string Deposit(ActionRequest a)
        {
            ObjectInfoData site = Site(Body(a.Body));
            var rows = site.listExploredResourcesRows;
            if (rows == null || a.Position < 0 || a.Position >= rows.Count)
                throw Refuse("no deposit row " + a.Position);
            RowExploredResourcesData data = rows[a.Position];
            foreach (UIExploredResourcesList list in Live<UIExploredResourcesList>())
                foreach (var row in Live<Game.UI.Windows.Elements.ObjectInfoElements.UIRowExploredResources>())
                {
                    var shown = HookTargets.Property<RowExploredResourcesData>(row, "ExploredResourcesData");
                    if (!ReferenceEquals(shown, data)) continue;
                    if (data.SelectedToMine == (a.Value == 1)) throw Refuse("the deposit is already so");
                    Call(list, "OnClickDepositToMine", row);
                    return "UIExploredResourcesList.OnClickDepositToMine";
                }
            throw Refuse("no deposit row is shown");
        }

        static string Move(ActionRequest a)
        {
            ObjectInfo from = Body(a.Body), to = Body(a.ToBody);
            ObjectInfoData fromData = Site(from);
            if (a.Amount > 0)
            {
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                ResourceDefinition rd = all.AllResourceDefinitions.GetByID(a.Item);
                if (rd == null || rd.ID != a.Item) throw Refuse("no resource " + a.Item);
                double max = fromData.CheckResources(rd);
                object outer = Closure(typeof(ObjectInfo), "<>c__DisplayClass452_0", "<>4__this", to, "rd2", rd);
                return Lambda(typeof(ObjectInfo), "<>c__DisplayClass452_1", "<OnDragAndDrop>b__0",
                    new object[] { "CS$<>8__locals1", outer, "oiDataFrom", fromData, "howMuchToLeaveMax", max,
                        "howMuchToLeaveMax1", max }, a.Amount.ToString(CultureInfo.InvariantCulture));
            }
            Facility f = FacilityAt(fromData, a);
            var sm = f as SpaceModule;
            if (sm == null) throw Refuse("the item is not a space module");
            if (a.Count > 1)
            {
                object outer = Closure(typeof(ObjectInfo), "<>c__DisplayClass452_0", "<>4__this", to);
                return Lambda(typeof(ObjectInfo), "<>c__DisplayClass452_2", "<OnDragAndDrop>b__2",
                    new object[] { "CS$<>8__locals2", outer, "sm", sm, "howMuchToLeaveMax", a.Count },
                    a.Count.ToString(CultureInfo.InvariantCulture));
            }
            Call(to, "OnDragAndDrop", new DragItem { objectInfoData = fromData, helpObj = sm });
            return "ObjectInfo.OnDragAndDrop (one module)";
        }
    }
}
