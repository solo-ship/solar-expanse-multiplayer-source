using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // The lobby's invite strip: "Invite friends", "Who can join" (the host), and the Steam join code with Copy (the
    // host). The lobby screen hosts it in one spot: Build(...) puts it at (x, y) of its content and returns the strip
    // (Height says how much room it took); Refresh() after every board change keeps its states right. All its logic
    // is InviteStrip (Core); this holds only widgets. Every widget logs "ui: click lobby.invite.*".
    public sealed class InviteStripView
    {
        const float RowH = 40f, Gap = 6f;
        const double FlashSeconds = 6.0;

        readonly LobbyKit kit;
        readonly IInviteSource src;
        readonly bool host;
        TMP_Text title, hint, whoLabel, whoHint, codeLabel, note;
        Button invite, copy, show;
        readonly Button[] who = new Button[InviteStrip.Choices.Length];
        TMP_InputField code;
        bool codeVisible;
        string flash;
        float flashUntil;

        public float Height { get; private set; }

        InviteStripView(LobbyKit kit, IInviteSource src)
        {
            this.kit = kit; this.src = src;
            host = src.Read().IsHost;
        }

        public static InviteStripView Build(LobbyKit kit, Transform parent, float x, float y, float width,
                                            IInviteSource source)
        {
            var v = new InviteStripView(kit, source ?? new LiveInviteSource());
            v.Make(parent, x, y, width);
            v.Refresh();
            return v;
        }

        // Compact rows (the lobby's right column has no scrolling and also holds the LAN code and the options):
        // the title over a full-width Invite friends, Who can join's label beside its three choices, the Steam
        // code's field beside Show and Copy.
        void Make(Transform parent, float x, float y, float width)
        {
            float top = y;
            title = kit.Text(parent, TextRole.Title, TextAlignmentOptions.MidlineLeft, false);
            LobbyKit.At(title, x, y, width, 28f);
            if (title != null) title.text = UiText.Get("lobby.invite.title");
            y += 32f;
            invite = kit.Button(parent, UiText.Get("lobby.invite.button"));
            LobbyKit.At(invite, x, y, width, 48f);
            kit.OnClick(invite, "invite.open", OpenInvite);
            y += 48f + Gap;
            hint = kit.Text(parent, TextRole.Hint, TextAlignmentOptions.TopLeft, true);
            LobbyKit.At(hint, x, y, width, 54f);
            y += 58f;
            if (host)
            {
                float labelW = Mathf.Round(width * 0.3f);
                whoLabel = kit.Text(parent, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(whoLabel, x, y, labelW, RowH);
                if (whoLabel != null) whoLabel.text = UiText.Get("lobby.invite.who");
                float bw = Mathf.Floor((width - labelW - 2f * 8f) / 3f);
                for (int i = 0; i < who.Length; i++)
                {
                    LobbyAccess a = InviteStrip.Choices[i];
                    who[i] = kit.Button(parent, UiText.Get(InviteStrip.ChoiceKey(a)));
                    LobbyKit.At(who[i], x + labelW + i * (bw + 8f), y, bw, RowH);
                    kit.OnClick(who[i], "invite.who." + a.ToString().ToLowerInvariant(), () => Choose(a));
                }
                y += RowH + Gap;
                whoHint = kit.Text(parent, TextRole.Hint, TextAlignmentOptions.TopLeft, true);
                LobbyKit.At(whoHint, x, y, width, 24f);
                y += 28f;
                codeLabel = kit.Text(parent, TextRole.Body, TextAlignmentOptions.MidlineLeft, false);
                LobbyKit.At(codeLabel, x, y, width, 24f);
                if (codeLabel != null) codeLabel.text = UiText.Get("lobby.invite.code");
                y += 28f;
                float bw2 = 104f;
                code = kit.Parts.Input(parent, JoinCode.MaxTextLength, true);
                if (code != null)
                {
                    kit.Built++;
                    code.readOnly = true;
                    LobbyKit.At(code, x, y, width - 2f * (bw2 + 8f), 46f);
                }
                show = kit.Button(parent, UiText.Get("lobby.code.show"));
                LobbyKit.At(show, x + width - 2f * bw2 - 8f, y + 3f, bw2, RowH);
                kit.OnClick(show, "invite.show", ToggleShow);
                copy = kit.Button(parent, UiText.Get("lobby.invite.copy"));
                LobbyKit.At(copy, x + width - bw2, y + 3f, bw2, RowH);
                kit.OnClick(copy, "invite.copy", CopyCode);
                y += 54f;
                note = kit.Text(parent, TextRole.Hint, TextAlignmentOptions.TopLeft, true);
                LobbyKit.At(note, x, y, width, 28f);
                y += 34f;
            }
            Height = y - top;
        }

        // Keeps every state right: called by the lobby after each board change (and fine every frame).
        public void Refresh()
        {
            try
            {
                InviteStrip s = InviteStrip.Build(src.Read());
                if (invite != null) invite.interactable = s.InviteEnabled;
                bool flashing = flash != null && Time.unscaledTime < flashUntil;
                if (!flashing) flash = null;
                // when nothing needs saying, the line under the button tells players the friends list works too
                SetText(hint, flash ?? UiText.Get(s.InviteHintKey ?? "lobby.invite.friendsList"));
                if (!host) return;
                for (int i = 0; i < who.Length; i++)
                    if (who[i] != null) who[i].interactable = s.WhoEnabled && InviteStrip.Choices[i] != s.Selected;
                SetText(whoHint, s.WhoHintKey != null ? UiText.Get(s.WhoHintKey) : "");
                string text = src.SteamCode ?? "";
                if (code != null && code.text != text) { code.text = text; code.ForceLabelUpdate(); }
                if (copy != null) copy.interactable = s.CopyEnabled;
                if (show != null)
                {
                    show.interactable = s.CopyEnabled;
                    LobbyKit.SetButton(show, UiText.Get(codeVisible ? "lobby.code.hide" : "lobby.code.show"));
                }
                SetText(note, s.CodeNoteKey != null ? UiText.Get(s.CodeNoteKey) : UiText.Get("lobby.invite.privacy"));
            }
            catch (Exception e) { Log.Error("ui: the invite strip could not refresh: " + e.Message); }
        }

        static void SetText(TMP_Text t, string text)
        {
            if (t == null) return;
            t.richText = false;
            t.parseCtrlCharacters = false;
            if (t.text != text) t.text = text;
        }

        void Say(string line)
        {
            flash = line;
            flashUntil = Time.unscaledTime + (float)FlashSeconds;
            Refresh();
        }

        void OpenInvite() { Say(InviteStrip.OverlayLine(src.OpenInvite())); }

        void Choose(LobbyAccess a)
        {
            src.SetAccess(a);
            Refresh();
        }

        void ToggleShow()
        {
            codeVisible = !codeVisible;
            if (code != null)
            {
                code.contentType = codeVisible ? TMP_InputField.ContentType.Standard
                                               : TMP_InputField.ContentType.Password;
                code.ForceLabelUpdate();
            }
            Refresh();
        }

        void CopyCode()
        {
            string text = src.SteamCode;
            if (string.IsNullOrEmpty(text)) return;
            GUIUtility.systemCopyBuffer = text;   // the code itself is never logged
            Say(UiText.Get("lobby.invite.copied"));
        }
    }
}
