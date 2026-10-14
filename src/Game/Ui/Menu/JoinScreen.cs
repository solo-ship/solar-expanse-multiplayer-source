using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // Board 4: join a game. The player's name, the join code (a secret: a password field that shows only dots, never
    // logged, never in ToString; JoinForm), Paste, and a line on the kind of code held (home-network or Steam). Join
    // checks the code and connects; the host's lobby comes next, where the agency is picked from its roster.
    public sealed class JoinScreen : MenuDialogScreen
    {
        const float W = 640f, H = 496f;

        readonly JoinForm form = new JoinForm();
        TMP_InputField nameField, codeField;
        TMP_Text kindNote;

        public JoinScreen(MenuScreens owner) : base(owner) { }

        protected override MenuScreen Kind { get { return MenuScreen.Join; } }

        public override bool Build()
        {
            if (!Frame("join.screen.title", W, H)) return false;
            Kit.Label(Body, TextRole.Small, "join.screen.name", 26f, 22f, 250f, 26f);
            nameField = Kit.Field(Body, "join.name", PlayerNames.MaxChars, false, 294f, 12f, 320f, 40f);
            Kit.Label(Body, TextRole.Small, "join.screen.code", 26f, 76f, 250f, 26f);
            codeField = Kit.Field(Body, "join.code", JoinCode.MaxTextLength, true, 26f, 106f, 488f, 40f);
            Button paste = Kit.Button(Body, "join.paste", "join.screen.paste", 522f, 106f, 92f, 40f, Paste);
            Kit.Label(Body, TextRole.Body, "join.screen.codeHint", 26f, 156f, 588f, 44f, true, true);
            Kit.Label(Body, TextRole.Body, "join.screen.network", 26f, 214f, 588f, 44f, true, true);
            kindNote = Kit.Label(Body, TextRole.Small, "join.screen.steamNote", 26f, 272f, 588f, 44f, true, true);
            StatusLine(26f, 326f, 588f, 40f);
            Button back = Kit.Button(Body, "join.back", "menu.back", 26f, 380f, 288f, 40f, GoBack);
            Button join = Kit.Button(Body, "join.join", "join.screen.join", 326f, 380f, 288f, 40f, Join);
            if (nameField == null || codeField == null || paste == null || back == null ||
                join == null || Status == null || kindNote == null)
                return false;
            nameField.text = Owner.Names.Get() ?? "";
            // whatever reaches the field (typing, a Ctrl+V of a code split over lines) is normalised as Paste does
            codeField.onValueChanged.AddListener(CodeChanged);
            MenuTestAdapter test = Owner.Test;
            if (test != null && test.Shot.Filled)
            {
                nameField.text = "Sam";
                codeField.text = MenuTestAdapter.FakeCode();
            }
            if (test != null && test.Shot.Refusal != null) Refused(test.Shot.Refusal);
            return true;
        }

        void CodeChanged(string text)
        {
            string clean = JoinForm.Normalise(text);
            if (clean != text) codeField.SetTextWithoutNotify(clean);
            form.SetCode(clean);
            MenuKit.SetLine(kindNote, form.KindKey ?? "join.screen.steamNote");
        }

        // The clipboard's code, white space taken out; anything that cannot be a code leaves the field as it was.
        void Paste()
        {
            string clip = null;
            try { clip = GUIUtility.systemCopyBuffer; } catch (Exception) { }
            string code = JoinForm.CleanPaste(clip);
            if (code == null) { Refused("join.screen.nothingToPaste"); return; }
            codeField.text = code;   // the field shows dots; the form follows its change
            Refused(null);
        }

        void Join()
        {
            form.Name = nameField.text;
            Owner.KeepName(form.Name);
            string why = form.Check();
            if (why == null) why = Owner.Actions.Join(form.Code, PlayerNames.Clean(form.Name));
            if (why != null) { Refused(why); return; }
            Log.Info("menu: join code accepted on the join screen");   // never the code itself
            Owner.Close();
            Owner.OnJoinAccepted();
        }
    }
}
