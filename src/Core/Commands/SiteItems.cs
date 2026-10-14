using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Which of a site's facilities a request names. Item ids repeat at a site: a save's first facilities carry ids
    // the agency's counter hands out again (in multiplayertest every agency's Earth site has build_veh_assembly 1,
    // build_observatory 2 and build_power_solar 3 beside a counter of 1), so an id alone can name another facility,
    // and a facility under construction can share both id and type with a finished one until it stacks into it
    // (Facility.OnBuildInternal, D 110731-110759). A request always carries the type (Item), so it is matched too.
    public static class SiteItems
    {
        // The facility a window acts on: the first, in the site's own list order, with this id and type that is
        // finished (the windows act on finished units: the exploration window lists only observatories with
        // BuildProgress >= 1, D 134206); else the first with this id and type; else the first with this id alone,
        // whose other type the caller reports as a changed target. -1 when no item has the id.
        public static int Facility(IList<SiteItem> items, long itemId, string content)
        {
            int unfinished = -1, other = -1;
            for (int i = 0; items != null && i < items.Count; i++)
            {
                SiteItem s = items[i];
                if (s.Id != itemId) continue;
                if (!string.Equals(s.Content, content, StringComparison.Ordinal))
                {
                    if (other < 0) other = i;
                    continue;
                }
                if (s.Built) return i;
                if (unfinished < 0) unfinished = i;
            }
            return unfinished >= 0 ? unfinished : other;
        }

        // The unfinished build a request names (a cancel, a reorder): id and type, never a finished item.
        public static int Unfinished(IList<SiteItem> items, long itemId, string content)
        {
            for (int i = 0; items != null && i < items.Count; i++)
            {
                SiteItem s = items[i];
                if (s.Id == itemId && !s.Built && string.Equals(s.Content, content, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }
    }
}
