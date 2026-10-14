using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // How a screen lays out the game's load-dialog frame (ModDialog) for its own content, the same way on every screen:
    //  - the frame's own vertical layout and size fitter go off (they size the window to the original's one save list
    //    and put its pieces back where the game had them);
    //  - the window takes the size the screen asks for, at most the canvas less a margin;
    //  - the title bar's height is read from the bar that holds the title (the frame's decoration around it spans the
    //    whole window, so its height is not the header's);
    //  - the frame's own scrolling list is placed where the screen wants it, its rows placed by the screen (the list's
    //    layout group would take a stretched row's width as its size) and the list as tall as its rows;
    //  - its OK and Cancel leave their button row and are placed by the screen.
    // Units are the window's own (the boards' pixels at 1920x1080), measured from its top-left corner.
    public static class DialogFrame
    {
        public const float DefaultHeader = 56f, Pad = 26f, ButtonH = 40f, ButtonW = 200f, ButtonGap = 12f;

        // Lays the frame out at (at most) wantW x wantH and returns the title bar's height.
        public static float Size(ModDialog d, float wantW, float wantH, out Vector2 size)
        {
            RectTransform win = d.Window;
            foreach (LayoutGroup g in win.GetComponents<LayoutGroup>()) g.enabled = false;
            foreach (ContentSizeFitter f in win.GetComponents<ContentSizeFitter>()) f.enabled = false;
            var canvas = win.parent != null ? UiWidgets.RootCanvas(win) : null;
            Vector2 area = canvas != null ? ((RectTransform)canvas.transform).rect.size : new Vector2(1920f, 1080f);
            size = new Vector2(Mathf.Round(Mathf.Min(wantW, area.x - 60f)), Mathf.Round(Mathf.Min(wantH, area.y - 40f)));
            win.anchorMin = win.anchorMax = new Vector2(0.5f, 0.5f);
            win.pivot = new Vector2(0.5f, 0.5f);
            win.sizeDelta = size;
            win.anchoredPosition = Vector2.zero;
            FreeButtons(d.Parts);
            return Header(d.Parts);
        }

        // Grows or shrinks the window to a height, its top edge staying where it is centred now (a dialog whose
        // content is known only once its text is set).
        public static void Height(ModDialog d, float h)
        {
            RectTransform win = d.Window;
            win.sizeDelta = new Vector2(win.sizeDelta.x, Mathf.Round(h));
        }

        // The height of the bar that holds the title: the nearest box around it that is not most of the window.
        public static float Header(WindowParts w)
        {
            if (w == null || w.Title == null || w.Root == null) return DefaultHeader;
            float winH = w.Root.rect.height;
            for (Transform x = w.Title.transform.parent; x != null && x != w.Root; x = x.parent)
            {
                float h = ((RectTransform)x).rect.height;
                if (h >= 20f && h <= 120f && h < winH * 0.5f) return h;
            }
            return DefaultHeader;
        }

        // OK and Cancel out of the frame's button row, straight under the window, so the screen places them; the row
        // goes once it holds no button.
        static void FreeButtons(WindowParts w)
        {
            foreach (Button b in new[] { w.Ok, w.Cancel })
            {
                if (b == null || b.transform.parent == w.Root) continue;
                Transform row = b.transform.parent;
                b.transform.SetParent(w.Root, false);
                if (row != null && row != w.Root && row.GetComponentsInChildren<Button>(true).Length == 0)
                    row.gameObject.SetActive(false);
            }
        }

        // Puts rt at (x, y) from its parent's top-left corner, w by h, in the parent's units; no layout group moves it.
        public static void At(Component c, float x, float y, float w, float h)
        {
            if (c == null) return;
            var rt = (RectTransform)c.transform;
            LayoutElement le = rt.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        // The buttons side by side, centred on the window at y, each its width (or ButtonW). Hidden ones are skipped.
        public static void ButtonRow(Vector2 size, float y, params Button[] buttons)
        {
            var shown = new List<Button>();
            foreach (Button b in buttons) if (b != null && b.gameObject.activeSelf) shown.Add(b);
            float total = shown.Count * ButtonW + (shown.Count - 1) * ButtonGap;
            float x = Mathf.Round((size.x - total) / 2f);
            foreach (Button b in shown)
            {
                At(b, x, y, ButtonW, ButtonH);
                x += ButtonW + ButtonGap;
            }
        }

        // ---- the frame's list ----

        // The list's scroll area: the highest object above the list that holds none of the frame's own controls.
        public static RectTransform ListArea(WindowParts w)
        {
            if (w == null || w.List == null || w.Root == null) return null;
            Transform keep = w.List;
            for (Transform x = w.List.parent; x != null && x != w.Root; x = x.parent)
            {
                if (Encloses(x, w.Title) || Encloses(x, w.Ok) || Encloses(x, w.Cancel) || Encloses(x, w.Close)) break;
                keep = x;
            }
            return keep as RectTransform;
        }

        static bool Encloses(Transform t, Component c) { return c != null && c.transform.IsChildOf(t); }

        // The frame's scrolling list at a place in the window, its rows to be placed by the screen: the list's own
        // layout group and size fitter off, the list across the viewport's width from its top. Returns the list.
        public static RectTransform PlaceList(WindowParts w, float x, float y, float width, float height)
        {
            RectTransform area = ListArea(w);
            if (area == null) return null;
            area.gameObject.SetActive(true);
            At(area, x, y, width, height);
            RectTransform list = w.List;
            foreach (LayoutGroup g in list.GetComponents<LayoutGroup>()) g.enabled = false;
            foreach (ContentSizeFitter f in list.GetComponents<ContentSizeFitter>()) f.enabled = false;
            list.anchorMin = new Vector2(0f, 1f);
            list.anchorMax = new Vector2(1f, 1f);
            list.pivot = new Vector2(0.5f, 1f);
            list.localScale = Vector3.one;
            list.sizeDelta = new Vector2(0f, list.sizeDelta.y);
            list.anchoredPosition = Vector2.zero;
            // the scrollbar only when the rows need it (the game's dialog always lists more than fits)
            ScrollRect sr = area.GetComponent<ScrollRect>();
            if (sr != null) sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            return list;
        }

        // The list's width for rows: the viewport's, less room for its scrollbar.
        public static float ListWidth(RectTransform list)
        {
            Canvas.ForceUpdateCanvases();
            float wd = list.rect.width;
            if (wd < 2f && list.parent is RectTransform) wd = ((RectTransform)list.parent).rect.width;
            return Mathf.Max(1f, wd - 12f);
        }

        // An item of a placed list: at y from its top, across its width less inset on each side, h tall.
        public static void Item(RectTransform rt, float y, float h, float inset)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(-2f * inset - 12f, h);
            rt.anchoredPosition = new Vector2(-6f, -y);
            LayoutElement le = rt.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
        }

        // The list as tall as its items, so the scroll view scrolls exactly them.
        public static void FitList(RectTransform list, float height)
        {
            list.sizeDelta = new Vector2(list.sizeDelta.x, Mathf.Max(1f, height));
        }

        // ---- rows ----

        // A row that shows something and is no choice: its own toggle keeps the game's normal look (a toggle switched
        // off or made not interactable takes the game's disabled tint, which hides its texts), but nothing of it takes
        // the pointer, so a click never selects it. The buttons on it (another control's graphics) keep theirs. Call
        // it once the row is built.
        public static void Inert(RowParts r)
        {
            if (r == null || r.Root == null) return;
            foreach (Graphic g in r.Root.GetComponentsInChildren<Graphic>(true))
            {
                Selectable owner = g.GetComponentInParent<Selectable>();
                if (owner != null && owner != r.Toggle && owner.transform.IsChildOf(r.Root)) continue;
                g.raycastTarget = false;
            }
        }

        // A text paragraph's height when wrapped at width.
        public static float TextHeight(TMP_Text t, float width)
        {
            if (t == null || string.IsNullOrEmpty(t.text)) return 0f;
            return Mathf.Ceil(t.GetPreferredValues(t.text, width, 0f).y);
        }

        public static void Destroy(Component c) { if (c != null) Object.Destroy(c.gameObject); }
    }
}
