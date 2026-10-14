using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Where a client ends its fixed step (plan "The one step cut", the client's inputs; M5's StepCut.Client replaced).
    // The step ends at the earliest of:
    //   * the aligned cut, nextDay - 60 s, every day, held while the day gate says no (and the gate's comparison runs
    //     there, whatever the reason for standing: a next-day landing entry is ready only after it has matched);
    //   * nextDay itself, every day, so no step crosses a boundary and a launch just after midnight cannot fire inside
    //     the crossing step;
    //   * each watched landing (an impact or a pull) and each watched checked launch, at AlignedBoundary.HoldPoint:
    //     held until the host's entry is ready; events chained less than a game second apart form a cluster whose
    //     first member alone waits, for every member's group;
    //   * each plain cut, no wait;
    //   * the host's tick of the entry at the head of the stream while it waits for it (HostTick), no wait.
    // A landing in the last minute that the ships see only in the step after the crossing is held at the start of that
    // step until Update has applied the entries ahead of it. One instance per world (it remembers which day it has
    // stood at the aligned cut for); Reset at each load. Allocation only while an event is within the step.
    public sealed class ClientCut
    {
        const int Landing = 0, Launch = 1, Plain = 2;

        struct Ev { public long Ticks, Id; public int Kind; }

        sealed class ByTime : IComparer<Ev>
        {
            public int Compare(Ev a, Ev b)
            {
                int c = a.Ticks.CompareTo(b.Ticks);
                if (c != 0) return c;
                c = a.Id.CompareTo(b.Id);
                return c != 0 ? c : a.Kind.CompareTo(b.Kind);
            }
        }

        static readonly ByTime Order = new ByTime();

        Ev[] evs = new Ev[64];
        long[] idBuf = new long[64], tickBuf = new long[64];
        int n;
        long approachedFor = long.MinValue;
        readonly List<long> ticks = new List<long>();
        readonly List<int> watched = new List<int>();

        // The mission whose entry the step is waiting for, when it reaches that event's hold point (Overdue asks the
        // client about it): -1 when none.
        public long WaitedId { get; private set; }

        public void Reset() { approachedFor = long.MinValue; }

        // The step from `now` to `end` (game ticks) on client day `day`, whose boundary is nextDay. nowMs is the real
        // clock for the waits' deadlines. hold: the step is not taken at all (out of step). Otherwise the target to end
        // the step at, StepCut.None for the whole step, and why (Impact and Day are holds, Approach and Landing cuts
        // that owe their time).
        public long Decide(CommandClient c, long day, long now, long end, long nextDay, long nowMs,
                           ImpactWatch impacts, MissionWatch launches, MissionWatch plain, out CutReason reason,
                           out bool hold)
        {
            reason = CutReason.None;
            hold = true;
            WaitedId = -1;
            long head = c.HeadImpactMission();
            if (head >= 0 && (impacts == null || !impacts.Contains(head)))
                c.OutOfStepNow("a landing entry for mission " + head + ", which this client does not watch");
            long headLaunch = c.HeadLaunchMission();
            if (headLaunch >= 0 && (launches == null || !launches.Contains(headLaunch)))
                c.OutOfStepNow("a launch outcome for mission " + headLaunch + ", which this client does not watch");
            if (c.OutOfStep) return StepCut.None;

            // the gate is asked only standing at the aligned cut (or past it): a step that would pass it stops there
            // first, once a day, because the host prints at the boundary itself
            long dayPoint = AlignedBoundary.Cut(nextDay);
            DayStep atDay = StepCut.ClientDay(now, end, dayPoint, approachedFor == nextDay);
            // YearReady is MayCross, and on the day whose crossing runs the year event the host's growth as well (M7)
            bool dayHold = atDay == DayStep.Ask && !c.YearReady(day);
            if (c.OutOfStep) return StepCut.None;

            Gather(impacts, launches, plain);
            long target = StepCut.None;
            Events(c, now, end, nextDay, nowMs, ref target, ref reason);
            if (c.OutOfStep) return StepCut.None;
            long tick = c.WaitingFor;
            if (tick > now && tick <= end) Take(ref target, ref reason, tick, CutReason.Landing);
            if ((atDay == DayStep.Approach || dayHold) && end >= dayPoint)
                Take(ref target, ref reason, Math.Max(dayPoint, now), dayHold ? CutReason.Day : CutReason.Approach);
            // no step crosses nextDay: a cut with no wait
            if (now < nextDay && end > nextDay) Take(ref target, ref reason, nextDay, CutReason.Landing);
            if (reason == CutReason.Approach) approachedFor = nextDay;
            hold = false;
            return target;
        }

        void Gather(ImpactWatch impacts, MissionWatch launches, MissionWatch plain)
        {
            n = 0;
            for (int i = 0; i < impacts.Count; i++) Add(impacts.IdAt(i), impacts.ArrivalAt(i), Landing);
            Copy(launches, Launch);
            Copy(plain, Plain);
            Array.Sort(evs, 0, n, Order);
        }

        void Copy(MissionWatch w, int kind)
        {
            if (w == null || w.Count == 0) return;
            if (idBuf.Length < w.Count)
            {
                idBuf = new long[w.Count * 2];
                tickBuf = new long[w.Count * 2];
            }
            int m = w.CopyTo(idBuf, tickBuf);
            for (int i = 0; i < m; i++) Add(idBuf[i], tickBuf[i], kind);
        }

        void Add(long id, long ticks, int kind)
        {
            if (n == evs.Length) Array.Resize(ref evs, n * 2);
            evs[n].Id = id;
            evs[n].Ticks = ticks;
            evs[n].Kind = kind;
            n++;
        }

        void Events(CommandClient c, long now, long end, long nextDay, long nowMs, ref long target,
                    ref CutReason reason)
        {
            // the watched events still ahead of the step's start, in time order, grouped into clusters
            if (n == 0) return;
            int ahead = 0;
            ticks.Clear();
            watched.Clear();
            for (int i = 0; i < n; i++)
            {
                Ev e = evs[i];
                if (e.Kind == Plain)
                {
                    long p = AlignedBoundary.HoldPoint(e.Ticks, nextDay);
                    if (p > now && p <= end) Take(ref target, ref reason, p, CutReason.Landing);
                    continue;
                }
                bool first = WaitedId < 0;
                if (first && AlignedBoundary.HoldPoint(e.Ticks, nextDay) <= end) Wait(c, e, nowMs);
                if (c.OutOfStep) return;
                if (e.Ticks > now)
                {
                    watched.Add(i);
                    ticks.Add(e.Ticks);
                    ahead++;
                }
                else if (e.Kind == Landing && now - e.Ticks < AlignedBoundary.MarginTicks && !c.ImpactReady(e.Id))
                {
                    // the ships saw the start of the step that crossed the boundary, so this landing is in the step
                    // that starts now: it waits for Update to apply every entry ahead of it, and for the entry itself
                    Take(ref target, ref reason, now, CutReason.Impact);
                }
            }
            if (ahead == 0) return;
            int[] starts = AlignedBoundary.Groups(ticks.ToArray());
            for (int g = 0; g < starts.Length; g++)
            {
                int from = starts[g], to = g + 1 < starts.Length ? starts[g + 1] : ahead;
                for (int k = from; k < to; k++)
                {
                    Ev e = evs[watched[k]];
                    long p = AlignedBoundary.HoldPoint(e.Ticks, nextDay);
                    if (p > end) continue;
                    if (k == from && !ClusterReady(c, from, to, watched))
                        Take(ref target, ref reason, Math.Max(p, now), CutReason.Impact);
                    else if (p > now)
                        Take(ref target, ref reason, p, CutReason.Landing);
                }
            }
        }

        // Every member's group has arrived, and the first is at the head (or past the gate).
        bool ClusterReady(CommandClient c, int from, int to, List<int> watched)
        {
            int m = to - from;
            var ids = new long[m];
            var landing = new bool[m];
            for (int i = 0; i < m; i++)
            {
                Ev e = evs[watched[from + i]];
                ids[i] = e.Id;
                landing[i] = e.Kind == Landing;
            }
            return c.ClusterReady(ids, landing, m);
        }

        // The earliest watched event the step reaches is the one whose arrival the client asks about: its entry not at
        // hand, or at hand but its landing or launch never coming, ends out of step on the host's clock.
        void Wait(CommandClient c, Ev e, long nowMs)
        {
            WaitedId = e.Id;
            if (e.Kind == Landing) c.ImpactOverdue(e.Id, e.Ticks, nowMs);
            else c.LaunchOverdue(e.Id, e.Ticks, nowMs);
        }

        static void Take(ref long target, ref CutReason reason, long at, CutReason why)
        {
            if (at >= target) return;
            target = at;
            reason = why;
        }
    }
}
