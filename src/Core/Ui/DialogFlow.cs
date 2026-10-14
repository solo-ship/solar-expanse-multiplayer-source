using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public enum DialogStepKind { Show, Refresh, Close }

    // What the screen does with the one dialog on screen: show one, refresh it in place, or close it (not the player).
    public sealed class DialogStep
    {
        public DialogStepKind Kind; public DialogSpec Spec;
        public bool State;   // Show: a dialog the view called for (else one a click asked for)
    }

    // Which session dialog is on screen, decided from the view and the capabilities alone (the screen only builds what
    // the steps say). One dialog at a time; the rest wait. Nothing shows while the game's own Load/Save or Settings
    // window is open (both close on Esc whatever is over them), and a session's end waits for the main menu. A waiting
    // dialog is checked again when its turn comes: one the view called for shows only while its cause still holds
    // (with the cause's latest words), and one a click asked for only while its rule still allows it and for
    // AskLifeMs after the click; anything stale is dropped, never shown late.
    public sealed class DialogFlow
    {
        public const long AskLifeMs = 5000;

        readonly DialogQueue queue = new DialogQueue();
        readonly DialogTracker tracker = new DialogTracker();
        DialogSpec open;
        bool openState;
        DialogSpec want;   // the view's cause at the last tick

        public DialogSpec Open { get { return open; } }

        // A dialog of this kind is on screen or waiting its turn (the pause screen's exit watches its own question).
        public bool Pending(DialogKind kind) { return open != null && open.Kind == kind || queue.Has(kind); }

        // Anything on screen, waiting, or held open by its cause: while not, a quiet view needs no tick.
        public bool Busy { get { return open != null || queue.Count > 0 || tracker.IsOpen; } }

        // gameWindowOpen is asked only when a dialog could show (it finds the game's windows).
        public List<DialogStep> Tick(SessionView v, Capability caps, Func<bool> gameWindowOpen, bool inGame,
                                     long nowMs)
        {
            var steps = new List<DialogStep>();
            want = DialogRules.FromView(v, caps);
            switch (tracker.Next(want))
            {
                case DialogTracker.Step.Open:
                    if (open != null && openState) CloseOpen(steps);
                    queue.DropKind(DialogKind.Dropped);
                    queue.DropKind(DialogKind.Reloading);
                    queue.DropKind(DialogKind.Progress);
                    queue.Add(want);
                    break;
                case DialogTracker.Step.Refresh:
                    if (open != null && openState && open.Id == want.Id)
                    {
                        open = want;
                        steps.Add(new DialogStep { Kind = DialogStepKind.Refresh, Spec = want, State = true });
                    }
                    else queue.Add(want);
                    break;
                case DialogTracker.Step.Close:
                    queue.DropKind(DialogKind.Dropped);
                    queue.DropKind(DialogKind.Reloading);
                    queue.DropKind(DialogKind.Progress);
                    if (open != null && openState && open.Overlay) CloseOpen(steps);
                    break;
            }
            DropStaleEnds(v, steps);
            Pump(v, caps, gameWindowOpen, inGame, nowMs, steps);
            return steps;
        }

        // A refusal, kick or session-over dialog tells how an earlier session ended. Once a new one is live (a join
        // that worked, a lobby that opened again) it is out of date: closed if open, never shown if waiting.
        void DropStaleEnds(SessionView v, List<DialogStep> steps)
        {
            if (want != null || v == null || !IsLive(v.Phase)) return;
            queue.DropKind(DialogKind.Refused);
            queue.DropKind(DialogKind.Mismatch);
            queue.DropKind(DialogKind.SessionOver);
            if (open == null || !openState) { tracker.Closed(); return; }
            if (open.Kind != DialogKind.Refused && open.Kind != DialogKind.Mismatch &&
                open.Kind != DialogKind.SessionOver) return;
            CloseOpen(steps);
            tracker.Closed();
        }

        static bool IsLive(SessionPhase p)
        {
            return p == SessionPhase.Lobby || p == SessionPhase.Starting || p == SessionPhase.Loading ||
                   p == SessionPhase.InGame;
        }

        // A click asks for carry on, leave or end (the rule already allowed it). It goes before anything waiting.
        public List<DialogStep> Ask(DialogSpec d, SessionView v, Capability caps, Func<bool> gameWindowOpen,
                                    bool inGame, long nowMs)
        {
            var steps = new List<DialogStep>();
            if (d == null || !d.Asked) return steps;
            d.AskedMs = nowMs;
            if (open != null && open.Id != d.Id)
            {
                // the player's click shows now, never unseen behind another dialog: one the view called for comes
                // back after it while its cause holds, another click's question gives way to the newer one
                if (openState) tracker.Lost();
                CloseOpen(steps);
            }
            queue.Add(d, open == null);
            Pump(v, caps, gameWindowOpen, inGame, nowMs, steps);
            return steps;
        }

        // The dialog on screen was closed by the player (a button, Esc), or never got built: a state dialog stays
        // closed while its cause holds.
        public void Closed(DialogSpec d)
        {
            if (open == null || d == null || d.Id != open.Id) return;
            if (openState) tracker.Closed();
            open = null;
        }

        // A new scene: what was on screen went with the old one (a state dialog opens again while its cause holds);
        // a confirm asked for in the old scene is not shown in the new one.
        public void SceneChanged()
        {
            DialogSpec lost = open;
            bool state = openState;
            open = null;
            if (lost != null && state) tracker.Lost();
            queue.DropAsked();
        }

        void CloseOpen(List<DialogStep> steps)
        {
            steps.Add(new DialogStep { Kind = DialogStepKind.Close, Spec = open });
            open = null;
        }

        void Pump(SessionView v, Capability caps, Func<bool> gameWindowOpen, bool inGame, long nowMs,
                  List<DialogStep> steps)
        {
            if (open != null || queue.Count == 0) return;
            if (gameWindowOpen != null && gameWindowOpen()) return;
            DialogSpec d;
            while ((d = queue.Take(false, inGame)) != null)
            {
                DialogSpec now = Current(d, v, caps, nowMs);
                if (now == null)
                {
                    if (!d.Asked && want == null) tracker.Closed();   // its cause is gone: nothing holds it open
                    continue;
                }
                open = now;
                openState = !now.Asked;
                steps.Add(new DialogStep { Kind = DialogStepKind.Show, Spec = now, State = openState });
                return;
            }
        }

        // The dialog as it should show now, or null when it is stale.
        DialogSpec Current(DialogSpec d, SessionView v, Capability caps, long nowMs)
        {
            if (!d.Asked) return want != null && want.Id == d.Id ? want : null;
            if (d.AskedMs < 0 || nowMs - d.AskedMs > AskLifeMs) return null;
            DialogSpec again = Rule(d.Kind, v, caps, d.Player);
            if (again == null) return null;
            again.AskedMs = d.AskedMs;
            return again;
        }

        static DialogSpec Rule(DialogKind kind, SessionView v, Capability caps, int player)
        {
            switch (kind)
            {
                case DialogKind.CarryOn: return DialogRules.CarryOn(v, caps, player);
                case DialogKind.ConfirmLeave: return DialogRules.ConfirmLeave(v, caps);
                case DialogKind.ConfirmEnd: return DialogRules.ConfirmEnd(v, caps);
                default: return null;
            }
        }
    }

    // A dialog button's session action, checked again at the click (the session behind may have changed since the
    // dialog opened): leave, end and carry on each need their capability, carry on a drop pause still held.
    public static class DialogActs
    {
        // True when the action is allowed now.
        public static bool Allowed(DialogAction what, DialogSpec spec, SessionView v, Capability caps)
        {
            switch (what)
            {
                case DialogAction.Leave: return CapabilityRules.Has(caps, Capability.LeaveNoPause);
                case DialogAction.EndSession: return CapabilityRules.Has(caps, Capability.EndSession);
                case DialogAction.MpSaves: return CapabilityRules.Has(caps, Capability.MpSaves);
                case DialogAction.CarryOn:
                    return spec != null && DialogRules.CarryOn(v, caps, spec.Player) != null;
                case DialogAction.Close:
                case DialogAction.CopyReport: return true;
                default: return false;
            }
        }

        // Runs a session action (leave, end, carry on) when it is allowed; false when refused or not one of them.
        public static bool Perform(DialogAction what, DialogSpec spec, ISessionActions actions)
        {
            if (actions == null || !Allowed(what, spec, actions.View, actions.Capabilities)) return false;
            switch (what)
            {
                case DialogAction.Leave: actions.Leave(); return true;
                case DialogAction.EndSession: actions.EndSession(); return true;
                case DialogAction.CarryOn: actions.CarryOnWithout(spec.Player); return true;
                default: return false;
            }
        }
    }
}
