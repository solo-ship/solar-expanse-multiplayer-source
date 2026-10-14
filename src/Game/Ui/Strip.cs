using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // StripRules resolved to Types once: the allowlist by Type identity. A subclass of a kept type, or a class of the
    // same name elsewhere, is not in the map and is removed. An entry that does not resolve (a game update moved a
    // vetted class) leaves Problem set, and every clone is refused until the table is vetted again.
    public sealed class StripTable
    {
        readonly Dictionary<Type, StripAction> map = new Dictionary<Type, StripAction>();
        public readonly string Problem;

        public StripTable(IEnumerable<string> entries)
        {
            var assemblies = new Dictionary<string, Assembly>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (!assemblies.ContainsKey(n)) assemblies[n] = a;
            }
            foreach (string e in entries)
            {
                int bar = e == null ? -1 : e.IndexOf('|');
                Assembly asm;
                Type t = null;
                if (bar > 0 && assemblies.TryGetValue(e.Substring(0, bar), out asm))
                {
                    try { t = asm.GetType(e.Substring(bar + 1), false); }
                    catch (Exception) { t = null; }
                }
                if (t == null || KeyOf(t) != e)
                {
                    if (Problem == null) Problem = "allowlist entry not found: " + e;
                    continue;
                }
                map[t] = StripRules.Decide(t.Assembly.GetName().Name, t.FullName);
            }
        }

        static StripTable game;
        public static StripTable Game
        {
            get
            {
                if (game == null)
                {
                    var all = new List<string>(StripRules.Kept) { StripRules.CustomScrollRect };
                    game = new StripTable(all);
                }
                return game;
            }
        }

        public StripAction Of(Type t)
        {
            StripAction a;
            return t != null && map.TryGetValue(t, out a) ? a : StripAction.Remove;
        }

        public static string KeyOf(Type t) { return StripRules.Key(t.Assembly.GetName().Name, t.FullName); }
    }

    // Negative controls (test seams only): each feeds one fail-closed check a bad clone through the real recipe.
    public sealed class StripControl
    {
        public StripTable Table;              // an allowlist that does not resolve
        public Type KeepType;                 // the strip leaves this one on: "game script left" must refuse
        public bool BuildAnyway;              // the census control: build despite KeepType, so a real leak is counted
        public Graphic PlantOutside;          // a target graphic outside the clone
        public bool SkipTint;                 // step 2b skipped
        public Graphic PlantTintKey;          // after Awake, put into the clone's tint snapshot
        public bool TintClassGone;            // step 2b as if a game update had changed MyControlChildElements
        public Action<GameObject> Plant;      // anything else, on the inactive clone before the strip
    }

    // DD0's clone recipe, built once for every part:
    //  1. instantiate under an INACTIVE holder, so none of the clone's scripts wakes;
    //  2. remove every component off the allowlist with DestroyImmediate, in dependency order (CustomScrollRect is
    //     replaced by a plain ScrollRect);
    //  2b. copy the original's tint snapshot (MyControlChildElements.originalTintColor) onto the clone's colours, by
    //     sibling path: a live tinted control's colours are already tinted, and the clone's Awake would tint again;
    //  3. cut every link back to the game: toggle groups cleared, every UnityEvent replaced by a new empty one (the
    //     persistent listeners go too, and MyToggle's Awake then hooks the new event), delegates cleared, and
    //     keyboard navigation kept inside the clone;
    //  4. activate. Then the tint snapshot taken by Awake must name only graphics inside the clone.
    // Any check that fails destroys the clone and returns null: nothing half-built is ever shown.
    public static class Strip
    {
        public const string ObjectPrefix = "SEMP ";
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                    BindingFlags.DeclaredOnly;

        // Steps 2 and 3 on a clone made under an inactive holder. Returns the number of components removed (-1 when
        // the allowlist did not resolve: then nothing is removed and the clone must not be used).
        internal static int GameScripts(GameObject clone)
        {
            if (clone == null || StripTable.Game.Problem != null) return -1;
            var removed = new List<string>();
            int n = Remove(clone, StripTable.Game, null, removed);
            CutLinks(clone);
            return n;
        }

        // The whole recipe. prepare runs on the inactive clone before the strip (it must not add, remove or reorder
        // objects: step 2b finds its graphics by sibling path). drawnLike: where the game draws such a part, for its
        // pixel snapping (default: the original itself, when it is in a scene; a prefab needs the place its copies go,
        // such as the list a row is shown in). Returns the active clone under parent, or null.
        public static RectTransform Clone(GameObject original, Transform parent, string part, out string problem,
                                          Action<GameObject> prepare = null, StripControl control = null,
                                          Transform drawnLike = null)
        {
            problem = null;
            StripTable table = control != null && control.Table != null ? control.Table : StripTable.Game;
            if (table.Problem != null) problem = "the allowlist could not be resolved (" + table.Problem + ")";
            else if (original == null) problem = "no original";
            else if (!(original.transform is RectTransform)) problem = "not a UI object";
            else if (parent == null || !parent.gameObject.activeInHierarchy)
                problem = "the parent is not active, so the check after Awake could not run";
            if (problem != null) { Refused(part, problem); return null; }

            var sw = Stopwatch.StartNew();
            var holder = new GameObject(ObjectPrefix + "holder " + part);
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            GameObject clone = null;
            var removed = new List<string>();
            int cut = 0;
            string snap = null;
            try
            {
                clone = Object.Instantiate(original, holder.transform, false);
                clone.name = ObjectPrefix + part;
                if (prepare != null) prepare(clone);
                if (control != null && control.Plant != null) control.Plant(clone);
                Remove(clone, table, control != null ? control.KeepType : null, removed);
                cut = CutLinks(clone);
                if (control != null && control.PlantOutside != null)
                {
                    Selectable s = clone.GetComponentInChildren<Selectable>(true);
                    if (s != null) s.targetGraphic = control.PlantOutside;
                }
                List<string> left = Left(clone, table);
                List<string> outside = OutsideRefs(clone.transform);
                if (left.Count > 0 && !(control != null && control.BuildAnyway))
                    problem = "game scripts left: " + string.Join(", ", left.ToArray());
                else if (outside.Count > 0)
                    problem = "references outside the clone: " + string.Join("; ", First(outside, 6));
                else problem = DropdownCheck(clone.transform);
                if (problem == null && !(control != null && control.SkipTint))
                    problem = CopyTint(clone.transform, original.transform, control != null && control.TintClassGone);
                if (problem == null)
                {
                    if (drawnLike == null && original.scene.IsValid()) drawnLike = original.transform;
                    snap = MatchSnapping(clone, drawnLike, parent);
                }
            }
            catch (Exception e) { problem = "the recipe threw: " + Threw(e); }
            if (problem != null)
            {
                Object.DestroyImmediate(holder);
                Refused(part, problem);
                return null;
            }

            // 4. Out of the holder. The clone becomes active at SetParent when its original is active (its Awake and
            // OnEnable run there); a copy of an inactive original is switched on just after. Then the check after
            // Awake.
            try
            {
                clone.transform.SetParent(parent, false);
                clone.SetActive(true);
                if (control != null && control.PlantTintKey != null) PlantTint(clone.transform, control.PlantTintKey);
                problem = TintKeysInside(clone.transform);
            }
            catch (Exception e) { problem = "activation threw: " + Threw(e); }
            finally { if (holder != null) Object.DestroyImmediate(holder); }
            if (problem != null)
            {
                Object.DestroyImmediate(clone);
                Refused(part, problem);
                return null;
            }
            sw.Stop();
            Log.Info("ui: stripped " + removed.Count + " components from " + part);
            Log.Info("ui: clone " + part + " of '" + PathOf(original.transform) + "' in " +
                     sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms; removed [" + Counted(removed) +
                     "]; links cut " + cut + "; kept game classes [" + Counted(GameClasses(clone)) + "]" +
                     (control != null && control.SkipTint ? "; CONTROL: tint step skipped" : "") +
                     (control != null && control.BuildAnyway ? "; CONTROL: built with a game script left" : "") +
                     (snap != null ? "; " + snap : ""));
            return (RectTransform)clone.transform;
        }

        // The game draws some parts on a nested canvas that does not snap to pixels (the load list's Scroll View):
        // at a UI scale that is not whole, a copy that snaps lands a pixel away from it. The clone gets the same
        // snapping as where the game draws it, on a nested canvas of its own (with a raycaster, so it still takes
        // clicks). Returns what it did, or null.
        static string MatchSnapping(GameObject clone, Transform drawnLike, Transform parent)
        {
            if (drawnLike == null || parent == null) return null;
            bool want = SnapsPixels(drawnLike), have = SnapsPixels(parent);
            // a nested canvas draws past the mask of the list it sits in (rows' bars and logos showed below the
            // window): inside a mask the part keeps the list's snapping, at most a pixel off the original's
            if (want != have && UnderMask(parent)) return "no canvas of its own inside a masked list";
            Canvas own = clone.GetComponent<Canvas>();
            SnapFix fix = PixelSnap.Fix(want, have, own != null);
            if (fix == SnapFix.SetOwnCanvas)
            {
                own.overridePixelPerfect = true;
                own.pixelPerfect = want;
                return "its own canvas " + (want ? "snaps" : "does not snap") + " to pixels, as the original's";
            }
            if (fix == SnapFix.None) return null;
            Canvas c = clone.AddComponent<Canvas>();
            c.overridePixelPerfect = true;
            c.pixelPerfect = want;
            Canvas root = UiWidgets.RootCanvas(parent);
            if (root != null) c.additionalShaderChannels = root.additionalShaderChannels;
            clone.AddComponent<GraphicRaycaster>();
            return "a nested canvas that " + (want ? "snaps" : "does not snap") + " to pixels, as the original's";
        }

        // Whether t lies inside a Mask or RectMask2D (a scrolling list's viewport).
        public static bool UnderMask(Transform t)
        {
            for (Transform x = t; x != null; x = x.parent)
                if (x.GetComponent<Mask>() != null || x.GetComponent<RectMask2D>() != null) return true;
            return false;
        }

        // Whether graphics under t snap to pixels: the nearest canvas that overrides it decides, else the root's.
        public static bool SnapsPixels(Transform t)
        {
            var overrides = new List<bool>();
            var perfect = new List<bool>();
            for (Transform x = t; x != null; x = x.parent)
            {
                Canvas c = x.GetComponent<Canvas>();
                if (c == null) continue;
                overrides.Add(c.overridePixelPerfect);
                perfect.Add(c.pixelPerfect);
            }
            return PixelSnap.Snaps(overrides, perfect);
        }

        static void Refused(string part, string problem)
        {
            Log.Error("ui: part refused: " + part + ": " + problem + " (nothing shown)");
        }

        // Every component the table does not keep, deepest objects first, in StripRules' order. Several passes: a
        // component another one requires is only removable once that one is gone.
        static int Remove(GameObject clone, StripTable table, Type keep, List<string> removed)
        {
            int total = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                var items = new List<Component>();
                var ranks = new List<int>();
                Component[] all = clone.GetComponentsInChildren<Component>(true);
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    Component c = all[i];
                    if (c == null) continue;   // a missing script: no code, nothing to run
                    Type t = c.GetType();
                    StripAction a = table.Of(t);
                    if (a == StripAction.Keep || (keep != null && t == keep)) continue;
                    if (a == StripAction.ReplaceWithScrollRect)
                    {
                        ReplaceScrollRect((ScrollRect)c);
                        removed.Add(t.Name + "->ScrollRect");
                        total++;
                        continue;
                    }
                    items.Add(c);
                    ranks.Add(StripRules.RemoveRank(t.Assembly.GetName().Name, t.FullName, c is Graphic,
                                                    c is CanvasRenderer));
                }
                if (items.Count == 0) break;
                foreach (int i in StripRules.Order(ranks))
                {
                    Component c = items[i];
                    string name = c.GetType().Name;
                    Object.DestroyImmediate(c);
                    if (c == null) { removed.Add(name); total++; }
                }
            }
            return total;
        }

        static List<string> Left(GameObject clone, StripTable table)
        {
            var left = new List<string>();
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
                if (c != null && table.Of(c.GetType()) != StripAction.Keep) left.Add(c.GetType().FullName);
            return left;
        }

        static void ReplaceScrollRect(ScrollRect old)
        {
            GameObject go = old.gameObject;
            RectTransform content = old.content, viewport = old.viewport;
            bool h = old.horizontal, v = old.vertical, inertia = old.inertia;
            ScrollRect.MovementType move = old.movementType;
            float elasticity = old.elasticity, decel = old.decelerationRate, sens = old.scrollSensitivity;
            Scrollbar hs = old.horizontalScrollbar, vs = old.verticalScrollbar;
            ScrollRect.ScrollbarVisibility hv = old.horizontalScrollbarVisibility, vv = old.verticalScrollbarVisibility;
            float hsp = old.horizontalScrollbarSpacing, vsp = old.verticalScrollbarSpacing;
            Object.DestroyImmediate(old);
            ScrollRect n = go.AddComponent<ScrollRect>();
            n.content = content; n.viewport = viewport; n.horizontal = h; n.vertical = v; n.movementType = move;
            n.elasticity = elasticity; n.inertia = inertia; n.decelerationRate = decel; n.scrollSensitivity = sens;
            n.horizontalScrollbar = hs; n.verticalScrollbar = vs; n.horizontalScrollbarVisibility = hv;
            n.verticalScrollbarVisibility = vv; n.horizontalScrollbarSpacing = hsp; n.verticalScrollbarSpacing = vsp;
        }

        // Step 3. Returns how many links were cut.
        static int CutLinks(GameObject clone)
        {
            int n = 0;
            Transform root = clone.transform;
            foreach (Toggle t in clone.GetComponentsInChildren<Toggle>(true))
            {
                if (t.group != null) n++;
                t.group = null;
            }
            foreach (MonoBehaviour c in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                for (Type ty = c.GetType(); ty != null && ty != typeof(MonoBehaviour); ty = ty.BaseType)
                    foreach (FieldInfo f in ty.GetFields(Fields))
                    {
                        if (typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(f.FieldType))
                        {
                            f.SetValue(c, Activator.CreateInstance(f.FieldType));
                            n++;
                        }
                        else if (typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(c) != null)
                        {
                            f.SetValue(c, null);
                            n++;
                        }
                    }
                var sel = c as Selectable;
                if (sel != null)
                {
                    // read and written through the field: the property's setter runs DoStateTransition, game code
                    // (MyDropdown's dereferences its nonInteractableText) on a clone that has not woken
                    var nav = (Navigation)NavigationField.GetValue(sel);
                    if (nav.mode != Navigation.Mode.Explicit)
                    {
                        // automatic navigation searches every selectable on screen, the game's included
                        if (nav.mode != Navigation.Mode.None) { nav.mode = Navigation.Mode.None; n++; }
                    }
                    else
                    {
                        if (Outside(nav.selectOnUp, root)) { nav.selectOnUp = null; n++; }
                        if (Outside(nav.selectOnDown, root)) { nav.selectOnDown = null; n++; }
                        if (Outside(nav.selectOnLeft, root)) { nav.selectOnLeft = null; n++; }
                        if (Outside(nav.selectOnRight, root)) { nav.selectOnRight = null; n++; }
                    }
                    NavigationField.SetValue(sel, nav);
                }
            }
            return n;
        }

        static readonly FieldInfo NavigationField =
            typeof(Selectable).GetField("m_Navigation", BindingFlags.Instance | BindingFlags.NonPublic);

        static bool Outside(Component c, Transform root) { return c != null && !c.transform.IsChildOf(root); }

        // "Type: message at <first frame>", so a throw nobody expected says where it came from.
        static string Threw(Exception e)
        {
            string at = "";
            if (e.StackTrace != null)
            {
                string first = e.StackTrace.Split('\n')[0].Trim();
                if (first.Length > 0) at = " " + first;
            }
            return e.GetType().Name + ": " + e.Message + at;
        }

        // Every reference held on the clone that points at a scene object outside it (an asset is fine), the vetted
        // classes' child-graphic lists included.
        static List<string> OutsideRefs(Transform clone)
        {
            var found = new List<string>();
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                for (Type ty = c.GetType(); ty != null && ty != typeof(Component) && ty != typeof(Object);
                     ty = ty.BaseType)
                    foreach (FieldInfo f in ty.GetFields(Fields))
                    {
                        object v;
                        try { v = f.GetValue(c); } catch (Exception) { continue; }
                        foreach (Object o in Objects(v)) Check(o, clone, c.GetType().Name + "." + f.Name, found);
                        IList children = ChildGraphics(v);
                        if (children != null)
                            foreach (object g in children)
                                Check(g as Object, clone, c.GetType().Name + ".childElements", found);
                    }
            }
            return found;
        }

        static void Check(Object o, Transform clone, string where, List<string> found)
        {
            if (o == null) return;
            Transform t = o is GameObject ? ((GameObject)o).transform
                        : o is Component ? ((Component)o).transform : null;
            if (t == null || !t.gameObject.scene.IsValid()) return;   // an asset: sprite, font, prefab
            if (!t.IsChildOf(clone)) found.Add(where + " -> " + PathOf(t));
        }

        static IEnumerable<Object> Objects(object v)
        {
            var o = v as Object;
            if (!ReferenceEquals(o, null)) { if (o != null) yield return o; yield break; }
            var list = v as IEnumerable;
            if (list == null || v is string) yield break;
            foreach (object x in list)
            {
                var ox = x as Object;
                if (ox != null) yield return ox;
            }
        }

        // ---- the tint snapshot (MyControlChildElements) ----

        // Found by name, not typeof, so a game update that renames the class refuses clones instead of breaking the
        // whole kit when the class loads.
        static bool tintLooked;
        static Type childElementsType;
        static FieldInfo childList, tintSnapshot;

        static bool TintFields()
        {
            if (!tintLooked)
            {
                tintLooked = true;
                try
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == StripRules.Game)
                            childElementsType = a.GetType("Game.UI.CommonControls.MyControlChildElements", false);
                    if (childElementsType != null)
                    {
                        childList = childElementsType.GetField("childElements", Fields);
                        tintSnapshot = childElementsType.GetField("originalTintColor", Fields);
                    }
                }
                catch (Exception) { childElementsType = null; }
            }
            return childElementsType != null && childList != null && tintSnapshot != null;
        }

        static IList ChildGraphics(object v)
        {
            if (v == null || !TintFields() || v.GetType() != childElementsType) return null;
            return childList.GetValue(v) as IList;
        }

        // The vetted control's MyControlChildElements, or null for any other component.
        static object ChildElementsOf(Component c)
        {
            if (c == null) return null;
            Type t = c.GetType();
            if (!StripRules.IsTinted(t.Assembly.GetName().Name, t.FullName)) return null;
            FieldInfo f = t.GetField("childElements", Fields);
            return f != null ? f.GetValue(c) : null;
        }

        static Dictionary<Graphic, Color> SnapshotOf(object childElements)
        {
            if (childElements == null || !TintFields()) return null;
            return tintSnapshot.GetValue(childElements) as Dictionary<Graphic, Color>;
        }

        // Step 2b. A missing helper field fails closed: a game update changed the class the step depends on.
        // The tint snapshot a vetted control took in its Awake (null for any other component, or before Awake). The
        // bench's controls plant faults through it.
        internal static Dictionary<Graphic, Color> TintSnapshotOf(Component c)
        {
            return SnapshotOf(ChildElementsOf(c));
        }

        static string CopyTint(Transform clone, Transform original, bool classGone)
        {
            foreach (Selectable c in clone.GetComponentsInChildren<Selectable>(true))
            {
                if (ChildElementsOf(c) == null) continue;
                if (classGone || !TintFields()) return "MyControlChildElements changed: the tint step cannot run";
                Transform ot = FindSame(original, clone, c.transform);
                Component os = ot != null ? ot.GetComponent(c.GetType()) : null;
                if (os == null) return "tint: no original for " + PathOf(c.transform);
                Dictionary<Graphic, Color> snap = SnapshotOf(ChildElementsOf(os));
                if (snap == null) continue;   // the original never woke: its colours are still untinted
                foreach (KeyValuePair<Graphic, Color> kv in snap)
                {
                    if (kv.Key == null) continue;
                    if (!kv.Key.transform.IsChildOf(original))
                        return "tint: the original's snapshot names a graphic outside it: " + PathOf(kv.Key.transform);
                    Transform ct = FindSame(clone, original, kv.Key.transform);
                    Graphic g = ct != null ? ct.GetComponent(kv.Key.GetType()) as Graphic : null;
                    if (g == null) return "tint: no clone graphic for " + PathOf(kv.Key.transform);
                    g.color = kv.Value;
                }
            }
            return null;
        }

        // After Awake: every graphic a vetted control will recolour lies inside the clone.
        static string TintKeysInside(Transform clone)
        {
            foreach (Selectable c in clone.GetComponentsInChildren<Selectable>(true))
            {
                Dictionary<Graphic, Color> snap = SnapshotOf(ChildElementsOf(c));
                if (snap == null) continue;
                foreach (Graphic g in snap.Keys)
                    if (g == null || !g.transform.IsChildOf(clone))
                        return "after Awake, the tint snapshot of " + PathOf(c.transform) +
                               " names a graphic outside the clone: " +
                               (g == null ? "(destroyed)" : PathOf(g.transform));
            }
            return null;
        }

        static void PlantTint(Transform clone, Graphic outside)
        {
            foreach (Selectable c in clone.GetComponentsInChildren<Selectable>(true))
            {
                Dictionary<Graphic, Color> snap = SnapshotOf(ChildElementsOf(c));
                if (snap == null) continue;
                snap[outside] = outside.color;
                Log.Warn("ui: CONTROL: planted " + PathOf(outside.transform) + " in the tint snapshot of " +
                         PathOf(c.transform));
                return;
            }
            Log.Warn("ui: CONTROL: no tint snapshot to plant into");
        }

        static string DropdownCheck(Transform clone)
        {
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || StripTable.KeyOf(c.GetType()) != StripRules.Dropdown) continue;
                FieldInfo f = c.GetType().GetField("nonInteractableText", BindingFlags.Instance | BindingFlags.Public);
                var text = f != null ? f.GetValue(c) as Component : null;
                if (text == null || !text.transform.IsChildOf(clone))
                    return "a MyDropdown without its own nonInteractableText";
            }
            return null;
        }

        // ---- small helpers, shared with the parts kit ----

        // The object in tree B at the place `inA` has in tree A (by sibling indexes); null when B is shaped otherwise.
        public static Transform FindSame(Transform rootB, Transform rootA, Transform inA)
        {
            if (rootB == null || rootA == null || inA == null || !inA.IsChildOf(rootA)) return null;
            var idx = new List<int>();
            for (Transform x = inA; x != rootA; x = x.parent) idx.Insert(0, x.GetSiblingIndex());
            Transform y = rootB;
            foreach (int i in idx)
            {
                if (i >= y.childCount) return null;
                y = y.GetChild(i);
            }
            return y;
        }

        public static string PathOf(Transform t)
        {
            if (t == null) return "(none)";
            string s = t.name;
            for (Transform x = t.parent; x != null; x = x.parent) s = x.name + "/" + s;
            return s;
        }

        public static bool IsMod(Transform t)
        {
            for (Transform x = t; x != null; x = x.parent)
                if (x.name.StartsWith(ObjectPrefix, StringComparison.Ordinal)) return true;
            return false;
        }

        static List<string> GameClasses(GameObject clone)
        {
            var l = new List<string>();
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Assembly.GetName().Name == StripRules.Game) l.Add(c.GetType().Name);
            return l;
        }

        static string Counted(List<string> names)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string n in names) { int k; counts.TryGetValue(n, out k); counts[n] = k + 1; }
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts) parts.Add(kv.Key + " x" + kv.Value);
            return string.Join(", ", parts.ToArray());
        }

        static string[] First(List<string> l, int n)
        {
            return l.GetRange(0, Math.Min(n, l.Count)).ToArray();
        }
    }
}
