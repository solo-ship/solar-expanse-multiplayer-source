namespace SolarExpanseMultiplayer
{
    public enum PauseExit { Game = 0, Leave, End }

    // DD21: in a session the pause screen's two exits ask with the mod's own dialog instead of the game's "SureExit"
    // pop-up: a client's become Leave session, the host's End session, in the dialogs' one wording (DialogRules).
    // Each only where the session offers it; otherwise (no session, or the capability off, as on M4) the game's own
    // exit runs as it is.
    public static class PauseExits
    {
        public static PauseExit For(SessionView v, Capability caps)
        {
            if (DialogRules.ConfirmEnd(v, caps) != null) return PauseExit.End;
            if (DialogRules.ConfirmLeave(v, caps) != null) return PauseExit.Leave;
            return PauseExit.Game;
        }
    }
}
