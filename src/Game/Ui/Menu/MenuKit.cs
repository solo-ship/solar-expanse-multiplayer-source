using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The menu screens' building blocks, all from the parts kit (DD0): the load dialog's frame (ModDialog), its own
    // OK button for every push button, the kit's texts, input fields, list rows and agency toggles. Positions are in
    // the window's own units, measured from its top-left corner (the boards' pixels at 1920x1080), so they scale
    // with the game's canvas at every resolution. Every clickable widget goes into the screens' UiMap with its id,
    // and every click logs "ui: click <id>" (the UI tools wait for that line).
    public sealed class MenuKit
    {
        public const float Header = 56f;   // the dialog frame's title bar

        public readonly GameParts Parts;
        readonly MenuScreens owner;

        public MenuKit(GameParts parts, MenuScreens owner)
        {
            Parts = parts;
            this.owner = owner;
        }

        // ---- the frame ----

        // A modal dialog of the given size with the table's title, its name field hidden, both its buttons hidden until
        // a screen uses them, and its own list area hidden (the screen lays out its own content) or, with list, kept
        // for the screen to place (DialogFrame.PlaceList): the game's own Scroll View, so the screen's rows scroll as
        // the game's. The frame's own layout is off (DialogFrame.Size), so it keeps the size the screen gives it.
        public ModDialog Dialog(Canvas canvas, string titleKey, float width, float height, Action onClosed,
                                bool list = false)
        {
            ModDialog d = ModDialog.Open(Parts, canvas, titleKey, onClosed);
            if (d == null) return null;
            Vector2 size;
            DialogFrame.Size(d, width, height, out size);
            if (d.Parts.Ok != null) d.Parts.Ok.gameObject.SetActive(false);
            if (d.Parts.Cancel != null) d.Parts.Cancel.gameObject.SetActive(false);
            RectTransform area = ListArea(d.Parts);
            if (area != null && !list) area.gameObject.SetActive(false);
            return d;
        }

        // The list's scroll area: the highest object above the list that holds none of the frame's own controls.
        public static RectTransform ListArea(WindowParts w) { return DialogFrame.ListArea(w); }

        // A plain box at a place in its parent (top-left based, window units).
        public static RectTransform Area(Transform parent, string name, float x, float y, float w, float h)
        {
            RectTransform rt = UiWidgets.Box(parent, name);
            At(rt, x, y, w, h);
            return rt;
        }

        public static void At(RectTransform rt, float x, float y, float w, float h)
        {
            Free(rt);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        // Placed by the screen, never by a layout group of the part it sits in (a cloned frame or row may have one).
        public static void Free(RectTransform rt)
        {
            LayoutElement le = rt.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
        }

        // The content box of a dialog: the window below its title bar.
        public static RectTransform Content(ModDialog d)
        {
            RectTransform c = UiWidgets.Box(d.Window, "content");
            Free(c);
            c.anchorMin = Vector2.zero; c.anchorMax = Vector2.one;
            c.pivot = new Vector2(0f, 1f);
            c.offsetMin = Vector2.zero; c.offsetMax = new Vector2(0f, -DialogFrame.Header(d.Parts));
            c.localScale = Vector3.one;
            return c;
        }

        // ---- texts ----

        // A kit text with a line from the table. sentence: the case as written (the kit's roles draw in capitals);
        // wrap: several lines in its box.
        public TMP_Text Label(Transform parent, TextRole role, string key, float x, float y, float w, float h,
                              bool sentence = false, bool wrap = false, params object[] args)
        {
            TMP_Text t = Parts.Text(parent, role);
            if (t == null) return null;
            At(t.rectTransform, x, y, w, h);
            Style(t, sentence, wrap);
            t.alignment = TextAlignmentOptions.TopLeft;
            SetLine(t, key, args);
            return t;
        }

        public static void Style(TMP_Text t, bool sentence, bool wrap)
        {
            if (t == null) return;
            if (sentence) t.fontStyle &= ~(FontStyles.UpperCase | FontStyles.LowerCase | FontStyles.SmallCaps);
            t.enableWordWrapping = wrap;
            t.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
        }

        // Our own line: never rich text (DD13), and from the table only.
        public static void SetLine(TMP_Text t, string key, params object[] args)
        {
            if (t == null) return;
            t.richText = false;
            t.parseCtrlCharacters = false;
            t.text = key == null ? "" : UiText.Get(key, args);
        }

        // Peer text (a save's title, a host's name): cleaned, '?' for what the fonts cannot draw, cut to fit.
        public static void SetPeer(TMP_Text t, string raw, int maxChars = 64)
        {
            if (t == null) return;
            UiFonts.SetPeerText(t, TextSanitiser.Clean(raw, maxChars, ""));
        }

        // ---- controls ----

        // The load dialog's own OK button, cloned, with a line from the table: every push button of the screens.
        public Button Button(Transform parent, string id, string key, float x, float y, float w, float h,
                             Action onClick)
        {
            var ok = Parts.Original(GameParts.POk) as Component;
            if (ok == null)
            {
                Log.Warn("ui: button " + id + " not built: no dialog button in the " + Parts.Scene);
                return null;
            }
            string problem;
            RectTransform rt = Strip.Clone(ok.gameObject, parent, "button " + id, out problem);
            if (rt == null) return null;
            At(rt, x, y, w, h);
            foreach (TMP_Text t in rt.GetComponentsInChildren<TMP_Text>(true)) SetLine(t, key);
            Button b = rt.GetComponent<Button>();
            // the game's own OK is off until a save is picked: its clone starts on, and a screen turns it off
            b.interactable = true;
            Wire(b, id, onClick);
            return b;
        }

        // A button's first text (its label).
        public static TMP_Text TextOf(Component c)
        {
            return c != null ? c.GetComponentInChildren<TMP_Text>(true) : null;
        }

        public void Wire(Button b, string id, Action onClick)
        {
            if (b == null) return;
            b.onClick.AddListener(() => Clicked(id, onClick));
            owner.Track(id, b.transform);
        }

        // A toggle's click (either way: the screen then puts its set right) as the widget id.
        public void WireToggle(Toggle t, string id, Action onClick)
        {
            if (t == null) return;
            t.onValueChanged.AddListener(on => { if (!Quiet) Clicked(id, onClick); });
            owner.Track(id, t.transform);
        }

        public void Clicked(string id, Action onClick)
        {
            Log.Info("ui: click " + id);
            if (onClick == null) return;
            try { onClick(); }
            catch (Exception e) { Log.Error("ui: the click on " + id + " failed: " + e); }
        }

        // The load dialog's name field. password: the field shows only its mask (the join code).
        public TMP_InputField Field(Transform parent, string id, int maxChars, bool password, float x, float y,
                                    float w, float h)
        {
            TMP_InputField f = Parts.Input(parent, maxChars, password);
            if (f == null) return null;
            At((RectTransform)f.transform, x, y, w, h);
            owner.Track(id, f.transform);
            return f;
        }

        // A list row in the load dialog's own list (scrolled by it, as the game's rows are), placed by the screen
        // (DialogFrame.PlaceList, Item): a save's name, no delete unless asked for; a click on it is reported as the
        // item's id.
        public RowParts ListRow(RectTransform list, string id, bool delete, Action onPick, Action onDelete)
        {
            RectTransform rt = Parts.ListRow(list);
            return rt != null ? RowControls(rt, id, delete, onPick, onDelete) : null;
        }

        RowParts RowControls(RectTransform rt, string id, bool delete, Action onPick, Action onDelete)
        {
            RowParts r = Parts.PartsOfRow(rt);
            // the save's name lies inside its details: those stay, their other texts go
            foreach (TMP_Text t in new[] { r.Date, r.Company, r.Difficulty, r.System, r.Epoch })
                if (t != null) t.gameObject.SetActive(false);
            if (r.TrashIcon != null) r.TrashIcon.SetActive(delete);
            if (r.Trash != null)
            {
                r.Trash.gameObject.SetActive(delete);
                if (delete) Wire(r.Trash, id + ".delete", onDelete);
            }
            if (r.Toggle != null)
            {
                r.Toggle.onValueChanged.AddListener(on => { if (!Quiet) Clicked(id, onPick); });
                owner.Track(id, r.Toggle.transform);
            }
            return r;
        }

        // While set, a toggle the screen moves itself is not reported as the player's click.
        public bool Quiet;

        // Sets toggles the way the game does (MyToggle's own refresh runs), as the screen's state says: only `on` is
        // on. Either way the player's click on a toggle reaches the screen, which then puts the set right, so a
        // second click on the picked item leaves it picked.
        public void Select(IList<Toggle> set, Toggle on)
        {
            Quiet = true;
            try
            {
                foreach (Toggle t in set)
                    if (t != null && t != on && t.isOn) GameParts.SetOn(t, false);
                if (on != null && !on.isOn) GameParts.SetOn(on, true);
            }
            finally { Quiet = false; }
        }

        // The new-game page's agency tile, with the agency's name under it (the kit's small text).
        public Toggle AgencyTile(Transform parent, string id, string wire, string shownName, float x, float y,
                                 float size, Action onPick)
        {
            Toggle t = Parts.AgencyToggle(parent, wire);
            if (t == null) return null;
            // scaled whole, logo and all, from the page's own size (its inside is not laid out for another size)
            var rt = (RectTransform)t.transform;
            Vector2 own = rt.sizeDelta;
            At(rt, x, y, own.x, own.y);
            float k = own.x > 1f ? size / own.x : 1f;
            rt.localScale = new Vector3(k, k, 1f);
            TMP_Text name = Parts.Text(parent, TextRole.Small);
            if (name != null)
            {
                At(name.rectTransform, x - 10f, y + own.y * k + 4f, size + 20f, 24f);
                name.alignment = TextAlignmentOptions.Top;
                name.enableWordWrapping = false;
                name.richText = false;
                name.parseCtrlCharacters = false;
                name.text = shownName ?? wire.ToUpperInvariant();
            }
            t.onValueChanged.AddListener(on => { if (!Quiet) Clicked(id, onPick); });
            owner.Track(id, t.transform);
            return t;
        }

        public static void Show(Component c, bool on)
        {
            if (c != null) c.gameObject.SetActive(on);
        }

        public static void Enable(Selectable s, bool on)
        {
            if (s != null) s.interactable = on;
        }

        public static void Destroy(Component c)
        {
            if (c != null) Object.Destroy(c.gameObject);
        }
    }
}
