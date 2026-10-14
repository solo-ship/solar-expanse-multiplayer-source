using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Where a step stops short of an impact's arrival (DD15): M6's aligned hold point (V2), one function for the host
    // and every client, so a landing in the last minute is cut at the same point on every game.
    public static class ImpactHold
    {
        public const long MarginTicks = AlignedBoundary.LeadTicks;

        public static long Point(long arrivalTicks, long nextDayTicks)
        {
            return AlignedBoundary.HoldPoint(arrivalTicks, nextDayTicks);
        }
    }

    // The impact missions a game watches (DD15): every human agency's flight that will hit an asteroid, by mission id,
    // with its arrival. The game side rebuilds it from its own missions after each load and each seat entry (Dirty);
    // M6 adds and removes single missions. The host also marks each one it has cut at, so it cuts at it once.
    public sealed class ImpactWatch
    {
        public const int MaxWatched = 1024;

        readonly List<long> ids = new List<long>();
        readonly List<long> arrivals = new List<long>();
        readonly HashSet<long> cut = new HashSet<long>();

        // The game must read its missions again before the watch is used.
        public bool Dirty = true;

        public int Count { get { return ids.Count; } }
        public long IdAt(int i) { return ids[i]; }
        public long ArrivalAt(int i) { return arrivals[i]; }

        public void Clear()
        {
            ids.Clear();
            arrivals.Clear();
        }

        // A mission, kept in arrival order (ties by id). A second Add of one id moves it. False past MaxWatched, which
        // the caller treats as a world it cannot follow.
        public bool Add(long id, long arrivalTicks)
        {
            Remove(id);
            if (ids.Count >= MaxWatched) return false;
            int at = 0;
            while (at < ids.Count && (arrivals[at] < arrivalTicks || (arrivals[at] == arrivalTicks && ids[at] < id)))
                at++;
            ids.Insert(at, id);
            arrivals.Insert(at, arrivalTicks);
            return true;
        }

        public bool Remove(long id)
        {
            int i = ids.IndexOf(id);
            if (i < 0) return false;
            ids.RemoveAt(i);
            arrivals.RemoveAt(i);
            return true;
        }

        public bool Contains(long id) { return ids.IndexOf(id) >= 0; }

        // The host's own cut at a mission's hold point is taken once: the step that follows starts there.
        public bool CutDone(long id) { return cut.Contains(id); }
        public void MarkCut(long id) { cut.Add(id); }
        public void ForgetCuts() { cut.Clear(); }
    }

    // Why a step was cut (the game side's log and statistics).
    // Approach: the client's step stops at the day point first, so the day's prints are compared standing there.
    public enum CutReason { None, Day, Impact, Landing, Approach }

    // What a client's step does about the day: runs short of its point, stops at the point first, or asks the gate.
    public enum DayStep { Short, Approach, Ask }

    // Where the host's fixed step must end (DD4, DD15; M6 "The one step cut"), in game ticks: StepCut.None runs it
    // whole. The client's own cut is ClientCut, built on the same points (V2), so every game ends its steps at the
    // same places near a boundary. The game side turns the answer into the engine's own step error (the Short form);
    // the host never waits at a cut:
    //   * the aligned cut, DayMarginTicks short of nextDay, once a day (where a client compares its prints and holds);
    //   * each watched impact or pull at its AlignedBoundary.HoldPoint, once, in arrival order, so each host step
    //     lands at most one watched event;
    //   * nextDay itself, every day, so no step crosses a boundary (a launch just after midnight never fires inside
    //     the crossing step, I-B).
    public static class StepCut
    {
        public const long None = long.MaxValue;
        public const long DayMarginTicks = 60L * TimeSpan.TicksPerSecond;

        // The day point: where the client compares its prints and holds, and where the host takes the print's moment
        // (the same point on both, so a print taken there shows the same game time): the aligned cut, whatever lands
        // in the last minute (an arrival there is held at this very point, V2). The watch no longer moves it.
        public static long DayPoint(long nextDay, ImpactWatch watch) { return AlignedBoundary.Cut(nextDay); }

        // The host stands at its day point (or past it, still short of nextDay): the moment its print's state is taken.
        public static bool AtDayPoint(long now, long nextDay, ImpactWatch watch)
        {
            return now >= DayPoint(nextDay, watch) && now < nextDay;
        }

        // Where the client stands against its day point (DayPoint). The gate is asked only standing at the point:
        // a step that would pass it stops there first (once a day: approached says it did), because the host prints at
        // the boundary itself and research finishes on whichever frame crosses its end, so prints compared up to a
        // step earlier would differ falsely.
        public static DayStep ClientDay(long now, long end, long dayPoint, bool approached)
        {
            if (end < dayPoint) return DayStep.Short;
            return now < dayPoint && !approached ? DayStep.Approach : DayStep.Ask;
        }

        // The host's step from now to end: once a day at the aligned cut, the client's own (dayCutFor is the nextDay it
        // last cut for), once at each watched hold point, at nextDay always, never a wait. The caller records the cut
        // it takes: the day's nextDay for a Day cut, watch.MarkCut(impactId) for an Impact cut.
        public static long Host(long now, long end, long nextDay, long dayCutFor, ImpactWatch watch,
            out CutReason reason, out long impactId)
        {
            reason = CutReason.None;
            impactId = -1;
            long target = None;
            long day = DayPoint(nextDay, watch);
            if (dayCutFor != nextDay && now < day && end >= day) Take(ref target, ref reason, day, CutReason.Day);
            for (int i = 0; watch != null && i < watch.Count; i++)
            {
                long id = watch.IdAt(i);
                if (watch.CutDone(id)) continue;
                long p = ImpactHold.Point(watch.ArrivalAt(i), nextDay);
                if (p <= now || p > end || p >= target) continue;
                target = p;
                reason = CutReason.Impact;
                impactId = id;
            }
            // no step crosses nextDay: the same cut a client makes (ClientCut), with no wait
            if (now < nextDay && end > nextDay) Take(ref target, ref reason, nextDay, CutReason.Landing);
            if (reason != CutReason.Impact) impactId = -1;
            return target;
        }

        // The host's cut at a watched event that is not an impact (a checked launch, an earlier landing to an asteroid
        // with a pending impact): once, at the event's hold point, no wait. Events whose hold point is already behind
        // the host are dropped (a load put them there). The caller removes the mission after taking the cut.
        public static long HostEvent(long now, long end, long nextDay, MissionWatch watch, out long mission)
        {
            mission = -1;
            while (watch != null && watch.Count > 0)
            {
                long hold = AlignedBoundary.HoldPoint(watch.NextTicks, nextDay);
                if (hold <= now) { watch.Remove(watch.NextMission); continue; }
                if (hold > end) return None;
                mission = watch.NextMission;
                return hold;
            }
            return None;
        }

        // A hold point the host has already passed (a load put it there, or a step that did not reach exactly its
        // cut) is never cut at: the step that follows would only be a tiny one.
        public static void PassedOver(long now, long nextDay, ImpactWatch watch)
        {
            for (int i = 0; watch != null && i < watch.Count; i++)
                if (ImpactHold.Point(watch.ArrivalAt(i), nextDay) <= now) watch.MarkCut(watch.IdAt(i));
        }

        static void Take(ref long target, ref CutReason reason, long at, CutReason why)
        {
            if (at >= target) return;
            target = at;
            reason = why;
        }

        // A catching-up client's step ends exactly at the host's paused date (M7 DD-W12): the earlier of the cut so far
        // and that date. A step that starts at or past the date is dropped by the caller (it waits for the release).
        public static long CatchUp(long now, long end, long catchUpTo, long target, out bool reached)
        {
            reached = now >= catchUpTo;
            if (reached || end < catchUpTo) return target;
            return Math.Min(target, catchUpTo);
        }
    }
}
