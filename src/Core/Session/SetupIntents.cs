namespace SolarExpanseMultiplayer
{
    // The host setup's three armed states (M8 DD26 "The setup's intents"), one trigger per method, so every arming
    // and clearing has a unit test:
    //  - PagesArmed: the setup opened the customisation pages; their hooks and the LoadScene prefix act only on it;
    //  - DialogArmed: the setup opened the load dialog; the Load prefix acts only on it. The dialog's OnHide clears
    //    it and nothing else, because OnHide follows every load that runs in the same frame;
    //  - HostIntent: "host once loaded" (Track U), armed only by an armed hand-over that lets the load run, cleared
    //    by a Load that returned false, Prev or Esc in the setup, the Menu scene coming back and the session starting.
    // One instance: MenuSessions.Intents in Track U, SessionRoot's in the full track. Main thread only.
    public sealed class SetupIntents
    {
        public bool PagesArmed { get; private set; }
        public bool DialogArmed { get; private set; }
        public bool HostIntent { get; private set; }

        public void PagesOpened() { PagesArmed = true; }

        // Prev on the first shown page, or Esc: the setup is left, so nothing is hosted.
        public void PagesLeft()
        {
            PagesArmed = false;
            HostIntent = false;
        }

        // The last Next. runs: the new game's load goes ahead (Track U), which arms the intent only from armed pages.
        public void PagesHandedOver(bool runs)
        {
            bool armed = PagesArmed;
            PagesArmed = false;
            if (runs && armed) HostIntent = true;
        }

        public void DialogOpened() { DialogArmed = true; }

        // The armed Load prefix let the load run (Track U).
        public void LoadRuns()
        {
            if (DialogArmed) HostIntent = true;
        }

        // The Load postfix: a load that returned false never reaches a world, so nothing is hosted.
        public void LoadReturned(bool ok)
        {
            if (!ok) HostIntent = false;
        }

        // The dialog's OnHide: it comes in the same frame as a load that runs, so it must not touch the intent.
        public void DialogHidden() { DialogArmed = false; }

        // The Menu scene came back: a load failed or was cancelled later, or the player left a game.
        public void MenuShown()
        {
            PagesArmed = false;
            DialogArmed = false;
            HostIntent = false;
        }

        // The session started: the intent is spent.
        public void SessionStarted() { HostIntent = false; }
    }
}
