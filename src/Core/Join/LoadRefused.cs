using System;

namespace SolarExpanseMultiplayer
{
    // Thrown by the joiner's game side when it refuses to start a load for a reason it can name (a JoinCodes value), so
    // the flow tells the player and the host that code instead of the generic LoadThrew.
    public sealed class LoadRefused : Exception
    {
        public readonly int Code;

        public LoadRefused(int code, string message) : base(message) { Code = code; }
    }
}
