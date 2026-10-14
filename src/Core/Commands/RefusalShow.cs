namespace SolarExpanseMultiplayer
{
    // A capture seam that shows the player's refusals in the game's own dialog as well as on the status line. A seam
    // without it (the tests' fakes) gets the refusal as a plain note.
    public interface IRefusalSink
    {
        void Refused(string text);
    }

    // Which answers to the player's own request are put in front of them (the status line alone went unseen: a
    // refused flight looked like "pressing OK and nothing happens"), and the repeat guard: the same text again within
    // RepeatMs (a burst of TooMany, a double click) is not shown a second time.
    public sealed class RefusalPopupGate
    {
        public const long RepeatMs = 2000;
        string last;
        long lastAt;

        // Superseded is a newer value replacing the old one: nothing went wrong.
        public static bool Shows(int code) { return code != 0 && code != RefuseCodes.Superseded; }

        public bool Take(string text, long nowMs)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text == last && nowMs - lastAt < RepeatMs) return false;
            last = text;
            lastAt = nowMs;
            return true;
        }
    }
}
