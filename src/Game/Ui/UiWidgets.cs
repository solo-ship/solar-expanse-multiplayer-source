using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // Layout helpers for the parts kit: the mod's own canvas, and placing a part on the screen.
    public static class UiWidgets
    {
        // Above the game's canvas, and so above its pause-screen blur, which a dialog over the pause menu needs. The
        // game draws its own pop-ups inside its one canvas, so one raised while a mod dialog is open shows under it;
        // the dialog is modal (the game reads it as a pop-up of its own), and closing it shows the game's.
        public const int OrderAbove = 1;

        // A mod canvas set up exactly as the game's root canvas (render mode, scaler, shader channels), so parts
        // scale as the game's do at every resolution.
        public static Canvas ModCanvas(Canvas game, string name, int above = OrderAbove)
        {
            if (game == null) return null;
            var go = new GameObject(Strip.ObjectPrefix + "canvas " + name);
            var c = go.AddComponent<Canvas>();
            c.renderMode = game.renderMode;
            c.worldCamera = game.worldCamera;
            c.planeDistance = game.planeDistance;
            c.sortingLayerID = game.sortingLayerID;
            c.sortingOrder = game.sortingOrder + above;
            c.pixelPerfect = game.pixelPerfect;
            c.additionalShaderChannels = game.additionalShaderChannels;
            CanvasScaler gs = game.GetComponent<CanvasScaler>();
            CanvasScaler s = go.AddComponent<CanvasScaler>();
            if (gs != null)
            {
                s.uiScaleMode = gs.uiScaleMode; s.referenceResolution = gs.referenceResolution;
                s.screenMatchMode = gs.screenMatchMode; s.matchWidthOrHeight = gs.matchWidthOrHeight;
                s.referencePixelsPerUnit = gs.referencePixelsPerUnit; s.scaleFactor = gs.scaleFactor;
                s.dynamicPixelsPerUnit = gs.dynamicPixelsPerUnit; s.physicalUnit = gs.physicalUnit;
                s.fallbackScreenDPI = gs.fallbackScreenDPI; s.defaultSpriteDPI = gs.defaultSpriteDPI;
            }
            go.AddComponent<GraphicRaycaster>();
            // a scaler added at run time applies its defaults at once and the copied settings only on its next
            // Update: switched off and on, it applies them now, before anything is placed
            s.enabled = false;
            s.enabled = true;
            Canvas.ForceUpdateCanvases();
            Log.Info("ui: canvas " + name + " over '" + game.name + "' (order " + c.sortingOrder + ", scale " +
                     c.transform.lossyScale.x.ToString("0.0000") + ", game " +
                     game.transform.lossyScale.x.ToString("0.0000") + ")");
            return c;
        }

        // Puts a part at its original's size and screen place, moved by offsetPx (screen pixels).
        public static void Place(RectTransform part, RectTransform orig, Vector2 offsetPx)
        {
            part.anchorMin = part.anchorMax = new Vector2(0.5f, 0.5f);
            part.pivot = orig.pivot;
            part.localRotation = Quaternion.identity;
            // scale relative to each one's root canvas: the two canvases scale alike, whenever the scaler runs
            Vector3 os = orig.lossyScale, og = RootCanvas(orig).transform.lossyScale, ps = part.parent.lossyScale,
                    pg = RootCanvas(part).transform.lossyScale;
            os = new Vector3(os.x / og.x, os.y / og.y, 1f);
            ps = new Vector3(ps.x / pg.x, ps.y / pg.y, 1f);
            part.localScale = new Vector3(os.x / ps.x, os.y / ps.y, 1f);
            part.sizeDelta = orig.rect.size;
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(Cam(orig), orig.position) + offsetPx;
            Vector3 wp;
            var into = (RectTransform)part.parent;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(into, sp, Cam(part), out wp))
                part.position = wp;
        }

        // Puts a part's bottom-left corner at a screen point, size in canvas units.
        public static void PlaceAt(RectTransform part, Vector2 screenMin, Vector2 size)
        {
            part.anchorMin = part.anchorMax = new Vector2(0.5f, 0.5f);
            part.pivot = Vector2.zero;
            part.localRotation = Quaternion.identity;
            part.localScale = Vector3.one;
            part.sizeDelta = size;
            Vector3 wp;
            var into = (RectTransform)part.parent;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(into, screenMin, Cam(part), out wp))
                part.position = wp;
        }

        // Whether a click could reach it now: shown, under no faded-out or click-blocking canvas group, its centre on
        // the screen.
        public static bool Clickable(RectTransform rt)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return false;
            for (Transform x = rt; x != null; x = x.parent)
            {
                var g = x.GetComponent<CanvasGroup>();
                if (g == null || !g.enabled) continue;
                if (g.alpha <= 0.01f || !g.blocksRaycasts || !g.interactable) return false;
                if (g.ignoreParentGroups) break;
            }
            Vector2 c = ScreenRect(rt).center;
            return c.x >= 0f && c.y >= 0f && c.x <= Screen.width && c.y <= Screen.height;
        }

        public static Rect ScreenRect(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            Camera cam = Cam(rt);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        public static Canvas RootCanvas(Transform t)
        {
            Canvas found = null;
            for (Transform x = t; x != null; x = x.parent)
            {
                var c = x.GetComponent<Canvas>();
                if (c != null) found = c;
            }
            return found != null ? found.rootCanvas : null;
        }

        static Camera Cam(Transform t)
        {
            Canvas c = RootCanvas(t);
            return c == null || c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
        }

        // A plain container (no graphic) under parent.
        public static RectTransform Box(Transform parent, string name)
        {
            var go = new GameObject(Strip.ObjectPrefix + name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }
    }

    // A modal mod dialog: the game's load-dialog frame over a click catcher, registered with ModalRegistry while it is
    // shown. Esc closes it (the game ignores that Esc, ModalPopups); so do its close and Cancel buttons, unless
    // Asking (given whether the Cancel button was the way) says to keep it: the owner then asks or acts itself.
    public sealed class ModDialog : MonoBehaviour
    {
        public RectTransform Window;
        public WindowParts Parts;
        public Func<bool, bool> Asking;
        Action closed;
        bool escPressed, done;
        readonly OwnedCloses owned = new OwnedCloses();

        // A question asked over this dialog: it closes when this one does.
        public void Own(ModDialog child)
        {
            if (child != null && child != this) owned.Add(child.Close);
        }

        public static ModDialog Open(GameParts parts, Canvas canvas, string titleKey, Action onClosed)
        {
            if (parts == null || canvas == null || parts.Missing != null) return null;
            RectTransform root = UiWidgets.Box(canvas.transform, "dialog " + titleKey);
            UiWidgets.Stretch(root);
            RectTransform blocker = parts.Blocker(root);
            RectTransform window = blocker != null ? parts.Window(root, titleKey) : null;
            if (window == null)
            {
                Object.Destroy(root.gameObject);
                Log.Warn("ui: dialog " + titleKey + " not shown: a part was refused");
                return null;
            }
            var d = root.gameObject.AddComponent<ModDialog>();
            d.Window = window;
            d.Parts = parts.PartsOfWindow(window);
            d.closed = onClosed;
            if (d.Parts.Close != null) d.Parts.Close.onClick.AddListener(() => d.UserClose(false));
            if (d.Parts.Cancel != null) d.Parts.Cancel.onClick.AddListener(() => d.UserClose(true));
            ModalRegistry.Open(root.gameObject);
            return d;
        }

        void Update()
        {
            if (!done && Input.GetKeyDown(KeyCode.Escape) && ModalRegistry.Top == gameObject) escPressed = true;
        }

        // Closed after every Update of the frame: the game's own Esc checks run in Update and must still see the
        // dialog open, or this very Esc would open the pause menu.
        void LateUpdate()
        {
            if (escPressed) { escPressed = false; UserClose(false); }
        }

        // The player's close (a button or Esc): through Asking when the owner set it.
        void UserClose(bool viaCancel)
        {
            if (done) return;
            Func<bool, bool> ask = Asking;
            if (ask != null)
            {
                bool go;
                try { go = ask(viaCancel); }
                catch (Exception e) { Log.Error("ui: a dialog's close question failed: " + e.Message); go = true; }
                if (!go) return;
            }
            Close();
        }

        public void Close()
        {
            if (done) return;
            done = true;
            string failed = owned.CloseAll();
            if (failed != null) Log.Error("ui: a question over a closing dialog failed to close: " + failed);
            ModalRegistry.Close(gameObject);
            Destroy(gameObject);
            if (closed != null)
            {
                try { closed(); }
                catch (Exception e) { Log.Error("ui: a dialog's close handler failed: " + e.Message); }
            }
        }

        // Hidden, it stops holding the game; shown again, it is modal again.
        void OnEnable() { if (!done) ModalRegistry.Open(gameObject); }

        void OnDisable() { ModalRegistry.Close(gameObject); }
    }

    // The strip under the game's speed buttons (DD10): a column under each button holding the players who chose it,
    // the column of the speed in effect lit with the panel's own mark, and a status line. Every piece is a clone of
    // the speed panel's own: its frame behind, the buttons' dark faces for the name chips, a lit mark for the column.
    public sealed class SpeedStrip
    {
        public const int Columns = 6;
        public RectTransform Root;
        public TMP_Text Status;
        readonly RectTransform[] columns = new RectTransform[Columns];
        readonly Image[] lit = new Image[Columns];
        readonly List<GameObject>[] chips = new List<GameObject>[Columns];
        GameParts parts;
        float chipHeight, pad;
        RectTransform gameFrame;   // the speed panel's own frame: the strip lines up with its left edge and width

        public static SpeedStrip Build(GameParts parts, Canvas canvas)
        {
            if (parts == null || canvas == null || parts.Missing != null || parts.Scene != "game") return null;
            var s = new SpeedStrip { parts = parts };
            s.Root = UiWidgets.Box(canvas.transform, "speed strip");
            s.SitBelowGameWindows();
            Image frame = s.Frame(canvas);
            if (frame == null) { s.Destroy(); return null; }
            for (int i = 0; i < Columns; i++)
            {
                s.chips[i] = new List<GameObject>();
                s.columns[i] = UiWidgets.Box(s.Root, "speed column " + i);
                s.lit[i] = parts.SpeedMark(s.columns[i], i);
                if (s.lit[i] == null) { s.Destroy(); return null; }
                UiWidgets.Stretch(s.lit[i].rectTransform);
                s.lit[i].gameObject.SetActive(false);
            }
            s.Status = parts.Text(s.Root, TextRole.Body);
            if (s.Status == null) { s.Destroy(); return null; }
            s.Status.alignment = TextAlignmentOptions.TopLeft;   // the pop-up's own text is centred
            s.Layout();
            return s;
        }

        // The mod's canvas draws over every game window, so the strip would cover the Plan Mission window and the
        // like. Instead it sits in the game's own hierarchy right after the speed panel (its parent's next child),
        // drawn as the panel is: below the game's windows and tooltips. Placement is by screen position, so the
        // parent does not matter; a layout group there must not move it.
        void SitBelowGameWindows()
        {
            var panel = parts.Original(GameParts.PPanel) as Component;
            Transform host = panel != null ? panel.transform.parent : null;
            if (host == null) return;
            Root.SetParent(host, false);
            Root.SetSiblingIndex(panel.transform.GetSiblingIndex() + 1);
            Root.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        }

        // The panel's own frame image (its children left out), stretched behind the strip.
        Image Frame(Canvas canvas)
        {
            var mark = (Image)parts.Original(GameParts.PMark + "0");
            Transform buttons = mark != null && mark.transform.parent != null ? mark.transform.parent.parent : null;
            Transform frame = buttons != null ? buttons.parent : null;
            if (frame == null || frame.GetComponent<Image>() == null)
            {
                Log.Warn("ui: speed strip not built: the speed panel has no frame image");
                return null;
            }
            gameFrame = frame as RectTransform;
            string problem;
            RectTransform rt = Strip.Clone(frame.gameObject, Root, "speed strip frame", out problem, clone =>
            {
                foreach (Transform ch in clone.transform) ch.gameObject.SetActive(false);
            });
            if (rt == null) return null;
            for (int i = rt.childCount - 1; i >= 0; i--) Object.DestroyImmediate(rt.GetChild(i).gameObject);
            UiWidgets.Stretch(rt);
            return rt.GetComponent<Image>();
        }

        // Under the six buttons, each column exactly as wide as its button.
        public void Layout()
        {
            var first = (Button)parts.Original(GameParts.PSpeedButton + "0");
            var last = (Button)parts.Original(GameParts.PSpeedButton + (Columns - 1));
            if (first == null || last == null) return;
            Canvas rc = UiWidgets.RootCanvas(Root);
            float k = rc != null && rc.scaleFactor > 0f ? rc.scaleFactor : 1f;
            Rect a = UiWidgets.ScreenRect((RectTransform)first.transform);
            Rect b = UiWidgets.ScreenRect((RectTransform)last.transform);
            pad = 8f;
            chipHeight = 20f;
            int most = 1;
            foreach (List<GameObject> l in chips) most = Math.Max(most, l.Count);
            float colH = pad + most * (chipHeight + 3f);
            float statusH = 28f;
            float h = colH + statusH + pad;
            float left, right;
            Rect f = gameFrame != null ? UiWidgets.ScreenRect(gameFrame) : Rect.MinMaxRect(a.xMin, 0f, b.xMax, 0f);
            HudPlacement.Span(f.xMin, f.xMax, a.xMin, b.xMax, Screen.width, 2f, out left, out right);
            UiWidgets.PlaceAt(Root, new Vector2(left, a.yMin - 2f * k - h * k), new Vector2((right - left) / k, h));
            for (int i = 0; i < Columns; i++)
            {
                var btn = (Button)parts.Original(GameParts.PSpeedButton + i);
                Rect r = UiWidgets.ScreenRect((RectTransform)btn.transform);
                UiWidgets.PlaceAt(columns[i], new Vector2(r.xMin, a.yMin - 2f * k - colH * k),
                                  new Vector2(r.width / k, colH - pad));
                for (int j = 0; j < chips[i].Count; j++)
                {
                    var c = (RectTransform)chips[i][j].transform;
                    c.anchorMin = new Vector2(0f, 1f);
                    c.anchorMax = new Vector2(1f, 1f);
                    c.pivot = new Vector2(0.5f, 1f);
                    c.sizeDelta = new Vector2(-6f, chipHeight);
                    c.anchoredPosition = new Vector2(0f, -3f - j * (chipHeight + 3f));
                }
            }
            RectTransform st = Status.rectTransform;
            st.anchorMin = new Vector2(0f, 0f); st.anchorMax = new Vector2(1f, 0f); st.pivot = new Vector2(0f, 0f);
            st.sizeDelta = new Vector2(-2f * pad, statusH);
            st.anchoredPosition = new Vector2(pad, pad / 2f);
        }

        // One player's chip under a speed button: the button's own dark face with the name in the panel's small text.
        public bool AddChip(int column, string cleanedName)
        {
            if (column < 0 || column >= Columns) return false;
            Image face = parts.SpeedButtonFace(columns[column], column);
            if (face == null) return false;
            TMP_Text t = parts.Text(face.transform, TextRole.Body);
            if (t == null) { Object.Destroy(face.gameObject); return false; }
            UiWidgets.Stretch(t.rectTransform);
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            chips[column].Add(face.gameObject);
            Layout();
            UiFonts.SetPeerText(t, cleanedName);
            return true;
        }

        public void ClearChips()
        {
            foreach (List<GameObject> l in chips)
            {
                foreach (GameObject g in l) if (g != null) Object.DestroyImmediate(g);
                l.Clear();
            }
            Layout();
        }

        // The column of the speed in effect (-1: none lit).
        public void Light(int column)
        {
            for (int i = 0; i < Columns; i++) if (lit[i] != null) lit[i].gameObject.SetActive(i == column);
        }

        public void Destroy() { if (Root != null) Object.Destroy(Root.gameObject); }
    }

    // A player's connection (DD27): four bars of rising height, lit ones the speed panel's own lit image with its icon
    // removed, unlit ones a speed button's dark face, and the ping as text beside them ("-" when none is known, so
    // the state never rests on colour alone).
    public sealed class SignalBarsWidget
    {
        public RectTransform Root;
        public TMP_Text Ping;
        readonly Image[] lit = new Image[SignalBars.Max];
        readonly Image[] unlit = new Image[SignalBars.Max];

        public static SignalBarsWidget Build(GameParts parts, Transform parent)
        {
            if (parts == null || parts.Missing != null || parts.Scene != "game") return null;
            var w = new SignalBarsWidget { Root = UiWidgets.Box(parent, "signal bars") };
            w.Root.sizeDelta = new Vector2(120f, 32f);
            for (int i = 0; i < SignalBars.Max; i++)
            {
                w.unlit[i] = parts.SpeedButtonFace(w.Root, 1);
                w.lit[i] = parts.SpeedMark(w.Root, 1);
                if (w.unlit[i] == null || w.lit[i] == null) { Object.Destroy(w.Root.gameObject); return null; }
                Transform icon = w.lit[i].transform;
                for (int c = icon.childCount - 1; c >= 0; c--) Object.DestroyImmediate(icon.GetChild(c).gameObject);
                foreach (Image im in new[] { w.unlit[i], w.lit[i] })
                {
                    RectTransform rt = im.rectTransform;
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                    rt.pivot = Vector2.zero;
                    rt.localScale = Vector3.one;
                    rt.sizeDelta = new Vector2(7f, 9f + 7f * i);
                    rt.anchoredPosition = new Vector2(i * 11f, 2f);
                }
            }
            w.Ping = parts.Text(w.Root, TextRole.Value);
            if (w.Ping == null) { Object.Destroy(w.Root.gameObject); return null; }
            RectTransform pt = w.Ping.rectTransform;
            pt.anchorMin = pt.anchorMax = new Vector2(0f, 0f);
            pt.pivot = Vector2.zero;
            pt.sizeDelta = new Vector2(80f, 32f);
            pt.anchoredPosition = new Vector2(SignalBars.Max * 11f + 6f, 0f);
            w.Ping.alignment = TextAlignmentOptions.BottomLeft;
            w.Ping.enableWordWrapping = false;
            w.Set(-1, "-");
            return w;
        }

        // text: the ping as the table words it ("~45 ms"), or "-" when none is known.
        public void Set(int pingMs, string text)
        {
            int bars = SignalBars.For(pingMs);
            for (int i = 0; i < SignalBars.Max; i++)
            {
                lit[i].gameObject.SetActive(i < bars);
                unlit[i].gameObject.SetActive(i >= bars);
            }
            Ping.text = string.IsNullOrEmpty(text) ? "-" : text;
        }
    }
}
