namespace SolarExpanseMultiplayer
{
    // What an applied entry takes off the watch lists (the landings, the checked launches, the plain cuts), the same on
    // the host and on every client so every game cuts at the same points. A cancel (a player's 61, or any 71) ends every
    // pending event of its mission, inside the call that cancels it; a launch outcome (74) ends the launch only, since
    // the landing is still ahead. Landings leave the impact watch where they land.
    public static class FlightWatchSync
    {
        public static void Applied(ActionRequest a, ImpactWatch impacts, MissionWatch launches, MissionWatch plain)
        {
            if (a == null) return;
            switch (a.Kind)
            {
                case ActionKind.FlightCancel:
                case ActionKind.MissionCancelled:
                    if (impacts != null) impacts.Remove(a.ItemId);
                    if (launches != null) launches.Remove(a.ItemId);
                    if (plain != null) plain.Remove(a.ItemId);
                    break;
                case ActionKind.MissionLaunched:
                    if (launches != null) launches.Remove(a.ItemId);
                    break;
            }
        }
    }
}
