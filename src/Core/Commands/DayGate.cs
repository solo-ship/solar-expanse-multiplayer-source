namespace SolarExpanseMultiplayer
{
    // May the client run the day event that ends `day`? Only when the host has closed that day, every entry up to the
    // close is applied, the day's prints were compared, and no fetch of the host's copy is still out. The Game side
    // holds the step in the Short form while this says no (DD4); the conditions are all here so they get real tests.
    public sealed class DayGate
    {
        // What one piece of an object takes to reach the client at the host's pacing (ObjectBytesPerMinute): a full
        // 56 KiB piece (the ObjectData cap) is 3.3 s of the budget. The allowances below are half as much again, so
        // a fetch that is merely paced is never called late; Task 7a's budget test validates them (provisional).
        const long MaxPieceBytes = 56 * 1024;
        public const int PaceMs = (int)(MaxPieceBytes * 60000 / ActionLimits.ObjectBytesPerMinute) + 1;
        // per piece after an object's first, and per object after the first of a day's fetches
        public const int PieceAllowanceMs = PaceMs * 3 / 2, ObjectAllowanceMs = PaceMs * 3 / 2;

        const long WholeDay = System.TimeSpan.TicksPerDay;
        // A running host's Clock message changes its date every time (4 a second); a date that has not changed for this
        // long is a paused host, not a slow one.
        public const int HostFrozenMs = 1000;

        readonly ResultOrder order;
        readonly bool disabled;
        long comparedDay = -1;
        int fetches, fetchesSince;
        long fetchSince;
        long holdSince = -1, holdDay;
        long hostTicks = long.MinValue, lateSince = -1;
        long waitDay, waitLateMs, waitLastMs = -1, hostMovedMs;
        bool waiting;
        long announcedAtStart;
        int holds;
        long maxHold, totalHold, lastHoldDay = -1, lastHoldMs;

        // disabled: -semp-no-day-gate, the negative control: the gate is always open.
        public DayGate(ResultOrder order, bool disabled = false)
        {
            this.order = order;
            this.disabled = disabled;
        }

        public bool MayCross(long day)
        {
            if (disabled || order.StartedAfter(day)) return true;
            DayClosed c = order.ClosedFor(day);
            return c != null && order.LastApplied >= c.LastSeq && comparedDay == day && fetches == 0;
        }

        // The client compared the day's prints with the host's and they matched.
        public void Compared(long day) { comparedDay = day; }

        // An ObjectRequest went out. A restore changes what was compared, so the comparison has to be made again.
        public void FetchStarted(long nowMs)
        {
            if (fetches == 0)
            {
                fetchSince = nowMs;
                fetchesSince = 0;
                announcedAtStart = order.PiecesAnnounced;
            }
            fetches++;
            fetchesSince++;
            comparedDay = -1;
        }

        // An object was restored (all its pieces in and applied).
        public void FetchEnded()
        {
            if (fetches > 0) fetches--;
        }

        // Past the deadline, stretched by every extra piece announced and every further object requested since the
        // fetches began (not only those still held: an object restored early must not shorten what a later one is
        // given): the caller goes out of step instead of holding for ever.
        public bool FetchOverdue(long nowMs)
        {
            if (fetches == 0) return false;
            long extra = order.PiecesAnnounced - announcedAtStart;
            long limit = ActionLimits.FetchDeadlineMs + (long)PieceAllowanceMs * extra
                + (long)ObjectAllowanceMs * (fetchesSince - 1);
            return nowMs - fetchSince > limit;
        }

        // A hold is counted when it ends, so Holding can be called on every step of one.
        public void Holding(long day, long nowMs)
        {
            // lateness belongs to one hold of one day, even when the glue never released the one before
            if (holdSince >= 0 && day != holdDay) lateSince = -1;
            holdDay = day;
            if (holdSince < 0)
            {
                holdSince = nowMs;
                lateSince = -1;   // lateness belongs to one hold
            }
        }

        public void Released(long nowMs)
        {
            // a crossing ends the day's wait, held or not; the next hold starts its own close lateness (Holding)
            EndWait();
            if (holdSince < 0) return;
            long ms = nowMs - holdSince;
            holdSince = -1;   // CloseOverdue clears the lateness itself once nothing is held
            holds++;
            totalHold += ms;
            if (ms > maxHold) maxHold = ms;
            lastHoldDay = holdDay;
            lastHoldMs = ms;
        }

        // The hold of `day` that just ended, as the summary counted it, once (the per-day log line): its length in
        // ms, or -1 when that day's hold is not the last one counted.
        public long TakeHold(long day)
        {
            if (lastHoldDay != day) return -1;
            lastHoldDay = -1;
            return lastHoldMs;
        }

        // The host's game clock, from its Clock messages (the client feeds every one).
        public void HostAt(long hostGameTicks, long nowMs)
        {
            if (hostGameTicks != hostTicks) hostMovedMs = nowMs;
            hostTicks = hostGameTicks;
        }

        // The host's clock is a whole day past the client's own next boundary and the day being held for has no
        // DayClosed, for longer than a close can reasonably wait in the host's output budget (Clock uses the
        // reserve, so it can overtake a queued DayClosed): the close is not coming. A host paused before the
        // boundary, or a client simply ahead of the host (the hold the gate exists for), never meets the condition.
        public bool CloseOverdue(long localNextDayTicks, long nowMs)
        {
            if (disabled || holdSince < 0 || order.ClosedFor(holdDay) != null
                || hostTicks < localNextDayTicks + System.TimeSpan.TicksPerDay)
            {
                lateSince = -1;
                return false;
            }
            if (lateSince < 0) lateSince = nowMs;
            return nowMs - lateSince > ActionLimits.FetchDeadlineMs;
        }

        // -semp-no-day-gate: the driver skips its comparisons too.
        public bool Disabled { get { return disabled; } }

        // The impact wait: the client has reached an arrival and the host's entry for it (and every entry before it) is
        // not at hand yet. The glue asks on every step, so asking again for the same key (the mission) keeps what has
        // been counted.
        public void BeginWait(long day)
        {
            if (waiting && waitDay == day) return;
            waiting = true;
            waitDay = day;
            waitLateMs = 0;
            waitLastMs = -1;
        }

        public void EndWait()
        {
            waiting = false;
            waitLateMs = 0;
            waitLastMs = -1;
        }

        // The wait's own overdue check, on the host's clock and nothing else: a host clock a whole day past the
        // client's next boundary for more than FetchDeadlineMs means the landing is not coming. It keeps its own count:
        // CloseOverdue resets the shared one on every call once DayClosed is present, which is the usual state during
        // a wait. Only the time the host's date moves is counted: a pause (a popup, the planner, a join batch) freezes
        // the date, possibly already a day ahead, and a host that hitches now and then loses nothing already counted,
        // so neither a pause nor a hitching host can keep a landing that never comes waiting for ever.
        public bool WaitOverdue(long localNextDayTicks, long nowMs)
        {
            if (disabled || !waiting || hostTicks < localNextDayTicks + WholeDay)
            {
                waitLateMs = 0;
                waitLastMs = -1;
                return false;
            }
            long since = waitLastMs;
            waitLastMs = nowMs;
            if (since >= 0 && nowMs > since && nowMs - hostMovedMs <= HostFrozenMs) waitLateMs += nowMs - since;
            return waitLateMs > ActionLimits.FetchDeadlineMs;
        }
        // A link that may resume (M7): the fetches in flight will never arrive; the day is compared again.
        public void DropFetches()
        {
            fetches = 0;
            comparedDay = -1;
        }

        // A new stream position (AgencyState): nothing of the old one carries over. The statistics do.
        public void Reset()
        {
            EndWait();
            comparedDay = -1;
            fetches = 0;
            holdSince = -1;
            hostTicks = long.MinValue;
            lateSince = -1;
        }

        public string Summary(bool reset)
        {
            string s = holds + " holds, max " + maxHold + " ms, total " + totalHold + " ms";
            if (reset) { holds = 0; maxHold = 0; totalHold = 0; }
            return s;
        }
    }
}
