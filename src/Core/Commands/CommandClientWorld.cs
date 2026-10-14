using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The client's side of the shared world (M7 Task 7; plan DD-W5, DD-W9, DD-W10, DD-W12):
    //   * origin checks, made when an entry is applied (the HumanSet at that point of the stream);
    //   * the year event: YearReady holds the aligned cut until every part of the year's PopulationGrowth (91) is here,
    //     ApplyInEventNow opens it for the game's own year handlers (the rolls are replaced by the host's rows),
    //     CloseInEvent ends it (a row never taken, or a body with no row, is out of step);
    //   * the world print at the day-event point (BeforeDayEvent): restores due heals first, then compares the host's
    // print and, on value days, the block sums; a differing group is found from the host's group prints and healed at
    //     the host's next close (the pieces hold the day they are for);
    // * Suspend / ResumeFrom / Answer: a dropped link that may resume (the stream, the gate and the heal history stay).
    // It knows no Unity and no game class: the world side comes in as IWorldWorld, so every path is tested here.
    public sealed partial class CommandClient
    {
        sealed class GroupAsk
        {
            public long Day; public int Area; public bool ValueOnly;
            public GroupPrint[] Mine;
            public GroupPrint[][] Got; public int Pieces, Have;
            public long AskedMs, AskedHost;
        }

        sealed class HealAsk
        {
            public int Area; public string Agency, Group; public long AskedDay; public bool Discrete;
            public long AskedMs, AskedHost, FirstMs;
            public int Pieces, Have; public HealData[] Got;
            public long Day = -1; public uint AfterSeq;
        }

        // the game's world side (null = M6's behaviour exactly: no world print, no year hold, no origin rule)
        public IWorldWorld World { get; set; }
        // is this agency one a person plays (the glue hands in its HumanSet); null = origins are not checked
        public Func<string, bool> IsHuman { get; set; }
        // this game's M4 fingerprint now (the glue's), for the resume's check
        public Func<Fingerprint> FingerprintNow { get; set; }

        HealPlan plan = new HealPlan(ActionLimits.HealRepeatDays, ActionLimits.ValueHealsPerAgency);
        readonly TwoDayRule twoDay = new TwoDayRule();
        readonly List<GroupAsk> groupAsks = new List<GroupAsk>();
        readonly List<HealAsk> heals = new List<HealAsk>();
        // the heal and group-print requests recently sent (real ms): a persistent difference must not flood the host's
        // inbound limiter; over it the comparison is simply made again at a later day-event point
        readonly long[] healSentAt = new long[HealAsksPerMinute];
        int healSentNext;
        public const int HealAsksPerMinute = 12;

        long hostTicksSeen = long.MinValue;
        bool suspended, catchingUp, sawCatchPause;
        long catchUpTo;
        long progressDay = long.MinValue, progressMs = long.MinValue;
        uint progressSeq;

        // the year
        bool yearOpen, yearComplete, yearHolding;
        long yearDay = -1, yearHoldHost = long.MinValue, yearLateSince = -1;
        GrowthBook yearBook;
        readonly List<ActionResult> yearParts = new List<ActionResult>();

        // ---- states the glue reads ----

        // The open 91's rows during the client's year event; else null.
        public GrowthBook Growth { get { return yearOpen ? yearBook : null; } }
        // Between a Resume and the release: the host's clock is paused far ahead, so its overdue checks report false.
        public bool CatchingUp { get { return catchingUp; } }
        // The host's paused date (ticks) while catching up, else 0.
        public long CatchUpTo { get { return catchingUp ? catchUpTo : 0; } }
        public bool Suspended { get { return suspended; } }

        void WorldReset(bool newPlan)
        {
            groupAsks.Clear();
            heals.Clear();
            if (newPlan) plan = new HealPlan(ActionLimits.HealRepeatDays, ActionLimits.ValueHealsPerAgency);
            twoDay.Clear();
            for (int i = 0; i < healSentAt.Length; i++) healSentAt[i] = long.MinValue;
            healSentNext = 0;
            suspended = false;
            catchingUp = false;
            sawCatchPause = false;
            catchUpTo = 0;
            progressDay = long.MinValue;
            progressMs = long.MinValue;
            yearOpen = yearComplete = yearHolding = false;
            yearDay = -1;
            yearHoldHost = long.MinValue;
            yearLateSince = -1;
            yearBook = null;
            yearParts.Clear();
            hostTicksSeen = long.MinValue;
            hostPaused = false;
        }

        bool LocalDayNow(out long day)
        {
            return DayKey.TryCurrent(world.NextDayTicks, world.OriginTicks, out day);
        }

        static string AreaName(int area)
        {
            return area >= 0 && area < DriftAreas.All.Length ? DriftAreas.All[area] : "area" + area;
        }

        // ---- origins ----

        // Checked when the entry is applied (a seat entry before it may have changed the HumanSet): a rival's entry
        // names
        // a rival, a person's entry names an agency a person plays.
        string OriginProblem(ActionResult entry)
        {
            Func<string, bool> human = IsHuman;
            if (human == null || entry.Action == null) return null;
            string agency = entry.Action.Agency;
            if (entry.Origin == EntryOrigin.Ai && agency != null && human(agency))
                return "an AI entry for the human agency " + agency;
            if (entry.Origin == EntryOrigin.Player && agency != null && !human(agency))
                return "a player entry for " + agency + ", which no person plays";
            return null;
        }

        // ---- the year (DD-W5) ----

        void NoteArrived(ActionResult r)
        {
            if (r.Action == null || r.Action.Kind != ActionKind.PopulationGrowth) return;
            if (r.Action.Value == 1) { yearComplete = true; yearDay = r.Day; }
        }

        // May the client cross `day` (its aligned cut)? MayCross, and on the day whose crossing runs the year event,
        // every
        // part of that year's 91 with every entry before it.
        public bool YearReady(long day)
        {
            bool may = MayCross(day);
            if (!may || World == null || gate.Disabled || !inStream) return may;
            if (order.StartedAfter(day)) return true;
            bool due;
            try { due = World.YearEventDue(world.NextDayTicks); }
            catch (Exception e)
            {
                Fail("the year question threw " + e.GetType().Name);
                return false;
            }
            if (!due) { yearHolding = false; return true; }
            if (yearComplete && yearDay == day + 1) { yearHolding = false; return true; }
            if (!yearHolding)
            {
                yearHolding = true;
                yearHoldHost = hostTicksSeen;
                yearLateSince = -1;
            }
            gate.Holding(day, nowMs());
            holding = true;
            return false;
        }

        // The year hold's own overdue check, on the host's clock like the close's: a host a whole day past this game's
        // next boundary, for longer than a day event can take, and still no 91.
        void YearOverdue(long now)
        {
            if (!yearHolding || catchingUp) { yearLateSince = -1; return; }
            if (hostTicksSeen < world.NextDayTicks + TimeSpan.TicksPerDay) { yearLateSince = -1; return; }
            if (yearLateSince < 0) yearLateSince = now;
            if (now - yearLateSince > ActionLimits.FetchDeadlineMs)
                Fail("the year's population growth never came (day " + lastDay + ")");
        }

        // The 91 is at the head with its day reached and no year event took it.
        void YearNotTaken(ActionResult entry, long localDay)
        {
            if (!yearOpen && localDay >= entry.Day)
                Fail("the year's population growth of day " + entry.Day + " was not taken by this game's year event");
        }

        // The client's year event begins (the transpiler's begin): every entry ahead of the 91 is applied first, then
        // the
        // 91's parts are opened as Growth. null = open; a reason = out of step. localDay is the day the game has now
        // (the
        // 91's day, or the one before it when the event runs before the game moves nextDay on).
        public string ApplyInEventNow(int kind, long localDay)
        {
            if (outOfStep) return outWhy;
            if (kind != ActionKind.PopulationGrowth) return Stop("no in-event apply for " + ActionKind.Name(kind));
            if (World == null) return Stop("a year event with no world side");
            if (applying) return Stop("the year event began while an entry was being applied");
            if (!inStream) return Stop("the year event came with no stream");
            if (yearOpen) return Stop("the year event began twice");
            try { return OpenYear(localDay); }
            catch (Exception e)
            {
                Log.Error("command: the year event threw: " + ExceptionText.Chain(e));
                return Stop("the year event threw " + e.GetType().Name);
            }
        }

        string OpenYear(long localDay)
        {
            // a 91 is only ever behind host-decided entries of its own day; M6's apply-ahead (the landings' one)
            // applies them first, and refuses anything else
            long day = yearComplete ? yearDay : localDay;
            if (localDay != day && localDay != day - 1)
                return Stop("the year event is on day " + localDay + " but the growth came for day " + day);
            ActionResult first = FirstGrowth();
            if (first != null)
            {
                string ahead = AheadOf(first.Seq, day);
                if (ahead != null) return ahead;
            }
            if (!inStream) return outWhy;
            ActionResult head = order.PeekEntry();
            if (head == null || head.Action.Kind != ActionKind.PopulationGrowth || head.Day != day)
                return Stop("the year event began with no population growth at the head of the stream");
            yearParts.Clear();
            var rows = new List<GrowthRow[]>();
            bool last = false;
            for (int i = 0; !last; i++)
            {
                ActionResult p = order.EntryAt(i);
                if (p == null || p.Action.Kind != ActionKind.PopulationGrowth || p.Day != day)
                    return Stop("the year's population growth is not whole (" + yearParts.Count
                        + " part(s), no last one)");
                yearParts.Add(p);
                rows.Add(p.Action.Growth);
                last = p.Action.Value == 1;
            }
            yearBook = new GrowthBook(rows);
            yearOpen = true;
            // the game's own handlers now change the world to the host's rows: the capture hooks let it by
            applying = true;
            applyingKind = ActionKind.PopulationGrowth;
            applyingEntry = yearParts[0].Action;
            return null;
        }

        // The first queued 91, or null.
        ActionResult FirstGrowth()
        {
            for (int i = 0; ; i++)
            {
                ActionResult r = order.EntryAt(i);
                if (r == null) return null;
                if (r.Action != null && r.Action.Kind == ActionKind.PopulationGrowth) return r;
            }
        }

        // The growth prefix asks for the host's row of one (agency, body). A body the host did not roll is out of step.
        public bool TakeGrowth(string agency, int body, out int delta)
        {
            delta = 0;
            if (!yearOpen) return false;
            if (yearBook.TryTake(agency, body, out delta)) return true;
            Fail("the year rolled " + agency + " body " + body + ", which the host did not");
            return false;
        }

        // After the event, always (the transpiler's finally): leftovers are a reason; marks every part applied.
        public string CloseInEvent(int kind)
        {
            if (kind != ActionKind.PopulationGrowth) return Stop("no in-event close for " + ActionKind.Name(kind));
            if (!yearOpen) return outOfStep ? outWhy : null;
            yearOpen = false;
            applying = false;
            applyingKind = -1;
            applyingEntry = null;
            string left = yearBook.Leftovers();
            yearBook = null;
            yearComplete = false;
            yearHolding = false;
            if (left != null) { yearParts.Clear(); return Stop(left); }
            try
            {
                foreach (ActionResult p in yearParts) order.Applied(p);
            }
            catch (Exception e)
            {
                yearParts.Clear();
                return Stop("the year's parts were not at the head: " + e.Message);
            }
            if (Logging)
            {
                foreach (ActionResult p in yearParts) LogWorldEntry(p);
                Say("command: year growth " + yearParts.Count + " part(s) applied, up to #" + order.LastApplied);
            }
            yearParts.Clear();
            return null;
        }

        // ---- the world print at the day-event point (DD-W9, DD-W10) ----

        // The client's last look before its day event for `day`: heals due now are restored, then this game's world is
        // read and compared with the host's. Never holds: a difference starts a heal, which holds only the day it is
        // for.
        public void BeforeDayEvent(long day)
        {
            if (World == null || !inStream || outOfStep || suspended || applying) return;
            try { WorldEvent(day); }
            catch (Exception e)
            {
                Log.Error("command: the world print threw: " + ExceptionText.Chain(e));
                Fail("the world print of day " + day + " threw " + e.GetType().Name);
            }
        }

        void WorldEvent(long day)
        {
            DayClosed close = order.ClosedFor(day);
            int restored = RestoreDue(day, close);
            if (!inStream || close == null || close.World == null) return;
            DaySnapshot snap = World.ReadWorld();
            if (snap == null) { Fail("the world could not be read at day " + day); return; }
            AreaPrint[] mine = WorldPrint.Areas(snap);
            int[] diffs = WorldPrint.Differs(close.World, mine);
            bool values = close.Values != null && !catchingUp;
            if (diffs.Length == 0 && !values) { LogWorld("world: day " + day + " ok"); return; }
            if (diffs.Length > 0 && Logging)
            {
                var sb = new System.Text.StringBuilder();
                foreach (int a in diffs) sb.Append(' ').Append(AreaName(a));
                Say("world: day " + day + " DIFFERS" + sb);
            }
            foreach (int area in diffs)
            {
                if (!inStream) return;
                AreaDiffers(day, area, false, restored);
            }
            if (values && inStream) CompareValues(day, close, snap, diffs, restored);
            if (diffs.Length == 0 && inStream && Logging) Say("world: day " + day + " ok");
        }

        void LogWorld(string line) { if (Logging) Say(line); }

        int growthPart;

        // The line the host logged for the same entry (tools\command-compare.ps1 holds the two equal).
        void LogWorldEntry(ActionResult r)
        {
            string line = WorldLog.EntryLine(r, ref growthPart);
            if (line != null) Say(line);
        }

        // One area's print differs (Discrete) or its value sums do (valueOnly).
        void AreaDiffers(long day, int area, bool valueOnly, int restoredAreas)
        {
            if (WorldTolerance.For(area) == WorldTolerance.NotCompared) return;   // habitability is never compared
            // a heal is in flight for the area: it differs until the heal is restored, so it is compared again then
            if (Outstanding(area)) return;
            if ((restoredAreas & (1 << area)) != 0)
            {
                Fail("world: " + AreaName(area) + " still differs after the heal (day " + day + ")");
                return;
            }
            if (!plan.Restorable(area))
            {
                Fail("world: " + AreaName(area) + " differs at day " + day + " and cannot be healed");
                return;
            }
            // during a catch-up only a Discrete difference matters, and it fails the resume
            if (catchingUp)
            {
                if (!valueOnly) Fail("catch-up: " + AreaName(area) + " differs at day " + day);
                return;
            }
            long now = nowMs();
            if (!HealBudget(now)) return;
            var ask = new GroupAsk { Day = day, Area = area, ValueOnly = valueOnly, AskedMs = now,
                AskedHost = hostTicksSeen,
                Mine = WorldPrint.Groups(World.ReadItems(area, null), area, valueOnly) };
            if (!SendHeal(new HealRequest { Day = day, Area = area })) return;
            groupAsks.Add(ask);
        }

        void CompareValues(long day, DayClosed close, DaySnapshot snap, int[] discreteAreas, int restoredAreas)
        {
            // named as the host names its own sums (CommandServiceWorld.NamedValues), so the keys meet
            AreaValue[] mine = WorldPrint.Named(WorldPrint.Values(snap), World.AgencyNames());
            var byKey = new Dictionary<string, AreaValue>(StringComparer.Ordinal);
            if (mine != null)
                foreach (AreaValue v in mine) byKey[v.Area + "|" + v.Agency + "|" + v.Field] = v;
            twoDay.Today = day;
            var areas = new SortedDictionary<int, string>();
            foreach (AreaValue h in close.Values)
            {
                if (Array.IndexOf(discreteAreas, h.Area) >= 0) continue;   // found by its print already
                AreaValue m;
                byKey.TryGetValue(h.Area + "|" + h.Agency + "|" + h.Field, out m);
                string problem = WorldTolerance.Problem(h, m, twoDay, day);
                if (problem == null) continue;
                // production and research progress never put a client out of step (I2): logged, never healed
                if (WorldTolerance.ProgressOnly(h.Area, h.Field)) { LogWorld("world: day " + day + " value (not healed): "
                    + problem); continue; }
                if (!areas.ContainsKey(h.Area)) areas[h.Area] = problem;
            }
            foreach (KeyValuePair<int, string> kv in areas)
            {
                if (!inStream) return;
                LogWorld("world: day " + day + " value DIFFERS: " + kv.Value);
                AreaDiffers(day, kv.Key, true, restoredAreas);
            }
        }

        bool Outstanding(int area)
        {
            foreach (GroupAsk a in groupAsks) if (a.Area == area) return true;
            foreach (HealAsk h in heals) if (h.Area == area) return true;
            return false;
        }

        // At most HealAsksPerMinute requests a minute; over it nothing is sent and the comparison is made again later.
        bool HealBudget(long now)
        {
            int used = 0;
            for (int i = 0; i < healSentAt.Length; i++)
                if (healSentAt[i] != long.MinValue && now - healSentAt[i] <= 60000) used++;
            return used < healSentAt.Length;
        }

        bool SendHeal(HealRequest r)
        {
            string bad = r.Validate();
            if (bad != null) { Fail("a heal request cannot be sent: " + bad); return false; }
            if (!sendToHost(r)) { Fail("the heal request could not be sent"); return false; }
            healSentAt[healSentNext] = nowMs();
            healSentNext = (healSentNext + 1) % healSentAt.Length;
            return true;
        }

        // ---- the host's answers ----

        string HealReceived(HealData d)
        {
            if (!inStream) return null;
            if (World == null) { Fail("the host sent a heal this game never asked for"); return null; }
            if (d.Group == null) GroupPrintsReceived(d); else RowsReceived(d);
            return null;
        }

        GroupAsk FindAsk(int area, long day)
        {
            foreach (GroupAsk a in groupAsks) if (a.Area == area && a.Day == day) return a;
            return null;
        }

        HealAsk FindHeal(int area, string agency, string group)
        {
            foreach (HealAsk h in heals)
                if (h.Area == area && h.Group == group && h.Agency == agency) return h;
            return null;
        }

        void GroupPrintsReceived(HealData d)
        {
            GroupAsk ask = FindAsk(d.Area, d.Day);
            if (ask == null)
            {
                Fail("group prints of " + AreaName(d.Area) + " day " + d.Day + " nobody asked for");
                return;
            }
            if (d.Code == HealCodes.NoPrint)
            {
                // the host no longer keeps that day: not out of step, the comparison is made again at the next point
                groupAsks.Remove(ask);
                LogWorld("world: " + AreaName(d.Area) + " day " + d.Day + " has no kept print");
                return;
            }
            if (d.Code != HealCodes.Ok) { Fail("the host answered the group prints with code " + d.Code); return; }
            if (ask.Got == null)
            {
                ask.Pieces = d.Pieces;
                ask.Got = new GroupPrint[d.Pieces][];
            }
            else if (ask.Pieces != d.Pieces) { Fail("group-print pieces disagree about their count"); return; }
            if (d.Piece < 0 || d.Piece >= ask.Pieces) { Fail("group-print piece " + d.Piece + " of " + ask.Pieces); return; }
            if (ask.Got[d.Piece] != null) { Fail("group-print piece " + d.Piece + " arrived twice"); return; }
            ask.Got[d.Piece] = d.Groups ?? new GroupPrint[0];
            if (++ask.Have < ask.Pieces) return;
            groupAsks.Remove(ask);
            FindDifferingGroups(ask);
        }

        static bool SumsDiffer(int area, double[] host, double[] mine)
        {
            double tol = WorldTolerance.For(area);
            if (tol == WorldTolerance.NotCompared) return false;
            int n = Math.Max(host == null ? 0 : host.Length, mine == null ? 0 : mine.Length);
            for (int i = 0; i < n; i++)
            {
                double a = host != null && i < host.Length ? host[i] : 0, b = mine != null && i < mine.Length
                    ? mine[i] : 0;
                double r = Math.Abs(a - b) / Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
                if (double.IsNaN(r) || r > tol) return true;
            }
            return false;
        }

        static string GroupKey(GroupPrint g) { return (g.Agency ?? "") + "\n" + g.Group; }

        // The host's group prints of the day the area differed against this game's own, taken at that same moment.
        void FindDifferingGroups(GroupAsk ask)
        {
            var host = new List<GroupPrint>();
            foreach (GroupPrint[] piece in ask.Got) host.AddRange(piece);
            if (host.Count > ActionLimits.MaxGroupPrints) { Fail("more than " + ActionLimits.MaxGroupPrints
                + " group prints"); return; }
            var mineBy = new Dictionary<string, GroupPrint>(StringComparer.Ordinal);
            foreach (GroupPrint g in ask.Mine) mineBy[GroupKey(g)] = g;
            var hostKeys = new HashSet<string>(StringComparer.Ordinal);
            var differing = new List<GroupPrint>();
            foreach (GroupPrint h in host)
            {
                hostKeys.Add(GroupKey(h));
                GroupPrint m;
                bool differs;
                if (!mineBy.TryGetValue(GroupKey(h), out m)) differs = true;
                else if (ask.ValueOnly) differs = SumsDiffer(ask.Area, h.Sums, m.Sums);
                else differs = m.Count != h.Count || !string.Equals(m.Discrete, h.Discrete, StringComparison.Ordinal);
                if (differs) differing.Add(h);
            }
            foreach (GroupPrint m in ask.Mine) if (!hostKeys.Contains(GroupKey(m))) differing.Add(m);
            if (differing.Count == 0)
            {
                if (!ask.ValueOnly) Fail("world: " + AreaName(ask.Area) + " differs at day " + ask.Day
                    + " but no group does");
                else LogWorld("world: " + AreaName(ask.Area) + " value drift is inside every group's tolerance");
                return;
            }
            if (differing.Count > 2 * ActionLimits.MaxHealsPerClose)
            {
                Fail("heal: " + differing.Count + " groups of " + AreaName(ask.Area) + " differ, more than "
                    + 2 * ActionLimits.MaxHealsPerClose);
                return;
            }
            foreach (GroupPrint g in differing)
            {
                if (!plan.MayAsk(ask.Area, g.Agency, g.Group)) continue;
                var req = new HealRequest { Day = ask.Day, Area = ask.Area, Agency = g.Agency, Group = g.Group };
                if (!SendHeal(req)) return;
                plan.Asked(ask.Area, g.Agency, g.Group, ask.Day);
                heals.Add(new HealAsk { Area = ask.Area, Agency = g.Agency, Group = g.Group, AskedDay = ask.Day,
                    Discrete = !ask.ValueOnly, AskedMs = nowMs(), AskedHost = hostTicksSeen });
                LogWorld("heal: asked " + AreaName(ask.Area) + " " + (g.Agency ?? "-") + " " + g.Group + " (day "
                    + ask.Day + ")");
            }
        }

        void RowsReceived(HealData d)
        {
            HealAsk h = FindHeal(d.Area, d.Agency, d.Group);
            if (h == null)
            {
                Fail("a heal of " + AreaName(d.Area) + " " + d.Agency + " " + d.Group + " nobody asked for");
                return;
            }
            if (d.Code == HealCodes.NoPrint)
            {
                heals.Remove(h);
                plan.Forget(h.Area, h.Agency, h.Group);
                LogWorld("heal: " + AreaName(h.Area) + " " + h.Group + " has no kept print");
                return;
            }
            if (d.Code != HealCodes.Ok) { Fail("the host cannot heal " + AreaName(d.Area) + " " + d.Group + " (code "
                + d.Code + ")"); return; }
            if (h.Got == null)
            {
                long local;
                if (!LocalDayNow(out local)) { Fail("the client's own day has no key"); return; }
                if (d.Day < h.AskedDay) { Fail("a heal for day " + d.Day
                    + " before the day it was asked for"); return; }
                // the rows came after this game left their day: they belong to a point that is gone
                if (d.Day < local)
                {
                    heals.Remove(h);
                    plan.Forget(h.Area, h.Agency, h.Group);
                    LogWorld("heal: rows for day " + d.Day + " came late (this game is on " + local + "), dropped");
                    return;
                }
                h.Day = d.Day; h.AfterSeq = d.AfterSeq; h.Pieces = d.Pieces; h.Got = new HealData[d.Pieces];
                h.FirstMs = nowMs();
            }
            else if (h.Day != d.Day || h.AfterSeq != d.AfterSeq || h.Pieces != d.Pieces)
            {
                Fail("pieces of one heal disagree about their day, position or count");
                return;
            }
            if (d.Piece < 0 || d.Piece >= h.Pieces) { Fail("heal piece " + d.Piece + " of " + h.Pieces); return; }
            if (h.Got[d.Piece] != null) { Fail("heal piece " + d.Piece + " arrived twice"); return; }
            h.Got[d.Piece] = d;
            h.Have++;
        }

        // Does a heal for `day` still wait for pieces? The day is held (a fetch, with FetchDeadlineMs); a piece that
        // does
        // not come in time is out of step.
        bool HealHolds(long day, long now)
        {
            bool hold = false;
            foreach (HealAsk h in heals)
            {
                if (h.Day != day || h.Got == null || h.Have >= h.Pieces) continue;
                hold = true;
                long limit = ActionLimits.FetchDeadlineMs + (long)DayGate.PieceAllowanceMs * (h.Pieces - 1);
                if (!catchingUp
                    && now - h.FirstMs > limit) { Fail("a heal piece did not arrive in time"); return true; }
            }
            return hold;
        }

        // Restores every heal whole for this day-event point, before this game reads its own print. Returns the areas
        // restored, as a bit set.
        int RestoreDue(long day, DayClosed close)
        {
            int areas = 0;
            for (int i = heals.Count - 1; i >= 0 && inStream; i--)
            {
                HealAsk h = heals[i];
                if (h.Got == null || h.Day > day) continue;
                if (h.Day < day)
                {
                    // never restored at its own point (the gate should have held it): dropped, found again
                    heals.RemoveAt(i);
                    plan.Forget(h.Area, h.Agency, h.Group);
                    continue;
                }
                if (h.Have < h.Pieces) { Fail("a heal's pieces are missing at day " + day); return areas; }
                if (close == null || close.LastSeq != h.AfterSeq)
                {
                    Fail("a heal for day " + day + " is after seq " + h.AfterSeq + ", the day closed at " + (close
                        == null ? "nothing" : close.LastSeq.ToString()));
                    return areas;
                }
                heals.RemoveAt(i);
                string bad;
                bool was = applying;
                // a restore is the world changing under the host's numbers: the capture hooks let it by
                applying = true;
                try { bad = World.Restore(h.Got); }
                catch (Exception e)
                {
                    Log.Error("command: restoring a heal threw: " + ExceptionText.Chain(e));
                    bad = "restoring threw " + e.GetType().Name;
                }
                finally { applying = was; }
                if (bad != null) { Fail("heal of " + AreaName(h.Area) + " " + h.Group + ": " + bad); return areas; }
                string again = plan.Healed(h.Area, h.Agency, h.Group, day, h.Discrete);
                if (again != null) { Fail(again); return areas; }
                areas |= 1 << h.Area;
                LogWorld("heal: restored " + AreaName(h.Area) + " " + (h.Agency ?? "-") + " " + h.Group + " day "
                    + day + (h.Discrete ? " discrete" : " value"));
            }
            return areas;
        }

        // ---- every step: the waits that never end ----

        void WorldUpdate(long localDay, long now)
        {
            if (World == null) return;
            if (catchingUp)
            {
                SendProgress(localDay, now);
                return;
            }
            YearOverdue(now);
            if (!inStream) return;
            // an ask the host never answered: its clock is days past the request and the deadline is gone
            foreach (GroupAsk a in groupAsks)
                if (a.Have == 0 && Unanswered(a.AskedMs, a.AskedHost,
                    now)) { Fail("a group-print request was never answered"); return; }
            foreach (HealAsk h in heals)
                if (h.Got == null && Unanswered(h.AskedMs, h.AskedHost,
                    now)) { Fail("a heal request was never answered"); return; }
        }

        bool Unanswered(long askedMs, long askedHost, long now)
        {
            return askedHost != long.MinValue && hostTicksSeen >= askedHost + 3 * TimeSpan.TicksPerDay
                && now - askedMs > ActionLimits.FetchDeadlineMs;
        }

        // ---- a link that may resume (DD-W12) ----

        // The link dropped and the session may resume it: nothing pending will be answered, nor any fetch or heal, but
        // the
        // stream position, the gate and the heal history stay. (M5's LinkLost when it will not.)
        public void Suspend()
        {
            pending.Clear();
            budget.Clear();
            fetched.Clear();
            fetching = 0;
            gate.DropFetches();
            checkedDay = -1;
            holding = false;
            groupAsks.Clear();
            heals.Clear();
            plan.DropOutstanding();
            suspended = true;
        }

        // Resume was answered: the host replays from nextSeq (the entry after this game's last applied one). Whatever
        // this
        // game held beyond that goes; the day's prints are compared again and fetched anew.
        public void ResumeFrom(uint nextSeq, long hostPausedTicks)
        {
            if (outOfStep) return;
            if (!suspended || !inStream) { Fail("a resume with no suspended stream"); return; }
            if (nextSeq != order.LastApplied + 1)
            {
                Fail("the host resumes from #" + nextSeq + " but this game applied through #" + order.LastApplied);
                return;
            }
            order.TrimToApplied();
            ClearDay();
            suspended = false;
            catchingUp = true;
            sawCatchPause = false;
            catchUpTo = hostPausedTicks;
            progressDay = long.MinValue;
            progressMs = long.MinValue;
            yearComplete = false;
            yearHolding = false;
            if (Logging) Say("command: resuming from #" + nextSeq + " toward " + hostPausedTicks);
        }

        // The host released everyone (or the resume ended): the host-clock checks count again.
        public void CatchUpEnded()
        {
            catchingUp = false;
            catchUpTo = 0;
            sawCatchPause = false;
        }

        // Every Clock of the host's: its date for the overdue checks, and the end of a catch-up. The release is the
        // host's
        // pause for CatchingUp going from its Clock after it was there (a Clock sent before the pause began lists
        // none).
        void NoteClock(Clock clock)
        {
            hostTicksSeen = TicksOf(clock.Game);
            hostPaused = clock.Scale == 0f && !clock.Locked;
            if (!catchingUp) return;
            bool listed = false;
            if (clock.Pauses != null)
                foreach (Pause p in clock.Pauses) if (p != null && p.Reason == PauseWhy.CatchingUp) listed = true;
            if (listed) sawCatchPause = true;
            else if (sawCatchPause) CatchUpEnded();
        }

        // Has this game reached the host's paused date (within half a second)?
        public bool CatchUpReached()
        {
            return catchingUp && world.GameTicks + TimeSpan.TicksPerSecond / 2 >= catchUpTo;
        }

        // The host asked for this game's fingerprint once every entry up to c.Seq is applied and this game stands at
        // the
        // paused date. null: not yet (ask again next step).
        public ResumeProgress Answer(ResumeCheck c)
        {
            if (c == null || !catchingUp || !inStream || outOfStep) return null;
            if (order.LastApplied < c.Seq || world.GameTicks + TimeSpan.TicksPerSecond / 2 < c.PausedTicks) return null;
            long day;
            if (!LocalDayNow(out day)) { Fail("the client's own day has no key"); return null; }
            Func<Fingerprint> make = FingerprintNow;
            Fingerprint print = make == null ? null : make();
            // not paused yet, or no print: asked again next step (the deadline is the host's)
            if (print == null) return null;
            return new ResumeProgress { Day = day, LastApplied = order.LastApplied, Done = true, Print = print };
        }

        // ResumeProgress when the day or the applied entry has moved, at most ten a second: the host's last entry must be
        // reported too, or its check (which waits for everything it sent to be applied) would never be sent.
        void SendProgress(long localDay, long now)
        {
            bool due = localDay != progressDay || order.LastApplied != progressSeq;
            if (!due || (progressMs != long.MinValue && now - progressMs < 100)) return;
            progressDay = localDay;
            progressSeq = order.LastApplied;
            progressMs = now;
            sendToHost(new ResumeProgress { Day = localDay, LastApplied = order.LastApplied });
        }
    }
}
