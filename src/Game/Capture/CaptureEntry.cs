namespace SolarExpanseMultiplayer
{
    // The one entry the session and the dev triggers call (docs/superpowers/carry/t9-shared-lines.md). Everything else
    // of the captures is patch classes the hook plan applies. Stateless: the seam is the session's.
    public static class CaptureEntry
    {
        // The session's seam, whether or not an entry is being applied (the dev triggers need it then too).
        static ICaptureSeam SeamNow()
        {
            var owner = (object)Session.Commands as ICaptureOwner;
            return owner != null ? owner.Capture : null;
        }

        // The dev trigger "click <n>": the script line's action is driven through the game's own window, so the real
        // capture runs (WindowClicks). Registered on CommandSession.ClickCapture; false for a kind nobody clicks.
        public static bool Click(ActionRequest a) { return WindowClicks.Drive(a); }

        // The dev trigger "trip <name>": the tripwire's line for a table entry, as the real path writes it, and for a
        // greyed path also what the greyed control does (the note and a log line). Needs a session.
        public static void Trip(string name)
        {
            ICaptureSeam seam = SeamNow();
            if (seam == null || !seam.Active)
            {
                Log.Warn("command: trip " + TextSanitiser.OneLine(name) + ": no session with commands");
                return;
            }
            if (!Tripwire.Trigger(seam, name))
            {
                Log.Warn("command: trip " + TextSanitiser.OneLine(name) + ": no such tripwire entry");
                return;
            }
            GreyedButtons.Reach(seam, name);
        }
    }
}
