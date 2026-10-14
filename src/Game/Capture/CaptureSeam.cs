using System;

namespace SolarExpanseMultiplayer
{
    // The pending value of a set action that is still on its way (CommandClient.TryPendingValue).
    public delegate bool PendingValueReader(string key, out int value);

    // The captures' view of a session (ICaptureSeam), built by CommandSession over its own members: the host's own
    // click or a client's, whether a session with commands runs and an entry is being applied, the pending value
    // (always none for the host, whose click is applied at once), the line for the player and the refusals shown in
    // the game's dialog. It owns the lists a window edited and the host has not answered, so they live and die with
    // the session.
    public sealed class CaptureSeam : ICaptureSeam, IRefusalSink
    {
        readonly Func<bool> active, applying;
        readonly Action<ActionRequest> click;
        readonly PendingValueReader pending;
        readonly Action<string> note, refused;

        public CaptureSeam(Func<bool> active, Func<bool> applying, Action<ActionRequest> click,
            PendingValueReader pending, Action<string> note, Action<string> refused)
        {
            if (refused == null) throw new ArgumentNullException("refused");
            if (active == null) throw new ArgumentNullException("active");
            if (applying == null) throw new ArgumentNullException("applying");
            if (click == null) throw new ArgumentNullException("click");
            if (pending == null) throw new ArgumentNullException("pending");
            if (note == null) throw new ArgumentNullException("note");
            this.active = active;
            this.applying = applying;
            this.click = click;
            this.pending = pending;
            this.note = note;
            this.refused = refused;
            Memory = new CaptureMemory();
        }

        public CaptureMemory Memory { get; private set; }
        public bool Active { get { return active(); } }
        public bool Applying { get { return applying(); } }
        public void Click(ActionRequest a) { click(a); }
        public bool TryPendingValue(string key, out int value) { return pending(key, out value); }
        public void Note(string text) { note(text); }
        public void Refused(string text) { refused(text); }
    }
}
