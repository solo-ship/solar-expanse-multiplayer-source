using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The lobby (mock-up boards 5 and 6): the players grouped by agency in the game's own scrolling list, the
    // agencies under it, the host's join code and the game's options beside it, and the footer's buttons. A modal
    // mod dialog over the menu (the full track) or over the paused game (Track U). Everything it shows comes from
    // LobbyBoard; it is built again only when the board's shape changes, and otherwise only its words are rewritten.
    public sealed class LobbyScreen
    {
        const float Pad = 30f, RowH = 50f, GroupH = 38f, FooterH = 48f, CardH = 70f;

        readonly LobbyKit kit;
        readonly LobbyScreens owner;
        public readonly ISessionActions Actions;
        ModDialog dialog;
        WindowParts w;
        RectTransform content;
        ScrollRect scroll;
        Vector2 size;
        float header;
        string shape;
        LobbyBoard board;
        LobbyLine flash;
        float flashUntil;
        bool codeVisible;

        // what the words are written to
        TMP_Text count, untested, status;
        readonly List<RowW> rows = new List<RowW>();
        readonly List<CardW> cards = new List<CardW>();
        readonly List<TMP_Text[]> options = new List<TMP_Text[]>();
        readonly List<TMP_Text> notes = new List<TMP_Text>();
        Button anyway, show, copy, newCode;
        TMP_InputField code;
        InviteStripView invite;
        float nameW, agencyW, thirdW, connW, cardW;

        sealed class RowW
        {
            public LobbyPlayer Data;
            public TMP_Text Name, Role, Agency, Third, Ping;
            public Button AgencyButton, Kick;
            public Toggle Tick;
            public SignalBarsWidget Bars;
        }

        sealed class CardW { public TMP_Text Name, Status, Last; public Button Toggle; public float X, W; }

        LobbyScreen(LobbyKit kit, LobbyScreens owner, ISessionActions actions)
        {
            this.kit = kit; this.owner = owner; Actions = actions;
        }

        public bool Alive { get { return dialog != null; } }

        public static LobbyScreen Open(LobbyKit kit, LobbyScreens owner, ISessionActions actions)
        {
            var s = new LobbyScreen(kit, owner, actions);
            s.dialog = ModDialog.Open(kit.Parts, kit.Canvas, "lobby.titlePlain", s.Closed);
            if (s.dialog == null) return null;
            s.w = s.dialog.Parts;
            s.header = kit.Frame(s.dialog, 1640f, 940f, out s.size);
            s.scroll = s.w.List != null ? s.w.List.GetComponentInParent<ScrollRect>() : null;
            s.content = UiWidgets.Box(s.dialog.Window, "lobby content");
            UiWidgets.Stretch(s.content);
            // Cancel, Esc and the close go through AskClose (LobbyCloseRules): the host is asked before its lobby
            // closes, a joiner leaves; OK starts (the host), is Ready (a joiner), or opens the picker to join a
            // running game: each logs the id the map gives it now
            s.dialog.Asking = s.AskClose;
            if (s.w.Cancel != null)
                s.w.Cancel.onClick.AddListener(() => LobbyKit.Clicked(s.board != null && s.board.Leave.Shown
                    ? "leave" : "close", () => { }));
            if (s.w.Ok != null)
                s.w.Ok.onClick.AddListener(() => LobbyKit.Clicked(s.board != null && s.board.Start.Shown
                    ? "start" : s.board != null && s.board.JoinGame.Shown ? "joingame" : "ready", s.Primary));
            s.Refresh();
            if (s.dialog == null) return null;
            Log.Info("ui: built lobby (" + kit.Built + " parts)");
            return s;
        }

        void Closed()
        {
            dialog = null;
            kit.Unregister();
            owner.ScreenClosed(this);
        }

        public void Close() { if (dialog != null) dialog.Close(); }

        // The player closes the lobby (its button, Esc, the close): false keeps it open.
        bool AskClose(bool viaButton)
        {
            LobbyCloseAction a = LobbyCloseRules.On(Actions.View, Actions.Capabilities, viaButton);
            switch (a)
            {
                case LobbyCloseAction.Keep: return false;
                case LobbyCloseAction.AskEndLobby: ConfirmCloseLobby(); return false;
                case LobbyCloseAction.Leave:
                    Log.Info("ui: the lobby screen closed: leaving the host's lobby");
                    Actions.Leave();
                    return true;
                default: return true;
            }
        }

        // "Close your lobby?": yes ends the hosted session (everyone back to the menu, the Steam lobby left).
        public void ConfirmCloseLobby()
        {
            ModDialog q = LobbyConfirm.Open(owner.KitFor(kit), new LobbyLine("lobby.closeLobby.title"),
                                            new LobbyLine("lobby.closeLobby.body"), "lobby.closeLobby.button", () =>
                                            {
                                                Log.Info("ui: the host closes its lobby");
                                                Actions.EndSession();
                                                Close();
                                            });
            if (dialog != null) dialog.Own(q);
        }

        // ---- the board ----

        public void Refresh()
        {
            if (dialog == null) return;
            try
            {
                board = LobbyBoard.Build(Actions.View, Actions.Capabilities);
                string s = board.Shape();
                if (s != shape)
                {
                    float at = scroll != null ? scroll.verticalNormalizedPosition : 1f;
                    Build();
                    shape = s;
                    if (scroll != null) scroll.verticalNormalizedPosition = at;
                }
                Apply();
            }
            catch (Exception e) { Log.Error("ui: the lobby could not show the session: " + e); }
        }

        void Clear()
        {
            Gone(content);
            Gone(w.List);
            rows.Clear(); cards.Clear(); options.Clear(); notes.Clear();
            anyway = show = copy = newCode = null;
            code = null;
            invite = null;
            codeVisible = false;   // the new code field starts as dots, and its button must say Show
            kit.Unregister();
        }

        // Hidden at once (a list's layout must not count them), destroyed at the frame's end: a rebuild may come from
        // a click on one of them.
        static void Gone(Transform parent)
        {
            if (parent == null) return;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject g = parent.GetChild(i).gameObject;
                g.SetActive(false);
                Object.Destroy(g);
            }
        }

        void Build()
        {
            Clear();
            float W = size.x, H = size.y, top = header + 16f;
            float leftW = Mathf.Round((W - 2f * Pad) * 0.62f), rightX = Pad + leftW + 40f, rightW = W - Pad - rightX;
            float footerY = H - FooterH - 18f;
            bool anyToggle = false;
            foreach (LobbyCard c in board.Cards) anyToggle |= c.Toggle;
            float cardsY = footerY - 18f - CardH - (anyToggle ? 42f : 0f), cardsLabelY = cardsY - 32f;
            float listY = top + 60f, listH = cardsLabelY - 12f - listY;

            count = kit.Text(content, TextRole.Title, TextAlignmentOptions.MidlineLeft, false);
            LobbyKit.At(count, Pad, top, leftW * 0.45f, 28f);
            untested = kit.Text(content, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
            if (untested != null) untested.color = UiColours.Warning;
            LobbyKit.At(untested, Pad + leftW * 0.45f, top, leftW * 0.55f, 28f);
            if (untested != null) untested.gameObject.SetActive(board.Untested);

            float rowW = leftW - 24f;
            nameW = Mathf.Round(rowW * 0.30f); agencyW = Mathf.Round(rowW * 0.2f);
            thirdW = Mathf.Round(rowW * 0.18f); connW = Mathf.Round(rowW * 0.14f);
            float[] x = Columns();
            string[] heads = { "lobby.col.player", "lobby.col.agency",
                               board.ReadyColumn ? "lobby.col.readyHead" : "lobby.col.status", "lobby.col.conn" };
            float[] widths = { nameW, agencyW, thirdW, connW };
            for (int i = 0; i < heads.Length; i++)
            {
                TMP_Text h = kit.Text(content, TextRole.Small, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(h, Pad + x[i], top + 32f, widths[i], 24f);
                LobbyKit.Set(h, new LobbyLine(heads[i]));
            }
            PlaceList(Pad, listY, leftW, listH);
            BuildRows(rowW);

            TMP_Text agLabel = kit.Text(content, TextRole.Title, TextAlignmentOptions.MidlineLeft, false);
            LobbyKit.At(agLabel, Pad, cardsLabelY, leftW, 28f);
            LobbyKit.Set(agLabel, new LobbyLine("lobby.section.agencies"));
            BuildCards(Pad, cardsY, leftW);

            float y = top;
            if (board.CodeShown) y = BuildCode(rightX, y, rightW);
            else
            {
                // any member may invite friends to the lobby it is in; only the host has the codes
                invite = InviteStripView.Build(kit, content, rightX, y, rightW, null);
                y += invite.Height + 8f;
            }
            y = BuildOptions(rightX, y, rightW);
            // the right column has no scrolling: say so if it ever runs into the footer (two codes, the invite
            // strip and every option row on a saved game with a shared agency is the tallest case)
            if (y > footerY - 8f)
                Log.Warn("ui: the lobby's right column runs " + Mathf.Round(y - footerY + 8f) + " px into the footer");
            BuildFooter(W, footerY);
            LobbyKit.Describe(dialog.Window, "lobby window");
            kit.PublishMap("lobby");
        }

        float[] Columns()
        {
            float c0 = 16f, c1 = c0 + nameW + 8f, c2 = c1 + agencyW + 8f, c3 = c2 + thirdW + 8f;
            return new[] { c0, c1, c2, c3 };
        }

        // The window's own scrolling list, moved to the left column; it keeps its scrollbar and its look, and its
        // rows are stacked here (DialogFrame.PlaceList).
        void PlaceList(float x, float y, float width, float height)
        {
            if (w.List != null) DialogFrame.PlaceList(w, x, y, width, height);
        }

        // One item of the list at its height, stacked under the one before; the list grows to hold them.
        float stackY;

        RectTransform ListItem(RectTransform item, float width, float height)
        {
            if (item == null) return null;
            DialogFrame.At(item, 0f, stackY, width, height);
            stackY += height;
            DialogFrame.FitList(w.List, stackY);
            return item;
        }

        void BuildRows(float rowW)
        {
            stackY = 0f;
            if (w.List == null) return;
            float[] x = Columns();
            foreach (LobbyGroup g in board.Groups)
            {
                RectTransform head = ListItem(UiWidgets.Box(w.List, "group " + g.AgencyId), rowW, GroupH);
                TMP_Text name = kit.Text(head, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(name, 8f, 0f, nameW, GroupH);
                LobbyKit.Set(name, g.Name, nameW);
                TMP_Text n = kit.Text(head, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(n, x[1], 0f, rowW - x[1], GroupH);
                LobbyKit.Set(n, g.Count);
                foreach (LobbyPlayer p in g.Players) rows.Add(BuildRow(p, rowW, x));
            }
            for (int i = 0; i < board.EmptySlots; i++)
            {
                RowParts r = kit.Row(w.List);
                if (r == null) continue;
                ListItem(r.Root, rowW, RowH);
                TMP_Text t = kit.Text(r.Root, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(t, x[0], 0f, rowW - 32f, RowH);
                LobbyKit.Set(t, new LobbyLine("lobby.waitingSlot"));
                DialogFrame.Inert(r);
            }
        }

        RowW BuildRow(LobbyPlayer p, float rowW, float[] x)
        {
            var rw = new RowW { Data = p };
            RowParts r = kit.Row(w.List);
            if (r == null) return rw;
            ListItem(r.Root, rowW, RowH);
            if (r.Name != null) r.Name.text = "";
            // this player's own row is the list's selected row; no row is a control (DialogFrame.Inert, below)
            GameParts.SetOn(r.Toggle, p.Me);
            rw.Name = kit.Text(r.Root, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
            rw.Role = kit.Text(r.Root, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
            if (p.Role != null)
            {
                LobbyKit.At(rw.Name, x[0], 3f, nameW, 26f);
                LobbyKit.At(rw.Role, x[0], 27f, nameW, 20f);
            }
            else LobbyKit.At(rw.Name, x[0], 0f, nameW, RowH);
            if (p.AgencyButton)
            {
                rw.AgencyButton = kit.Button(r.Root, "");
                LobbyKit.At(rw.AgencyButton, x[1], 8f, agencyW, RowH - 16f);
                kit.OnClick(rw.AgencyButton, "agency", () => owner.OpenPickerOver(Actions));
            }
            else
            {
                rw.Agency = kit.Text(r.Root, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(rw.Agency, x[1], 0f, agencyW, RowH);
            }
            rw.Third = kit.Text(r.Root, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
            // the Ready column: the game's tick box before the words (the words alone where no tick box is found)
            if (board.ReadyColumn) rw.Tick = kit.TickBox(r.Root);
            float tick = rw.Tick != null ? LobbyKit.PlaceTick(rw.Tick, x[2], RowH / 2f, 22f) + 8f : 0f;
            LobbyKit.At(rw.Third, x[2] + tick, 0f, thirdW - tick, RowH);
            if (kit.InGame) rw.Bars = SignalBarsWidget.Build(kit.Parts, r.Root);
            if (rw.Bars != null) LobbyKit.At(rw.Bars.Root, x[3], (RowH - 32f) / 2f, connW + 40f, 32f);
            else
            {
                rw.Ping = kit.Text(r.Root, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(rw.Ping, x[3], 0f, connW, RowH);
            }
            if (p.Kick)
            {
                rw.Kick = kit.Button(r.Root, UiText.Get("lobby.kick"));
                LobbyKit.At(rw.Kick, rowW - 108f, 8f, 100f, RowH - 16f);
                int id = p.Id;
                kit.OnClick(rw.Kick, "kick." + id, () => ConfirmKick(id));
            }
            DialogFrame.Inert(r);
            return rw;
        }

        void BuildCards(float x, float y, float width)
        {
            int n = board.Cards.Count;
            if (n == 0) return;
            cardW = Mathf.Floor(Mathf.Min(240f, (width - (n - 1) * 10f) / n));
            for (int i = 0; i < n; i++)
            {
                LobbyCard c = board.Cards[i];
                var cw = new CardW();
                float cx = x + i * (cardW + 10f);
                RowParts r = kit.Row(content);
                if (r != null)
                {
                    LobbyKit.At(r.Root, cx, y, cardW, CardH);
                    if (r.Name != null) r.Name.text = "";
                }
                Transform into = r != null ? (Transform)r.Root : content;
                float tx = 10f;
                Toggle logo = kit.AgencyToggle(into, c.Id);
                if (logo != null)
                {
                    logo.enabled = false;
                    LobbyKit.PlainLogo(logo);
                    tx = 6f + LobbyKit.Fit(logo, 6f, 7f, 56f) + 6f;
                }
                float tw = cardW - tx - 6f;
                cw.X = tx; cw.W = tw;
                cw.Name = kit.Text(into, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(cw.Name, tx, 4f, tw, 22f);
                cw.Status = kit.Text(into, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(cw.Status, tx, 26f, tw, 20f);
                cw.Last = kit.Text(into, TextRole.Hint, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(cw.Last, tx, 46f, tw, 20f);
                if (c.Toggle)
                {
                    cw.Toggle = kit.Button(content, "");
                    LobbyKit.At(cw.Toggle, cx, y + CardH + 6f, cardW, 34f);
                    string id = c.Id;
                    kit.OnClick(cw.Toggle, "rule." + id, () => Rule(id));
                }
                DialogFrame.Inert(r);
                cards.Add(cw);
            }
        }

        // The Steam invite strip first (Invite friends, Who can join, the Steam join code), then the LAN code on one
        // row (the field shows dots until Show).
        float BuildCode(float x, float y, float width)
        {
            invite = InviteStripView.Build(kit, content, x, y, width, null);
            y += invite.Height + 8f;
            TMP_Text label = kit.Text(content, TextRole.Title, TextAlignmentOptions.MidlineLeft, false);
            LobbyKit.At(label, x, y, width, 28f);
            LobbyKit.Set(label, new LobbyLine("lobby.section.code"));
            y += 34f;
            const float bw = 104f, gap = 8f;
            float fieldW = width - 3f * (bw + gap);
            code = kit.Parts.Input(content, JoinCode.MaxTextLength, true);
            if (code != null)
            {
                kit.Built++;
                code.readOnly = true;
                LobbyKit.At(code, x, y, fieldW, 46f);
            }
            show = kit.Button(content, "");
            LobbyKit.At(show, x + fieldW + gap, y + 3f, bw, 40f);
            kit.OnClick(show, "code.show", ToggleCode);
            copy = kit.Button(content, UiText.Get("lobby.code.copy"));
            LobbyKit.At(copy, x + fieldW + gap + (bw + gap), y + 3f, bw, 40f);
            kit.OnClick(copy, "code.copy", CopyCode);
            newCode = kit.Button(content, UiText.Get("lobby.code.new"));
            LobbyKit.At(newCode, x + fieldW + gap + 2f * (bw + gap), y + 3f, bw, 40f);
            kit.OnClick(newCode, "code.new", NewCode);
            y += 54f;
            TMP_Text privacy = kit.Text(content, TextRole.Hint, TextAlignmentOptions.TopLeft, true);
            LobbyKit.At(privacy, x, y, width, 54f);
            LobbyKit.Set(privacy, new LobbyLine("lobby.code.privacy"));
            y += 58f;
            return y + 8f;
        }

        float BuildOptions(float x, float y, float width)
        {
            TMP_Text label = kit.Text(content, TextRole.Title, TextAlignmentOptions.MidlineLeft, false);
            LobbyKit.At(label, x, y, width, 28f);
            LobbyKit.Set(label, new LobbyLine(Actions.View != null && Actions.View.IsHost ? "lobby.section.options"
                                                                                          : "lobby.section.game"));
            y += 34f;
            foreach (LobbyOption o in board.Options)
            {
                TMP_Text l = kit.Text(content, TextRole.Small, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(l, x, y, width * 0.45f, 32f);
                TMP_Text v = kit.Text(content, TextRole.Body, TextAlignmentOptions.MidlineRight, false);
                LobbyKit.At(v, x + width * 0.45f, y, width * 0.55f, 32f);
                options.Add(new[] { l, v });
                y += 32f;
            }
            y += 8f;
            foreach (LobbyLine n in board.Notes)
            {
                TMP_Text t = kit.Text(content, TextRole.Hint, TextAlignmentOptions.TopLeft, true);
                LobbyKit.At(t, x, y, width, 48f);
                notes.Add(t);
                y += 52f;
            }
            return y;
        }

        void BuildFooter(float W, float y)
        {
            LobbyKit.At(w.Cancel, Pad, y, 260f, FooterH);
            if (w.Cancel != null)
                kit.Register(board.Leave.Shown ? "leave" : "close", (RectTransform)w.Cancel.transform);
            LobbyKit.At(w.Ok, W - Pad - 260f, y, 260f, FooterH);
            if (w.Ok != null)
            {
                w.Ok.gameObject.SetActive(board.Start.Shown || board.Ready.Shown || board.JoinGame.Shown);
                kit.Register(board.Start.Shown ? "start" : board.JoinGame.Shown ? "joingame" : "ready",
                             (RectTransform)w.Ok.transform);
            }
            if (board.StartAnyway.Shown)
            {
                anyway = kit.Button(content, UiText.Get(board.StartAnyway.Key));
                LobbyKit.At(anyway, W - Pad - 260f - 10f - 240f, y, 240f, FooterH);
                kit.OnClick(anyway, "startanyway", ConfirmAnyway);
            }
            float sx = Pad + 280f, sw = W - Pad - 260f - 20f - (anyway != null ? 250f : 0f) - sx;
            status = kit.Text(content, TextRole.Body, TextAlignmentOptions.Center, true);
            LobbyKit.At(status, sx, y - 4f, sw, FooterH + 8f);
        }

        // ---- the words ----

        // A card's lines stacked and centred up and down in the card, whichever of them have words.
        static void CentreCard(CardW cw)
        {
            TMP_Text[] t = { cw.Name, cw.Status, cw.Last };
            var heights = new[] { 22f, 20f, 20f };
            var has = new bool[3];
            for (int i = 0; i < 3; i++) has[i] = t[i] != null && !string.IsNullOrEmpty(t[i].text);
            float[] tops = CardLayout.Tops(CardH, heights, has);
            for (int i = 0; i < 3; i++)
                if (t[i] != null && tops[i] >= 0f) LobbyKit.At(t[i], cw.X, tops[i], cw.W, heights[i]);
        }

        void Apply()
        {
            LobbyKit.Set(w.Title, board.Title, size.x - 140f);
            LobbyKit.Set(count, board.Count);
            LobbyKit.Set(untested, new LobbyLine("lobby.untested"));
            foreach (RowW r in rows) ApplyRow(r);
            for (int i = 0; i < cards.Count && i < board.Cards.Count; i++)
            {
                LobbyCard c = board.Cards[i];
                LobbyKit.Set(cards[i].Name, c.Name, cardW);
                LobbyKit.Set(cards[i].Status, c.Status, cardW);
                LobbyKit.Set(cards[i].Last, c.Last, cardW);
                CentreCard(cards[i]);
                if (cards[i].Toggle != null) LobbyKit.SetButton(cards[i].Toggle, c.ToggleLabel, cardW);
            }
            for (int i = 0; i < options.Count && i < board.Options.Count; i++)
            {
                LobbyKit.Set(options[i][0], board.Options[i].Label);
                LobbyKit.Set(options[i][1], board.Options[i].Value);
            }
            for (int i = 0; i < notes.Count && i < board.Notes.Count; i++) LobbyKit.Set(notes[i], board.Notes[i]);
            if (code != null && code.text != board.JoinCode)
            {
                code.text = board.JoinCode ?? "";
                code.ForceLabelUpdate();
            }
            if (show != null) LobbyKit.SetButton(show, UiText.Get(codeVisible ? "lobby.code.hide" : "lobby.code.show"));
            if (newCode != null) newCode.interactable = board.NewCode;
            if (invite != null) invite.Refresh();
            LobbyKit.SetButton(w.Cancel, UiText.Get(board.Leave.Shown ? board.Leave.Key : board.Close.Key));
            LobbyButton primary = board.Start.Shown ? board.Start : board.JoinGame.Shown ? board.JoinGame : board.Ready;
            if (w.Ok != null && primary.Shown)
            {
                LobbyKit.SetButton(w.Ok, UiText.Get(primary.Key));
                w.Ok.interactable = primary.Enabled;
            }
            if (anyway != null) anyway.interactable = board.StartAnyway.Enabled;
            LobbyLine line = flash != null && Time.realtimeSinceStartup < flashUntil ? flash : board.Status;
            LobbyKit.Set(status, line);
        }

        void ApplyRow(RowW r)
        {
            LobbyPlayer p = null;
            foreach (LobbyGroup g in board.Groups)
                foreach (LobbyPlayer q in g.Players) if (q.Id == r.Data.Id) p = q;
            if (p == null) return;
            r.Data = p;
            if (r.Name != null) UiFonts.SetPeerText(r.Name, p.Name, nameW);
            LobbyKit.Set(r.Role, p.Role);
            LobbyKit.Set(r.Agency, p.Agency, agencyW);
            if (r.AgencyButton != null) LobbyKit.SetButton(r.AgencyButton, p.Agency, agencyW - 20f);
            LobbyKit.Set(r.Third, p.Third);
            if (r.Tick != null && r.Tick.isOn != p.ThirdOn) r.Tick.isOn = p.ThirdOn;
            if (r.Bars != null) r.Bars.Set(p.PingMs, p.Ping.Text());
            LobbyKit.Set(r.Ping, p.Ping);
        }

        // ---- the actions ----

        void Primary()
        {
            if (board == null) return;
            if (board.Start.Shown && board.Start.Enabled) Actions.Start(false);
            else if (board.JoinGame.Shown && board.JoinGame.Enabled) owner.OpenPickerOver(Actions);
            else if (board.Ready.Shown && board.Ready.Enabled) Actions.SetReady(!board.ReadyOn);
        }

        void Rule(string agencyId)
        {
            foreach (LobbyCard c in board.Cards)
                if (c.Id == agencyId && c.Toggle) Actions.SetAgencyRule(agencyId, c.ToggleTo);
        }

        void ToggleCode()
        {
            codeVisible = !codeVisible;
            if (code != null)
            {
                code.contentType = codeVisible ? TMP_InputField.ContentType.Standard
                                               : TMP_InputField.ContentType.Password;
                code.ForceLabelUpdate();
            }
            Apply();
        }

        void CopyCode()
        {
            if (board == null || string.IsNullOrEmpty(board.JoinCode)) return;
            GUIUtility.systemCopyBuffer = board.JoinCode;   // the code itself is never logged
            Flash(new LobbyLine("lobby.code.copied"));
        }

        void NewCode()
        {
            string key = Actions.NewCode();
            Flash(key == null ? new LobbyLine("lobby.code.newDone") : new LobbyLine(key, null, false, true));
            Refresh();
        }

        void Flash(LobbyLine line)
        {
            flash = line;
            flashUntil = Time.realtimeSinceStartup + 5f;
            Apply();
        }

        public void ConfirmAnyway()
        {
            if (board == null || !board.StartAnyway.Enabled) return;
            ModDialog q = LobbyConfirm.Open(owner.KitFor(kit), new LobbyLine("lobby.anyway.title"), board.AnywayBody,
                                            "lobby.startAnyway", () => Actions.Start(true));
            if (dialog != null) dialog.Own(q);   // the question goes with the lobby
        }

        public void ConfirmKick(int player)
        {
            if (board == null) return;
            LobbyPlayer p = null;
            foreach (LobbyGroup g in board.Groups) foreach (LobbyPlayer q in g.Players) if (q.Id == player) p = q;
            if (p == null || !p.Kick) return;
            object[] name = { p.Name };
            ModDialog ask = LobbyConfirm.Open(owner.KitFor(kit), new LobbyLine("lobby.kick.title", name, true),
                                            new LobbyLine("lobby.kick.body", name, true), "lobby.kick",
                                            () => Actions.Kick(player));
            if (dialog != null) dialog.Own(ask);   // the question goes with the lobby
        }

        // The first player the host may remove (the dev trigger's kick state).
        public int FirstKickable()
        {
            if (board != null)
                foreach (LobbyGroup g in board.Groups) foreach (LobbyPlayer p in g.Players) if (p.Kick) return p.Id;
            return -1;
        }

        public void PublishMap() { if (dialog != null) kit.PublishMap("lobby"); }
    }

    // A yes-or-no question over a screen (Start anyway, Remove a player): the game's dialog frame, its question in
    // the title, the line under it in the game's body text, centred, and its two buttons under that; the window as
    // tall as that.
    public static class LobbyConfirm
    {
        const float W = 660f, TextPad = 40f, Gap = 24f;

        public static ModDialog Open(LobbyKit kit, LobbyLine title, LobbyLine body, string okKey, Action ok)
        {
            if (kit == null) return null;
            ModDialog d = ModDialog.Open(kit.Parts, kit.Canvas, "lobby.cancel", () => kit.Unregister());
            if (d == null) return null;
            Vector2 size;
            float header = kit.Frame(d, W, 400f, out size);
            WindowParts w = d.Parts;
            LobbyKit.HideList(w);
            LobbyKit.Set(w.Title, title, size.x - 120f);
            float tw = size.x - 2f * TextPad, y = header + Gap;
            TMP_Text text = kit.Text(d.Window, TextRole.Body, TextAlignmentOptions.Top, true);
            if (text != null)
            {
                LobbyKit.Set(text, body, 3f * tw);
                float th = DialogFrame.TextHeight(text, tw);
                DialogFrame.At(text, TextPad, y, tw, th);
                y += th + Gap;
            }
            LobbyKit.SetButton(w.Cancel, UiText.Get("lobby.cancel"));
            kit.OnClick(w.Cancel, "confirm.cancel", () => { });
            if (w.Ok != null) { w.Ok.gameObject.SetActive(true); w.Ok.interactable = true; }
            LobbyKit.SetButton(w.Ok, UiText.Get(okKey));
            kit.OnClick(w.Ok, "confirm.ok", () => { d.Close(); ok(); });
            DialogFrame.ButtonRow(size, y, w.Cancel, w.Ok);
            DialogFrame.Height(d, y + DialogFrame.ButtonH + Gap);
            kit.PublishMap("confirmation");
            Log.Info("ui: built confirmation " + okKey + " (" + kit.Built + " parts)");
            return d;
        }
    }
}
