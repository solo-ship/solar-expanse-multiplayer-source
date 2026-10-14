namespace SolarExpanseMultiplayer
{
    // The main menu's look while host setup's page is up: the game fades its menu out when it opens its own new-game
    // pages, and host setup does the same. What the menu looked like before is kept, and put back exactly when the
    // page closes (Back, Esc, a refusal, any close), so the menu is never left faded or unclickable. The one close
    // that leaves the menu alone is the hand-over to the game's own pages: they have faded it out themselves, and
    // their own Back fades it in again.
    // Only a fully shown menu is kept: host setup can open while the game's own fade-in is still running (the
    // pages' Back, the load dialog closing), and that half-faded look must never be what comes back. Then the menu's
    // resting look (Shown) is kept instead, which is also the only look a fully shown menu can have.
    public sealed class MenuFade
    {
        public struct Look
        {
            public float Alpha;
            public bool BlocksRaycasts, Active;
        }

        // The menu at rest, as the game leaves it after its own fade-in.
        public static readonly Look Shown = new Look { Alpha = 1f, BlocksRaycasts = true, Active = true };

        Look saved;

        public bool Faded { get; private set; }

        // A look that can come back: wholly opaque, shown, taking clicks, and no fade running on it.
        public static bool FullyShown(Look l, bool tweening)
        {
            return !tweening && l.Active && l.BlocksRaycasts && l.Alpha >= 1f;
        }

        // The menu as it is now, before the fade (tweening: a fade of the game's is running on it). A second fade
        // before a close keeps the first look.
        public void Out(Look now, bool tweening)
        {
            if (Faded) return;
            saved = FullyShown(now, tweening) ? now : Shown;
            Faded = true;
        }

        // The look to put back when the page closes, or null: nothing was faded, or the game's own pages are open.
        public Look? Close(bool gamePagesOpen)
        {
            if (!Faded) return null;
            Faded = false;
            if (gamePagesOpen) return null;
            return saved;
        }

        // A new menu: the old one's look means nothing.
        public void Forget() { Faded = false; }
    }

    // A modal frame's close (ModPage): it closes once, and its handler always runs then; a handler that throws is
    // reported, never thrown into the game's frame.
    public sealed class CloseOnce
    {
        public bool Done { get; private set; }

        // True the first time only: the frame then takes itself down and runs its handler (Run).
        public bool Start()
        {
            if (Done) return false;
            Done = true;
            return true;
        }

        // Runs the handler: null when it ran (or there is none), else why it failed.
        public static string Run(System.Action handler)
        {
            if (handler == null) return null;
            try { handler(); return null; }
            catch (System.Exception e) { return e.GetType().Name + ": " + e.Message; }
        }
    }
}
