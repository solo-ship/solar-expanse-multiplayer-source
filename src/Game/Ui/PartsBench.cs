using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The parts kit's bench (test seams only): builds each part by the kit's own recipe beside its game original,
    // drives both through the same states as a mouse would, compares them pixel by pixel in the game's frame, runs
    // every fail-closed check's negative control, and counts the game's handlers before and after. Driven by the dev
    // trigger file <data>\dev\uiparts holding one command; captures go to <data>\uiparts\, results to the mod log
    // ("ui: bench ..."). Commands:
    //   menu | game          every part of that scene beside its original, in every state, at the current size
    //   sweep menu|game      the same at 1280x720, 1920x1080, 2560x1440 and 3840x2160, then back to 1280x720
    //   controls             every fail-closed check's negative control (at the menu)
    //   modal                a mod dialog: the pop-up getters, then 30 s for a speed hotkey and Esc, then (in game)
    //                        20 s for Esc with no dialog
    //   keys                 (in game) 20 s for Esc with no dialog: the pause screen must open (the keys reach the
    //                        game; Esc and the hotkeys stopping at a dialog need the pop-up hook, so they are in modal)
    //   res <w> <h> | state | loadsave | tomenu
    // Each original is compared undimmed and on plain black: for its comparison it is lifted, on a canvas of its own,
    // above a black backdrop that covers the rest of the game's UI and its dimming overlays, and its parents' canvas
    // groups are ignored; it is put back as it was right after.
    public sealed partial class PartsBench : MonoBehaviour
    {
        static readonly int[][] Sizes =
        {
            new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 },
        };

        sealed class Pair { public string Name; public RectTransform Orig, Part; public Action Restore; }

        // Above every game canvas (the game's own highest are its drop-down lists at 30000 and its cursor at 32767):
        // the backdrop, then the lifted original, then the bench's parts.
        const int BackdropOrder = 31000, LiftOrder = 31001, BenchOrder = 31002;
        enum Side { Right, Below, Left, Above }

        float nextPoll;
        bool busy;
        string outDir;
        BenchChecks checks;
        readonly List<GameObject> built = new List<GameObject>();
        readonly Dictionary<int, Canvas> canvases = new Dictionary<int, Canvas>();
        readonly Dictionary<int, GameObject> backdrops = new Dictionary<int, GameObject>();
        readonly List<Pair> lifted = new List<Pair>();

        public static bool SeamsOpen()
        {
            return TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath);
        }

        void Awake()
        {
            if (!SeamsOpen() || Boot.DataDir == null) { enabled = false; return; }
            outDir = Path.Combine(Boot.DataDir, "uiparts");
            Directory.CreateDirectory(outDir);
            Log.Info("ui: bench ready; captures in " + outDir);
        }

        void Update()
        {
            if (busy || !DevTriggers.Every(ref nextPoll, Time.realtimeSinceStartup)) return;
            string cmd = DevTriggers.Take("uiparts");
            if (cmd != null) StartCoroutine(Run(cmd));
        }

        IEnumerator Run(string cmd)
        {
            busy = true;
            checks = new BenchChecks(outDir);
            if (SempRoot.Instance != null) SempRoot.Instance.LabelHidden = true;
            string[] a = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = a.Length > 0 ? a[0] : "";
            Log.Info("ui: bench command " + cmd + " at " + BenchChecks.Res + " in " +
                     SceneManager.GetActiveScene().name);
            IEnumerator body = null;
            switch (verb)
            {
                case "menu": body = MenuSet(); break;
                case "game": body = GameSet(); break;
                case "controls": body = Controls(); break;
                case "modal": body = Modal(); break;
                case "keys": body = KeysReachGame(); break;
                case "sweep": body = Sweep(a.Length > 1 ? a[1] : "menu"); break;
                case "res": body = SetRes(Num(a, 1, 1280), Num(a, 2, 720)); break;
                case "state": BenchChecks.GameState("now"); break;
                case "loadsave": body = LoadSave(); break;
                case "tomenu": body = ToMenu(); break;
                default: Log.Warn("ui: bench: unknown command " + cmd); break;
            }
            if (body != null) yield return Safe(body, cmd);
            Clear();
            if (Array.IndexOf(new[] { "menu", "game", "controls", "modal", "keys", "sweep" }, verb) >= 0)
                checks.Summary(verb);
            if (SempRoot.Instance != null) SempRoot.Instance.LabelHidden = false;
            Log.Info("ui: bench done " + cmd);
            busy = false;
        }

        static int Num(string[] a, int i, int d) { int v; return a.Length > i && int.TryParse(a[i], out v) ? v : d; }

        // Nested steps, an exception from any of them logged as a failure instead of lost.
        IEnumerator Safe(IEnumerator root, string what)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                object cur;
                try
                {
                    if (!top.MoveNext()) { stack.Pop(); continue; }
                    cur = top.Current;
                }
                catch (Exception e) { checks.Fail(what + " threw: " + e); yield break; }
                if (cur is IEnumerator) { stack.Push((IEnumerator)cur); continue; }
                yield return cur;
            }
        }

        static WaitForSecondsRealtime Wait(float s) { return new WaitForSecondsRealtime(s); }

        IEnumerator Sweep(string set)
        {
            foreach (int[] s in Sizes)
            {
                yield return SetRes(s[0], s[1]);
                yield return set == "game" ? GameSet() : MenuSet();
                Clear();
            }
            yield return SetRes(1280, 720);
        }

        IEnumerator SetRes(int w, int h)
        {
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
            for (int i = 0; i < 20 && (Screen.width != w || Screen.height != h); i++) yield return Wait(0.25f);
            yield return Wait(0.6f);
            Log.Info("ui: bench resolution asked " + w + "x" + h + "; got " + BenchChecks.Res);
        }

        // ---------------------------------------------------------------- the menu

        IEnumerator MenuSet()
        {
            GameParts parts = GameParts.InMenu();
            if (parts.Missing != null) { checks.Fail("menu part missing: " + parts.Missing); yield break; }
            var menu = (MenuSceneUI)parts.Original(GameParts.PMenu);
            BenchChecks.GameState("menu before");

            // the load dialog, opened by the player's own click: its census starts once the game's window is open
            Click(GameParts.Field<Button>(menu, "btnLoad").gameObject);
            yield return Wait(0.8f);
            var window = (Component)parts.Original(GameParts.PWindow);
            if (!window.gameObject.activeInHierarchy) { checks.Fail("the load dialog did not open"); yield break; }
            var list = (RectTransform)parts.Original(GameParts.PList);
            List<Toggle> rows = RowsOf(list);
            var buttonsRoot = ((Button)parts.Original(GameParts.PMenuButton)).transform.parent;
            string snapWindow = BenchChecks.Snapshot(window.transform), snapButtons = BenchChecks.Snapshot(buttonsRoot);
            HandlerCensus c0 = checks.Census();
            string t0 = BenchChecks.Clock();

            GameObject menuButton = ((Button)parts.Original(GameParts.PMenuButton)).gameObject;
            Pair button = MakePair("menu-button", menuButton, Side.Right);
            if (button != null) { yield return States(button); Hide(button); }
            Pair win = MakePair("window", window.gameObject, Side.Right);
            if (win != null) { yield return Capture("window-normal", win); Hide(win); }
            if (rows.Count == 0) checks.Fail("no save rows to compare (the test save is missing)");
            else
            {
                // a row that is not on: an on row looks selected in every state
                Toggle row = rows.Count > 1 ? rows[1] : rows[0];
                Pair live = MakePair("row-live", row.gameObject, Side.Right);
                if (live != null) { yield return States(live); Hide(live); }
                Pair on = MakePair("row-on", rows[0].gameObject, Side.Right);
                if (on != null) { yield return Capture("row-on", on); Hide(on); }
                Component prefab = (Component)parts.Original(GameParts.PRow);
                Pair fromPrefab = MakePair("row-prefab", prefab.gameObject, Side.Right, (RectTransform)row.transform);
                if (fromPrefab != null)
                {
                    CopyLook(row.transform, fromPrefab.Part, false);
                    yield return null;
                    yield return States(fromPrefab);
                    Hide(fromPrefab);
                }
                var roles = new[] { TextRole.Title, TextRole.Body, TextRole.Small, TextRole.Value, TextRole.Button };
                yield return TextPairs(parts, roles, row.transform);
            }
            Clear();
            yield return null;
            checks.Judge("menu (load dialog open)", c0, t0, checks.Census(), BenchChecks.Clock());
            checks.SameOriginal("load dialog", "after the set", snapWindow, BenchChecks.Snapshot(window.transform));
            checks.SameOriginal("menu buttons", "after the set", snapButtons, BenchChecks.Snapshot(buttonsRoot));
            Click(((Button)parts.Original(GameParts.PCancel)).gameObject);
            yield return Wait(0.5f);
            yield return ShowcaseSpan(parts, "menu");
            yield return AgencySet(parts);
            BenchChecks.GameState("menu after");
        }

        IEnumerator AgencySet(GameParts parts)
        {
            var menu = (MenuSceneUI)parts.Original(GameParts.PMenu);
            Click(GameParts.Field<Button>(menu, "btnSingle").gameObject);   // NEW GAME, as the player clicks it
            yield return Wait(1.5f);
            var corp = (CorporationSelectUI)parts.Original(GameParts.PAgencies);
            var toggles = new List<Toggle>();
            foreach (object item in (IList)GameParts.Field<IList>(corp, "corporationItems"))
                toggles.Add(GameParts.Field<Toggle>(item, "toggle"));
            if (toggles.Count < 2 || toggles.Any(t => t == null))
            {
                checks.Fail("agency toggles not found");
                yield break;
            }
            Transform listRoot = toggles[0].transform.parent;
            string snap = BenchChecks.Snapshot(listRoot);
            string selected = corp.SelectedCompany != null ? corp.SelectedCompany.name : "none";
            HandlerCensus c0 = checks.Census();
            string t0 = BenchChecks.Clock();
            Pair on = MakePair("agency-on", toggles[0].gameObject, Side.Below);
            if (on != null) { yield return Capture("agency-on", on); Hide(on); }
            Pair off = MakePair("agency-off", toggles[1].gameObject, Side.Below);
            if (off != null)
            {
                yield return States(off);
                // a click on the clone stays on the clone
                Click(off.Part.gameObject);
                yield return Wait(0.3f);
                string now = corp.SelectedCompany != null ? corp.SelectedCompany.name : "none";
                bool cloneOn = off.Part.GetComponent<Toggle>().isOn;
                checks.Control("agency-clone-click", now == selected && cloneOn,
                               "game selection " + selected + " -> " + now + "; clone on " + cloneOn);
            }
            Clear();
            yield return null;
            checks.Judge("agency page", c0, t0, checks.Census(), BenchChecks.Clock());
            checks.SameOriginal("agency toggles", "after the set", snap, BenchChecks.Snapshot(listRoot));
            Transform back = FindDeep(corp.transform, "BackButton");
            if (back == null) back = FindDeep(corp.transform.parent, "BackButton");
            if (back != null) Click(back.gameObject);
            yield return Wait(1.0f);
        }

        // ---------------------------------------------------------------- shared steps

        // Each role's game text beside its source (rows: the live row's own texts), then the kit's Text part.
        IEnumerator TextPairs(GameParts parts, TextRole[] roles, Transform liveRow)
        {
            foreach (TextRole role in roles)
            {
                string source = parts.RoleSource(role);
                Transform t = parts.TextOf(source);
                if (liveRow != null && source.StartsWith(GameParts.PRow + ".", StringComparison.Ordinal))
                {
                    Component el = liveRow.GetComponents<Component>()
                        .FirstOrDefault(c => c != null && c.GetType().Name == "LoadSaveDialogElement");
                    var lt = GameParts.Field<TMP_Text>(el, source.Substring(source.IndexOf('.') + 1));
                    t = lt != null ? lt.transform : null;
                }
                if (t == null || !t.gameObject.activeInHierarchy)
                {
                    Log.Info("ui: bench text " + role + ": its source " + source + " is not shown here");
                    continue;
                }
                Log.Info("ui: bench text " + role + " copies " + source + " ('" + Strip.PathOf(t) + "', font " +
                         t.GetComponent<TMP_Text>().font.name + ", size " + t.GetComponent<TMP_Text>().fontSize + ")");
                Pair p = MakePair("text-" + role, t.gameObject, Side.Below);
                if (p != null) { yield return Capture("text-" + role, p); Hide(p); }
            }
        }

        // The showcase with the game's own dialogs closed, counted on its own.
        IEnumerator ShowcaseSpan(GameParts parts, string scene)
        {
            HandlerCensus c0 = checks.Census();
            string t0 = BenchChecks.Clock();
            yield return KitShowcase(parts, scene);
            Clear();
            yield return null;
            checks.Judge("kit showcase (" + scene + ")", c0, t0, checks.Census(), BenchChecks.Clock());
        }

        // Every kit part as a screen gets it (texts from the table, peer text through PeerText), for the eye.
        IEnumerator KitShowcase(GameParts parts, string scene)
        {
            Canvas canvas = BenchCanvas(parts.GameCanvas);
            RectTransform box = UiWidgets.Box(canvas.transform, "kit showcase");
            UiWidgets.Stretch(box);
            Track(box.gameObject);
            Canvas.ForceUpdateCanvases();
            Rect area = box.rect;
            float left = area.yMax - 40f, right = area.yMax - 40f;
            string atlas0 = UiFonts.AtlasState(TitleFont(parts));
            // the window in the middle with a row in its list; every other part in a column of its own beside it
            RectTransform w = parts.Window(box, "name.fallback");
            if (w == null) checks.Fail("kit window refused");
            RectTransform row = parts.ListRow(w != null ? parts.PartsOfWindow(w).List : box);
            if (row == null) checks.Fail("kit row refused");
            else
            {
                string peer = "Zo\u00EB \uD55C\uAD6D \u0416 \u5B57\u6F22 \uFF37 <b>x</b> " + new string('W', 30);
                Canvas.ForceUpdateCanvases();
                RowParts rp = parts.PartsOfRow(row);
                UiFonts.SetPeerText(rp.Name, peer, rp.NameWidth());
            }
            Button mb = parts.MenuButton(box, "name.fallback");
            if (mb == null) checks.Fail("kit menu button refused");
            else Stack((RectTransform)mb.transform, area, true, ref left);
            TMP_InputField input = parts.Input(box, 24, true);
            if (input == null) checks.Fail("kit input refused");
            else Stack((RectTransform)input.transform, area, true, ref left);
            if (scene == "menu")
            {
                Toggle agency = parts.AgencyToggle(box, "nasa");
                if (agency == null) checks.Fail("kit agency toggle refused");
                else Stack((RectTransform)agency.transform, area, true, ref left);
            }
            foreach (TextRole role in Enum.GetValues(typeof(TextRole)))
            {
                TMP_Text t = parts.Text(box, role);
                if (t == null) { checks.Fail("kit text " + role + " refused"); continue; }
                t.text = role + " TEXT";
                Stack(t.rectTransform, area, false, ref right);
            }
            if (scene == "game")
            {
                RectTransform notice = parts.Notice(box);
                if (notice == null) checks.Fail("kit notice refused");
                else
                {
                    // the content a screen gives it: peer text, and an icon of the game's (a live card's, as the kit
                    // has no notification of its own to take one from; without one the prefab's own image stays)
                    NoticeParts np = parts.PartsOfNotice(notice);
                    var live = Object.FindObjectsOfType<Game.UI.MainWindow.NotificationUI>(true)
                        .FirstOrDefault(c => c != null && !Strip.IsMod(c.transform));
                    var liveImage = live != null ? GameParts.Field<Image>(live, "image") : null;
                    bool icon = liveImage != null && liveImage.sprite != null;
                    if (np.Image != null && icon) np.Image.sprite = liveImage.sprite;
                    Log.Info("ui: bench kit notice icon " + (icon ? "from a live card ('" + liveImage.sprite.name + "')"
                                                                  : "left as the prefab's (no live card)"));
                    if (np.Text != null) UiFonts.SetPeerText(np.Text, "Sam joined the game as ESA.");
                    Stack(notice, area, false, ref right);
                }
                Image mark = parts.SpeedMark(box);
                if (mark == null) checks.Fail("kit speed mark refused");
                else Stack(mark.rectTransform, area, true, ref left);
            }
            Track(box.gameObject);
            yield return Natural("kit-" + scene);
            string atlas1 = UiFonts.AtlasState(TitleFont(parts));
            if (atlas0 != atlas1) checks.Fail("a font atlas grew while the kit drew peer text: " + atlas1);
            else Log.Info("ui: bench atlas unchanged after the kit's peer text (" + atlas0 + ")");
            Object.DestroyImmediate(box.gameObject);
        }

        // Down a column at the left or right edge of the area, top first.
        static void Stack(RectTransform rt, Rect area, bool leftSide, ref float y)
        {
            Vector2 size = rt.rect.size;
            float x = leftSide ? area.xMin + 40f + size.x / 2f : area.xMax - 40f - size.x / 2f;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y - size.y / 2f);
            y -= size.y + 16f;
        }

        static TMP_FontAsset TitleFont(GameParts parts)
        {
            Transform t = parts.TextOf(GameParts.PTitle);
            TMP_Text text = t != null ? t.GetComponent<TMP_Text>() : null;
            return text != null ? text.font : null;
        }

        Pair MakePair(string name, GameObject original, Side side, RectTransform placeLike = null,
                      Action<GameObject> prepare = null)
        {
            RectTransform like = placeLike ?? (RectTransform)original.transform;
            Canvas canvas = BenchCanvas(UiWidgets.RootCanvas(like));
            string problem;
            // a prefab is drawn where its live copy is
            RectTransform part = Strip.Clone(original, canvas.transform, "bench " + name, out problem, prepare, null,
                                             placeLike);
            if (part == null) { checks.Fail("part " + name + " refused: " + problem); return null; }
            Track(part.gameObject);
            UiWidgets.Place(part, like, Offset(like, side));
            Log.Info("ui: bench pair " + name + " at " + BenchChecks.Res + ": original " +
                     R(UiWidgets.ScreenRect(like)) + ", part " + R(UiWidgets.ScreenRect(part)) + "; pixel snapping " +
                     Strip.SnapsPixels(like) + " / " + Strip.SnapsPixels(part));
            var p = new Pair { Name = name, Orig = like, Part = part };
            Lift(p);
            return p;
        }

        // The original over the backdrop for its comparison (see the class comment), put back by Hide or Clear.
        void Lift(Pair p)
        {
            GameObject g = p.Orig.gameObject;
            Canvas root = UiWidgets.RootCanvas(p.Orig);
            Canvas c = g.GetComponent<Canvas>();
            bool addedCanvas = c == null, hadOverride = false;
            int hadOrder = 0, hadLayer = 0;
            if (addedCanvas)
            {
                c = g.AddComponent<Canvas>();
                if (root != null) c.additionalShaderChannels = root.additionalShaderChannels;
            }
            else { hadOverride = c.overrideSorting; hadOrder = c.sortingOrder; hadLayer = c.sortingLayerID; }
            c.overrideSorting = true;
            c.sortingOrder = LiftOrder;
            if (root != null) c.sortingLayerID = root.sortingLayerID;
            CanvasGroup cg = g.GetComponent<CanvasGroup>();
            bool addedGroup = cg == null, hadIgnore = false;
            if (addedGroup) cg = g.AddComponent<CanvasGroup>();
            else hadIgnore = cg.ignoreParentGroups;
            cg.ignoreParentGroups = true;
            p.Restore = () =>
            {
                if (cg != null) { if (addedGroup) DestroyImmediate(cg); else cg.ignoreParentGroups = hadIgnore; }
                if (c != null)
                {
                    if (addedCanvas) DestroyImmediate(c);
                    else { c.overrideSorting = hadOverride; c.sortingOrder = hadOrder; c.sortingLayerID = hadLayer; }
                }
            };
            lifted.Add(p);
        }

        void Unlift(Pair p)
        {
            if (p == null || p.Restore == null) return;
            try { p.Restore(); }
            catch (Exception e) { checks.Fail("could not put the original " + p.Name + " back: " + e.Message); }
            p.Restore = null;
            lifted.Remove(p);
        }

        static Vector2 Offset(RectTransform orig, Side side)
        {
            Rect r = UiWidgets.ScreenRect(orig);
            float gap = Mathf.Round(Screen.height / 72f);
            // the side asked for, or the opposite one when the part would not fit on the screen there
            if (side == Side.Below && r.yMin - r.height - gap < 0f) side = Side.Above;
            if (side == Side.Above && r.yMax + r.height + gap > Screen.height) side = Side.Below;
            if (side == Side.Below) return new Vector2(0f, -Mathf.Round(r.height + gap));
            if (side == Side.Above) return new Vector2(0f, Mathf.Round(r.height + gap));
            if (side == Side.Left || r.xMax + r.width + gap > Screen.width)
                return new Vector2(-Mathf.Round(r.width + gap), 0f);
            return new Vector2(Mathf.Round(r.width + gap), 0f);
        }

        static string R(Rect r)
        {
            return "(" + r.x.ToString("0.0") + "," + r.y.ToString("0.0") + " " + r.width.ToString("0.0") + "x" +
                   r.height.ToString("0.0") + ")";
        }

        void Hide(Pair p)
        {
            if (p == null) return;
            if (p.Part != null) p.Part.gameObject.SetActive(false);
            Unlift(p);
        }

        // Normal, hover, pressed, selected and disabled, driven through the event system as a mouse would on both.
        // "Disabled" is set on the original and put back; "selected" is one object at a time, so it takes two frames.
        IEnumerator States(Pair p, bool expectSame = true)
        {
            var o = p.Orig.GetComponent<Selectable>();
            var c = p.Part.GetComponent<Selectable>();
            yield return Capture(p.Name + "-normal", p, expectSame);
            if (o == null || c == null)
            {
                Log.Info("ui: bench " + p.Name + " has no selectable: normal only");
                yield break;
            }
            Hover(o.gameObject, true); Hover(c.gameObject, true);
            yield return Wait(0.35f);
            yield return Capture(p.Name + "-hover", p, expectSame);
            if (o is TMP_InputField)
            {
                // pressing a field selects it, and only one object holds the selection: two frames
                Press(o.gameObject, true);
                yield return Wait(0.35f);
                Texture2D pa = null, pb = null;
                yield return BenchChecks.Grab(t => pa = t);
                Press(o.gameObject, false);
                EventSystem.current.SetSelectedGameObject(null);
                Press(c.gameObject, true);
                yield return Wait(0.35f);
                yield return BenchChecks.Grab(t => pb = t);
                checks.Compare(p.Name + "-pressed", pa, p.Orig, pb, p.Part, expectSame);
                Destroy(pa); Destroy(pb);
            }
            else
            {
                Press(o.gameObject, true); Press(c.gameObject, true);
                yield return Wait(0.35f);
                yield return Capture(p.Name + "-pressed", p, expectSame);
            }
            Press(o.gameObject, false); Press(c.gameObject, false);
            Hover(o.gameObject, false); Hover(c.gameObject, false);
            EventSystem.current.SetSelectedGameObject(o.gameObject);
            yield return Wait(0.35f);
            Texture2D a = null, b = null;
            yield return BenchChecks.Grab(t => a = t);
            EventSystem.current.SetSelectedGameObject(c.gameObject);
            yield return Wait(0.35f);
            yield return BenchChecks.Grab(t => b = t);
            checks.Compare(p.Name + "-selected", a, p.Orig, b, p.Part, expectSame);
            Destroy(a); Destroy(b);
            EventSystem.current.SetSelectedGameObject(null);
            bool was = o.interactable;
            o.interactable = false; c.interactable = false;
            yield return Wait(0.35f);
            yield return Capture(p.Name + "-disabled", p, expectSame);
            o.interactable = was; c.interactable = true;
            yield return Wait(0.35f);
            if (o.interactable != was) checks.Fail(p.Name + ": the original's interactable was not put back");
        }

        IEnumerator Capture(string name, Pair p, bool expectSame = true)
        {
            Backdrop(UiWidgets.RootCanvas(p.Orig), true);
            yield return Wait(0.1f);
            Texture2D tex = null;
            yield return BenchChecks.Grab(t => tex = t);
            try
            {
                checks.Save(tex, BenchChecks.Res + "-" + name + ".png");
                checks.Compare(name, tex, p.Orig, tex, p.Part, expectSame);
            }
            finally { Destroy(tex); }
        }

        // The whole screen as the player sees it (no backdrop).
        IEnumerator Natural(string name)
        {
            foreach (GameObject b in backdrops.Values) if (b != null) b.SetActive(false);
            yield return Wait(0.3f);
            Texture2D tex = null;
            yield return BenchChecks.Grab(t => tex = t);
            checks.Save(tex, BenchChecks.Res + "-" + name + ".png");
            Destroy(tex);
        }

        static PointerEventData Ptr()
        {
            return new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        }
        static void Hover(GameObject g, bool on)
        {
            if (on) ExecuteEvents.Execute(g, Ptr(), ExecuteEvents.pointerEnterHandler);
            else ExecuteEvents.Execute(g, Ptr(), ExecuteEvents.pointerExitHandler);
        }
        static void Press(GameObject g, bool down)
        {
            if (down) ExecuteEvents.Execute(g, Ptr(), ExecuteEvents.pointerDownHandler);
            else ExecuteEvents.Execute(g, Ptr(), ExecuteEvents.pointerUpHandler);
        }
        static void Click(GameObject g)
        {
            if (g != null) ExecuteEvents.Execute(g, Ptr(), ExecuteEvents.pointerClickHandler);
        }

        // The rows the game's load list shows (its own children only).
        static List<Toggle> RowsOf(RectTransform list)
        {
            return list.GetComponentsInChildren<Toggle>(false).Where(t => t.transform.parent == list).ToList();
        }

        // Shows each of the prefab clone's objects as the live one shows it (which children are on, the texts).
        static void CopyLook(Transform live, Transform made, bool rich)
        {
            foreach (Transform t in live.GetComponentsInChildren<Transform>(true))
            {
                if (t == live) continue;
                Transform m = Strip.FindSame(made, live, t);
                if (m == null) continue;
                m.gameObject.SetActive(t.gameObject.activeSelf);
                var lt = t.GetComponent<TMP_Text>();
                var mt = m.GetComponent<TMP_Text>();
                if (lt != null && mt != null) { mt.richText = rich; mt.text = lt.text; }
            }
            var lToggle = live.GetComponent<Toggle>();
            var mToggle = made.GetComponent<Toggle>();
            if (lToggle != null && mToggle != null) GameParts.SetOn(mToggle, lToggle.isOn);
        }

        // ---------------------------------------------------------------- canvases and clean-up

        internal Canvas BenchCanvas(Canvas game)
        {
            Canvas have;
            if (canvases.TryGetValue(game.GetInstanceID(), out have) && have != null) return have;
            Canvas c = UiWidgets.ModCanvas(game, "bench", BenchOrder - game.sortingOrder);
            canvases[game.GetInstanceID()] = c;
            Track(c.gameObject);
            return c;
        }

        // A plain black screen under the game's canvas, so a part and its original sit on the same background.
        void Backdrop(Canvas game, bool on)
        {
            if (game == null || game.renderMode != RenderMode.ScreenSpaceOverlay) return;
            GameObject b;
            if (!backdrops.TryGetValue(game.GetInstanceID(), out b) || b == null)
            {
                if (!on) return;
                b = new GameObject(Strip.ObjectPrefix + "bench backdrop");
                var c = b.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = BackdropOrder;
                c.sortingLayerID = game.sortingLayerID;
                var img = new GameObject(Strip.ObjectPrefix + "black", typeof(RectTransform)).AddComponent<Image>();
                img.transform.SetParent(b.transform, false);
                img.color = Color.black;
                img.raycastTarget = false;
                UiWidgets.Stretch(img.rectTransform);
                backdrops[game.GetInstanceID()] = b;
                Track(b);
            }
            b.SetActive(on);
        }

        internal void Track(GameObject g) { if (g == null) return; built.Add(g); checks.Track(g); }

        void Clear()
        {
            foreach (Pair p in lifted.ToArray()) Unlift(p);
            foreach (GameObject g in built) if (g != null) DestroyImmediate(g);
            built.Clear();
            canvases.Clear();
            backdrops.Clear();
        }

        // The first shown object of that name under root (a page's own Back, not a hidden page's).
        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(false)) if (t.name == name) return t;
            return null;
        }

        IEnumerator LoadSave()
        {
            string save = Boot.AutoLoad ?? Boot.Seed ?? "multiplayertest";
            Log.Info("ui: bench loading " + save);
            SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance.Load(save);
            yield break;
        }

        IEnumerator ToMenu()
        {
            var pause = Object.FindObjectsOfType<Game.UI.Screens.PauseScreen>(true).FirstOrDefault();
            string scene = pause != null ? GameParts.Field<string>(pause, "menuScene") : null;
            Log.Info("ui: bench to the menu scene '" + scene + "'");
            if (!string.IsNullOrEmpty(scene)) SceneManager.LoadScene(scene);
            yield break;
        }
    }
}
