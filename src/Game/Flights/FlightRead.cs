using System;
using System.Collections.Generic;
using Company = Game.Company;
using MissionInfo = Game.Info.MissionInfo;

namespace SolarExpanseMultiplayer
{
    // What the game says of its missions, for the Missions print and the day's touches (plan "FlightRead"): rows keyed
    // by mission id, never list order, one agency at a time, and never `complete` (the label UI sets it at landing, so
    // it differs between two games by a frame). One instance per session, host or client; reset on each load.
    //
    // The print is "as of the close of a day": the live missions, the ones that landed that day and the ones an entry
    // of that day cancelled. The day comes from FlightTouches (the host's close asks for the touches just before it
    // makes the prints) or is set by the client's comparison through PrintDay.
    public sealed class FlightRead
    {
        // game ticks -> the day that ends at or after them, and the origin of the day numbers; set by the session
        public Func<long> OriginTicks;

        readonly Dictionary<long, HashSet<long>> cancelledOn = new Dictionary<long, HashSet<long>>();
        readonly HashSet<long> empty = new HashSet<long>();

        // The game's current day (set by the session: the host's from its clock, a client's from its day point).
        public Func<long> Today;

        // The day an event happening now counts for.
        public long Now { get { return Today != null ? Today() : PrintDay; } }

        // The day the next Missions print is made for.
        public long PrintDay { get; set; }

        // A load: nothing the old world cancelled belongs to the new one.
        public void Reset()
        {
            cancelledOn.Clear();
            lastLogged.Clear();
            PrintDay = 0;
        }

        public void Cancelled(long missionId) { Cancelled(missionId, Now); }

        // A mission was cancelled inside an entry (or the game's own cancel) on `day`.
        public void Cancelled(long missionId, long day)
        {
            HashSet<long> set;
            if (!cancelledOn.TryGetValue(day, out set)) cancelledOn[day] = set = new HashSet<long>();
            set.Add(missionId);
            // only the last few days are ever asked for
            if (cancelledOn.Count > 8)
            {
                long oldest = long.MaxValue;
                foreach (long d in cancelledOn.Keys) if (d < oldest) oldest = d;
                cancelledOn.Remove(oldest);
            }
        }

        // The day that ends at or after the game time (the day an event at that time counts for).
        public long DayOf(long ticks)
        {
            long origin = OriginTicks != null ? OriginTicks() : 0;
            long since = ticks - origin;
            if (since <= 0) return 0;
            long days = (since + TimeSpan.TicksPerDay - 1) / TimeSpan.TicksPerDay;
            return Math.Max(0, days - 1);
        }

        // Every mission of one agency, as rows, by mission id.
        public List<MissionRow> Rows(string agencyWire)
        {
            var rows = new List<MissionRow>();
            Manager.MissionInfoManager m = MonoBehaviourSingleton<Manager.MissionInfoManager>.Instance;
            if (m == null || m.ListMissionInfo == null) return rows;
            foreach (MissionInfo mi in m.ListMissionInfo)
            {
                if (mi == null || mi.company == null || GameAgencies.Wire(mi.company) != agencyWire) continue;
                if (mi.trajectoryObject == null) continue;
                try { rows.Add(FlightMap.RowOf(mi)); }
                catch (InvalidOperationException e)
                {
                    throw new InvalidOperationException("mission " + mi.id + " of " + agencyWire + ": " + e.Message);
                }
            }
            rows.Sort((a, b) => a.MissionId.CompareTo(b.MissionId));
            return rows;
        }

        // The Missions print of one agency for `day`.
        public string Print(string agencyWire, long day)
        {
            HashSet<long> today;
            if (!cancelledOn.TryGetValue(day, out today)) today = empty;
            IList<MissionRow> rows = MissionPrint.ForDay(Rows(agencyWire), day, DayOf, today);
            string print = MissionPrint.Of(agencyWire, rows);
            LogRows(agencyWire, day, rows, print);
            return print;
        }

        // The rows behind a print, written when the agency's print changes, so a print that differs between two games
        // can be read field by field from both logs.
        readonly Dictionary<string, string> lastLogged = new Dictionary<string, string>(StringComparer.Ordinal);

        void LogRows(string agency, long day, IList<MissionRow> rows, string print)
        {
            string last;
            if (lastLogged.TryGetValue(agency, out last) && last == print) return;
            lastLogged[agency] = print;
            Log.Info("flight: missions " + agency + " day " + day + " print " + print + " rows " + rows.Count + ": "
                + MissionPrint.Text(rows, 256));
        }

        public string Print(string agencyWire) { return Print(agencyWire, PrintDay); }

        // The objects the day's close must print because flights changed them: each human agency's Missions when it
        // has a row for the day, and the target site's builds and facilities of each mission landing that day (a
        // landing delivers modules and ships). The day is remembered for the prints that follow.
        public IList<PrintEntry> Touches(long day, IEnumerable<string> humanAgencies)
        {
            PrintDay = day;
            var list = new List<PrintEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string agency in humanAgencies)
            {
                List<MissionRow> rows = Rows(agency);
                HashSet<long> today;
                if (!cancelledOn.TryGetValue(day, out today)) today = empty;
                IList<MissionRow> forDay = MissionPrint.ForDay(rows, day, DayOf, today);
                if (forDay.Count == 0) continue;
                list.Add(new PrintEntry { Kind = ObjectKind.Missions, Agency = agency, Body = 0 });
                foreach (MissionRow r in forDay)
                {
                    if (r.Cancelled || DayOf(r.ArriveTicks) != day) continue;
                    if (seen.Add("b" + agency + r.Target))
                        list.Add(new PrintEntry { Kind = ObjectKind.SiteBuilds, Agency = agency, Body = r.Target });
                    if (seen.Add("f" + agency + r.Target))
                        list.Add(new PrintEntry { Kind = ObjectKind.SiteFacilities, Agency = agency, Body = r.Target });
                }
            }
            return list;
        }
    }
}
