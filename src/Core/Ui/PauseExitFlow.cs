namespace SolarExpanseMultiplayer
{
    public enum ExitStep { Wait = 0, FinalSave, Run, Cancelled }

    // DD21's order for a pause-screen exit in a session, as a small state machine the glue steps every frame. The mod's
    // confirmation is asked first (the glue); the player's yes (Confirm: the dialog performed the leave or the end) is
    // what starts the exit, whatever way the session then goes: a client's leave loads the menu by itself, the host's
    // end goes to Ending. The host then saves the exit save (the session's final save) exactly once and waits for the
    // copies to drain or the deadline; a client has nothing to save and waits for its session to be over (or a
    // ceiling). Then, and only then, the game's own scene load or quit runs, without a second autosave. A no, or a
    // question that went away unanswered (closed, never shown, or the session ended under it), cancels it.
    public sealed class PauseExitFlow
    {
        public const int ClientSettleMs = 1500;
        // A yes whose leave never took the session anywhere still exits after this long.
        public const int CeilingMs = 10000;

        public bool Active { get; private set; }
        public bool ToWindows { get; private set; }
        public bool Confirmed { get { return confirmed; } }
        bool confirmed, stamped, saved, done;
        long since, deadlineAt;

        public void Begin(bool toWindows)
        {
            Active = true; ToWindows = toWindows; confirmed = false; stamped = false; saved = false; done = false;
        }

        // The player said yes: the dialog ran the leave or the end.
        public void Confirm() { if (Active) confirmed = true; }

        // A click on an exit while this one is under way: true = the click is taken (its question is still asked,
        // or the yes was given); false = the question went away unanswered, so the click asks again.
        public bool Busy(bool asking) { return Active && (confirmed || asking); }

        // asking: the exit's own question is still on screen or queued. copiesIdle: the host has nothing left to
        // send. deadlineMs: how long the host waits for the copies once the exit save is written (SaveCopy's).
        public ExitStep Step(SessionPhase phase, bool isHost, bool asking, bool copiesIdle, long nowMs, long deadlineMs)
        {
            if (!Active || done) return ExitStep.Wait;
            bool over = phase == SessionPhase.Ending || phase == SessionPhase.Idle;
            if (!confirmed)
            {
                if (!over && asking) return ExitStep.Wait;
                Active = false;
                return ExitStep.Cancelled;
            }
            if (!stamped) { stamped = true; since = nowMs; }
            if (isHost && over)
            {
                if (!saved) { saved = true; deadlineAt = nowMs + deadlineMs; return ExitStep.FinalSave; }
                if (!copiesIdle && nowMs < deadlineAt) return ExitStep.Wait;
                return Finish();
            }
            // a client (its leave), or a host whose end never reached Ending
            if (phase == SessionPhase.Idle) return Finish();
            long wait = phase == SessionPhase.Ending ? ClientSettleMs : CeilingMs;
            return nowMs - since < wait ? ExitStep.Wait : Finish();
        }

        ExitStep Finish()
        {
            done = true;
            Active = false;
            return ExitStep.Run;
        }
    }
}
