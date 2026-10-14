using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The strip under the game's speed buttons (DD10, mock-up boards 8, 9 and 15), drawn from a HudModel: the kit's
    // SpeedStrip (its frame, a column under each button with the players' chips, the lit column) with the lines below
    // the chips: the pause line, the speed play runs or resumes at, the speed lock, the status line, a joiner's
    // progress bar (a speed button's dark face with the panel's lit image over it) and the host's Carry on (the pause
    // screen's own button). Every piece is a clone from the parts kit; every line is set as peer text, because a line
    // holding a name is (DD13). Built once per world into an active container; hidden by switching the root off.
    public sealed class SpeedHud
    {
        const float Pad = 8f, LineH = 22f, BarH = 6f, ButtonH = 30f, Gap = 4f, BottomPad = 12f;

        public RectTransform Root { get { return strip != null ? strip.Root : null; } }
        public Button CarryOn { get; private set; }

        readonly GameParts parts;
        SpeedStrip strip;
        TMP_Text pauseHead, runLead, run, locked, status, hint, carryLabel, slowest;
        RectTransform bar, barFill, carryRt;
        Image runIcon;            // the lit button's own image, inline in the run line
        int runIconFor = -1;
        string chipsShown, shown;
        Action<int> carryOnClicked;
        int carryFor = -1;

        SpeedHud(GameParts parts) { this.parts = parts; }

        public static SpeedHud Build(GameParts parts, Canvas canvas, Action<int> onCarryOn)
        {
            SpeedStrip s = SpeedStrip.Build(parts, canvas);
            if (s == null) return null;
            var h = new SpeedHud(parts) { strip = s, carryOnClicked = onCarryOn };
            try
            {
                if (!h.BuildLines()) { h.Destroy(); return null; }
            }
            catch (Exception e)
            {
                Log.Error("ui: the speed strip's lines failed: " + e.Message);
                h.Destroy();
                return null;
            }
            Log.Info("ui: built hud.strip (" + s.Root.GetComponentsInChildren<Graphic>(true).Length + " parts)");
            return h;
        }

        bool BuildLines()
        {
            Transform root = strip.Root;
            pauseHead = Line(root, TextRole.Body);
            runLead = Line(root, TextRole.Body);
            run = Line(root, TextRole.Body);
            locked = Line(root, TextRole.Body);
            status = Line(root, TextRole.Body);
            hint = Line(root, TextRole.Hint);
            slowest = Line(root, TextRole.Small);
            if (pauseHead == null || runLead == null || run == null || locked == null || status == null ||
                hint == null || slowest == null) return false;
            pauseHead.enableWordWrapping = false;
            runLead.enableWordWrapping = false;
            slowest.enableWordWrapping = false;
            slowest.alignment = TextAlignmentOptions.Top;
            // as wide as its column, but never cut ("SLOWES"): it may run past the column's edges, centred on it
            slowest.overflowMode = TextOverflowModes.Overflow;
            // the game's own warning colour for "Paused:" and its teal for the slowest choice (UiColours)
            pauseHead.color = UiColours.Warning;
            slowest.color = UiColours.Accent;
            Image track = parts.SpeedButtonFace(root, 1);
            Image fill = track != null ? parts.SpeedMark(track.transform, 1) : null;
            if (fill == null) return false;
            Transform icon = fill.transform;
            for (int c = icon.childCount - 1; c >= 0; c--) Object.DestroyImmediate(icon.GetChild(c).gameObject);
            bar = track.rectTransform;
            barFill = fill.rectTransform;
            // the dialogs' own push button: at its own size its words stay readable in the strip
            CarryOn = parts.DialogButton(root, UiText.Get("hud.carryOn"));
            if (CarryOn == null) return false;
            carryRt = (RectTransform)CarryOn.transform;
            carryLabel = CarryOn.GetComponentInChildren<TMP_Text>(true);
            CarryOn.onClick.AddListener(() =>
            {
                Log.Info("ui: click hud.carryon");
                if (carryFor >= 0 && carryOnClicked != null) carryOnClicked(carryFor);
            });
            return true;
        }

        TMP_Text Line(Transform root, TextRole role)
        {
            TMP_Text t = parts.Text(root, role);
            if (t == null) return null;
            t.enableWordWrapping = true;
            // never Truncate: it drops a whole line whose height is a hair over its box (the "Paused:" lead
            // vanished); the peer-text width cap keeps every line inside the strip
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = TextAlignmentOptions.TopLeft;
            return t;
        }

        public void Show(bool on)
        {
            if (!on) shown = null;   // shown again, it is laid out again
            if (Root != null && Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on);
        }

        // Puts the model on screen. Call only while shown: a part is built only into an active container.
        public void Apply(HudModel m)
        {
            // nothing changed (the model, or the window size the kit lays out by): no text set, no layout
            string all = m.Signature() + "|" + Screen.width + "x" + Screen.height;
            if (all == shown) return;
            shown = all;
            string sig = m.ChipSignature();
            if (sig != chipsShown)
            {
                strip.ClearChips();
                for (int col = 0; col < HudModel.Columns; col++)
                    foreach (HudChip c in m.Chips[col]) strip.AddChip(col, c.Name);
                chipsShown = sig;
            }
            strip.Light(m.Lit);
            strip.Layout();
            carryFor = m.CarryOnPlayer;
            Arrange(m);
        }

        // Below the chips, top down: "Slowest" under the lit column, the pause line ("Paused:" in the game's warning
        // colour, then the reasons in the kit's own status text), the speed play runs at (with the lit button's own
        // image between its lead and whose choice it is), the lock, the status line, the progress bar, Carry on and
        // its hint. The kit lays the strip out for its chips and one line; the strip's root is then grown downwards
        // for the rest, its top edge and the columns staying put.
        void Arrange(HudModel m)
        {
            RectTransform root = strip.Root;
            float width = root.rect.width - 2f * Pad;
            float top = ChipsBottom(root);
            float y = Caption(root, m, top) + Gap;
            y = Inline(pauseHead, m.PauseHead, null, strip.Status, m.PauseRest, width, y);
            y = Inline(runLead, m.RunLead, RunIcon(m), run, m.RunWho, width, y);
            y = Put(locked, m.LockLine, width, y);
            y = Put(status, m.StatusText, width, y);
            bool showBar = m.ProgressPct >= 0;
            bar.gameObject.SetActive(showBar);
            if (showBar)
            {
                y += Gap;
                Pin(bar, Pad, y, width, BarH);
                barFill.anchorMin = new Vector2(0f, 0f);
                barFill.anchorMax = new Vector2(Mathf.Clamp01(m.ProgressPct / 100f), 1f);
                barFill.pivot = new Vector2(0f, 0.5f);
                barFill.offsetMin = Vector2.zero;
                barFill.offsetMax = Vector2.zero;
                barFill.localScale = Vector3.one;
                barFill.gameObject.SetActive(m.ProgressPct > 0);
                y += BarH + Gap;
            }
            bool carry = m.CarryOnPlayer >= 0 && m.CarryOnText != null;
            carryRt.gameObject.SetActive(carry);
            if (carry)
            {
                y += Gap;
                carryRt.anchorMin = carryRt.anchorMax = new Vector2(0f, 1f);
                carryRt.pivot = new Vector2(0f, 1f);
                carryRt.localScale = Vector3.one;
                float w = width;
                if (carryLabel != null)
                {
                    UiFonts.SetPeerText(carryLabel, m.CarryOnText, Mathf.Max(1f, width - 32f));
                    w = Mathf.Min(w, carryLabel.GetPreferredValues(carryLabel.text).x + 32f);
                }
                carryRt.sizeDelta = new Vector2(w, ButtonH);
                carryRt.anchoredPosition = new Vector2(Pad, -y);
                y += ButtonH + Gap;
            }
            y = Put(hint, carry ? m.CarryOnHint : null, width, y);
            Grow(root, top, y + BottomPad);
        }

        // "Slowest" right under the lit column, as wide as it; returns where the lines start.
        float Caption(RectTransform root, HudModel m, float y)
        {
            Transform col = m.Lit >= 0 ? root.Find(Strip.ObjectPrefix + "speed column " + m.Lit) : null;
            bool on = col != null && !string.IsNullOrEmpty(m.SlowestCaption);
            slowest.gameObject.SetActive(on);
            if (!on) return y;
            var c = new Vector3[4];
            ((RectTransform)col).GetWorldCorners(c);
            float left = root.InverseTransformPoint(c[0]).x - root.rect.xMin;
            float right = root.InverseTransformPoint(c[2]).x - root.rect.xMin;
            slowest.text = m.SlowestCaption;
            float h = Mathf.Max(12f, slowest.GetPreferredValues(slowest.text).y);
            Pin(slowest.rectTransform, left, y + 1f, Mathf.Max(1f, right - left), h);
            return y + 1f + h;
        }

        // The lit button's own image (the speed panel's mark for that button, its icon with it), rebuilt only when
        // the lit button changes; hidden when nothing runs.
        Image RunIcon(HudModel m)
        {
            if (m.Lit != runIconFor)
            {
                if (runIcon != null) Object.DestroyImmediate(runIcon.gameObject);
                runIcon = m.Lit >= 0 ? parts.SpeedMark(strip.Root, m.Lit) : null;
                runIconFor = m.Lit;
            }
            return runIcon;
        }

        // A line in up to three pieces: a lead that does not wrap, an optional image after it, then the rest, which
        // wraps (two lines at most) in the room left beside them. A null lead hides the whole line.
        float Inline(TMP_Text lead, string leadText, Image icon, TMP_Text rest, string restText, float width, float y)
        {
            bool on = !string.IsNullOrEmpty(leadText);
            lead.gameObject.SetActive(on);
            if (icon != null) icon.gameObject.SetActive(on);
            if (!on) { rest.gameObject.SetActive(false); return y; }
            lead.text = leadText;
            float x = Mathf.Min(width * 0.6f, lead.GetPreferredValues(leadText).x) + 5f;
            Pin(lead.rectTransform, Pad, y, x, LineH);
            if (icon != null)
            {
                RectTransform ir = icon.rectTransform;
                float ih = LineH * 0.75f;
                float aspect = ir.sizeDelta.y > 0f ? ir.sizeDelta.x / ir.sizeDelta.y : 2f;
                float iw = ih * Mathf.Clamp(aspect, 1f, 4f);
                ir.anchorMin = ir.anchorMax = new Vector2(0f, 1f);
                ir.pivot = new Vector2(0f, 1f);
                ir.localScale = Vector3.one;
                ir.sizeDelta = new Vector2(iw, ih);
                ir.anchoredPosition = new Vector2(Pad + x, -y - (LineH - ih) / 2f);
                x += iw + 5f;
            }
            float h = LineH;
            bool more = !string.IsNullOrEmpty(restText);
            rest.gameObject.SetActive(more);
            if (more)
            {
                float rw = Mathf.Max(1f, width - x);
                UiFonts.SetPeerText(rest, restText, Mathf.Max(1f, 2f * rw - 40f));
                h = Mathf.Min(Mathf.Max(LineH, rest.GetPreferredValues(rest.text, rw, 0f).y), 2f * LineH + 4f);
                Pin(rest.rectTransform, Pad + x, y, rw, h);
            }
            return y + h + Gap;
        }

        // The bottom of the lowest chip column, measured down from the strip's top edge.
        static float ChipsBottom(RectTransform root)
        {
            float topY = root.rect.yMax, lowest = 0f;
            var c = new Vector3[4];
            foreach (Transform ch in root)
            {
                if (!ch.name.StartsWith(Strip.ObjectPrefix + "speed column", StringComparison.Ordinal)) continue;
                ((RectTransform)ch).GetWorldCorners(c);
                float bottom = root.InverseTransformPoint(c[0]).y;
                lowest = Mathf.Max(lowest, topY - bottom);
            }
            return lowest;
        }

        // A line of text pinned to the strip's top-left, as tall as it wraps (at most two lines); null hides it.
        float Put(TMP_Text t, string line, float width, float y)
        {
            bool on = !string.IsNullOrEmpty(line);
            t.gameObject.SetActive(on);
            if (!on) return y;
            // two lines at most: the peer-text cap is twice the width, less room for the wrap's lost space
            UiFonts.SetPeerText(t, line, Mathf.Max(1f, 2f * width - 40f));
            float h = Mathf.Max(LineH, t.GetPreferredValues(t.text, width, 0f).y);
            h = Mathf.Min(h, 2f * LineH + 4f);
            Pin(t.rectTransform, Pad, y, width, h);
            return y + h + Gap;
        }

        static void Pin(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        // The kit placed the root bottom-left with its columns at fixed places: pin the columns to the top edge, then
        // set the height, keeping the top edge where it was.
        static void Grow(RectTransform root, float chipsBottom, float height)
        {
            foreach (Transform ch in root)
            {
                if (!ch.name.StartsWith(Strip.ObjectPrefix + "speed column", StringComparison.Ordinal)) continue;
                var rt = (RectTransform)ch;
                Vector3 at = rt.position;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.position = at;
            }
            float top = root.anchoredPosition.y + root.sizeDelta.y;
            height = Mathf.Max(height, chipsBottom);
            root.sizeDelta = new Vector2(root.sizeDelta.x, height);
            root.anchoredPosition = new Vector2(root.anchoredPosition.x, top - height);
        }

        // The game's own tooltip draws below the mod's canvas, so over the strip it would be hidden by it: the strip
        // goes clear (not inactive: its layout and hover test stay) while the tooltip is over it.
        public void YieldToTooltip()
        {
            if (Root == null) return;
            CanvasGroup g = Root.GetComponent<CanvasGroup>();
            if (g == null) g = Root.gameObject.AddComponent<CanvasGroup>();
            bool over = false;
            ToolTipManager tm = MonoBehaviourSingleton<ToolTipManager>.Instance;
            if (tm != null && tm.TooltipShow() && Root.gameObject.activeInHierarchy)
            {
                var tip = tm.ToolTipWindow != null ? tm.ToolTipWindow.transform as RectTransform : null;
                if (tip != null)
                {
                    Rect t = UiWidgets.ScreenRect(tip), s = UiWidgets.ScreenRect(Root);
                    over = HudPlacement.Overlaps(t.xMin, t.yMin, t.xMax, t.yMax, s.xMin, s.yMin, s.xMax, s.yMax);
                }
            }
            g.alpha = over ? 0f : 1f;
            g.blocksRaycasts = !over;
        }

        // The speed buttons' panel or the strip itself is under the pointer (DD10: the strip shows any time then).
        public bool Hovered(Vector2 mouse)
        {
            if (Root != null && Root.gameObject.activeInHierarchy && Contains(Root, mouse)) return true;
            var panel = parts.Original(GameParts.PPanel) as Component;
            return panel != null && panel.gameObject.activeInHierarchy &&
                   Contains((RectTransform)panel.transform, mouse);
        }

        static bool Contains(RectTransform rt, Vector2 screen)
        {
            Canvas c = UiWidgets.RootCanvas(rt);
            Camera cam = c == null || c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, screen, cam);
        }

        public void Destroy()
        {
            if (strip != null) strip.Destroy();
            strip = null;
        }
    }
}
