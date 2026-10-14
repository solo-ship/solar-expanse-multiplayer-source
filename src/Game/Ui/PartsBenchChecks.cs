using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The parts bench's measuring side (test seams only): the handler census, the originals' snapshot, a game-state
    // probe, and a part compared with its original pixel by pixel in the game's own frame.
    public sealed class BenchChecks
    {
        readonly string outDir;
        // instance ids of everything the bench built, kept after the objects die
        readonly HashSet<int> made = new HashSet<int>();
        static List<FieldInfo> staticDelegates;
        static readonly Dictionary<Type, FieldInfo[]> instanceDelegates = new Dictionary<Type, FieldInfo[]>();
        static readonly HashSet<Type> KitTypes = new HashSet<Type>
        {
            typeof(PartsBench), typeof(BenchChecks), typeof(Strip), typeof(GameParts), typeof(UiWidgets),
            typeof(ModDialog), typeof(SpeedStrip), typeof(SignalBarsWidget), typeof(TmpGlyphs), typeof(UiFonts),
            typeof(ModalRegistry), typeof(ModalPopups),
        };

        public int Compared, Same, Different, ControlsPassed, ControlsFailed;
        public readonly List<string> Failures = new List<string>();

        public BenchChecks(string outDir) { this.outDir = outDir; }

        public static string Res { get { return Screen.width + "x" + Screen.height; } }

        // Everything under root, so a handler left on it is still known as ours once it is destroyed.
        public void Track(GameObject root)
        {
            if (root == null) return;
            made.Add(root.GetInstanceID());
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c != null) { made.Add(c.GetInstanceID()); made.Add(c.gameObject.GetInstanceID()); }
        }

        public void Fail(string what)
        {
            Failures.Add(what);
            Log.Error("ui: bench FAIL: " + what);
        }

        public void Control(string name, bool held, string detail)
        {
            if (held) { ControlsPassed++; Log.Info("ui: bench control " + name + ": held (" + detail + ")"); }
            else { ControlsFailed++; Fail("control " + name + " did not hold (" + detail + ")"); }
        }

        // ---- the census ----

        // Every static delegate of the game's code, every delegate field on the game's scene objects and the runtime
        // listeners of every game button and toggle. A handler whose target is the kit, a clone, or something the
        // bench built and destroyed counts as ours.
        public HandlerCensus Census()
        {
            var cs = new HandlerCensus();
            Assembly game = typeof(MenuSceneUI).Assembly;
            if (staticDelegates == null)
            {
                staticDelegates = new List<FieldInfo>();
                Type[] types;
                try { types = game.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
                foreach (Type t in types)
                {
                    if (t.ContainsGenericParameters) continue;
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public |
                                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (typeof(Delegate).IsAssignableFrom(f.FieldType)) staticDelegates.Add(f);
                }
            }
            foreach (FieldInfo f in staticDelegates)
            {
                Delegate d;
                try { d = f.GetValue(null) as Delegate; } catch (Exception) { continue; }
                cs.Add("static " + f.DeclaringType.FullName + "." + f.Name, Count(d, cs, f.Name));
            }
            foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || !mb.gameObject.scene.IsValid() || Strip.IsMod(mb.transform)) continue;
                Type ty = mb.GetType();
                if (ty.Assembly == game)
                {
                    FieldInfo[] fs;
                    if (!instanceDelegates.TryGetValue(ty, out fs))
                    {
                        var l = new List<FieldInfo>();
                        for (Type x = ty; x != null && x.Assembly == game; x = x.BaseType)
                            foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) l.Add(f);
                        instanceDelegates[ty] = fs = l.ToArray();
                    }
                    foreach (FieldInfo f in fs)
                        cs.Add("inst " + ty.Name + "." + f.Name + "#" + mb.GetInstanceID(),
                               Count(f.GetValue(mb) as Delegate, cs, ty.Name + "." + f.Name));
                }
                var b = mb as Button;
                if (b != null) cs.Add("ui " + b.name + ".onClick#" + b.GetInstanceID(), RuntimeCalls(b.onClick));
                var tg = mb as Toggle;
                if (tg != null)
                    cs.Add("ui " + tg.name + ".onValueChanged#" + tg.GetInstanceID(), RuntimeCalls(tg.onValueChanged));
            }
            return cs;
        }

        int Count(Delegate d, HandlerCensus cs, string where)
        {
            if (d == null) return 0;
            Delegate[] list = d.GetInvocationList();
            foreach (Delegate h in list)
            {
                bool ours = h.Method.DeclaringType != null && KitTypes.Contains(h.Method.DeclaringType);
                var target = h.Target as Object;
                if (!ReferenceEquals(target, null))
                {
                    if (target == null) ours |= made.Contains(target.GetInstanceID());   // destroyed: known by id
                    else
                    {
                        var c = target as Component;
                        if (c != null && Strip.IsMod(c.transform)) ours = true;
                        if (made.Contains(target.GetInstanceID())) ours = true;
                    }
                }
                if (ours) cs.Ours.Add(where + " -> " + h.Method.DeclaringType + "." + h.Method.Name);
            }
            return list.Length;
        }

        public static int RuntimeCalls(UnityEngine.Events.UnityEventBase e)
        {
            try
            {
                object calls = typeof(UnityEngine.Events.UnityEventBase)
                    .GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(e);
                var rt = calls.GetType().GetField("m_RuntimeCalls", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(calls) as IList;
                return rt != null ? rt.Count : -1;
            }
            catch (Exception) { return -1; }
        }

        // Game time stood still between two counts when there is no game clock, or it is at 0 and the date held.
        public static string Clock()
        {
            try
            {
                Manager.TimeController tc = MonoBehaviourSingleton<Manager.TimeController>.Instance;
                if (tc == null) return "none";
                return tc.CurrentTimeScale.ToString("0") + "@" + tc.CurrentTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception) { return "none"; }
        }

        public bool Judge(string set, HandlerCensus before, string clockBefore, HandlerCensus after, string clockAfter)
        {
            bool still = clockBefore == clockAfter && (clockBefore == "none" || clockBefore.StartsWith("0@"));
            string verdict = HandlerCensus.Verdict(before, after, still);
            List<string> diffs = HandlerCensus.Differences(before, after);
            Log.Info("ui: bench census " + set + ": " + verdict + "; " + after.Counts.Count + " delegates, ours " +
                     after.Ours.Count + (after.Ours.Count > 0 ? " [" + Join(after.Ours, 6) + "]" : "") + "; " +
                     diffs.Count + " differ" + (diffs.Count > 0 ? " [" + Join(diffs, 10) + "]" : "") + "; game time " +
                     (still ? "still" : "ran (" + clockBefore + " -> " + clockAfter + ")"));
            if (verdict == HandlerCensus.Fail) { Fail("census " + set); return false; }
            return true;
        }

        static string Join(List<string> l, int n)
        {
            return string.Join("; ", l.GetRange(0, Math.Min(n, l.Count)).ToArray());
        }

        // ---- the originals ----

        // What a clone could disturb on its original: active flags, components, colours, texts, toggle states and
        // groups, listener counts, sizes.
        public static string Snapshot(Transform root)
        {
            var sb = new StringBuilder();
            if (root == null) return "(none)";
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                sb.Append(Strip.PathOf(t)).Append(" a=").Append(t.gameObject.activeSelf);
                var rt = t as RectTransform;
                if (rt != null) sb.Append(" s=").Append(rt.rect.size.ToString("0.0")).Append(" p=")
                                  .Append(rt.anchoredPosition.ToString("0.0"));
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c == null) { sb.Append(" [missing]"); continue; }
                    sb.Append(' ').Append(c.GetType().Name);
                    var g = c as Graphic;
                    if (g != null)
                        sb.Append("{c=").Append(ColorUtility.ToHtmlStringRGBA(g.color)).Append(" e=").Append(g.enabled)
                          .Append('}');
                    var tx = c as TMP_Text;
                    if (tx != null) sb.Append("{t=").Append(tx.text).Append('}');
                    var s = c as Selectable;
                    if (s != null) sb.Append("{i=").Append(s.interactable).Append('}');
                    var tg = c as Toggle;
                    if (tg != null)
                        sb.Append("{on=").Append(tg.isOn).Append(" g=")
                          .Append(tg.group != null ? tg.group.GetInstanceID() : 0)
                          .Append(" l=").Append(RuntimeCalls(tg.onValueChanged)).Append('}');
                    var b = c as Button;
                    if (b != null) sb.Append("{l=").Append(RuntimeCalls(b.onClick)).Append('}');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public void SameOriginal(string part, string when, string a, string b)
        {
            if (a == b)
            {
                Log.Info("ui: bench originals " + part + " " + when + ": UNCHANGED (" + a.Split('\n').Length +
                         " objects)");
                return;
            }
            string[] la = a.Split('\n'), lb = b.Split('\n');
            var diffs = new List<string>();
            for (int i = 0; i < Math.Max(la.Length, lb.Length) && diffs.Count < 4; i++)
            {
                string x = i < la.Length ? la[i] : "(none)", y = i < lb.Length ? lb[i] : "(none)";
                if (x != y) diffs.Add("was [" + x + "] now [" + y + "]");
            }
            Fail("originals " + part + " " + when + " CHANGED: " + string.Join(" | ", diffs.ToArray()));
        }

        // ---- the game state a part must not touch ----

        public static void GameState(string when)
        {
            var sb = new StringBuilder();
            sb.Append("scene ").Append(SceneManager.GetActiveScene().name);
            Try(sb, "saves", () =>
            {
                var dir = new DirectoryInfo(Manager.LoadSaveManager.FullSaveDirectory());
                FileInfo[] files = dir.Exists ? dir.GetFiles() : new FileInfo[0];
                Array.Sort(files, (x, y) => string.CompareOrdinal(x.Name, y.Name));
                long h = 17;
                foreach (FileInfo f in files) h = h * 31 + (f.Name + f.Length + f.LastWriteTimeUtc.Ticks).GetHashCode();
                return files.Length + " files #" + h.ToString("X");
            });
            Try(sb, "window", () =>
            {
                Game.UI.UIManager ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance;
                if (ui == null) return "no UIManager";
                var cur = ui.Current;
                return cur != null ? cur.name : "none";
            });
            // the game's own pop-ups, read from their objects (the getters may be answering for a mod dialog)
            Try(sb, "pop-ups", () =>
            {
                Game.UI.UIManager ui = SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance;
                if (ui == null) return "no UIManager";
                var parts = new List<string>();
                foreach (string f in new[] { "popupWindowYesNoMenu", "popupWindow", "popupWindowContractOK",
                                             "popupWindowYesNoBeforeTutorial" })
                {
                    var b = GameParts.Field<Behaviour>(ui, f);
                    parts.Add(f + "=" + (b == null ? "none" : b.isActiveAndEnabled.ToString()));
                }
                return string.Join(" ", parts.ToArray());
            });
            Try(sb, "pause screen", () =>
            {
                foreach (var p in Object.FindObjectsOfType<Game.UI.Screens.PauseScreen>(true))
                    return p.Visible.ToString();
                return "none";
            });
            Try(sb, "notifications",
                () => Object.FindObjectsOfType<Game.UI.MainWindow.NotificationUI>(true).Length.ToString());
            Try(sb, "clock", Clock);
            Try(sb, "selected", () =>
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                return es != null && es.currentSelectedGameObject != null ? es.currentSelectedGameObject.name : "none";
            });
            Log.Info("ui: bench state " + when + ": " + sb);
        }

        static void Try(StringBuilder sb, string what, Func<string> f)
        {
            string v;
            try { v = f(); } catch (Exception e) { v = "?(" + e.GetType().Name + ")"; }
            sb.Append("; ").Append(what).Append(' ').Append(v);
        }

        // ---- pixels ----

        public static IEnumerator Grab(Action<Texture2D> got)
        {
            yield return new WaitForEndOfFrame();
            got(ScreenCapture.CaptureScreenshotAsTexture());
        }

        public void Save(Texture2D tex, string file)
        {
            try { File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG()); }
            catch (Exception e) { Log.Warn("ui: bench could not save " + file + ": " + e.Message); }
        }

        // The original's area in frame A against the part's area in frame B (one frame for a side-by-side pair),
        // where both are on screen. Saves the two crops side by side. expectSame false: a control that must differ.
        public bool Compare(string name, Texture2D ta, RectTransform orig, Texture2D tb, RectTransform part,
                            bool expectSame = true)
        {
            Rect ro = UiWidgets.ScreenRect(orig), rc = UiWidgets.ScreenRect(part);
            int dx = Mathf.RoundToInt(rc.x - ro.x), dy = Mathf.RoundToInt(rc.y - ro.y);
            int w = ta.width, h = ta.height;
            int x0 = Math.Max(0, Math.Max(Mathf.RoundToInt(ro.xMin), -dx));
            int y0 = Math.Max(0, Math.Max(Mathf.RoundToInt(ro.yMin), -dy));
            int x1 = Math.Min(w, Math.Min(Mathf.RoundToInt(ro.xMax), w - dx));
            int y1 = Math.Min(h, Math.Min(Mathf.RoundToInt(ro.yMax), h - dy));
            int cw = x1 - x0, ch = y1 - y0;
            Color32[] pa = ta.GetPixels32(), pb = ReferenceEquals(ta, tb) ? pa : tb.GetPixels32();
            var a = new int[Math.Max(0, cw * ch)];
            var b = new int[a.Length];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    Color32 ca = pa[(y0 + y) * w + x0 + x], cb = pb[(y0 + y + dy) * w + x0 + x + dx];
                    a[y * cw + x] = (ca.r << 16) | (ca.g << 8) | ca.b;
                    b[y * cw + x] = (cb.r << 16) | (cb.g << 8) | cb.b;
                }
            PixelCompare r = PixelCompare.Of(a, b, cw, ch);
            bool sizeSame = Mathf.Abs(ro.width - rc.width) < 0.05f && Mathf.Abs(ro.height - rc.height) < 0.05f;
            bool same = r.Same && sizeSame;
            Compared++;
            if (same) Same++; else Different++;
            Log.Info("ui: bench compare " + Res + " " + name + ": " + r + "; size original " +
                     ro.width.ToString("0.00") + "x" + ro.height.ToString("0.00") + " part " +
                     rc.width.ToString("0.00") + "x" + rc.height.ToString("0.00") +
                     (sizeSame ? "" : " (SIZE DIFFERS)") + (expectSame ? "" : " (a control: must differ)"));
            if (expectSame && !same) Fail("compare " + Res + " " + name);
            SaveCrop(name, pa, pb, w, h, x0, y0, cw, ch, dx, dy);
            return same;
        }

        void SaveCrop(string name, Color32[] pa, Color32[] pb, int w, int h, int x0, int y0, int cw, int ch,
                      int dx, int dy)
        {
            if (cw <= 0 || ch <= 0) return;
            try
            {
                const int pad = 6, gap = 4;
                int cx = Math.Max(0, x0 - pad), cy = Math.Max(0, y0 - pad);
                int ow = Math.Min(w, x0 + cw + pad) - cx, oh = Math.Min(h, y0 + ch + pad) - cy;
                var px = new Color32[(ow * 2 + gap) * oh];
                for (int y = 0; y < oh; y++)
                {
                    for (int x = 0; x < ow; x++)
                    {
                        px[y * (ow * 2 + gap) + x] = pa[(cy + y) * w + cx + x];
                        int bx = cx + x + dx, by = cy + y + dy;
                        px[y * (ow * 2 + gap) + ow + gap + x] = bx >= 0 && bx < w && by >= 0 && by < h
                            ? pb[by * w + bx] : new Color32(0, 0, 0, 255);
                    }
                    for (int x = 0; x < gap; x++) px[y * (ow * 2 + gap) + ow + x] = new Color32(255, 0, 255, 255);
                }
                var crop = new Texture2D(ow * 2 + gap, oh, TextureFormat.RGBA32, false);
                crop.SetPixels32(px);
                crop.Apply();
                Save(crop, "pair-" + Res + "-" + name.Replace('/', '_').Replace(' ', '_') + ".png");
                Object.Destroy(crop);
            }
            catch (Exception e) { Log.Warn("ui: bench crop failed: " + e.Message); }
        }

        public void Summary(string set)
        {
            Log.Info("ui: bench summary " + set + " " + Res + ": " + Compared + " compared, " + Same + " same, " +
                     Different + " different; controls " + ControlsPassed + " held, " + ControlsFailed + " failed; " +
                     Failures.Count +
                     " failure(s)" + (Failures.Count > 0 ? " [" + Join(Failures, 12) + "]" : "") + " -> " +
                     (Failures.Count == 0 ? "PASS" : "FAIL"));
        }
    }
}
