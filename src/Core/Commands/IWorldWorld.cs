using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The game as the world side of the drivers sees it (M7 plan "The drivers' world parts"). The host's CommandService and
    // every client's CommandClient hold one as `World` (null = M6's behaviour exactly). The game side implements it
    // (Tasks 11, 12); the tests use WorldFakes.
    public interface IWorldWorld
    {
        DaySnapshot ReadWorld();                              // M3's reader at the day-event point (market skips applied)
        IList<ItemRow> ReadItems(int area, string agency);    // the area's items, for group prints and heals
        // host: one group's rows, in pieces; Code set when it cannot
        HealData[] ReadGroup(int area, string agency, string group, int maxRows);
        string Restore(HealData[] pieces);                    // client: null = restored; else out of step
        bool YearEventDue(long nextDayTicks);                 // nextYear <= nextDay (the same on every game)
        // the snapshot's agency positions as wire names (a value day's sums are named so); null before the first read
        string[] AgencyNames();
    }
}
