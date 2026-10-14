namespace SolarExpanseMultiplayer
{
    // When the session root writes ui-map.json: after any screen registered, moved or removed a widget, at most once
    // a second (a screen that settles over a few frames writes once, not every frame). A change asked for inside the
    // second is written when the second is up, never dropped. Times are the caller's clock in seconds.
    public sealed class MapSchedule
    {
        public const float Every = 1f;
        bool wanted;
        float next = float.NegativeInfinity;

        public bool Wanted { get { return wanted; } }

        public void Changed() { wanted = true; }

        // The write that was due did not happen (no folder, or the file system refused): it is tried again a
        // second later, never given up.
        public void Failed() { wanted = true; }

        // True once per change, and not again within a second of the last true.
        public bool Due(float now)
        {
            if (!wanted || now < next) return false;
            wanted = false;
            next = now + Every;
            return true;
        }
    }
}
