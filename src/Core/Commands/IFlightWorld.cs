using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The game's flight side as the drivers see it (plan "The drivers' flight parts"). The game implements it (Tasks 9
    // and 10); tests use FlightFakes. A driver whose Flights is null behaves exactly as M5's.
    public interface IFlightWorld
    {
        // ---- host ----
        // a game-made plan, or the host's own creation of an entry's mission, is in the game's planner queue
        bool PlansPending { get; }
        // HostLoaded: every queued session plan dropped
        void DropQueued();
        // the Missions and next-day site prints owed for the day
        IList<PrintEntry> FlightTouches(long day);

        // ---- client ----
        // the running apply over frames: null while it runs, then its outcome
        ApplyOutcome Poll();
        // nowMs when its plan left the game's queue; -1 while queued
        long ApplyStarted { get; }
        // 73 only: this game's own landing of that mission is recorded; throws for a mission this game never had
        bool Landed(long missionId);
    }
}
