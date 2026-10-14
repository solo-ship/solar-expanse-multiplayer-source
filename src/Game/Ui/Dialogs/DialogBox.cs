using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // One session dialog on screen: the kit's ModDialog (the game's load-dialog frame over its own shadow, modal, Esc
    // closes it), its title, its body as text rows in the frame's own list (the report's lines among them, so a long
    // report scrolls as the game's save list does), and its two buttons, the frame's own OK and Cancel. Every line is
    // a table line; every line that may hold another player's text goes through PeerText (PeerLines).
    public sealed class DialogBox
    {
        const int MaxLinesPerParagraph = 6;
        const float W = 640f, WideW = 980f, Pad = 26f, MaxBody = 560f, TextInset = 4f, GapHeight = 10f, LineGap = 8f,
            ViewportSlack = 8f;

        public DialogSpec Spec { get; private set; }
        public bool State { get; private set; }      // a dialog the view called for (not one a click asked for)
        public bool Replaced;                       // closed by the screens for a newer cause, not by the player
        public int PartCount { get { return buttons + rows.Count; } }

        GameParts parts;
        ModDialog dialog;
        Action<DialogBox, DialogAction> act;
        Action<DialogBox> closed;
        int buttons;
        readonly List<GameObject> rows = new List<GameObject>();

        public static DialogBox Open(GameParts parts, Canvas canvas, DialogSpec spec, bool state,
                                     Action<DialogBox, DialogAction> act, Action<DialogBox> closed)
        {
            var box = new DialogBox { Spec = spec, State = state, parts = parts, act = act, closed = closed };
            box.dialog = ModDialog.Open(parts, canvas, spec.TitleKey, () => box.OnClosed());
            if (box.dialog == null)
            {
                Log.Warn("ui: dialog " + spec.Kind + " not shown: a part was refused");
                return null;
            }
            try
            {
                box.Title(spec);
                box.Buttons(spec);
                box.Body(spec);
            }
            catch (Exception e)
            {
                Log.Error("ui: dialog " + spec.Kind + " could not be laid out: " + e.Message);
                box.Replaced = true;
                box.dialog.Close();
                return null;
            }
            return box;
        }

        public void Close() { if (dialog != null) dialog.Close(); }

        void OnClosed()
        {
            dialog = null;
            if (closed != null) closed(this);
        }

        // Same cause, new words (a reload's progress): the title and the body again; the buttons stay.
        public void Refresh(DialogSpec spec)
        {
            if (dialog == null) return;
            Spec = spec;
            Title(spec);
            Body(spec);
        }

        void Title(DialogSpec spec)
        {
            TMP_Text t = dialog.Parts.Title;
            if (t == null) return;
            if (spec.TitlePeer) UiFonts.SetPeerText(t, spec.Title());
            else { t.richText = false; t.parseCtrlCharacters = false; t.text = spec.Title(); }
        }

        // OK and Cancel of the game's frame: the first and second buttons; one with no key is hidden. Cancel's own
        // close (ModDialog's) is replaced by the button's action.
        void Buttons(DialogSpec spec)
        {
            WindowParts w = dialog.Parts;
            Wire(w.Ok, spec.OkKey, spec.OkAction);
            if (w.Cancel != null) w.Cancel.onClick.RemoveAllListeners();
            Wire(w.Cancel, spec.OtherKey, spec.OtherAction);
            buttons = (spec.OkKey != null ? 1 : 0) + (spec.OtherKey != null ? 1 : 0);
        }

        void Wire(Button b, string key, DialogAction what)
        {
            if (b == null) return;
            if (key == null) { b.gameObject.SetActive(false); return; }
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                t.richText = false;
                t.parseCtrlCharacters = false;
                t.text = UiText.Get(key);
            }
            string id = b == dialog.Parts.Ok ? "dialog.ok" : "dialog.other";
            b.onClick.AddListener(() =>
            {
                Log.Info("ui: click " + id);
                if (act != null) act(this, what);
            });
        }

        // The window laid out for its words (DialogFrame): as wide as the kind needs, its body in the frame's own
        // list under the title bar (so a long report scrolls as the game's save list does), the buttons centred under
        // it, the window as tall as that up to MaxBody. Each line is a paragraph in the game's body text (a small
        // line in its hint text), wrapped by PeerWrap; the report's lines follow the first paragraph. A question
        // (and every short dialog, all but a mismatch report) is centred, as the game's own yes-or-no pop-up is.
        void Body(DialogSpec spec)
        {
            WindowParts w = dialog.Parts;
            if (w.List == null) throw new InvalidOperationException("the frame has no list");
            foreach (GameObject g in rows) if (g != null) Object.DestroyImmediate(g);
            rows.Clear();
            bool wide = spec.Kind == DialogKind.Mismatch;
            Vector2 size;
            float header = DialogFrame.Size(dialog, wide ? WideW : W, 1000f, out size);
            float bodyW = size.x - 2f * Pad, top = header + Pad - 6f;
            RectTransform list = DialogFrame.PlaceList(w, Pad, top, bodyW, MaxBody);
            Canvas.ForceUpdateCanvases();
            float width = DialogFrame.ListWidth(list) - 2f * TextInset;
            bool centred = spec.Kind != DialogKind.Mismatch;
            float y = 0f;
            bool first = true;
            foreach (DialogLine l in spec.Lines)
            {
                if (l.Gap) { y += GapHeight; continue; }
                y += Paragraph(list, l.Text(), l.Small ? TextRole.Hint : TextRole.Body, width, y, centred, l.Warning);
                if (first && spec.Report != null)
                {
                    y += GapHeight;
                    foreach (string r in spec.Report) y += Paragraph(list, r, TextRole.Body, width, y, false);
                    y += GapHeight;
                }
                first = false;
            }
            DialogFrame.FitList(list, y);
            float bodyH = Mathf.Min(Mathf.Max(y, 20f), MaxBody);
            // the viewport is a few units shorter than its scroll view: the area holds the whole body, so the
            // scrollbar shows only for a report longer than MaxBody
            RectTransform area = DialogFrame.ListArea(w);
            if (area != null) area.sizeDelta = new Vector2(area.sizeDelta.x, bodyH + ViewportSlack);
            float buttonsY = top + bodyH + ViewportSlack + Pad - 6f;
            bool anyButton = (w.Ok != null && w.Ok.gameObject.activeSelf) ||
                             (w.Cancel != null && w.Cancel.gameObject.activeSelf);
            DialogFrame.ButtonRow(size, buttonsY, w.Cancel, w.Ok);
            DialogFrame.Height(dialog, anyButton ? buttonsY + DialogFrame.ButtonH + Pad : buttonsY);
        }

        float Paragraph(RectTransform list, string text, TextRole role, float width, float y, bool centred,
                        bool warning = false)
        {
            TMP_Text t = parts.Text(list, role);
            if (t == null) throw new InvalidOperationException("the text part was refused");
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = centred ? TextAlignmentOptions.Top : TextAlignmentOptions.TopLeft;
            int n = PeerLines.Set(t, text, width, MaxLinesPerParagraph);
            if (warning) t.color = UiColours.Warning;
            float h = n > 0 ? Mathf.Ceil(t.GetPreferredValues(t.text, width, 0f).y) : 0f;
            rows.Add(t.gameObject);
            DialogFrame.Item(t.rectTransform, y, h, TextInset);
            return h + LineGap;
        }

        // Its buttons' screen rectangles in the widget map (test seams only).
        public void Register(UiMap map)
        {
            if (map == null || !map.Writes || dialog == null) return;
            Canvas.ForceUpdateCanvases();
            Put(map, "dialog.ok", dialog.Parts.Ok);
            Put(map, "dialog.other", dialog.Parts.Cancel);
        }

        internal static void Put(UiMap map, string id, Component c)
        {
            if (c == null || !c.gameObject.activeInHierarchy) return;
            Rect r = UiWidgets.ScreenRect((RectTransform)c.transform);
            map.Register(id, new UiRect(r.xMin, Screen.height - r.yMax, r.width, r.height));
        }
    }

    // Text that may hold another player's words, over several lines: every line through PeerText (PeerWrap), joined
    // with this mod's own line breaks, never read as markup.
    static class PeerLines
    {
        public static int Set(TMP_Text t, string text, float width, int maxLines)
        {
            t.richText = false;
            t.parseCtrlCharacters = false;
            List<string> lines = PeerWrap.Lines(text, new TmpGlyphs(t), width, maxLines);
            t.text = string.Join("\n", lines.ToArray());
            return lines.Count;
        }
    }
}
