using System;

namespace SolarExpanseMultiplayer
{
    // Writes the rows of the world area a leave reason names (WorldRowsText): the joiner as it leaves out of step, the
    // host as it reads the joiner's leave. The two logs' "world rows:" lines then diff to the item and field.
    public static class WorldRowsLog
    {
        public static void Write(string who, string why)
        {
            try
            {
                int area = WorldRowsText.AreaIn(why);
                if (area < 0 || !StateReader.IsPrepared) return;
                string[] ids = StateReader.Agencies;
                var names = new string[ids.Length];
                for (int i = 0; i < ids.Length; i++) names[i] = AgencyWire.From(ids[i]);
                var sink = new AreaTextSink(area, names);
                StateReader.Visit(sink);
                sink.Lines.Sort(StringComparer.Ordinal);
                Log.Info("world rows: " + who + " " + DriftAreas.All[area] + ", " + sink.Lines.Count + " rows"
                    + (sink.Dropped > 0 ? " (" + sink.Dropped + " more not written)" : ""));
                foreach (string l in sink.Lines) Log.Info("world rows: " + l);
            }
            catch (Exception e) { Log.Warn("world rows: could not be written: " + e.GetType().Name); }
        }
    }
}
