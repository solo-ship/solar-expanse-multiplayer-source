using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // Board 3: host a game, on the game's own new-game page frame (MenuParts.Page): its "New game > ..." line, its
    // scrolling list on the left with two of its items above as the tabs, the heading with its line, Back and Next.
    // The first tab holds "New game" and the Multiplayer saves (their capability), the second "My saves" (the game's
    // own load dialog). Right of the list: what the pick means, the player's name and, by the capabilities, either the
    // early track's own agency (the host picks it before the game's pages, DD26) or the lobby's options (the full
    // track). Back goes to the Multiplayer screen; Next hosts the pick.
    public sealed class HostSetupScreen : MenuDialogScreen
    {
        const float TabH = 44f, TabGap = 6f, ColW = 560f, OptGap = 60f;

        MenuControls m;
        HostSetupState state;
        IList<MpSaveRow> saves;
        List<string> claimable = new List<string>();
        int players = HostOptions.DefaultPlayers;
        bool lateJoin = true;
        PageParts page;
        float paneX, paneY, optX;

        readonly List<Toggle> tabs = new List<Toggle>();
        readonly List<Toggle> items = new List<Toggle>();
        readonly List<Toggle> tiles = new List<Toggle>();
        readonly List<string> tileIds = new List<string>();
        RectTransform newPane, savePane, ownPane, agencyPane, optionsPane;
        TMP_Text heading, saveDate, saveAgencies, saveHost, saveAge, playersValue;
        Button lateButton;
        TMP_InputField nameField;
        Button next;

        public HostSetupScreen(MenuScreens owner) : base(owner) { }

        protected override MenuScreen Kind { get { return MenuScreen.HostSetup; } }

        public override bool Build()
        {
            m = Owner.Controls;
            saves = Owner.MpSaves;
            state = HostSetupState.Opening(m);
            if (Owner.TakeReopenOwnTab() && m.OwnSavesTab) state.SelectTab(true);
            foreach (KeyValuePair<string, string> a in Owner.GameAgencies()) claimable.Add(a.Key);
            MenuTestAdapter test = Owner.Test;
            if (test != null) test.Prepare(state);
            if (!PageFrame() || !Tabs() || !Panes() || !Footer()) return false;
            Render();
            string pending = Owner.TakePendingRefusal();
            if (pending != null) Refused(pending);
            return true;
        }

        // The page where the game draws its own, over its own blurred backdrop, modal.
        bool PageFrame()
        {
            RectTransform root = UiWidgets.Box(Owner.Canvas.transform, "host setup");
            UiWidgets.Stretch(root);
            // whatever closes the page (Back, Esc, a move on, a failed build), the menu's look comes back first
            ModPage modal = ModPage.Open(root, () => { Owner.RestoreMenu(); Closed(); });
            CloseFrame = modal.Close;
            Owner.FadeMenuOut();
            if (Kit.Parts.Blocker(root) == null || Owner.Extra.Backdrop(root) == null) return false;
            page = Owner.Extra.Page(root);
            if (page == null) return false;
            Body = page.Root;
            MenuKit.SetLine(page.Crumb, "host.setup.crumb");
            heading = page.Heading;
            heading.enableWordWrapping = false;
            Rect h = MenuParts.TopLeftRect(page.Heading.rectTransform, page.Root);
            paneX = h.x;
            paneY = h.yMax + 30f;
            optX = paneX + ColW + OptGap;
            return true;
        }

        // Two of the list's own items above the list, as the tabs; the list moves down to make room.
        bool Tabs()
        {
            Rect r = MenuParts.TopLeftRect(page.Scroll, page.Root);
            float w = r.width / 2f - TabGap / 2f;
            Toggle first = Tab("host.tab.mp", m.FirstTabKey, r.x, r.y, w, false);
            Toggle own = Tab("host.tab.own", "host.setup.tab.own", r.x + w + TabGap, r.y, w, true);
            if (first == null || own == null) return false;
            MenuKit.At(page.Scroll, r.x, r.y + TabH + TabGap, r.width, r.height - TabH - TabGap);
            MenuKit.Show(first, m.FirstTab);
            MenuKit.Show(own, m.OwnSavesTab);
            return true;
        }

        Toggle Tab(string id, string key, float x, float y, float w, bool ownTab)
        {
            Toggle t = Owner.Extra.Item(page.Root);
            if (t == null) return null;
            MenuKit.At((RectTransform)t.transform, x, y, w, TabH);
            TMP_Text name = MenuParts.ItemText(t);
            MenuKit.SetLine(name, key);
            if (name != null) UiWidgets.Stretch(name.rectTransform);   // the item's own text box is wider than a tab
            Kit.WireToggle(t, id, () => { state.SelectTab(ownTab); Render(); });
            tabs.Add(t);
            return t;
        }

        // The rows of the tab shown, in the page's own scrolling list, made again whenever the tab changes.
        void Items()
        {
            foreach (Toggle t in items) if (t != null) MenuKit.Destroy(t);
            items.Clear();
            int count = state.Count(m, saves.Count);
            for (int i = 0; i < count; i++)
            {
                int index = i;
                string id = state.OwnTab ? "host.item.own"
                    : m.NewGameItem && i == 0 ? "host.item.new" : "host.item.save." + i;
                Toggle t = Owner.Extra.Item(page.Content);   // laid out by the list, as the game's own items
                if (t == null) continue;
                TMP_Text name = MenuParts.ItemText(t);
                if (state.OwnTab) MenuKit.SetLine(name, "host.setup.ownSaves");
                else if (m.NewGameItem && i == 0) MenuKit.SetLine(name, "host.setup.newGame");
                else SaveItem(t, name, saves[i - (m.NewGameItem ? 1 : 0)]);
                Kit.WireToggle(t, id, () => { state.Item = index; Render(); });
                items.Add(t);
            }
        }

        // A save's item: its title, and under it the game date and its agencies in the kit's body text.
        void SaveItem(Toggle t, TMP_Text name, MpSaveRow s)
        {
            if (name == null) return;
            var rt = (RectTransform)t.transform;
            name.rectTransform.anchoredPosition += new Vector2(0f, 9f);
            UiFonts.SetPeerText(name, TextSanitiser.Clean(s.Title, 64, ""));
            Vector2 size = rt.sizeDelta;
            TMP_Text sub = Kit.Label(rt, TextRole.Body, null, 12f, size.y - 24f, size.x - 24f, 20f, true, false);
            if (sub == null) return;
            sub.alignment = TextAlignmentOptions.Top;
            UiFonts.SetPeerText(sub, MpSaveText.Date(s) + " - " + MpSaveText.Agencies(s, Owner.AgencyName));
        }

        bool Panes()
        {
            newPane = MenuKit.Area(Body, "new game", paneX, paneY, ColW, 180f);
            Kit.Label(newPane, TextRole.Body, m.AgencyTiles ? "host.setup.newGameEarly" : "host.setup.newGameText",
                      0f, 0f, ColW, 100f, true, true);
            Kit.Label(newPane, TextRole.Body, "host.setup.saveKeeps", 0f, 110f, ColW, 50f, true, true);

            savePane = MenuKit.Area(Body, "save", paneX, paneY, ColW, 180f);
            saveDate = Kit.Label(savePane, TextRole.Body, null, 0f, 0f, ColW, 26f, true, false);
            saveAgencies = Kit.Label(savePane, TextRole.Body, null, 0f, 30f, ColW, 50f, true, true);
            saveHost = Kit.Label(savePane, TextRole.Body, null, 0f, 84f, ColW, 26f, true, false);
            saveAge = Kit.Label(savePane, TextRole.Body, null, 0f, 114f, ColW, 26f, true, false);
            Kit.Label(savePane, TextRole.Body, "host.setup.saveClaims", 0f, 144f, ColW, 36f, true, true);

            ownPane = MenuKit.Area(Body, "own saves", paneX, paneY, ColW, 180f);
            Kit.Label(ownPane, TextRole.Body, "host.setup.ownSavesText", 0f, 0f, ColW, 100f, true, true);

            // the player's name, whichever pick
            Kit.Label(Body, TextRole.Small, "host.setup.options", optX, paneY, 400f, 26f);
            Kit.Label(Body, TextRole.Small, "host.setup.name", optX, paneY + 44f, 220f, 26f);
            nameField = Kit.Field(Body, "host.name", PlayerNames.MaxChars, false, optX + 260f, paneY + 36f, 300f, 40f);
            if (nameField == null || saveDate == null) return false;
            nameField.text = Owner.Names.Get() ?? "";

            optionsPane = MenuKit.Area(Body, "options", optX, paneY + 90f, ColW, 110f);
            if (!Options()) return false;

            agencyPane = MenuKit.Area(Body, "agency", paneX, paneY + 190f, ColW, 150f);
            if (!Tiles()) return false;
            Rect back = MenuParts.TopLeftRect((RectTransform)page.Back.transform, page.Root);
            StatusLine(paneX, back.y - 42f, ColW + OptGap + ColW, 36f);
            return true;
        }

        // The lobby's options (the full track): co-op comes with M8b, Steam's friends-only with M9; the player limit
        // is one to five.
        bool Options()
        {
            Kit.Label(optionsPane, TextRole.Small, "host.setup.players", 0f, 12f, 220f, 26f);
            Button less = Kit.Button(optionsPane, "host.players.less", "host.setup.fewer", 420f, 2f, 40f, 40f,
                () => { players = HostOptions.Step(players, -1); Render(); });
            playersValue = Kit.Label(optionsPane, TextRole.Button, null, 460f, 2f, 60f, 40f);
            Button more = Kit.Button(optionsPane, "host.players.more", "host.setup.more", 520f, 2f, 40f, 40f,
                () => { players = HostOptions.Step(players, +1); Render(); });
            Kit.Label(optionsPane, TextRole.Small, "host.setup.lateJoin", 0f, 66f, 330f, 26f);
            lateButton = Kit.Button(optionsPane, "host.late", "host.setup.on", 420f, 56f, 140f, 40f,
                () => { lateJoin = !lateJoin; Render(); });
            if (less == null || more == null || playersValue == null || lateButton == null) return false;
            playersValue.alignment = TextAlignmentOptions.Center;
            return true;
        }

        // The early track's own agency: the new-game page's tiles, every playable agency of this game.
        bool Tiles()
        {
            Kit.Label(agencyPane, TextRole.Small, "host.setup.agency", 0f, 0f, ColW, 26f);
            float x = 0f;
            foreach (KeyValuePair<string, string> a in Owner.GameAgencies())
            {
                string wire = a.Key;
                Toggle t = Kit.AgencyTile(agencyPane, "host.agency." + wire, wire, a.Value, x, 32f, 96f,
                    () => { state.Agency = wire; Render(); });
                if (t == null) return false;
                tiles.Add(t);
                tileIds.Add(wire);
                x += 116f;
            }
            return true;
        }

        // The page's own Back and Next.
        bool Footer()
        {
            MenuKit.SetLine(MenuKit.TextOf(page.Back), "menu.back");
            MenuKit.SetLine(MenuKit.TextOf(page.Next), "host.setup.next");
            Kit.Wire(page.Back, "host.back", GoBack);
            Kit.Wire(page.Next, "host.next", Next);
            next = page.Next;
            return true;
        }

        // Everything the state shows, after every pick.
        void Render()
        {
            if (TabChanged() || items.Count == 0) Items();
            Kit.Select(tabs, state.OwnTab ? tabs[1] : tabs[0]);
            Kit.Select(items, state.Item >= 0 && state.Item < items.Count ? items[state.Item] : null);
            HostPick pick = state.Pick(m, saves.Count);
            MenuKit.Show(newPane, pick == HostPick.NewGame);
            MenuKit.Show(savePane, pick == HostPick.MpSave);
            MenuKit.Show(ownPane, pick == HostPick.OwnSave);
            MenuKit.Show(agencyPane, pick == HostPick.NewGame && m.AgencyTiles);
            MenuKit.Show(optionsPane, m.OptionsShown(pick));
            Kit.Select(tiles, state.Agency != null && tileIds.Contains(state.Agency)
                ? tiles[tileIds.IndexOf(state.Agency)] : null);
            if (playersValue != null) playersValue.text = players.ToString();
            if (lateButton != null)
                MenuKit.SetLine(MenuKit.TextOf(lateButton), lateJoin ? "host.setup.on" : "host.setup.off");
            switch (pick)
            {
                case HostPick.NewGame: MenuKit.SetLine(heading, "host.setup.newGame"); break;
                case HostPick.OwnSave: MenuKit.SetLine(heading, "host.setup.tab.own"); break;
                case HostPick.MpSave: ShowSave(saves[state.MpSaveIndex(m, saves.Count)]); break;
                default: MenuKit.SetLine(heading, null); break;
            }
            string blocked = state.NextBlockedWhy(m, saves.Count, claimable);
            MenuKit.Enable(next, blocked == null);
            Hint(blocked);
        }

        bool? shownOwnTab;

        bool TabChanged()
        {
            bool changed = shownOwnTab != state.OwnTab;
            shownOwnTab = state.OwnTab;
            return changed;
        }

        void ShowSave(MpSaveRow s)
        {
            MenuKit.SetPeer(heading, s.Title);
            UiFonts.SetPeerText(saveDate, UiText.Get("host.setup.saveDate", MpSaveText.Date(s)));
            UiFonts.SetPeerText(saveAgencies, UiText.Get("host.setup.saveAgencies",
                MpSaveText.Agencies(s, Owner.AgencyName)));
            UiFonts.SetPeerText(saveHost, UiText.Get("host.setup.saveHost",
                TextSanitiser.Clean(s.HostName, PlayerNames.MaxChars, UiText.Get("name.fallback"))));
            MenuKit.SetLine(saveAge, "host.setup.saveAge",
                MpSaveText.Age(s.LastPlayedUtcTicks, DateTime.UtcNow.Ticks, LocalOffset()));
        }

        static TimeSpan LocalOffset()
        {
            try { return TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow); } catch (Exception) { return TimeSpan.Zero; }
        }

        void Next()
        {
            ISessionActions a = Owner.Actions;
            if (nameField != null) Owner.KeepName(nameField.text);
            var opts = a as IHostOptions;
            if (opts != null) opts.SetHostOptions(players, lateJoin);
            string why;
            switch (state.Pick(m, saves.Count))
            {
                case HostPick.NewGame:
                    why = m.AgencyTiles ? a.PickAgency(state.Agency) : null;
                    if (why == null) why = a.HostNewGame(new NewGameSetup { AiRivals = true });
                    if (why == null)
                    {
                        Log.Info("menu: hosting a new game; the game's pages come next");
                        Owner.Close();
                    }
                    break;
                case HostPick.MpSave:
                    why = a.HostMpSave(saves[state.MpSaveIndex(m, saves.Count)].Name);
                    if (why == null) { Log.Info("menu: hosting a multiplayer save"); Owner.Close(); }
                    break;
                case HostPick.OwnSave:
                    why = Owner.HostOwnSave();
                    break;
                default:
                    why = "host.setup.pickOne";
                    break;
            }
            if (why != null) Refused(why);
        }
    }
}
