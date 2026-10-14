using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanseMultiplayer
{
    // A menu screen in a modal frame: a mod dialog (the load dialog's frame) or a page (host setup). Closing it by Esc
    // or its Back button goes back to its parent screen (MenuFlow); a screen moving on closes it without going back.
    public abstract class MenuDialogScreen : IMenuScreen
    {
        protected readonly MenuScreens Owner;
        protected readonly MenuKit Kit;
        protected ModDialog Dialog;
        protected RectTransform Body;
        protected TMP_Text Status;     // a refusal's line
        protected Action CloseFrame;   // closes the dialog or page (its close handler then runs)
        bool leaving;

        protected MenuDialogScreen(MenuScreens owner)
        {
            Owner = owner;
            Kit = owner.Kit;
        }

        protected abstract MenuScreen Kind { get; }

        public abstract bool Build();

        protected bool Frame(string titleKey, float width, float height, bool list = false)
        {
            Dialog = Kit.Dialog(Owner.Canvas, titleKey, width, height, Closed, list);
            if (Dialog == null) return false;
            Body = MenuKit.Content(Dialog);
            CloseFrame = Dialog.Close;
            return true;
        }

        // The frame's close handler: by the player (Back, Esc) it goes back; by the screen moving on it does not.
        protected void Closed()
        {
            if (!leaving) Owner.Back(Kind);
        }

        public void Close()
        {
            leaving = true;
            if (CloseFrame != null) CloseFrame();
        }

        public virtual void Tick() { }

        // Back, as Esc: the parent screen.
        protected void GoBack()
        {
            if (CloseFrame != null) CloseFrame();
        }

        // Shows what an action answered: null clears the line, a key shows its line (and goes to the log).
        protected void Refused(string key)
        {
            if (key != null) Log.Info("menu: " + Kind + ": refused (" + key + ")");
            Hint(key);
        }

        // Why a button is off (Next before a pick): the same line, not logged (it follows every pick).
        protected void Hint(string key)
        {
            if (Status == null) return;
            MenuKit.SetLine(Status, key);
            Status.gameObject.SetActive(key != null);
        }

        protected TMP_Text StatusLine(float x, float y, float w, float h)
        {
            Status = Kit.Label(Body, TextRole.Small, null, x, y, w, h, true, true);
            if (Status != null) Status.gameObject.SetActive(false);
            return Status;
        }

        // A push button with a title and a line under it (the Multiplayer screen's two choices): the dialog's own
        // button, its own label holding the title, the kit's body text under it.
        protected Button TwoLineButton(string id, string titleKey, string hintKey, float y, Action onClick)
        {
            const float w = 560f, h = 76f;
            Button b = Kit.Button(Body, id, titleKey, 26f, y, w, h, onClick);
            if (b == null) return null;
            TMP_Text title = MenuKit.TextOf(b);
            if (title != null)
            {
                MenuKit.At(title.rectTransform, 22f, 12f, w - 44f, 30f);
                title.alignment = TextAlignmentOptions.Top;
                title.fontSize = title.fontSize * 1.25f;
                title.enableWordWrapping = false;
            }
            TMP_Text hint = Kit.Label(b.transform, TextRole.Body, hintKey, 22f, 44f, w - 44f, 24f, true, false);
            if (hint != null) hint.alignment = TextAlignmentOptions.Top;
            return b;
        }
    }

    // Board 2: the Multiplayer screen. Host a game, Join a game, the Steam invitation line and the local-network
    // note. Host shows while the session offers any way to host; Join while it offers join by code.
    public sealed class MultiplayerScreen : MenuDialogScreen
    {
        public MultiplayerScreen(MenuScreens owner) : base(owner) { }

        protected override MenuScreen Kind { get { return MenuScreen.Multiplayer; } }

        public override bool Build()
        {
            MenuControls m = Owner.Controls;
            if (!Frame("menu.mp.title", 612f, 560f)) return false;
            if (Kit.Label(Body, TextRole.Body, "menu.mp.intro", 26f, 22f, 560f, 26f, true, true) == null) return false;
            // top down: whichever of Host and Join the session offers, then the line, the Steam invite and the note;
            // the window ends under Back, so a choice that is not offered leaves no gap
            float y = 64f;
            Button host = null, join = null;
            if (m.Host)
            {
                host = TwoLineButton("menu.mp.host", "menu.mp.host", "menu.mp.hostHint", y,
                    () => Owner.Open(MenuScreen.HostSetup));
                if (host == null) return false;
                y += 92f;
            }
            if (m.Join)
            {
                join = TwoLineButton("menu.mp.join", "menu.mp.join", "menu.mp.joinHint", y,
                    () => Owner.Open(MenuScreen.Join));
                if (join == null) return false;
                y += 92f;
            }
            // the dialog's own line under its title, as the separator above the Steam line
            Image line = Owner.Extra.Line(Body);
            if (line != null) MenuKit.At(line.rectTransform, 26f, y, 560f, 1f);
            TMP_Text invite = Kit.Label(Body, TextRole.Body, "menu.mp.steamInvite", 26f, y + 14f, 560f, 60f, true,
                true);
            TMP_Text note = Kit.Label(Body, TextRole.Hint, "menu.mp.network", 26f, y + 80f, 560f, 44f, true, true);
            Button back = Kit.Button(Body, "menu.mp.back", "menu.back", 226f, y + 140f, 160f, 40f, GoBack);
            if (line == null || invite == null || note == null || back == null) return false;
            DialogFrame.Height(Dialog, DialogFrame.Header(Dialog.Parts) + y + 140f + 40f + 26f);
            // the window grows from its top, so a tall one ran off the bottom: its middle goes to the screen's middle
            RectTransform win = Dialog.Window;
            float mid = win.localPosition.y + (0.5f - win.pivot.y) * win.rect.height;
            win.localPosition = new UnityEngine.Vector3(win.localPosition.x, win.localPosition.y - mid,
                win.localPosition.z);
            // the first choice has the game's selected look, as a menu's default does (and Enter takes it)
            Button first = host ?? join;
            if (first != null && UnityEngine.EventSystems.EventSystem.current != null)
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(first.gameObject);
            return true;
        }
    }
}
