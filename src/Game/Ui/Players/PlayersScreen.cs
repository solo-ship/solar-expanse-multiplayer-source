using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The PLAYERS screen (DD27, mock-up board 14), opened from the pause menu's PLAYERS button: a ModDialog (the
    // game's load-dialog frame) widened for the list, holding in its scrolling list the count, the players grouped by
    // agency (the agency's own logo and name over its players), each a row of the game's save list with the status,
    // the signal bars with the ping beside them and, for the host behind their capabilities, Carry on and Remove;
    // then the session's log. Back (the dialog's Cancel), Esc and the close button close it; End session (its OK) is
    // the host's, behind its capability. Bound only to the session's view, actions and notices (PlayersModel).
    public sealed class PlayersScreen
    {
        const float Margin = 16f, HeaderH = 40f, LineH = 24f, Spacing = 4f, RowH = 46f, Pad = 26f;
        const float WinW = 1400f, WinH = 820f, ButtonH = 34f;

        sealed class RowRefs
        {
            public TMP_Text Name, Status; public SignalBarsWidget Bars;
            public Color Plain;   // the cloned status text's own colour, for a status that is not a warning
        }

        // A row's button before it is placed: the pause screen's own button, scaled to the row.
        sealed class RowButtonPart
        {
            public Button Button; public RectTransform Rt; public TMP_Text Label; public float K;
        }

        readonly GameParts parts;
        readonly Canvas canvas;
        readonly AgencyInfo agencies;
        readonly Action<string, RectTransform> register;
        readonly Func<ISessionActions, bool> askEnd;   // the session dialogs' one End session question
        ISessionActions session;
        ModDialog dialog;
        ConfirmBox confirm;
        string shown;
        readonly Dictionary<int, RowRefs> rows = new Dictionary<int, RowRefs>();
        readonly List<GameObject> items = new List<GameObject>();
        List<BannedPlayer> bans = new List<BannedPlayer>();   // the host's unban list, read on open and on a change
        TMP_Text[] headers;       // the list's column headers, placed once the first row shows where its name starts
        float nameInset = -1f;
        bool fitLogged;

        public bool IsOpen { get { return dialog != null; } }
        public event Action Closed;

        public PlayersScreen(GameParts parts, Canvas canvas, AgencyInfo agencies,
                             Action<string, RectTransform> register, Func<ISessionActions, bool> askEnd)
        {
            this.parts = parts;
            this.canvas = canvas;
            this.agencies = agencies;
            this.register = register;
            this.askEnd = askEnd;
        }

        public bool Open(ISessionActions s)
        {
            Close();
            session = s;
            ReadBans();
            dialog = ModDialog.Open(parts, canvas, "hud.players.title", OnClosed);
            if (dialog == null) return false;
            try { Frame(); }
            catch (Exception e)
            {
                Log.Error("ui: the PLAYERS screen failed: " + e.Message);
                Close();
                return false;
            }
            Refresh();
            Log.Info("ui: built players (" + items.Count + " parts)");
            return true;
        }

        public void Close()
        {
            if (confirm != null && confirm.Dialog != null) confirm.Dialog.Close();
            confirm = null;
            if (dialog != null) dialog.Close();
        }

        void OnClosed()
        {
            dialog = null;
            shown = null;
            rows.Clear();
            items.Clear();
            if (confirm != null && confirm.Dialog != null) confirm.Dialog.Close();
            confirm = null;
            Action c = Closed;
            if (c != null) c();
        }

        // The window at board 14's size (the game's dialog is sized for one save name), its list across it and its two
        // buttons centred under it (DialogFrame).
        void Frame()
        {
            WindowParts w = dialog.Parts;
            Vector2 size;
            float header = DialogFrame.Size(dialog, WinW, WinH, out size);
            float listY = header + Pad / 2f, buttonsY = size.y - Pad - DialogFrame.ButtonH;
            DialogFrame.PlaceList(w, Pad, listY, size.x - 2f * Pad, buttonsY - Pad / 2f - listY);
            Label(w.Cancel, UiText.Get("hud.players.back"));
            if (w.Cancel != null) w.Cancel.onClick.AddListener(() => Log.Info("ui: click players.back"));
            if (w.Close != null) w.Close.onClick.AddListener(() => Log.Info("ui: click players.close"));
            if (w.Ok != null)
            {
                Label(w.Ok, UiText.Get("hud.players.end"));
                w.Ok.onClick.AddListener(() =>
                {
                    Log.Info("ui: click players.end");
                    AskEnd();
                });
            }
            frameSize = size;
            buttonsAt = buttonsY;
            PlaceButtons();
            Canvas.ForceUpdateCanvases();
        }

        Vector2 frameSize;
        float buttonsAt;

        // Back, and End session when the host may: centred together under the list.
        void PlaceButtons()
        {
            WindowParts w = dialog.Parts;
            DialogFrame.ButtonRow(frameSize, buttonsAt, w.Cancel, w.Ok);
        }

        // Rebuilds the list when its shape changed, else puts the new statuses and pings on the rows in place.
        public void Refresh()
        {
            if (dialog == null || session == null) return;
            PlayersModel m = PlayersModel.From(session.View, session.Capabilities, agencies.Name,
                                               session.Notices != null ? session.Notices.History() : null);
            WindowParts w = dialog.Parts;
            if (w.Title != null) UiFonts.SetPeerText(w.Title, m.Title);
            if (w.Ok != null && w.Ok.gameObject.activeSelf != m.ShowEnd)
            {
                w.Ok.gameObject.SetActive(m.ShowEnd);
                PlaceButtons();
            }
            string sig = m.Signature() + "|bans:" + (m.ShowBans ? string.Join(",", BanStrings.Names(bans).ToArray())
                                                                : "");
            if (sig != shown) { Rebuild(m); shown = sig; }
            else
                foreach (PlayersGroup g in m.Groups)
                    foreach (PlayersRow r in g.Rows)
                    {
                        RowRefs refs;
                        if (!rows.TryGetValue(r.Id, out refs)) continue;
                        SetName(refs.Name, r);
                        refs.Status.text = r.Status;
                        refs.Status.color = r.Warn ? UiColours.Warning : refs.Plain;
                        if (refs.Bars != null) Bars(refs.Bars, r);
                    }
            Register(w);
        }

        void Register(WindowParts w)
        {
            if (w.Cancel != null && w.Cancel.gameObject.activeInHierarchy)
                register("players.back", (RectTransform)w.Cancel.transform);
            if (w.Ok != null && w.Ok.gameObject.activeInHierarchy)
                register("players.end", (RectTransform)w.Ok.transform);
            if (w.Close != null && w.Close.gameObject.activeInHierarchy)
                register("players.close", (RectTransform)w.Close.transform);
        }

        void Rebuild(PlayersModel m)
        {
            WindowParts w = dialog.Parts;
            RectTransform list = w.List;
            if (list == null) return;
            foreach (GameObject g in items) if (g != null) Object.DestroyImmediate(g);
            items.Clear();
            rows.Clear();
            float width = DialogFrame.ListWidth(list);
            float y = Spacing;

            y = TextItem(list, TextRole.Body, m.CountLine, y, width);
            if (m.Untested != null) y = TextItem(list, TextRole.Hint, m.Untested, y, width, true);
            y = TextItem(list, TextRole.Hint, m.PingNote, y, width);
            y = ColumnHeaders(list, m, y);
            nameInset = -1f;
            foreach (PlayersGroup g in m.Groups)
            {
                y = GroupHeader(list, g, y, width);
                foreach (PlayersRow r in g.Rows) y = PlayerRowItem(list, r, m, y, width);
            }
            PlaceHeaders(width);
            if (m.ShowBans) y = BanList(list, y, width);
            y += Spacing * 2f;
            y = TextItem(list, TextRole.Body, m.LogTitle, y, width);
            if (m.Log.Count == 0) y = TextItem(list, TextRole.Hint, m.EmptyLog, y, width);
            foreach (string line in m.Log) y = TextItem(list, TextRole.Hint, line, y, width);
            DialogFrame.FitList(list, y + Spacing);
        }

        float TextItem(RectTransform list, TextRole role, string text, float y, float width, bool warning = false)
        {
            TMP_Text t = parts.Text(list, role);
            if (t == null) return y;
            if (warning) t.color = UiColours.Warning;
            items.Add(t.gameObject);
            t.enableWordWrapping = false;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            Item(t.rectTransform, y, LineH, Margin);
            UiFonts.SetPeerText(t, text, width - 2f * Margin);
            return y + LineH + Spacing;
        }

        // Board 14's column headers (the game's load dialog has none to clone): the dialog's small label text, one per
        // column, placed over the rows' columns once the first row is built (PlaceHeaders).
        float ColumnHeaders(RectTransform list, PlayersModel m, float y)
        {
            RectTransform box = UiWidgets.Box(list, "players columns");
            items.Add(box.gameObject);
            Item(box, y, LineH, 0f);
            headers = new TMP_Text[3];
            string[] words = { m.ColPlayer, m.ColStatus, m.ColConnection };
            for (int i = 0; i < 3; i++)
            {
                TMP_Text t = parts.Text(box, TextRole.Small);
                if (t == null) continue;
                t.enableWordWrapping = false;
                t.alignment = TextAlignmentOptions.MidlineLeft;
                t.text = words[i];
                headers[i] = t;
            }
            return y + LineH + Spacing;
        }

        // Over the name (where the first row's name text starts), the status and the connection columns.
        void PlaceHeaders(float width)
        {
            if (headers == null) return;
            float rw = width - 2f * Margin;
            float[] xs = { Margin + Mathf.Max(0f, nameInset), Margin + rw * 0.40f, Margin + rw * 0.63f };
            float[] ws = { rw * 0.38f, rw * 0.22f, rw * 0.30f };
            for (int i = 0; i < 3; i++)
                if (headers[i] != null) Cell(headers[i].rectTransform, xs[i], ws[i], LineH);
        }

        float GroupHeader(RectTransform list, PlayersGroup g, float y, float width)
        {
            RectTransform box = UiWidgets.Box(list, "players group " + g.AgencyId);
            items.Add(box.gameObject);
            Item(box, y + Spacing, HeaderH, 0f);
            float x = Margin;
            Sprite logo = agencies.Logo(g.AgencyId);
            if (logo != null)
            {
                Image img = parts.SpeedMark(box, 1);
                if (img != null)
                {
                    for (int c = img.transform.childCount - 1; c >= 0; c--)
                        Object.DestroyImmediate(img.transform.GetChild(c).gameObject);
                    img.sprite = logo;
                    img.type = Image.Type.Simple;
                    img.preserveAspect = true;
                    img.color = Color.white;
                    Cell(img.rectTransform, x, HeaderH - 6f, HeaderH - 6f);
                    x += HeaderH + 4f;
                }
            }
            TMP_Text name = parts.Text(box, TextRole.Body);
            TMP_Text count = parts.Text(box, TextRole.Hint);
            if (name != null)
            {
                name.enableWordWrapping = false;
                name.alignment = TextAlignmentOptions.MidlineLeft;
                Cell(name.rectTransform, x, width * 0.4f, HeaderH);
                UiFonts.SetPeerText(name, g.Name, width * 0.4f);
                x += Mathf.Min(width * 0.4f, name.GetPreferredValues(name.text).x) + 14f;
            }
            if (count != null)
            {
                count.enableWordWrapping = false;
                count.alignment = TextAlignmentOptions.MidlineLeft;
                Cell(count.rectTransform, x, width * 0.4f, HeaderH);
                count.text = g.Count;
            }
            return y + Spacing + HeaderH + Spacing;
        }

        float PlayerRowItem(RectTransform list, PlayersRow r, PlayersModel m, float y, float width)
        {
            RectTransform row = parts.ListRow(list);
            if (row == null) return y;
            items.Add(row.gameObject);
            RowParts rp = parts.PartsOfRow(row);
            float h = RowH;
            Item(row, y, h, Margin);
            if (rp.TrashIcon != null) rp.TrashIcon.SetActive(false);
            if (rp.Trash != null) rp.Trash.gameObject.SetActive(false);
            float rw = width - 2f * Margin;
            var refs = new RowRefs();
            if (rp.Name != null)
            {
                if (nameInset < 0f)
                {
                    var c = new Vector3[4];
                    rp.Name.rectTransform.GetWorldCorners(c);
                    nameInset = row.InverseTransformPoint(c[0]).x - row.rect.xMin + rp.Name.margin.x;
                }
                // the row's own name draws in the list's dim grey capitals; the player's name is the body text
                // (board 14), where the row's name starts
                rp.Name.gameObject.SetActive(false);
            }
            refs.Name = parts.Text(row, TextRole.Body);
            if (refs.Name != null)
            {
                refs.Name.enableWordWrapping = false;
                refs.Name.alignment = TextAlignmentOptions.MidlineLeft;
                Cell(refs.Name.rectTransform, Mathf.Max(0f, nameInset), rw * 0.38f - Mathf.Max(0f, nameInset), h);
                SetName(refs.Name, r);
            }

            refs.Status = parts.Text(row, TextRole.Body);
            if (refs.Status != null)
            {
                refs.Status.enableWordWrapping = false;
                refs.Status.alignment = TextAlignmentOptions.MidlineLeft;
                Cell(refs.Status.rectTransform, rw * 0.40f, rw * 0.22f, h);
                refs.Status.text = r.Status;
                refs.Plain = refs.Status.color;
                refs.Status.color = r.Warn ? UiColours.Warning : refs.Plain;
            }
            refs.Bars = SignalBarsWidget.Build(parts, row);
            if (refs.Bars != null)
            {
                RectTransform b = refs.Bars.Root;
                float k = Mathf.Min(1f, h * 0.8f / Mathf.Max(1f, b.sizeDelta.y));
                b.anchorMin = b.anchorMax = new Vector2(0f, 0.5f);
                b.pivot = new Vector2(0f, 0.5f);
                b.localScale = new Vector3(k, k, 1f);
                b.anchoredPosition = new Vector2(rw * 0.63f, 0f);
                Bars(refs.Bars, r);
            }
            float left = rw * 0.63f + 8f;
            if (refs.Bars != null) left += refs.Bars.Root.sizeDelta.x * refs.Bars.Root.localScale.x;
            RowButtons(row, r, m, left, rw - 8f, h);
            DialogFrame.Inert(rp);   // a row is not a choice here; its buttons still are
            rows[r.Id] = refs;
            return y + h + Spacing;
        }

        // The bars count from the ping; this PC's own row has no ping and shows them full.
        static void Bars(SignalBarsWidget w, PlayersRow r)
        {
            w.Set(r.Bars == SignalBars.Max ? 0 : r.PingMs, r.Ping);
        }

        // The host's buttons at the row's right end, between the signal bars (left) and the row's edge (right): Remove
        // rightmost with board 14's words, "Remove from session"; when those do not fit beside Carry on in the room
        // the row has, the short "Remove" (the measured widths go to the log once).
        void RowButtons(RectTransform row, PlayersRow r, PlayersModel m, float left, float right, float rowH)
        {
            RowButtonPart carry = r.CanCarryOn ? MakeButton(row, m.CarryOn, "players.carryon." + r.Id, rowH, () =>
            {
                ISessionActions s = session;
                if (s == null) return;
                if (ActionRules.MayCarryOn(s.View, s.Capabilities, r.Id)) s.CarryOnWithout(r.Id);
                else Refused("carry on without " + r.Id);
            }) : null;
            RowButtonPart remove = r.CanRemove
                ? MakeButton(row, m.Remove, "players.remove." + r.Id, rowH, () => AskRemove(r.Id)) : null;
            RowButtonPart ban = r.CanBan
                ? MakeButton(row, m.Ban, "players.ban." + r.Id, rowH, () => AskBan(r.Id)) : null;
            float room = right - left;
            float need = Width(remove) + Width(ban) + Width(carry) + (remove != null && carry != null ? 8f : 0f)
                + (ban != null ? 8f : 0f);
            if (remove != null && need > room)
            {
                float full = Width(remove);
                SetWidth(remove, m.RemoveShort);
                if (!fitLogged)
                {
                    fitLogged = true;
                    Log.Info("ui: players: '" + m.Remove + "' needs " + full.ToString("0") + " units (" +
                             need.ToString("0") + " with the other button) of the row's " + room.ToString("0") +
                             "; '" + m.RemoveShort + "' (" + Width(remove).ToString("0") + ") is used");
                }
            }
            float x = right;
            foreach (RowButtonPart p in new[] { remove, ban, carry })
            {
                if (p == null) continue;
                p.Rt.anchoredPosition = new Vector2(x, 0f);
                x -= Width(p) + 8f;
            }
        }

        RowButtonPart MakeButton(RectTransform row, string text, string id, float rowH, Action click)
        {
            Button b = parts.DialogButton(row, text);
            if (b == null) return null;
            var p = new RowButtonPart { Button = b, Rt = (RectTransform)b.transform, K = 1f };
            p.Label = b.GetComponentInChildren<TMP_Text>(true);
            p.Rt.anchorMin = p.Rt.anchorMax = new Vector2(0f, 0.5f);
            p.Rt.pivot = new Vector2(1f, 0.5f);
            p.Rt.localScale = Vector3.one;
            p.Rt.sizeDelta = new Vector2(p.Rt.sizeDelta.x, Mathf.Min(ButtonH, rowH - 8f));
            SetWidth(p, text);
            b.onClick.AddListener(() =>
            {
                Log.Info("ui: click " + id);
                click();
            });
            register(id, p.Rt);
            return p;
        }

        // The button as wide as its words plus the game button's own side room.
        static void SetWidth(RowButtonPart p, string text)
        {
            if (p.Label != null) p.Label.text = text;
            float w = p.Label != null ? p.Label.GetPreferredValues(text).x + 32f : p.Rt.sizeDelta.x;
            p.Rt.sizeDelta = new Vector2(w, p.Rt.sizeDelta.y);
        }

        // On the row, in the row's own units.
        static float Width(RowButtonPart p) { return p == null ? 0f : p.Rt.sizeDelta.x * p.K; }

        // Each of the host's actions is asked again when pressed (ActionRules), on the session of that moment: the
        // view may have changed since the button or the question was drawn.
        void AskRemove(int player)
        {
            if (session == null || dialog == null) return;
            if (!ActionRules.MayKick(session.View, session.Capabilities, player))
            {
                Refused("remove " + player);
                return;
            }
            ConfirmModel q = PlayersModel.RemoveConfirm(session.View, player, agencies.Name);
            ISessionActions s = session;
            confirm = ConfirmBox.Open(parts, canvas, q, () =>
            {
                if (ActionRules.MayKick(s.View, s.Capabilities, player)) s.Kick(player);
                else Refused("remove " + player);
            }, () => confirm = null, register);
            if (confirm != null) dialog.Own(confirm.Dialog);   // the question goes with the screen
        }

        // The host's Ban, asked first (Steam keeps the player in its lobby; the game turns them away).
        void AskBan(int player)
        {
            if (session == null || dialog == null) return;
            if (!ActionRules.MayBan(session.View, session.Capabilities, player))
            {
                Refused("ban " + player);
                return;
            }
            ConfirmModel q = PlayersModel.BanConfirm(session.View, player);
            ISessionActions s = session;
            confirm = ConfirmBox.Open(parts, canvas, q, () =>
            {
                if (!ActionRules.MayBan(s.View, s.Capabilities, player)) { Refused("ban " + player); return; }
                s.Ban(player);
                ReadBans();
                Refresh();
            }, () => confirm = null, register);
            if (confirm != null) dialog.Own(confirm.Dialog);   // the question goes with the screen
        }

        void ReadBans() { bans = BanBook.List(); }

        // The host's banned players under the list, each with Unban; the click ids number the rows (never the id).
        float BanList(RectTransform list, float y, float width)
        {
            y += Spacing * 2f;
            y = TextItem(list, TextRole.Body, UiText.Get("hud.bans.title"), y, width);
            if (bans.Count == 0) return TextItem(list, TextRole.Hint, UiText.Get("hud.bans.none"), y, width);
            List<string> names = BanStrings.Names(bans);
            float rw = width - 2f * Margin;
            for (int i = 0; i < bans.Count; i++)
            {
                RectTransform row = parts.ListRow(list);
                if (row == null) continue;
                items.Add(row.gameObject);
                RowParts rp = parts.PartsOfRow(row);
                Item(row, y, RowH, Margin);
                if (rp.TrashIcon != null) rp.TrashIcon.SetActive(false);
                if (rp.Trash != null) rp.Trash.gameObject.SetActive(false);
                if (rp.Name != null) rp.Name.gameObject.SetActive(false);
                TMP_Text name = parts.Text(row, TextRole.Body);
                if (name != null)
                {
                    name.enableWordWrapping = false;
                    name.alignment = TextAlignmentOptions.MidlineLeft;
                    Cell(name.rectTransform, Mathf.Max(0f, nameInset), rw * 0.6f, RowH);
                    UiFonts.SetPeerText(name, names[i], rw * 0.6f);
                }
                BannedPlayer who = bans[i];
                RowButtonPart unban = MakeButton(row, UiText.Get("hud.bans.unban"), "players.unban." + i, RowH,
                                                 () => Unban(who));
                if (unban != null) unban.Rt.anchoredPosition = new Vector2(rw - 8f, 0f);
                DialogFrame.Inert(rp);
                y += RowH + Spacing;
            }
            return y;
        }

        void Unban(BannedPlayer who)
        {
            if (session == null || session.View == null || !session.View.IsHost
                || !CapabilityRules.Has(session.Capabilities, Capability.Ban))
            {
                Refused("unban");
                return;
            }
            if (!BanBook.Unban(who.SteamId)) Log.Warn("ui: players: the ban could not be lifted");
            ReadBans();
            Refresh();
        }

        static void Refused(string what)
        {
            Log.Warn("ui: players: " + what + " refused: the session no longer allows it");
        }

        // The name through peer text, its "(you)" or "(the host)" kept whole: only the name itself is cut.
        static void SetName(TMP_Text t, PlayersRow r)
        {
            if (t == null) return;
            float width = NameWidth(t);
            UiFonts.SetPeerText(t, PlayersModel.NameFor(r, new TmpGlyphs(t), width), width);
        }

        public void AskRemoveFor(int player) { AskRemove(player); }

        // End session asks with the session dialogs' one question (the pause screen's exits ask the same, DD21),
        // drawn over this screen; the question's own End button checks again at its click (DialogActs).
        public void AskEnd()
        {
            if (session == null || dialog == null) return;
            if (!ActionRules.MayEndSession(session.View, session.Capabilities)) { Refused("end session"); return; }
            if (askEnd == null || !askEnd(session)) Refused("end session");
        }

        // ---- layout helpers ----

        // The name's room: the first column of the row.
        static float NameWidth(TMP_Text name)
        {
            if (name == null) return 1f;
            var row = name.rectTransform.parent as RectTransform;
            float rw = row != null ? row.rect.width : name.rectTransform.rect.width;
            return Mathf.Max(1f, rw * 0.38f - 16f);
        }

        // An item of the list, top down, across the list less inset on each side (DialogFrame.Item).
        internal static void Item(RectTransform rt, float y, float h, float inset) { DialogFrame.Item(rt, y, h, inset); }

        // A cell of a row or header: from x, w wide, h tall, centred on the row's middle.
        static void Cell(RectTransform rt, float x, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, 0f);
        }

        // A game button's label: our own text (never read as markup).
        internal static void Label(Button b, string text)
        {
            if (b == null) return;
            foreach (TMP_Text t in b.GetComponentsInChildren<TMP_Text>(true))
            {
                t.richText = false;
                t.parseCtrlCharacters = false;
                t.text = text ?? "";
            }
        }
    }
}
