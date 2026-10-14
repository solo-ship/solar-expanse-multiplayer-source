using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The lobby's and the picker's building blocks, every one a part of the kit (DD0): the game's dialog button, its
    // texts, rows and agency toggles, placed by the screens in the window's own units from its top-left corner.
    // Every clickable widget is registered in the session's UiMap and logs "ui: click <id>" when clicked.
    public sealed class LobbyKit
    {
        public const string IdPrefix = "lobby.";

        public readonly GameParts Parts;
        public readonly Canvas Canvas;
        readonly UiMap map;
        readonly Action mapChanged;
        readonly Dictionary<string, RectTransform> registered = new Dictionary<string, RectTransform>();
        public int Built;                                   // parts cloned for the screen, for its "ui: built" line

        // mapChanged: the map's owner (MenuSessions.MapChanged), told after every change so it writes the file.
        public LobbyKit(GameParts parts, Canvas canvas, UiMap map, Action mapChanged)
        {
            Parts = parts; Canvas = canvas; this.map = map; this.mapChanged = mapChanged;
        }

        public bool InGame { get { return Parts.Scene == "game"; } }

        // ---- parts ----

        // The game's own dialog button (the load dialog's OK), cloned by the kit's recipe, with the table's words.
        public Button Button(Transform parent, string text)
        {
            var orig = Parts.Original(GameParts.POk) as Component;
            if (orig == null) { Log.Warn("ui: lobby button not built: no dialog OK button"); return null; }
            string problem;
            RectTransform rt = Strip.Clone(orig.gameObject, parent, "lobby button", out problem);
            if (rt == null) return null;
            Built++;
            var b = rt.GetComponent<Button>();
            // the game's own OK is off until a save is picked: its clone starts on, and a screen turns it off
            b.interactable = true;
            SetButton(b, text);
            return b;
        }

        public TMP_Text Text(Transform parent, TextRole role, TextAlignmentOptions align, bool wrap)
        {
            TMP_Text t = Parts.Text(parent, role);
            if (t == null) return null;
            Built++;
            t.alignment = align;
            t.enableWordWrapping = wrap;
            t.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            return t;
        }

        public RowParts Row(Transform parent)
        {
            RectTransform rt = Parts.ListRow(parent);
            if (rt == null) return null;
            Built++;
            RowParts r = Parts.PartsOfRow(rt);
            // a player is no save: no delete
            if (r.TrashIcon != null) r.TrashIcon.SetActive(false);
            if (r.Trash != null) r.Trash.gameObject.SetActive(false);
            return r;
        }

        // The agency's own toggle from the new-game page (its logo); the menu has them, the game does not.
        public Toggle AgencyToggle(Transform parent, string agencyId)
        {
            if (InGame || Parts.Original(GameParts.PAgencies) == null) return null;
            Toggle t = Parts.AgencyToggle(parent, agencyId);
            if (t != null) Built++;
            return t;
        }

        // A logo shown on the lobby's dark rows: the toggle's own backing tile is switched off, so the logo sits on the
        // lobby's black, not on a light box. A toggle whose logo is the only picture is left as it is.
        public static void PlainLogo(Toggle t)
        {
            if (t == null) return;
            Graphic backing = t.targetGraphic != null ? t.targetGraphic : t.GetComponent<Image>();
            if (backing == null || t.GetComponentsInChildren<Image>(true).Length < 2) return;
            backing.enabled = false;
        }

        // The game's own tick box, for the Ready column: in game the notification panel's "pause on each
        // notification" toggle, else (and at the menu) the Settings window's "invert Y axis" toggle. Its label is
        // hidden; it shows a state and is no control (disabled, so a click never changes it). Null when neither is
        // there, and the column shows its words alone.
        public Toggle TickBox(Transform parent)
        {
            Toggle orig = null;
            if (InGame)
                orig = GameParts.Field<Toggle>(MonoBehaviourSingleton<Manager.NotificationManager>.Instance,
                                               "pauseToggle");
            if (orig == null)
                foreach (var sw in Object.FindObjectsOfType<Game.UI.Windows.SettingsWindow>(true))
                    if (sw != null && sw.gameObject.scene.IsValid() && !Strip.IsMod(sw.transform))
                    {
                        orig = GameParts.Field<Toggle>(sw, "invertYAxisToggle");
                        if (orig != null) break;
                    }
            if (orig == null) { Log.Warn("ui: tick box not built: no game tick box in this scene"); return null; }
            string problem;
            RectTransform rt = Strip.Clone(orig.gameObject, parent, "tick box", out problem);
            if (rt == null) return null;
            Built++;
            foreach (TMP_Text t in rt.GetComponentsInChildren<TMP_Text>(true))
            {
                t.text = "";
                t.gameObject.SetActive(false);
            }
            var tick = rt.GetComponent<Toggle>();
            if (tick != null) { GameParts.SetOn(tick, false); tick.enabled = false; }
            return tick;
        }

        // Puts a tick box's box (its target graphic) at (x, middle - size / 2), size tall, wherever the box sits in
        // the toggle's own rectangle. Returns the box's width.
        public static float PlaceTick(Toggle t, float x, float middle, float size)
        {
            if (t == null) return 0f;
            var rt = (RectTransform)t.transform;
            Graphic box = t.targetGraphic != null ? t.targetGraphic : t.graphic;
            RectTransform br = box != null ? box.rectTransform : rt;
            At(rt, 0f, 0f, rt.rect.width, rt.rect.height);
            var c = new Vector3[4];
            br.GetWorldCorners(c);
            Vector3 min = rt.InverseTransformPoint(c[0]), max = rt.InverseTransformPoint(c[2]);
            float k = size / Mathf.Max(1f, max.y - min.y);
            rt.localScale = new Vector3(k, k, 1f);
            rt.anchoredPosition = new Vector2(x - min.x * k, -(middle - size / 2f) - max.y * k);
            return (max.x - min.x) * k;
        }

        // ---- placing ----

        // Puts rt at (x, y) from its parent's top-left corner, w by h, in the parent's units.
        public static void At(Component c, float x, float y, float w, float h)
        {
            if (c == null) return;
            var rt = (RectTransform)c.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        // Puts a part at (x, y) at its own size, scaled to fit a box of that height (an agency's logo card: its
        // insides keep the proportions the game drew them at).
        public static float Fit(Component c, float x, float y, float box)
        {
            if (c == null) return 0f;
            var rt = (RectTransform)c.transform;
            Vector2 own = rt.rect.size;
            float k = box / Mathf.Max(1f, Mathf.Max(own.x, own.y));
            At(rt, x, y, own.x, own.y);
            rt.localScale = new Vector3(k, k, 1f);
            return own.x * k;
        }

        public static RectTransform Box(Transform parent, string name, float x, float y, float w, float h)
        {
            RectTransform rt = UiWidgets.Box(parent, name);
            At(rt, x, y, w, h);
            return rt;
        }

        // ---- words ----

        // A line of the table on a text; one that holds another player's words goes through PeerText (width cap,
        // '?' for what the fonts cannot draw). Rich text stays off either way.
        public static void Set(TMP_Text t, LobbyLine line, float width = -1f)
        {
            if (t == null) return;
            t.richText = false;
            t.parseCtrlCharacters = false;
            if (line == null) { t.text = ""; return; }
            if (line.Peer) UiFonts.SetPeerText(t, line.Text(), width > 0f ? width : t.rectTransform.rect.width);
            else t.text = line.Text();
        }

        public static void SetButton(Button b, string text)
        {
            if (b == null) return;
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                t.richText = false;
                t.parseCtrlCharacters = false;
                t.text = text ?? "";
            }
        }

        public static void SetButton(Button b, LobbyLine line, float width)
        {
            if (b == null) return;
            if (line == null || !line.Peer) { SetButton(b, line != null ? line.Text() : ""); return; }
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true)) Set(t, line, width);
        }

        // ---- clicks and the map ----

        // id is the part after "lobby.", made of [a-z0-9_.]. A throwing action is logged, never passed to Unity.
        public void OnClick(Button b, string id, Action act)
        {
            if (b == null) return;
            b.onClick.AddListener(() => Clicked(id, act));
            Register(id, (RectTransform)b.transform);
        }

        public void OnToggle(Toggle t, string id, Action act)
        {
            if (t == null) return;
            t.onValueChanged.AddListener(on => Clicked(id, act));
            Register(id, (RectTransform)t.transform);
        }

        public static void Clicked(string id, Action act)
        {
            Log.Info("ui: click " + IdPrefix + id);
            try { act(); }
            catch (Exception e) { Log.Error("ui: the lobby's " + id + " failed: " + e); }
        }

        public void Register(string id, RectTransform rt)
        {
            if (map == null || !map.Writes || rt == null) return;
            registered[IdPrefix + id] = rt;
        }

        // Puts every registered widget's place on screen into the session's one map (MenuSessions.Map) now: after a
        // build, or when the window's size changed. A widget that is gone or hidden leaves the map. The map's file is
        // written by its owner (told through mapChanged), never here; a map that may not write (no test seams, no
        // player path) stores nothing.
        public void PublishMap(string screen)
        {
            if (map == null || !map.Writes) return;
            try
            {
                Canvas.ForceUpdateCanvases();
                map.SetWindow(Screen.width, Screen.height);
                foreach (KeyValuePair<string, RectTransform> kv in registered)
                {
                    RectTransform rt = kv.Value;
                    if (rt == null || !rt.gameObject.activeInHierarchy) { map.Remove(kv.Key); continue; }
                    Rect r = UiWidgets.ScreenRect(rt);
                    map.Register(kv.Key, new UiRect(r.xMin, Screen.height - r.yMax, r.width, r.height));
                }
            }
            catch (Exception e) { Log.Warn("ui: the " + screen + "'s widgets were not put in the map: " + e.Message); }
            Changed();
        }

        // Forgets this screen's widgets (it closed or is built again).
        public void Unregister()
        {
            bool gone = false;
            if (map != null) foreach (string id in registered.Keys) if (map.Remove(id)) gone = true;
            registered.Clear();
            if (gone) Changed();
        }

        void Changed()
        {
            if (mapChanged == null) return;
            try { mapChanged(); }
            catch (Exception e) { Log.Warn("ui: the widget map's owner was not told: " + e.Message); }
        }

        // Test seams only: the window's own pieces and where they sit, so a screenshot that looks wrong can be
        // matched to the part that moved.
        public static void Describe(RectTransform root, string what)
        {
            if (root == null || !PartsBench.SeamsOpen()) return;
            var sb = new System.Text.StringBuilder();
            foreach (RectTransform c in root)
                sb.Append(c.name).Append(' ').Append(c.anchorMin).Append('-').Append(c.anchorMax).Append(' ')
                  .Append(c.rect.size).Append(c.gameObject.activeSelf ? "" : " (hidden)").Append("; ");
            Log.Info("ui: " + what + " " + root.rect.size + ": " + sb);
        }

        public static void Destroy(Component c) { if (c != null) Object.Destroy(c.gameObject); }

        // ---- the window ----

        // Sizes a mod dialog's window and readies its frame for the screen's own layout (DialogFrame): the title bar
        // across the top, the OK and Cancel buttons out of their row so the screen places them. Returns the title
        // bar's height.
        public float Frame(ModDialog d, float wantW, float wantH, out Vector2 size)
        {
            float header = DialogFrame.Size(d, wantW, wantH, out size);
            if (d.Parts.Close != null && d.Parts.Close.gameObject.activeSelf)
            {
                var cr = (RectTransform)d.Parts.Close.transform;
                cr.anchorMin = cr.anchorMax = cr.pivot = new Vector2(1f, 1f);
                cr.anchoredPosition = new Vector2(-8f, -8f);
            }
            Describe(d.Window, "window");
            return header;
        }

        // A window that shows no list (the picker, a question): the window's own list area is hidden.
        public static void HideList(WindowParts w)
        {
            if (w == null || w.List == null) return;
            ScrollRect sr = w.List.GetComponentInParent<ScrollRect>();
            if (sr != null) sr.gameObject.SetActive(false);
            else w.List.gameObject.SetActive(false);
        }
    }
}
