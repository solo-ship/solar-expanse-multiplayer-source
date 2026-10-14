using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // Board 12, right: the Multiplayer saves folder's list (DD24), opened from the Load Game dialog's "Multiplayer
    // saves" button. Each row: the session's title, its game date, its agencies and who played them, when it was last
    // played, and Delete (with a confirmation). Picking a row offers its agencies to play alone (the solo picker); Load
    // plays it solo through the session (ISessionActions.PlaySolo). Behind MpSaves and SoloPlay, off in the early
    // track: built, never shown there.
    public sealed class MpSavesScreen : MenuDialogScreen
    {
        const float W = 1060f, H = 730f, RowX = 24f, RowW = 1012f, RowH = 50f, RowGap = 8f, RowY = 40f, ListH = 300f;
        const float DateX = 290f, AgenciesX = 430f, AgeX = 800f;

        MenuControls m;
        IList<MpSaveRow> saves;
        int picked = -1;
        string agency;
        readonly List<Toggle> rowToggles = new List<Toggle>();
        readonly List<Toggle> tiles = new List<Toggle>();
        readonly List<string> tileIds = new List<string>();
        RectTransform soloPane, tilesBox;
        TMP_Text playAs;
        Button load;
        ModDialog confirm;

        public MpSavesScreen(MenuScreens owner) : base(owner) { }

        protected override MenuScreen Kind { get { return MenuScreen.MpSaves; } }

        public override bool Build()
        {
            m = Owner.Controls;
            saves = Owner.MpSaves;
            MenuTestAdapter test = Owner.Test;
            if (test != null && saves.Count > 0) { picked = test.Shot.Pick; agency = test.Shot.Agency; }
            if (!Frame("menu.saves.title", W, H, true)) return false;
            if (saves.Count == 0)
                Kit.Label(Body, TextRole.Body, "menu.saves.empty", RowX, RowY + 20f, RowW, 60f, true, true);
            else if (!Headings() || !Rows()) return false;

            soloPane = MenuKit.Area(Body, "solo", RowX, 370f, RowW, 230f);
            playAs = Kit.Label(soloPane, TextRole.Title, null, 0f, 0f, RowW, 26f);
            tilesBox = MenuKit.Area(soloPane, "tiles", 0f, 34f, RowW, 130f);
            Kit.Label(soloPane, TextRole.Hint, "menu.saves.soloHint", 0f, 172f, RowW, 50f, true, true);
            StatusLine(RowX, 606f, RowW, 26f);
            Button back = Kit.Button(Body, "saves.back", "menu.back", W / 2f - 178f, 630f, 170f, 40f, GoBack);
            load = Kit.Button(Body, "saves.load", "menu.saves.load", W / 2f + 8f, 630f, 170f, 40f, Load);
            if (playAs == null || back == null || load == null) return false;
            Render();
            if (test != null && test.Shot.Confirm && picked >= 0) AskDelete(picked);
            return true;
        }

        bool Headings()
        {
            Kit.Label(Body, TextRole.Small, "menu.saves.session", RowX + 14f, 10f, 260f, 24f);
            Kit.Label(Body, TextRole.Small, "menu.saves.date", RowX + DateX, 10f, 130f, 24f);
            Kit.Label(Body, TextRole.Small, "menu.saves.agencies", RowX + AgenciesX, 10f, 360f, 24f);
            Kit.Label(Body, TextRole.Small, "menu.saves.lastPlayed", RowX + AgeX, 10f, 140f, 24f);
            return true;
        }

        bool Rows()
        {
            TimeSpan offset = TimeSpan.Zero;
            try { offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow); } catch (Exception) { }
            // the dialog's own Scroll View, under the headings: the rows scroll as the game's own list does
            RectTransform list = DialogFrame.PlaceList(Dialog.Parts, RowX, DialogFrame.Header(Dialog.Parts) + RowY,
                                                       RowW, ListH);
            if (list == null) return false;
            float y = 0f;
            for (int i = 0; i < saves.Count; i++)
            {
                int index = i;
                MpSaveRow s = saves[i];
                RowParts r = Kit.ListRow(list, "saves.row." + i, m.DeleteSaves,
                    () => { Pick(index); }, () => AskDelete(index));
                if (r == null) return false;
                DialogFrame.Item(r.Root, y, RowH, 0f);
                y += RowH + RowGap;
                // the session's title in the body text's white capitals (board 12), the row's own name left empty:
                // the game's list draws it in its dim row grey
                if (r.Name != null) r.Name.text = "";
                TMP_Text title = Column(r.Root, TextSanitiser.Clean(s.Title, 64, ""), 14f, DateX - 24f);
                if (title != null) title.fontStyle |= FontStyles.UpperCase;
                Column(r.Root, MpSaveText.Date(s), DateX, 130f);
                Column(r.Root, MpSaveText.Agencies(s, Owner.AgencyName), AgenciesX, 360f);
                Column(r.Root, MpSaveText.Age(s.LastPlayedUtcTicks, DateTime.UtcNow.Ticks, offset), AgeX, 140f);
                rowToggles.Add(r.Toggle);
            }
            DialogFrame.FitList(list, y);
            return true;
        }

        // A detail column drawn over the row, in the row's own text.
        TMP_Text Column(RectTransform row, string text, float x, float w)
        {
            TMP_Text t = Kit.Parts.Text(row, TextRole.Body);
            if (t == null) return null;
            MenuKit.At(t.rectTransform, x, 0f, w, RowH);
            MenuKit.Style(t, true, false);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.richText = false;
            t.parseCtrlCharacters = false;
            UiFonts.SetPeerText(t, text);   // the index's words come from another player's copy
            return t;
        }

        void Pick(int index)
        {
            if (picked != index) agency = null;
            picked = index;
            Render();
        }

        // The solo picker for the picked save: its agencies in the game, as the new-game page's tiles.
        void Tiles()
        {
            for (int i = tilesBox.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(tilesBox.GetChild(i).gameObject);
            tiles.Clear();
            tileIds.Clear();
            if (picked < 0 || picked >= saves.Count) return;
            float x = 0f;
            foreach (string wire in MpSaveText.SoloOffer(saves[picked]))
            {
                string id = wire;
                Toggle t = Kit.AgencyTile(tilesBox, "saves.agency." + id, id, Owner.AgencyName(id), x, 0f, 96f,
                    () => { agency = id; Render(); });
                if (t == null) continue;
                tiles.Add(t);
                tileIds.Add(id);
                x += 116f;
            }
        }

        int tilesFor = -2;

        void Render()
        {
            Kit.Select(rowToggles, picked >= 0 && picked < rowToggles.Count ? rowToggles[picked] : null);
            bool solo = m.SoloPicker && picked >= 0 && picked < saves.Count;
            MenuKit.Show(soloPane, solo);
            if (solo)
            {
                if (tilesFor != picked) { Tiles(); tilesFor = picked; }
                UiFonts.SetPeerText(playAs, UiText.Get("menu.saves.playAs", TextSanitiser.Clean(saves[picked].Title,
                    64, "")));
                Kit.Select(tiles, agency != null && tileIds.Contains(agency) ? tiles[tileIds.IndexOf(agency)] : null);
            }
            string blocked = !solo ? "menu.saves.pickSave" : agency == null ? "menu.saves.pickAgency" : null;
            MenuKit.Enable(load, blocked == null);
            Hint(saves.Count == 0 ? null : blocked);
        }

        void Load()
        {
            if (picked < 0 || picked >= saves.Count || agency == null) return;
            string why = Owner.Actions.PlaySolo(saves[picked].Name, agency);
            if (why != null) { Refused(why); return; }
            Log.Info("menu: playing a multiplayer save alone");
            Owner.Close();
        }

        // ---- delete ----

        void AskDelete(int index)
        {
            if (!m.DeleteSaves || index < 0 || index >= saves.Count || confirm != null) return;
            MpSaveRow s = saves[index];
            confirm = Kit.Dialog(Owner.Canvas, "menu.saves.deleteTitle", 612f, 300f, () => confirm = null);
            if (confirm == null) return;
            if (Dialog != null) Dialog.Own(confirm);   // the question goes with the list
            RectTransform body = MenuKit.Content(confirm);
            TMP_Text line = Kit.Label(body, TextRole.Body, null, 26f, 22f, 560f, 90f, true, true);
            if (line == null) return;
            line.alignment = TextAlignmentOptions.Top;
            // the question wraps (three lines at most): the window is as tall as it, the buttons under it
            UiFonts.SetPeerText(line, UiText.Get("menu.saves.deleteAsk", TextSanitiser.Clean(s.Title, 64, "")),
                                3f * 560f - 60f);
            float h = DialogFrame.TextHeight(line, 560f);
            MenuKit.At(line.rectTransform, 26f, 22f, 560f, h);
            float y = 22f + h + 24f;
            Kit.Button(body, "saves.delete.cancel", "menu.back", 110f, y, 190f, 40f, () => confirm.Close());
            Kit.Button(body, "saves.delete.ok", "menu.saves.delete", 312f, y, 190f, 40f, () => Delete(s.Name));
            DialogFrame.Height(confirm, DialogFrame.Header(confirm.Parts) + y + 40f + 24f);
        }

        void Delete(string name)
        {
            if (confirm != null) confirm.Close();
            var store = Owner.Actions as IMpSaves;
            string why = store != null ? store.DeleteMpSave(name) : M4SessionAdapter.NotYet;
            if (why != null) { Refused(why); return; }
            Log.Info("menu: a multiplayer save was deleted");
            Owner.Open(MenuScreen.MpSaves);   // the list again, from the folder as it is now
        }
    }
}
