using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // The "seat:", "bankrupt:" and "craft:" log lines (plan "Log lines (binding for Tasks 7-10)"): the tools compare
    // them as whole lines between the host and each client, so both sides make them here, from the entry alone.
    public static class StreamLines
    {
        // The line for an applied entry of one of those kinds, or null for any other kind.
        public static string Of(ActionResult r)
        {
            if (r == null || r.Action == null) return null;
            ActionRequest a = r.Action;
            switch (a.Kind)
            {
                case ActionKind.SeatClaimed:
                    return "seat: " + a.Agency + " claimed #" + r.Seq + " money " + Money(a.Amount);
                case ActionKind.SeatReleased: return "seat: " + a.Agency + " released #" + r.Seq;
                case ActionKind.AgencyBankrupt: return "bankrupt: " + a.Agency + " stopped #" + r.Seq + " day " + r.Day;
                case ActionKind.CraftFinished:
                    return "craft: " + a.Agency + " item " + a.ItemId + " finished as " + MadeId(r.Counters) + " #"
                        + r.Seq;
                default: return null;
            }
        }

        // Money as both games write it: round-trip digits, the invariant culture.
        public static string Money(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        // The craft id a completion made: the ship counter's value before it moved, else the launch vehicle's; -1 when
        // neither moved (a finished facility).
        public static long MadeId(CounterRow[] counters)
        {
            long vehicle = -1;
            if (counters == null) return -1;
            foreach (CounterRow c in counters)
            {
                if (c == null || c.After == c.Before) continue;
                if (c.Which == CounterKind.SpacecraftCount) return c.Before;
                if (c.Which == CounterKind.LaunchVehicleCount && vehicle < 0) vehicle = c.Before;
            }
            return vehicle;
        }
    }
}
