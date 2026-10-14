using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The session's dialogs and notices (M8 Task 21; boards 9, 10, 11, 13, 15 and 16), bound only to SessionView,
    // ISessionActions and UiText. One instance, owned by MenuSessions; it draws on the top layer of the scene's one mod
    // canvas (MenuSessions.Ui), over every screen, and its notices on the bottom one, under every screen. Four times
    // a second it asks DialogRules what the view calls for (a refusal, the session over, a refused claim, the player's
    // own drop or reload) and shows it once per cause; a click elsewhere asks for carry on, leave or end. One dialog
    // at a time; the rest wait in a queue, as does any dialog while the game's own Load/Save or Settings window is
    // open (both close on Esc whatever is over them), and a session's end until the main menu. Behind the test seams the dev triggers "ui-dialog <state>" and "ui-notices <state>" put a
    // stand-in session (DialogsTestSession) behind the screens; "off" puts the real one back.
    public sealed class DialogScreens
    {
        const float TickSeconds = 0.25f;

        readonly MenuSessions owner;
        readonly NoticeStack notices;
        readonly GameWindows windows = new GameWindows();
        DialogFlow flow = new DialogFlow();
        ISessionActions actions;
        DialogsTestSession test;

        GameParts parts;
        Canvas canvas, noticeCanvas;   // the shared canvas's dialogs layer and, in game, its HUD layer
        bool inGame, refusedHere;
        DialogBox open;
        float nextTick, nextDev;
        bool tickFailed;

        // The Session over dialog's "Multiplayer saves" (behind MpSaves): the saves list is Task 18's screen.
        public event Action MpSavesWanted;

        // A refused claim's dialog closed (DialogRules.PickerAfter): the join's agency picker opens again (Task 19's,
        // with its earlier answers kept).
        public event Action PickerWanted;

        public DialogScreens(MenuSessions owner)
        {
            this.owner = owner;
            actions = owner.Actions;
            notices = new NoticeStack(this);
        }

        public ISessionActions Actions { get { return actions; } }
        internal bool InGame { get { return inGame; } }

        // A dialog is on screen or waiting its turn.
        public bool Asking { get { return open != null || flow.Busy; } }

        // The pause screen's exit question (Leave or End session) is on screen or waiting: the exit watches only its
        // own question for a "no", never the other dialogs (a drop overlay held by its cause would hold it forever).
        public bool AskingExit
        {
            get { return flow.Pending(DialogKind.ConfirmLeave) || flow.Pending(DialogKind.ConfirmEnd); }
        }

        // Counts the leaves and ends a dialog's button performed: the exit's yes.
        public int ExitsAnswered { get { return exitsAnswered; } }
        int exitsAnswered;

        // A new scene: the old one's parts, canvas and dialog normally went with it (closed here if not, so nothing
        // left behind holds the game as a pop-up; the shared canvas itself is MenuSessions.Ui's). A state dialog lost
        // that way opens again while its cause holds (a session's end shows at the menu it lands on); a confirm asked
        // for in the old scene is dropped.
        public void OnScene(bool game)
        {
            inGame = game;
            DialogBox lost = open;
            open = null;
            if (lost != null) Dismiss(lost);
            flow.SceneChanged();
            windows.Forget();
            parts = null;
            canvas = null;
            noticeCanvas = null;
            refusedHere = false;
            notices.Reset();
            if (owner.Map != null && owner.Map.RemovePrefix("dialog.") + owner.Map.RemovePrefix("notice.") > 0)
                owner.MapChanged();
        }

        public void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                if (!tickFailed) { tickFailed = true; Log.Error("ui: the session dialogs failed: " + e); }
            }
        }

        void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (DevTriggers.Every(ref nextDev, now) && TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath)) DevPoll();
            if (now < nextTick) return;
            nextTick = now + TickSeconds;
            if (test != null) test.Tick();
            SessionView v = actions.View;
            Capability caps = actions.Capabilities;
            bool quiet = v == null || v.Phase == SessionPhase.Idle && v.EndedWhy == null && v.Status == null;
            if (quiet && !flow.Busy && !notices.Shown) return;
            if (Ready()) Run(flow.Tick(v, caps, windows.AnyOpen, inGame, NetClock.NowMs));
            if (inGame && !quiet && Ready()) notices.Update(v, caps, actions.Notices);
            else notices.Clear();
        }

        // ---- the dialogs ----

        // The host's "Carry on without <name>" (a drop notice's click). False when it may not show.
        public bool AskCarryOn(int player)
        {
            return Ask(DialogRules.CarryOn(actions.View, actions.Capabilities, player));
        }

        // A client's "Leave session" (the pause screen's exits, DD21). False when it may not show.
        public bool AskLeave() { return Ask(DialogRules.ConfirmLeave(actions.View, actions.Capabilities)); }

        // The host's "End session" (the pause screen's exits and the PLAYERS screen, DD16, DD21).
        public bool AskEndSession() { return Ask(DialogRules.ConfirmEnd(actions.View, actions.Capabilities)); }

        // The PLAYERS screen's End session, on the session it shows. Behind the test seams that may be a fixture
        // (its "end" state): the question is then asked, and answered, on that fixture, as a ui-dialog state would.
        public bool AskEndSession(ISessionActions on)
        {
            if (on != null && on != actions)
            {
                if (!TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath)) return false;
                Bind(on, null);
                Log.Info("dev screen: the PLAYERS screen's stand-in session is behind the dialogs");
            }
            return AskEndSession();
        }

        // The pause screen's two exits in a session (DD21): true when the mod's own question was asked (Leave session
        // for a client, End session for the host) and the game's own "SureExit" must not show; false when the game's
        // exit runs as it is (no session, or the session does not offer it, as on M4). The prefixes that call this
        // are the full track's (Task 15).
        public bool AskPauseExit()
        {
            switch (PauseExits.For(actions.View, actions.Capabilities))
            {
                case PauseExit.End: return AskEndSession();
                case PauseExit.Leave: return AskLeave();
                default: return false;
            }
        }

        bool Ask(DialogSpec d)
        {
            if (d == null || !Ready()) return false;
            Log.Info("ui: dialog asked: " + d.Kind);
            Run(flow.Ask(d, actions.View, actions.Capabilities, windows.AnyOpen, inGame, NetClock.NowMs));
            return true;
        }

        // Does what the flow decided: one dialog shown, refreshed or closed.
        void Run(List<DialogStep> steps)
        {
            foreach (DialogStep s in steps)
            {
                switch (s.Kind)
                {
                    case DialogStepKind.Close:
                        if (open != null) { DialogBox b = open; open = null; Dismiss(b); }
                        break;
                    case DialogStepKind.Refresh:
                        if (open != null && open.Spec.Id == s.Spec.Id) open.Refresh(s.Spec);
                        break;
                    case DialogStepKind.Show:
                        open = DialogBox.Open(parts, canvas, s.Spec, s.State, Act, Closed);
                        if (open == null) { flow.Closed(s.Spec); break; }   // never shown: not retried every tick
                        Log.Info("ui: built dialog " + s.Spec.Kind + " (" + open.PartCount + " parts)");
                        open.Register(owner.Map);
                        owner.MapChanged();
                        break;
                }
            }
        }

        // A dialog's button. The session actions go through DialogActs, which checks them again at the click.
        void Act(DialogBox box, DialogAction what)
        {
            switch (what)
            {
                case DialogAction.CopyReport:
                    string text = box.Spec.CopyText();
                    if (text == null) return;
                    GUIUtility.systemCopyBuffer = text;
                    Log.Info("ui: dialog report copied (" + text.Split('\n').Length + " lines)");
                    return;
                case DialogAction.MpSaves:
                    box.Close();
                    if (!DialogActs.Allowed(what, box.Spec, actions.View, actions.Capabilities)) return;
                    Action wanted = MpSavesWanted;
                    if (wanted != null) wanted();
                    else Log.Info("ui: the Multiplayer saves list is not in this build yet");
                    return;
                case DialogAction.Leave:
                case DialogAction.EndSession:
                case DialogAction.CarryOn:
                    box.Close();
                    if (!DialogActs.Perform(what, box.Spec, actions))
                        Log.Warn("ui: dialog action " + what + " refused: not allowed now");
                    else if (what != DialogAction.CarryOn) exitsAnswered++;
                    return;
                default:
                    box.Close();
                    return;
            }
        }

        // A dialog closed by its button, Esc, or the screens. A refused claim's leads back to the agency picker (on the
        // real session only: a stand-in's would open the real picker).
        void Closed(DialogBox box)
        {
            if (box != open) return;
            if (!box.Replaced) flow.Closed(box.Spec);
            open = null;
            if (owner.Map != null && owner.Map.RemovePrefix("dialog.") > 0) owner.MapChanged();
            if (box.Replaced || !DialogRules.PickerAfter(box.Spec)) return;
            Action picker = PickerWanted;
            if (actions != owner.Actions || picker == null)
            {
                Log.Info("ui: the agency picker would open again now");
                return;
            }
            try { picker(); }
            catch (Exception e) { Log.Error("ui: reopening the agency picker failed: " + e); }
        }

        void Dismiss(DialogBox box)
        {
            box.Replaced = true;
            box.Close();
        }

        // ---- the parts ----

        // This scene's shared parts and the top (dialogs) layer of its one mod canvas, with the bottom (HUD) layer for
        // the notices in game; on first use. A part the game no longer has refuses every dialog and notice here
        // (nothing half-built shows), logged once per scene.
        internal bool Ready()
        {
            if (canvas != null && parts != null && (!inGame || noticeCanvas != null)) return true;
            if (refusedHere) return false;
            // a scene that is closing has no parts to find: not a refusal, and tried again in the next scene
            if (GameParts.SceneGone(inGame)) return false;
            parts = owner.Ui.Parts(inGame);
            if (parts.Missing != null)
            {
                refusedHere = true;
                Log.Warn("ui: session dialogs off in this scene: part missing: " + parts.Missing);
                return false;
            }
            canvas = owner.Ui.Layer(inGame, UiLayer.Dialogs);
            noticeCanvas = inGame ? owner.Ui.Layer(true, UiLayer.Hud) : null;
            return canvas != null && (!inGame || noticeCanvas != null);
        }

        internal GameParts Parts { get { return parts; } }
        internal Canvas Canvas { get { return canvas; } }
        internal Canvas NoticeCanvas { get { return noticeCanvas; } }
        internal UiMap Map { get { return owner.Map; } }
        internal void MapChanged() { owner.MapChanged(); }

        // ---- the dev triggers (test seams only) ----

        void DevPoll()
        {
            string d = DevTriggers.Take("ui-dialog");
            if (d != null) DevState(d, false);
            string n = DevTriggers.Take("ui-notices");
            if (n != null) DevState(n, true);
        }

        void DevState(string name, bool noticeState)
        {
            if (name == "off")
            {
                Bind(owner.Actions, null);
                Log.Info("dev screen: the real session is back behind the dialogs");
                return;
            }
            DialogState s = DialogStates.Make(name);
            bool known = s != null && Array.IndexOf(noticeState ? DialogStates.NoticeStates : DialogStates.Dialogs,
                                                     name) >= 0;
            if (!known)
            {
                Log.Warn("dev screen: no " + (noticeState ? "notice" : "dialog") + " state '" + name + "'");
                return;
            }
            Bind(new DialogsTestSession(s), s);
            Log.Info("dev screen: " + (noticeState ? "notices" : "dialog") + " state " + name);
            bool asked = true;
            if (s.Ask == DialogKind.CarryOn) asked = AskCarryOn(s.Player);
            else if (s.Ask == DialogKind.ConfirmLeave) asked = AskLeave();
            else if (s.Ask == DialogKind.ConfirmEnd) asked = AskEndSession();
            if (!asked) Log.Warn("dev screen: the state's dialog was refused");
            nextTick = 0f;
        }

        // Puts a session behind the screens: whatever shows now goes, and every cause is new.
        void Bind(ISessionActions session, DialogState state)
        {
            if (open != null) { DialogBox b = open; open = null; Dismiss(b); }
            flow = new DialogFlow();
            notices.Clear();
            actions = session ?? owner.Actions;
            test = session as DialogsTestSession;
        }
    }

    // The game's two windows a mod dialog never goes over (they close on Esc whatever is over them). Found once per
    // scene (they exist, hidden, from the scene's start), then only their activeInHierarchy is read; while none is
    // found yet, looked for again at most once a second.
    sealed class GameWindows
    {
        readonly List<Component> found = new List<Component>();
        float nextFind;

        public void Forget()
        {
            found.Clear();
            nextFind = 0f;
        }

        public bool AnyOpen()
        {
            if (found.Count == 0 && Time.realtimeSinceStartup >= nextFind)
            {
                nextFind = Time.realtimeSinceStartup + 1f;
                Add(Object.FindObjectsOfType<Game.UI.Windows.LoadSaveDialogWindow>(true));
                Add(Object.FindObjectsOfType<Game.UI.Windows.SettingsWindow>(true));
            }
            foreach (Component c in found)
                if (c != null && c.gameObject.activeInHierarchy) return true;
            return false;
        }

        void Add<T>(T[] all) where T : Component
        {
            foreach (T t in all)
                if (t != null && t.gameObject.scene.IsValid() && !Strip.IsMod(t.transform)) found.Add(t);
        }
    }
}
