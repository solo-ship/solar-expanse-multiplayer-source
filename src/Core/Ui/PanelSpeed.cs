namespace SolarExpanseMultiplayer
{
    // The game's own speed panel in a session (DD10): its selected image shows the session's real speed, not whatever
    // the engine happens to run at this frame. A client's engine runs catch-up and hold speeds silently, and a client
    // held by the session (no host, the host's clock stopped, its world still loading) is at 0, so the panel reads the
    // speed from here instead of from TimeController.
    public static class PanelSpeed
    {
        // The host's engine speed is the session's speed (the slowest request wins and is applied at once).
        public static float Host(float engineScale) { return Sane(engineScale); }

        // A client: the host's last Clock while it follows the host; 0 while the session holds it (not connected, its
        // world not loaded, no Clock yet, or the Clock has stopped coming: ClockClient's own hold rules).
        public static float Client(bool connected, bool inGame, bool haveClock, float clockScale, long clockAgeMs)
        {
            if (!connected || !inGame || !haveClock || ClockRules.IsStale(clockAgeMs)) return 0f;
            return Sane(clockScale);
        }

        // Which of the six buttons is lit for a speed, as the game's own refresh compares them (whole numbers): 0 the
        // pause button, 1-5 the five speeds in the panel's order; -1 for any other speed (only the six exact speeds
        // have a selected image).
        public static int Mark(int shown, int x1, int x2, int x3, int x4, int x5)
        {
            if (shown == 0) return 0;
            if (shown == x1) return 1;
            if (shown == x2) return 2;
            if (shown == x3) return 3;
            if (shown == x4) return 4;
            if (shown == x5) return 5;
            return -1;
        }

        // A speed that is not a number or below zero shows as paused, never as a running speed.
        static float Sane(float s) { return float.IsNaN(s) || float.IsInfinity(s) || s < 0f ? 0f : s; }
    }

    // When the mod asks the panel to refresh itself (the game refreshes it only when the engine's speed changes): each
    // time the shown speed changes in a session, and once when the session ends, so the game's own marks come back.
    public sealed class PanelRefresh
    {
        bool showing;
        float last;

        public bool Step(bool inSession, float shown)
        {
            if (!inSession)
            {
                if (!showing) return false;
                showing = false;
                return true;
            }
            if (showing && shown == last) return false;
            showing = true;
            last = shown;
            return true;
        }

        // A new panel (a new world): nothing of the old one's marks is known.
        public void Reset() { showing = false; }
    }
}
