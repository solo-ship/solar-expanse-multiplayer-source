namespace SolarExpanseMultiplayer
{
    // The client's own clock while it catches up after a drop (M7 DD-W12). M2's follower stops while the host is
    // paused, so
    // the clock asks for CatchUpSpeed until this game stands at the host's paused date, then for a pause until the host
    // releases everyone. The day gate and the world print still decide every day crossed; CommandClient.CatchUpTo is
    // where
    // the step cut (StepGate) ends the step. Task 14 starts the catch-up (Resume answered). Main thread only.
    public sealed class CatchUp
    {
        readonly CommandClient client;

        public CatchUp(CommandClient client) { this.client = client; }

        public bool Active { get { return client.CatchingUp; } }

        // The speed the clock asks of the game now: CatchUpSpeed, 0 once at the host's date, or -1 when no catch-up
        // runs.
        public float WantedSpeed
        {
            get
            {
                if (!client.CatchingUp || client.OutOfStep) return -1f;
                return client.CatchUpReached() ? 0f : ActionLimits.CatchUpSpeed;
            }
        }
    }
}
