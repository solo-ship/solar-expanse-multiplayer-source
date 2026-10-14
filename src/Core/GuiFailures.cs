using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The label's drawing (SempRoot.OnGUI) when it throws. It is drawn again on the next frame, never switched off for
    // good: a host load takes away for a few frames what the label reads, and that once left the label off for the
    // rest of the process. OnGUI runs several times a frame, so the rest of a failing frame is skipped. Each distinct
    // error is logged once, at most one line a second, and only MaxDistinct of them, so an error that comes back every
    // frame cannot flood the log; the lines held back are counted into the next one.
    public sealed class GuiFailures
    {
        public const int MaxDistinct = 16;
        public const int MaxKeyChars = 300;
        public const double LineEverySeconds = 1.0;
        readonly HashSet<string> logged = new HashSet<string>();
        int failedFrame = -1;
        bool failedOnce;
        double lastLine = double.NegativeInfinity;
        int unlogged;

        // True for the rest of a frame in which drawing failed.
        public bool Skip(int frame) { return failedOnce && frame == failedFrame; }

        // A failure in this frame: the line to log, or null when it is not logged.
        public string Failed(int frame, double now, string type, string message)
        {
            failedFrame = frame;
            failedOnce = true;
            string key = TextSanitiser.OneLine((type ?? "?") + ": " + (message ?? ""));
            if (key.Length > MaxKeyChars) key = key.Substring(0, MaxKeyChars);
            if (logged.Contains(key) || logged.Count >= MaxDistinct || now - lastLine < LineEverySeconds)
            {
                unlogged++;
                return null;
            }
            logged.Add(key);
            lastLine = now;
            string line = "OnGUI failed, drawn again next frame: " + key
                + (unlogged > 0 ? " (" + unlogged + " failures not logged since the last line)" : "");
            unlogged = 0;
            return line;
        }
    }
}
