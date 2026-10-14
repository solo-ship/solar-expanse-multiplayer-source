using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // A yes-or-no question over the PLAYERS screen (Remove, End session): a ModDialog (the game's load-dialog frame,
    // modal, Esc and Cancel close it) with the question in its title, the line in the game's body text and the hint
    // under it, centred, then its Cancel as the no and its OK as the yes, the window as tall as that (DialogFrame).
    // Every line may hold a name, so each is set as peer text.
    public sealed class ConfirmBox
    {
        const float W = 660f, TextPad = 40f, Gap = 20f;

        public ModDialog Dialog { get; private set; }

        public static ConfirmBox Open(GameParts parts, Canvas canvas, ConfirmModel m, Action yes, Action closed,
                                      Action<string, RectTransform> register)
        {
            ModDialog d = ModDialog.Open(parts, canvas, "hud.players.title", closed);
            if (d == null) return null;
            var box = new ConfirmBox { Dialog = d };
            try { box.Fill(parts, m, yes, register); }
            catch (Exception e)
            {
                Log.Error("ui: the question '" + m.Title + "' failed: " + e.Message);
                d.Close();
                return null;
            }
            Log.Info("ui: built players.confirm");
            return box;
        }

        void Fill(GameParts parts, ConfirmModel m, Action yes, Action<string, RectTransform> register)
        {
            Vector2 size;
            float header = DialogFrame.Size(Dialog, W, 420f, out size);
            WindowParts w = Dialog.Parts;
            LobbyKit.HideList(w);
            if (w.Title != null) UiFonts.SetPeerText(w.Title, m.Title, size.x - 120f);
            float tw = size.x - 2f * TextPad, y = header + Gap + 4f;
            foreach (var line in new[] { new { Text = m.Line, Role = TextRole.Body },
                                         new { Text = m.Hint, Role = TextRole.Hint } })
            {
                if (string.IsNullOrEmpty(line.Text)) continue;
                TMP_Text t = parts.Text(Dialog.Window, line.Role);
                if (t == null) continue;
                t.enableWordWrapping = true;
                t.alignment = TextAlignmentOptions.Top;
                // three lines at most, so a long name never pushes the buttons off
                UiFonts.SetPeerText(t, line.Text, 3f * tw - 60f);
                float h = Mathf.Min(DialogFrame.TextHeight(t, tw), 4f * t.fontSize + 8f);
                DialogFrame.At(t, TextPad, y, tw, h);
                y += h + 10f;
            }
            y += Gap - 10f;
            PlayersScreen.Label(w.Ok, m.Yes);
            PlayersScreen.Label(w.Cancel, m.No);
            if (w.Ok != null)
            {
                w.Ok.gameObject.SetActive(true);
                w.Ok.interactable = true;
                w.Ok.onClick.AddListener(() =>
                {
                    Log.Info("ui: click players.confirm.yes");
                    Dialog.Close();
                    if (yes != null) yes();
                });
            }
            if (w.Cancel != null) w.Cancel.onClick.AddListener(() => Log.Info("ui: click players.confirm.no"));
            DialogFrame.ButtonRow(size, y, w.Cancel, w.Ok);
            DialogFrame.Height(Dialog, y + DialogFrame.ButtonH + Gap + 4f);
            Canvas.ForceUpdateCanvases();
            if (w.Ok != null) register("players.confirm.yes", (RectTransform)w.Ok.transform);
            if (w.Cancel != null) register("players.confirm.no", (RectTransform)w.Cancel.transform);
        }
    }
}
