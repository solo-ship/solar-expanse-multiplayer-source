namespace SolarExpanseMultiplayer
{
    // The time a cut takes off a step, given back. The engine measures each step from the real time of the last one it
    // ran and forgets its own step error after a step, so what a cut takes off is otherwise lost: at 200000 one cut a
    // day is up to a tenth of the game's speed. A cut that is no hold (the host's, which never waits; a client's stop
    // at its day point or before a landing) owes the cut-off time, and the next running step takes it back through
    // the same step error. A hold owes nothing: holding is what it is for. In the engine's own time (physics seconds).
    public sealed class StepCarry
    {
        double owed;

        public double Owed { get { return owed; } }

        // The step error to add so a step of dPhys is want long; with owe, what it takes off is owed.
        public double Cut(double dPhys, double want, bool owe)
        {
            double off = dPhys - want;
            if (!(off > 0)) return 0;
            if (owe) owed += off;
            return off;
        }

        // Before a step: the step error to take away so the owed time is run now. Nothing while the game is paused (the
        // time is owed until it runs again), and nothing that is not a real number.
        public double Repay(bool running)
        {
            if (!running || !(owed > 0)) return 0;
            double o = owed;
            owed = 0;
            return o;
        }

        // A load: nothing owed to the old world.
        public void Forget() { owed = 0; }
    }
}
