using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The agency picker (mock-up board 5, "Pick your agency"): a card for every agency of the view, each the new-game
    // page's own agency toggle (its logo) with its name and state under it, and the line that says why a pick is not
    // possible or what the host answered. A pick claims through ISessionActions.PickAgency; an agency the host refused
    // in this join is never offered again (PickerMemory). In the game scene, which has no agency toggles, each card is
    // the load list's row instead.
    public sealed class AgencyPicker
    {
        const float CardWidth = 132f, Gap = 14f, Logo = 104f;

        readonly LobbyKit kit;
        readonly LobbyScreens owner;
        readonly PickerMemory memory;
        public readonly ISessionActions Actions;
        Action<string> picked;
        Action cancelled;
        ModDialog dialog;
        WindowParts w;
        RectTransform content;
        Vector2 size;
        float header;
        string shape;
        PickerBoard board;
        LobbyLine flash;
        bool settling, done;
        readonly List<CardW> cards = new List<CardW>();
        TMP_Text message;

        sealed class CardW { public string Id; public Toggle Toggle; public TMP_Text Name, Status, Last; }

        AgencyPicker(LobbyKit kit, LobbyScreens owner, ISessionActions actions, PickerMemory memory)
        {
            this.kit = kit; this.owner = owner; Actions = actions; this.memory = memory;
        }

        public bool Alive { get { return dialog != null; } }

        public static AgencyPicker Open(LobbyKit kit, LobbyScreens owner, ISessionActions actions, PickerMemory memory,
                                        Action<string> picked, Action cancelled)
        {
            if (!CapabilityRules.Has(actions.Capabilities, Capability.PickAgency))
            {
                Log.Warn("ui: picker not shown: this session cannot pick an agency");
                return null;
            }
            var p = new AgencyPicker(kit, owner, actions, memory) { picked = picked, cancelled = cancelled };
            p.dialog = ModDialog.Open(kit.Parts, kit.Canvas, "lobby.pick.title", p.Closed);
            if (p.dialog == null) return null;
            p.w = p.dialog.Parts;
            int n = Math.Max(4, actions.View != null ? actions.View.Agencies.Count : 0);
            p.header = kit.Frame(p.dialog, n * (CardWidth + Gap) - Gap + 80f, 520f, out p.size);
            LobbyKit.HideList(p.w);
            p.content = UiWidgets.Box(p.dialog.Window, "picker content");
            UiWidgets.Stretch(p.content);
            if (p.w.Ok != null) p.w.Ok.gameObject.SetActive(false);
            if (p.w.Cancel != null)
            {
                LobbyKit.At(p.w.Cancel, p.size.x / 2f - 120f, p.size.y - 48f - 22f, 240f, 48f);
                LobbyKit.SetButton(p.w.Cancel, UiText.Get("lobby.cancel"));
            }
            p.Refresh();
            Log.Info("ui: built picker (" + kit.Built + " parts)");
            return p;
        }

        void Closed()
        {
            dialog = null;
            kit.Unregister();
            owner.ScreenClosed(this);
            if (!done && cancelled != null)
            {
                try { cancelled(); }
                catch (Exception e) { Log.Error("ui: the picker's cancel handler failed: " + e); }
            }
        }

        public void Close() { if (dialog != null) dialog.Close(); }

        // The same picker answers a later caller too (it is opened again after a refusal).
        public void Rebind(Action<string> picked, Action cancelled)
        {
            if (picked != null) this.picked = picked;
            if (cancelled != null) this.cancelled = cancelled;
        }

        public void Refresh()
        {
            if (dialog == null) return;
            try
            {
                board = PickerBoard.Build(Actions.View, memory.Refused);
                string s = board.Shape();
                if (s != shape) { Build(); shape = s; }
                Apply();
            }
            catch (Exception e) { Log.Error("ui: the picker could not show the agencies: " + e); }
        }

        void Build()
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                GameObject g = content.GetChild(i).gameObject;
                g.SetActive(false);
                Object.Destroy(g);
            }
            cards.Clear();
            kit.Unregister();
            if (w.Cancel != null)
            {
                kit.Register("picker.cancel", (RectTransform)w.Cancel.transform);
                w.Cancel.onClick.RemoveListener(CancelClicked);
                w.Cancel.onClick.AddListener(CancelClicked);
            }
            int n = board.Cards.Count;
            float total = n * (CardWidth + Gap) - Gap, x0 = Mathf.Max(20f, (size.x - total) / 2f), y = header + 24f;
            for (int i = 0; i < n; i++)
            {
                PickerCard c = board.Cards[i];
                var cw = new CardW { Id = c.Id };
                float x = x0 + i * (CardWidth + Gap);
                cw.Toggle = kit.AgencyToggle(content, c.Id);
                if (cw.Toggle != null) LobbyKit.Fit(cw.Toggle, x + (CardWidth - Logo) / 2f, y, Logo);
                else
                {
                    RowParts r = kit.Row(content);
                    if (r != null)
                    {
                        LobbyKit.At(r.Root, x, y, CardWidth, Logo);
                        if (r.Name != null) r.Name.text = "";
                        cw.Toggle = r.Toggle;
                    }
                }
                cw.Name = kit.Text(content, TextRole.Body, TextAlignmentOptions.Center, false);
                LobbyKit.At(cw.Name, x - 4f, y + Logo + 8f, CardWidth + 8f, 26f);
                cw.Status = kit.Text(content, TextRole.Hint, TextAlignmentOptions.Top, true);
                LobbyKit.At(cw.Status, x - 4f, y + Logo + 36f, CardWidth + 8f, 44f);
                cw.Last = kit.Text(content, TextRole.Hint, TextAlignmentOptions.Top, true);
                LobbyKit.At(cw.Last, x - 4f, y + Logo + 82f, CardWidth + 8f, 40f);
                string id = c.Id;
                settling = true;
                if (cw.Toggle != null) GameParts.SetOn(cw.Toggle, c.Mine);
                settling = false;
                kit.OnToggle(cw.Toggle, "claim." + id, () => Clicked(id));
                cards.Add(cw);
            }
            message = kit.Text(content, TextRole.Body, TextAlignmentOptions.Center, true);
            LobbyKit.At(message, 30f, y + Logo + 130f, size.x - 60f, 56f);
            kit.PublishMap("picker");
        }

        void Apply()
        {
            for (int i = 0; i < cards.Count && i < board.Cards.Count; i++)
            {
                PickerCard c = board.Cards[i];
                CardW cw = cards[i];
                LobbyKit.Set(cw.Name, c.Name, CardWidth + 8f);
                LobbyKit.Set(cw.Status, c.Status, CardWidth + 8f);
                LobbyKit.Set(cw.Last, c.Last, CardWidth + 8f);
                if (cw.Toggle == null) continue;
                settling = true;
                try
                {
                    // a card that cannot be picked has the game's disabled look; this player's own is the selected one
                    cw.Toggle.interactable = c.Pickable || c.Mine;
                    if (cw.Toggle.isOn != c.Mine) GameParts.SetOn(cw.Toggle, c.Mine);
                }
                finally { settling = false; }
            }
            LobbyKit.Set(message, flash ?? board.Message, size.x - 60f);
        }

        void Clicked(string agencyId)
        {
            if (settling || dialog == null) return;
            PickerCard c = null;
            foreach (PickerCard x in board.Cards) if (x.Id == agencyId) c = x;
            if (c == null) return;
            if (!c.Pickable) { flash = c.WhyNot; Apply(); return; }
            memory.Asking(agencyId);
            flash = null;
            string key = Actions.PickAgency(agencyId);
            if (key != null)
            {
                Log.Info("ui: picker: " + agencyId + " not claimed (" + key + ")");
                flash = new LobbyLine(key, null, false, true);
                Refresh();
                return;
            }
            // a host that refuses at once (a fixture) has already answered: stay open with the agency marked
            if (memory.Refused.ContainsKey(agencyId)) { Refresh(); return; }
            Log.Info("ui: picker: claimed " + agencyId);
            done = true;
            Close();
            if (picked != null)
            {
                try { picked(agencyId); }
                catch (Exception e) { Log.Error("ui: the picker's pick handler failed: " + e); }
            }
        }

        void CancelClicked() { Log.Info("ui: click " + LobbyKit.IdPrefix + "picker.cancel"); }

        // A refusal came in: the agency is marked and the line says why.
        public void Refused() { flash = null; Refresh(); }

        public void PublishMap() { if (dialog != null) kit.PublishMap("picker"); }
    }
}
