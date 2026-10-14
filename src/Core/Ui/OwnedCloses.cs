using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The questions a screen's dialog asked over itself (Delete this save?, Remove <player>?): closed with it, so a
    // question never outlives the screen it belongs to (it stayed on screen, modal, until a restart). Each close
    // runs once, the newest first; one that throws does not stop the rest.
    public sealed class OwnedCloses
    {
        readonly List<Action> closes = new List<Action>();

        public int Count { get { return closes.Count; } }

        public void Add(Action close)
        {
            if (close != null) closes.Add(close);
        }

        // Runs every close once and forgets them: null when all ran, else why the ones that failed did.
        public string CloseAll()
        {
            if (closes.Count == 0) return null;
            Action[] now = closes.ToArray();
            closes.Clear();   // a close that asks to close its owner again finds nothing left to run
            string failed = null;
            for (int i = now.Length - 1; i >= 0; i--)
            {
                string f = CloseOnce.Run(now[i]);
                if (f != null) failed = failed == null ? f : failed + "; " + f;
            }
            return failed;
        }
    }
}
