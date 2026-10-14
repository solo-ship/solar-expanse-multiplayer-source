using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Copies of each agency's state as it stood at one day point (CommandHost's research for the day's prints): taken
    // for one nextDay, good for that day only, and dropped for an agency whose state an entry changed after the point
    // (its print is then taken at the close, so it never misses that entry).
    public sealed class PointCopies<T> where T : class
    {
        readonly Dictionary<string, T> copies = new Dictionary<string, T>();
        long forDay = long.MinValue;

        // A new day point: no copy of an earlier one is kept.
        public void Begin(long nextDay)
        {
            copies.Clear();
            forDay = nextDay;
        }

        public void Put(string agency, T copy)
        {
            if (agency != null && copy != null) copies[agency] = copy;
        }

        // An entry changed this agency's state after the point.
        public void Drop(string agency)
        {
            if (agency != null) copies.Remove(agency);
        }

        // A load: nothing of the old world.
        public void Forget()
        {
            copies.Clear();
            forDay = long.MinValue;
        }

        // The agency's copy for the day ending at nextDay, if one was taken at that day's point and still stands.
        public bool TryGet(string agency, long nextDay, out T copy)
        {
            copy = null;
            return agency != null && forDay == nextDay && copies.TryGetValue(agency, out copy);
        }
    }
}
