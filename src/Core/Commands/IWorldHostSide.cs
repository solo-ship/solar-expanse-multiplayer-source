namespace SolarExpanseMultiplayer
{
    // One held objective completion or contract activation the host noted (DD-W7): raised as a 92 or a 95 at the day
    // event. Objective is -1 for an activation.
    public sealed class WorldNote
    {
        public bool Activation;
        public string Agency, Contract;
        public int Objective;
    }

    // What the host's game adds for the world part of the driver beyond IWorldWorld (M7 Task 6). Every member is
    // optional in the driver: with none attached a note is always due, the world print has no per-agency names (a
    // value day then fails closed) and a resume cannot be checked, so it fails to a full rejoin.
    public interface IWorldHostSide
    {
        // Still to raise when its turn comes: the objective is not complete yet (a chain may have completed it
        // meanwhile), the contract is still Available for the agency.
        bool Due(WorldNote note);
        // The agencies' wire ids in the world print's agency order (the snapshot's agency index is a position in it).
        string[] AgencyNames();
        // M4's fingerprint of the host's world now, for a resume's release check.
        Fingerprint Print();
    }
}
