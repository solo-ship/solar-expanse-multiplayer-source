using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The two questions a Steam invitation can ask, on the dialogs layer of the scene's one mod canvas (like
    // DialogScreens, which it never touches): "Leave this game to join <name>?" (the session's LeaveGameFirstAsked,
    // answered through AnswerLeaveGameFirst) and "Couldn't join that game" with the problem in plain words
    // (SteamJoinFailed). One at a time; a question that cannot show yet (no canvas in this scene) waits for the next
    // frame. Owned by MenuSessions; main thread only.
    public sealed class InviteScreens
    {
        readonly MenuSessions owner;
        LobbyJoinRequest asked;          // a question waiting to be shown
        LobbyJoinProblem? failed;        // a problem waiting to be shown
        DialogBox open;
        bool isQuestion, answered;
        bool inGame;

        public InviteScreens(MenuSessions owner)
        {
            this.owner = owner;
            owner.Actions.LeaveGameFirstAsked += r => { asked = r; };
            owner.Actions.SteamJoinFailed += OnFailed;
        }

        public bool Showing { get { return open != null; } }

        // A new scene: the old one's dialog went with it. An unanswered question is a "stay": the join waits no more.
        public void OnScene(bool game)
        {
            inGame = game;
            DialogBox lost = open;
            open = null;
            if (lost != null)
            {
                lost.Replaced = true;
                try { lost.Close(); } catch (Exception) { }
                if (isQuestion && !answered) Answer(false);
            }
        }

        void OnFailed(LobbyJoinProblem p, string words)
        {
            if (p == LobbyJoinProblem.Replaced)
            {
                // a newer invitation took over: its own question follows, so this one closes without an answer
                asked = null;
                if (open != null && isQuestion) { answered = true; open.Replaced = true; open.Close(); }
                return;
            }
            failed = p;
        }

        public void Update()
        {
            if (open != null || (asked == null && failed == null)) return;
            try { Show(); }
            catch (Exception e)
            {
                Log.Error("ui: the invitation dialog failed: " + e.Message);
                asked = null;
                failed = null;
            }
        }

        void Show()
        {
            inGame = Object.FindObjectOfType<MenuSceneUI>() == null;
            GameParts parts = owner.Ui.Parts(inGame);
            if (parts.Missing != null) { Log.Warn("ui: invitation dialog not built: part missing: " + parts.Missing); }
            Canvas canvas = parts.Missing == null ? owner.Ui.Layer(inGame, UiLayer.Dialogs) : null;
            if (canvas == null)
            {
                // nothing can be drawn here: a question is a "stay" so the join does not hang, a problem is dropped
                if (asked != null) { asked = null; Answer(false); }
                failed = null;
                return;
            }
            DialogSpec spec;
            if (asked != null)
            {
                spec = InviteDialogs.LeaveFirst(asked, Session.Host != null, Session.LobbyOnly);
                isQuestion = true;
                answered = false;
                asked = null;
            }
            else
            {
                spec = InviteDialogs.JoinFailed(failed.Value);
                isQuestion = false;
                failed = null;
            }
            open = DialogBox.Open(parts, canvas, spec, false, Act, Closed);
            if (open == null)
            {
                if (isQuestion) Answer(false);
                return;
            }
            Log.Info("ui: built dialog " + spec.Id + " (" + open.PartCount + " parts)");
            open.Register(owner.Map);
            owner.MapChanged();
        }

        void Act(DialogBox box, DialogAction what)
        {
            if (isQuestion && what == DialogAction.Leave) Answer(true);
            box.Close();
        }

        // Closed by a button, Esc or the scene: Esc on the question is "Stay here".
        void Closed(DialogBox box)
        {
            if (box != open) return;
            open = null;
            if (isQuestion && !answered && !box.Replaced) Answer(false);
            if (owner.Map != null && owner.Map.RemovePrefix("dialog.") > 0) owner.MapChanged();
        }

        void Answer(bool yes)
        {
            if (answered) return;
            answered = true;
            Log.Info("ui: leave this game for the Steam invitation: " + (yes ? "yes" : "no"));
            owner.Actions.AnswerLeaveGameFirst(yes);
        }
    }
}
