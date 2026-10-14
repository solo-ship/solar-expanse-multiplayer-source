using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's checks of a nuke (kind 85; M7 DD-W8) that need no game: the window's own conditions as vanilla has them
    // (a NuclearBomb facility of the acting agency on an asteroid; no build progress, quantity or switch is asked), and
    // the one rule a session adds (Q3): while any mission of any agency lands on, launches from or carries the asteroid
    // before the host's next day, the nuke is refused, so a client that applies the entry at any point of the day can
    // never meet a landing the host did not cancel. The missions are M6's cancel set (TargetGone), read once.
    public static class NukeRules
    {
        // True when a mission the destruction would cancel has an event (its landing, or its launch while it has not left)
        // at or before nextDay. rows: every live mission (FlightCancels.Rows); nowTicks: the apply moment.
        public static bool FlightsDue(IList<GoneRow> rows, int asteroid, long nowTicks, long nextDayTicks)
        {
            if (rows == null) return false;
            foreach (TargetCancel c in TargetGone.Cancels(rows, asteroid, nowTicks))
            {
                foreach (GoneRow r in rows)
                {
                    if (r == null || r.MissionId != c.MissionId) continue;
                    if (r.ArriveTicks <= nextDayTicks || (!c.Launched && r.DepartTicks <= nextDayTicks)) return true;
                }
            }
            return false;
        }

        // 0, or the refusal. gone: the body is destroyed already; asteroid: it is an asteroid; bomb: the acting agency
        // has a NuclearBomb facility with that item id and descriptor on it.
        public static int Decide(bool gone, bool asteroid, bool bomb, bool flightsDue)
        {
            if (gone) return RefuseCodes.BadTarget;
            if (!asteroid || !bomb) return RefuseCodes.NotABomb;
            return flightsDue ? RefuseCodes.FlightsDueToday : 0;
        }
    }
}
