using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The session's notices as cards under the game's New Notifications tab (DD11; board 9): clones of the game's own
    // notification card, stacked below the game's own new notifications, from NoticeQueue.Visible and HiddenWarnings
    // only (NoticeCards). Routine ones fade with the queue; warnings stay until clicked (a click dismisses one), and a
    // drop notice the host may act on asks to carry on without that player instead. Never the game's
    // NotificationManager: it saves its notifications into the save, and its pause toggle can pause the game. Rebuilt
    // only when what the cards say changes. In game only.
    public sealed class NoticeStack
    {
        // Four notices and the "+N more" card, as board 9 shows them, at every resolution (the cards scale with the
        // game's canvas).
        public const int MaxCards = 5;
        const int MaxTextLines = 3;
        const float GapUnits = 4f, TextLeft = 50f, TextRight = 14f, TextPadY = 9f, EmblemSize = 30f, BarH = 5f;

        readonly DialogScreens screens;
        RectTransform box;
        string signature;
        NoticeQueue queue;
        int cardCount;
        AgencyInfo agencies;   // this world's agencies' emblems, read once per world

        public NoticeStack(DialogScreens screens) { this.screens = screens; }

        public bool Shown { get { return cardCount > 0; } }

        // A new scene: the cards went with the old one.
        public void Reset()
        {
            box = null;
            signature = null;
            cardCount = 0;
            agencies = null;
        }

        public void Clear()
        {
            if (cardCount == 0 && signature == "") return;
            if (box != null) Empty();
            signature = "";
            cardCount = 0;
            Unmap();
        }

        public void Update(SessionView v, Capability caps, NoticeQueue q)
        {
            if (q == null) { Clear(); return; }
            queue = q;
            List<NoticeCard> plan = NoticeCards.Plan(q.Visible(NetClock.NowMs), q.HiddenWarnings, MaxCards, v, caps);
            string sig = NoticeCards.Signature(plan);
            if (sig == signature && box != null) return;
            signature = sig;
            Build(plan);
        }

        void Build(List<NoticeCard> plan)
        {
            GameParts parts = screens.Parts;
            Canvas canvas = screens.NoticeCanvas;   // the HUD layer: under every screen and dialog
            if (parts == null || canvas == null) return;
            if (box == null)
            {
                box = UiWidgets.Box(canvas.transform, "notices");
                UiWidgets.Stretch(box);
                box.SetAsFirstSibling();   // under the speed strip on the same layer
            }
            Empty();
            Unmap();
            cardCount = 0;
            if (plan.Count > 0)
            {
                var list = parts.Original(GameParts.PNoticeList) as RectTransform;
                Canvas.ForceUpdateCanvases();
                float k = Scale(canvas);
                float centreX, top;
                Anchor(list, out centreX, out top);
                for (int i = 0; i < plan.Count; i++)
                {
                    RectTransform card = Card(parts, plan[i], i);
                    if (card == null) break;
                    Vector2 size = card.rect.size;
                    top -= size.y * k;
                    UiWidgets.PlaceAt(card, new Vector2(centreX - size.x * k / 2f, top), size);
                    top -= GapUnits * k;
                    cardCount++;
                }
                Canvas.ForceUpdateCanvases();
                Map(plan);
                Log.Info("ui: built notices (" + cardCount + " cards)");
            }
        }

        // One card: the game's card with the notice's line (peer text through PeerText, wrapped to the card's width
        // beside the emblem), as tall as its lines; the named player's agency emblem where the game shows its own
        // notification's picture (none when the player is not known), and a joining player's download as a bar under
        // the line (a speed button's dark face with the panel's lit image over it). Its click dismisses it or asks to
        // carry on. The card's own layout group sized its text for the empty card, so the card is laid out here.
        RectTransform Card(GameParts parts, NoticeCard c, int index)
        {
            RectTransform card = parts.Notice(box);
            if (card == null) { Log.Warn("ui: notice card refused"); return null; }
            foreach (LayoutGroup g in card.GetComponents<LayoutGroup>()) g.enabled = false;
            NoticeParts np = parts.PartsOfNotice(card);
            float w = card.rect.width, textW = w - TextLeft - TextRight, h = card.rect.height;
            bool bar = c.ProgressPct >= 0;
            if (np.Text != null)
            {
                PeerLines.Set(np.Text, NoticeCards.Text(c), textW, MaxTextLines);
                np.Text.enableWordWrapping = false;
                np.Text.alignment = TextAlignmentOptions.MidlineLeft;
                if (c.Warning) np.Text.color = UiColours.Warning;   // the game's own warning colour
                float need = Mathf.Ceil(np.Text.GetPreferredValues(np.Text.text, textW, 0f).y);
                h = Mathf.Max(h, need + 2f * TextPadY + (bar ? BarH + 6f : 0f));
                card.sizeDelta = new Vector2(card.sizeDelta.x, h);
                Pin(np.Text.rectTransform, TextLeft, TextPadY, textW, need);
            }
            Emblem(np.Image, c, h);
            if (bar) Bar(parts, card, c.ProgressPct, h, textW);
            card.name = Strip.ObjectPrefix + (c.More > 0 ? "notice more" : "notice " + index);
            if (np.Button != null) np.Button.onClick.AddListener(() => Clicked(c, Id(c, index)));
            return card;
        }

        // The card's picture: the agency's own logo, as the game's notifications show a company's.
        void Emblem(Image image, NoticeCard c, float cardH)
        {
            if (image == null) return;
            if (agencies == null) agencies = new AgencyInfo();
            Sprite logo = c.Agency != null ? agencies.Logo(c.Agency) : null;
            image.gameObject.SetActive(logo != null);
            if (logo == null) return;
            image.sprite = logo;
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            image.color = Color.white;
            RectTransform rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(EmblemSize, EmblemSize);
            rt.anchoredPosition = new Vector2((TextLeft - EmblemSize) / 2f, -cardH / 2f);
        }

        static void Bar(GameParts parts, RectTransform card, int pct, float cardH, float width)
        {
            Image track = parts.SpeedButtonFace(card, 1);
            Image fill = track != null ? parts.SpeedMark(track.transform, 1) : null;
            if (fill == null) return;
            for (int i = fill.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(fill.transform.GetChild(i).gameObject);
            Pin(track.rectTransform, TextLeft, cardH - TextPadY - BarH, width, BarH);
            RectTransform f = fill.rectTransform;
            f.anchorMin = Vector2.zero;
            f.anchorMax = new Vector2(Mathf.Clamp01(pct / 100f), 1f);
            f.offsetMin = f.offsetMax = Vector2.zero;
            f.localScale = Vector3.one;
            fill.gameObject.SetActive(pct > 0);
        }

        // At (x, y) from the card's top-left corner, w by h.
        static void Pin(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        void Clicked(NoticeCard c, string id)
        {
            Log.Info("ui: click " + id);
            if (c.CarryOnPlayer >= 0) { screens.AskCarryOn(c.CarryOnPlayer); return; }
            if (c.Id >= 0 && queue != null)
            {
                queue.Dismiss(c.Id);
                signature = null;   // rebuilt on the next tick
            }
        }

        static string Id(NoticeCard c, int index) { return c.More > 0 ? "notice.more" : "notice.card." + index; }

        // Where the column starts: under the game's own new notifications (the lowest one shown), or at the top of
        // the game's notification list when it shows none; centred on that list.
        static void Anchor(RectTransform list, out float centreX, out float top)
        {
            Rect a = list != null ? UiWidgets.ScreenRect(list) : new Rect(Screen.width / 2f, Screen.height, 0f, 0f);
            centreX = a.center.x;
            top = a.yMax;
            if (list == null) return;
            bool any = false;
            float lowest = top;
            foreach (Transform t in list)
            {
                if (t == null || !t.gameObject.activeInHierarchy || Strip.IsMod(t)) continue;
                Rect r = UiWidgets.ScreenRect((RectTransform)t);
                lowest = any ? Mathf.Min(lowest, r.yMin) : r.yMin;
                any = true;
            }
            if (any) top = lowest;
        }

        static float Scale(Canvas canvas)
        {
            Canvas root = canvas.rootCanvas;
            return root != null && root.scaleFactor > 0f ? root.scaleFactor : 1f;
        }

        void Map(List<NoticeCard> plan)
        {
            UiMap map = screens.Map;
            if (map == null || !map.Writes || box == null) return;
            int i = 0;
            foreach (Transform t in box)
            {
                if (i >= plan.Count) break;
                DialogBox.Put(map, Id(plan[i], i), t);
                i++;
            }
            screens.MapChanged();
        }

        void Unmap()
        {
            if (screens.Map != null && screens.Map.RemovePrefix("notice.") > 0) screens.MapChanged();
        }

        void Empty()
        {
            if (box == null) return;
            for (int i = box.childCount - 1; i >= 0; i--) Object.DestroyImmediate(box.GetChild(i).gameObject);
        }
    }
}
