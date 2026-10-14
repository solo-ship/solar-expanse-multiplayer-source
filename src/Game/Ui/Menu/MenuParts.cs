using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The pieces of the new-game page frame (board 3), found in the clone at the original's places.
    public sealed class PageParts
    {
        public RectTransform Root, Scroll, Content;
        public TMP_Text Crumb, Heading;
        public Button Back, Next;
    }

    // The menu screens' own parts beyond the kit's, by the same recipe (Strip.Clone, DD0) from the menu's own objects,
    // found once per menu. A part a game update moved is missing: the screen that needs it is not built (fail closed).
    //  - the new-game page frame: GameCustomization's star system page (StarSystemInfo: the teal frame, its
    //    "New game > ..." line, the scrolling list on the left, the heading with its line, Back and Next), with the
    //    star system pictures left out;
    //  - one item of that page's list (a MyToggle row: teal when on);
    //  - the panel's own blurred backdrop;
    //  - the load dialog's line under its title (a separator).
    public sealed class MenuParts
    {
        public const string PPage = "new-game page", PPageItem = "new-game list item", PBlur = "new-game backdrop",
            PLine = "dialog line";

        readonly GameParts kit;
        RectTransform page, item, scroll, content, heading, crumb, back, next, schemes, line;
        Image blur;
        int built;

        public string Missing { get; private set; }

        public MenuParts(GameParts kit)
        {
            this.kit = kit;
            try { Find(); }
            catch (Exception e) { Missing = "menu parts (" + e.GetType().Name + ": " + e.Message + ")"; }
            if (Missing != null) Log.Warn("ui: menu part missing: " + Missing);
            else Log.Info("ui: menu parts found (the new-game page, its list item, its backdrop, the dialog line)");
        }

        void Find()
        {
            Game.UI.Menu.GameCustomizationUI.GameCustomizationUI panel = null;
            foreach (var p in Object.FindObjectsOfType<Game.UI.Menu.GameCustomizationUI.GameCustomizationUI>(true))
                if (p != null && p.gameObject.scene.IsValid() && !Strip.IsMod(p.transform)) { panel = p; break; }
            if (panel == null) { Missing = "the new-game panel"; return; }
            var stars = GameParts.Field<Component>(panel, "starSystemSettingsUI");
            page = stars != null ? stars.transform.Find("StarSystemInfo") as RectTransform : null;
            if (page == null) { Missing = PPage; return; }
            scroll = page.Find("Scroll View") as RectTransform;
            content = scroll != null ? scroll.Find("Viewport/Content") as RectTransform : null;
            item = content != null && content.childCount > 0 ? content.GetChild(0) as RectTransform : null;
            crumb = page.Find("WindowDescription") as RectTransform;
            heading = page.Find("Name") as RectTransform;
            back = page.Find("BackButton") as RectTransform;
            next = page.Find("NextButton") as RectTransform;
            schemes = page.Find("SystemSchemes") as RectTransform;
            if (scroll == null || content == null || crumb == null || heading == null || back == null || next == null)
                Missing = PPage;
            else if (item == null || item.GetComponent<Toggle>() == null ||
                     item.GetComponentInChildren<TMP_Text>(true) == null)
                Missing = PPageItem;
            blur = GameParts.Field<Image>(panel, "blurBackground");
            if (Missing == null && blur == null) Missing = PBlur;
            var window = kit.Original(GameParts.PWindow) as Component;
            line = window != null ? window.transform.Find("Decoration/Gradient/Line") as RectTransform : null;
            if (Missing == null && (line == null || line.GetComponent<Image>() == null)) Missing = PLine;
        }

        bool Usable(string what)
        {
            if (Missing == null && kit.Missing == null) return true;
            Log.Warn("ui: " + what + " not built: part missing: " + (Missing ?? kit.Missing));
            return false;
        }

        RectTransform Clone(Transform original, Transform parent, string name, Action<GameObject> prepare = null)
        {
            string problem;
            return Strip.Clone(original.gameObject, parent, name + " #" + (++built), out problem, prepare);
        }

        // The page frame where the game draws it (parent: a box over the whole canvas), its list emptied, the
        // star system pictures gone, every text from the screen.
        public PageParts Page(Transform parent)
        {
            if (!Usable("new-game page")) return null;
            Transform orig = page;
            RectTransform rt = Clone(page, parent, "new-game page", clone =>
            {
                // hidden before the strip (nothing wakes), destroyed after: the tint step goes by sibling paths
                Transform list = Strip.FindSame(clone.transform, orig, content);
                if (list != null) foreach (Transform row in list) row.gameObject.SetActive(false);
                Transform pics = schemes != null ? Strip.FindSame(clone.transform, orig, schemes) : null;
                if (pics != null) pics.gameObject.SetActive(false);
            });
            if (rt == null) return null;
            // every piece is found by its sibling path before anything is destroyed: removing the star system
            // pictures first moved the pieces after them, and the clone was refused as shaped otherwise
            var p = new PageParts { Root = rt };
            p.Scroll = Same(rt, scroll);
            p.Content = Same(rt, content);
            RectTransform pics2 = schemes != null ? Same(rt, schemes) : null;
            p.Crumb = Text(Same(rt, crumb));
            p.Heading = Text(Same(rt, heading));
            p.Back = Btn(Same(rt, back));
            p.Next = Btn(Same(rt, next));
            if (p.Content != null)
                for (int i = p.Content.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(p.Content.GetChild(i).gameObject);
            if (pics2 != null) Object.DestroyImmediate(pics2.gameObject);
            if (p.Scroll == null || p.Content == null || p.Crumb == null || p.Heading == null || p.Back == null ||
                p.Next == null)
            {
                Log.Warn("ui: new-game page refused: its clone is shaped otherwise (" +
                         (p.Scroll == null ? "scroll " : "") + (p.Content == null ? "list " : "") +
                         (p.Crumb == null ? "crumb " : "") + (p.Heading == null ? "heading " : "") +
                         (p.Back == null ? "back " : "") + (p.Next == null ? "next" : "") + ")");
                Object.Destroy(rt.gameObject);
                return null;
            }
            foreach (TMP_Text t in rt.GetComponentsInChildren<TMP_Text>(true)) MenuKit.SetLine(t, null);
            return p;
        }

        // One item of the page's list, under list (laid out by the list) or anywhere (placed by the screen); off.
        public Toggle Item(Transform parent)
        {
            if (!Usable("new-game list item")) return null;
            RectTransform rt = Clone(item, parent, "new-game list item");
            if (rt == null) return null;
            Toggle t = rt.GetComponent<Toggle>();
            GameParts.SetOn(t, false);
            foreach (TMP_Text x in rt.GetComponentsInChildren<TMP_Text>(true)) MenuKit.SetLine(x, null);
            return t;
        }

        public static TMP_Text ItemText(Toggle item)
        {
            return item != null ? item.GetComponentInChildren<TMP_Text>(true) : null;
        }

        // The panel's blurred backdrop over the whole screen (only its own image: anything under it is left out).
        public RectTransform Backdrop(Transform parent)
        {
            if (!Usable("new-game backdrop")) return null;
            RectTransform rt = Clone(blur.transform, parent, "new-game backdrop", clone =>
            {
                foreach (Transform ch in clone.transform) ch.gameObject.SetActive(false);
            });
            if (rt == null) return null;
            for (int i = rt.childCount - 1; i >= 0; i--) Object.DestroyImmediate(rt.GetChild(i).gameObject);
            UiWidgets.Stretch(rt);
            Image img = rt.GetComponent<Image>();
            if (img != null) { img.enabled = true; img.raycastTarget = true; }
            return rt;
        }

        // The load dialog's own line under its title: the Multiplayer screen's separator.
        public Image Line(Transform parent)
        {
            if (!Usable("dialog line")) return null;
            RectTransform rt = Clone(line, parent, "dialog line");
            return rt != null ? rt.GetComponent<Image>() : null;
        }

        static RectTransform Same(RectTransform clone, Transform inOrig)
        {
            Transform root = inOrig;
            while (root != null && root.name != "StarSystemInfo") root = root.parent;
            return root != null ? Strip.FindSame(clone, root, inOrig) as RectTransform : null;
        }

        static TMP_Text Text(RectTransform rt) { return rt != null ? rt.GetComponent<TMP_Text>() : null; }
        static Button Btn(RectTransform rt) { return rt != null ? rt.GetComponent<Button>() : null; }

        // Where a part sits in a root, from the root's top-left corner, in the root's own units.
        public static Rect TopLeftRect(RectTransform part, RectTransform root)
        {
            var c = new Vector3[4];
            part.GetWorldCorners(c);
            // corner 1 is the top-left, corner 3 the bottom-right
            Vector3 a = root.InverseTransformPoint(c[1]), b = root.InverseTransformPoint(c[3]);
            Rect r = root.rect;
            return new Rect(a.x - r.xMin, r.yMax - a.y, b.x - a.x, a.y - b.y);
        }
    }

    // A full-screen modal mod screen (host setup on the new-game page frame): registered with ModalRegistry while it is
    // shown, so the game reads it as a pop-up and Esc reaches only it; Esc closes it, as ModDialog does.
    public sealed class ModPage : MonoBehaviour
    {
        Action closed;
        readonly CloseOnce once = new CloseOnce();
        bool escPressed;

        public static ModPage Open(RectTransform root, Action onClosed)
        {
            var p = root.gameObject.AddComponent<ModPage>();
            p.closed = onClosed;
            ModalRegistry.Open(root.gameObject);
            return p;
        }

        void Update()
        {
            if (!once.Done && Input.GetKeyDown(KeyCode.Escape) && ModalRegistry.Top == gameObject) escPressed = true;
        }

        // After every Update of the frame, as ModDialog: the game's own Esc checks must still see it open.
        void LateUpdate()
        {
            if (escPressed) { escPressed = false; Close(); }
        }

        // Closes once; its handler (which puts the menu back) always runs then.
        public void Close()
        {
            if (!once.Start()) return;
            ModalRegistry.Close(gameObject);
            Destroy(gameObject);
            string failed = CloseOnce.Run(closed);
            if (failed != null) Log.Error("ui: a page's close handler failed: " + failed);
        }

        void OnEnable() { if (!once.Done && closed != null) ModalRegistry.Open(gameObject); }

        void OnDisable() { ModalRegistry.Close(gameObject); }
    }
}
