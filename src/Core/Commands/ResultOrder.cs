using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public static class OrderVerdict { public const int Ok = 0, OutOfStep = 1; }

    // What a client keeps of the host's stream: entries in seq order, each waiting for its own day; the host's
    // DayClosed promises; and ObjectData pieces gathered until an object is whole. Every anomaly of a well-formed
    // stream returns OutOfStep (the caller rejoins fully) and ends the stream here until the next Reset: a
    // half-trusted stream is never kept.
    //
    // The host sends entries and closes in one order over one link, so the rules below can be exact, and each one
    // exists so the gate can never wait for something that cannot arrive: a DayClosed names only entries already
    // here, days close one after another, an entry is never for a closed day or one beyond the next, and an object
    // is due only at a point the stream can still reach.
    public sealed class ResultOrder
    {
        sealed class Group
        {
            public long Day; public int Kind; public string Agency; public int Body;
            public uint AfterSeq; public int Pieces, Have;
            public ObjectData[] Got;
        }

        readonly int maxQueued;
        readonly Queue<ActionResult> entries = new Queue<ActionResult>();
        readonly Dictionary<long, DayClosed> closed = new Dictionary<long, DayClosed>();
        readonly List<Group> groups = new List<Group>();
        bool started;
        uint accepted, applied, promised;
        long closedThrough, nextToForget, pieceCount, announced, resetDay;

        public ResultOrder(int maxQueued)
        {
            if (maxQueued < 1) throw new ArgumentOutOfRangeException("maxQueued");
            this.maxQueued = maxQueued;
        }

        public bool Started { get { return started; } }
        public uint LastApplied { get { return applied; } }
        public long ClosedThrough { get { return closedThrough; } }

        // Every piece beyond the first of an object, counted as each object is first heard of and never taken back:
        // the gate stretches its fetch deadline by what was announced since its fetches began, so an object being
        // restored early cannot shorten the time a later one is given.
        public long PiecesAnnounced { get { return announced; } }

        // A new stream position, from AgencyState: the stream continues with nextSeq on `day`. Everything held from
        // an earlier position goes.
        public void Reset(long day, uint nextSeq)
        {
            if (nextSeq < 1) throw new ArgumentException("the stream starts at seq 1 or later", "nextSeq");
            started = true;
            accepted = applied = promised = nextSeq - 1;
            closedThrough = day - 1;
            resetDay = day;
            nextToForget = day;
            pieceCount = 0;
            entries.Clear();
            closed.Clear();
            groups.Clear();
        }

        int Fail(string w, out string why)
        {
            started = false;
            why = w;
            return OrderVerdict.OutOfStep;
        }

        public int Accept(ActionResult r, long localDay, out string why)
        {
            why = null;
            if (!started) return Fail("the stream has not started", out why);
            if (r == null) return Fail("an empty entry", out why);
            if (accepted == uint.MaxValue || r.Seq != accepted + 1)
                return Fail("seq " + r.Seq + " is not the next after " + accepted, out why);
            // days never go back inside the stream: an entry's day is at most one past the last close and never at
            // or before it, and a close always covers every entry so far, so the next entry is for a later day
            if (r.Day < localDay)
                return Fail("day " + r.Day + " has been left (the client is on " + localDay + ")", out why);
            if (r.Day <= closedThrough)
                return Fail("an entry for day " + r.Day + ", closed through " + closedThrough, out why);
            if (r.Day > closedThrough + 1)
                return Fail("an entry for day " + r.Day + " before day " + (r.Day - 1) + " closed", out why);
            if (entries.Count >= maxQueued) return Fail("the entry queue is over " + maxQueued, out why);
            entries.Enqueue(r);
            accepted = r.Seq;
            return OrderVerdict.Ok;
        }

        public int Accept(DayClosed d, out string why)
        {
            why = null;
            if (!started) return Fail("the stream has not started", out why);
            if (d == null) return Fail("an empty close", out why);
            if (d.Day != closedThrough + 1)
                return Fail("day " + d.Day + " closed after " + closedThrough + ": not the next day", out why);
            if (d.LastSeq < promised)
                return Fail("LastSeq " + d.LastSeq + " is below an earlier promise of " + promised, out why);
            // exact, not "at most": a close that named fewer entries than came would leave a later day's entry inside
            // its promise, and one that named more would make the gate wait for entries that never arrive
            if (d.LastSeq != accepted)
                return Fail("LastSeq " + d.LastSeq + " but " + accepted + " entries came", out why);
            if (closed.Count >= maxQueued) return Fail("more than " + maxQueued + " closed days wait", out why);
            closed[d.Day] = d;
            closedThrough = d.Day;
            promised = d.LastSeq;
            return OrderVerdict.Ok;
        }

        public int Accept(ObjectData o, out string why)
        {
            why = null;
            if (!started) return Fail("the stream has not started", out why);
            if (o == null) return Fail("an empty object", out why);
            if (o.Code != ObjectCodes.Ok) return Fail("the host answered the fetch with code " + o.Code, out why);
            // the host's copy is taken at the close, so it belongs exactly after that close's last entry; a day not
            // closed yet, or one the client has already left, has no kept close and cannot be restored
            DayClosed close = ClosedFor(o.Day);
            if (close == null) return Fail("an object of day " + o.Day + ", whose close is no longer kept", out why);
            if (close.LastSeq != o.AfterSeq)
                return Fail("an object after seq " + o.AfterSeq + " but day " + o.Day + " closed at " + close.LastSeq,
                    out why);
            if (o.Pieces > ActionLimits.MaxPieces) return Fail("an object of " + o.Pieces + " pieces", out why);
            // a count below 1 fails here too: no piece number is in range
            if (o.Piece < 0 || o.Piece >= o.Pieces) return Fail("piece " + o.Piece + " of " + o.Pieces, out why);
            if (pieceCount >= maxQueued) return Fail("the piece queue is over " + maxQueued, out why);
            Group g = Find(o.Day, o.Kind, o.Agency, o.Body);
            if (g == null)
            {
                g = new Group { Day = o.Day, Kind = o.Kind, Agency = o.Agency, Body = o.Body, AfterSeq = o.AfterSeq,
                    Pieces = o.Pieces, Got = new ObjectData[o.Pieces] };
                groups.Add(g);
                announced += o.Pieces - 1;
            }
            else if (g.Pieces != o.Pieces)   // the position is the day's close, the same for every piece
                return Fail("pieces of one object disagree about its size", out why);
            if (g.Got[o.Piece] != null) return Fail("piece " + o.Piece + " arrived twice", out why);
            g.Got[o.Piece] = o;
            g.Have++;
            pieceCount++;
            return OrderVerdict.Ok;
        }

        // The host loaded or faulted: this stream is over for every client.
        public int Accept(StreamReset s, out string why)
        {
            return Fail("the host started a new stream", out why);
        }

        Group Find(long day, int kind, string agency, int body)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                Group g = groups[i];
                if (g.Day == day && g.Kind == kind && g.Body == body && g.Agency == agency) return g;
            }
            return null;
        }

        // The next entry, or whole object, that may be applied while the client is on localDay; null when none.
        // An object comes first when it belongs at this point of the stream: it is the host's copy as of the close,
        // so it goes in after the entries of its day and before anything later.
        public Message NextDue(long localDay)
        {
            Forget(localDay);
            for (int i = 0; i < groups.Count; i++)
            {
                Group g = groups[i];
                if (g.Have == g.Pieces && g.Day == localDay && g.AfterSeq == applied) return g.Got[0];
            }
            if (entries.Count > 0)
            {
                ActionResult head = entries.Peek();
                if (head.Day == localDay) return head;
            }
            return null;
        }

        // The whole object for a due ObjectData, in piece order (its first piece is what NextDue returns).
        public ObjectData[] PiecesOf(ObjectData due)
        {
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Got[0] == due && groups[i].Have == groups[i].Pieces) return groups[i].Got;
            return null;
        }

        // True when the client's day is outside the stream (before where it began) or something queued can never be
        // applied because the client is already past its day: the gate should have prevented it, so the caller goes
        // out of step rather than wait.
        public bool Behind(long localDay)
        {
            // one day before the stream's start is still inside it: a snapshot taken while the host re-ran a day event
            // (its handler threw after the close) is on the day the host has closed already (StartedAfter)
            if (localDay < resetDay - 1) return true;
            if (entries.Count > 0 && entries.Peek().Day < localDay) return true;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Day < localDay) return true;
            return false;
        }

        // The stream began after `day`: the host had closed it before the state that started the stream was taken,
        // so the client crosses it with nothing to wait for or compare (its world is the host's copy from after the
        // close).
        public bool StartedAfter(long day) { return started && day < resetDay; }

        // The next entry in the stream, whatever its day, or null when none is queued.
        public ActionResult PeekEntry() { return entries.Count == 0 ? null : entries.Peek(); }

        public int QueuedEntries { get { return entries.Count; } }

        // The index-th queued entry (0 = the next one), or null past the end. No allocation.
        public ActionResult EntryAt(int index)
        {
            int i = 0;
            foreach (ActionResult r in entries)
                if (i++ == index) return r;
            return null;
        }

        // The index (0 = the next entry) of the first queued landing entry (landing true) or checked-launch entry (false)
        // for that mission, or -1. No allocation.
        public int IndexOfEvent(long missionId, bool landing)
        {
            int i = 0;
            foreach (ActionResult r in entries)
            {
                ActionRequest a = r.Action;
                if (a != null && a.ItemId == missionId
                    && (landing ? FlightEvents.IsLanding(a) : FlightEvents.IsLaunch(a))) return i;
                i++;
            }
            return -1;
        }

        // The message NextDue returned has been applied.
        public void Applied(Message m)
        {
            var r = m as ActionResult;
            if (r != null)
            {
                if (entries.Count == 0 || !ReferenceEquals(entries.Peek(), r))
                    throw new InvalidOperationException("only the next entry can be applied");
                entries.Dequeue();
                applied = r.Seq;
                return;
            }
            var od = m as ObjectData;
            if (od == null) throw new ArgumentException("not a stream message", "m");
            for (int i = 0; i < groups.Count; i++)
            {
                Group g = groups[i];
                if (g.Got[0] != od || g.Have != g.Pieces) continue;
                pieceCount -= g.Pieces;
                groups.RemoveAt(i);
                return;
            }
            throw new InvalidOperationException("no whole object to apply");
        }

        // The host's close of `day`, while the client has not yet left that day.
        public DayClosed ClosedFor(long day)
        {
            DayClosed d;
            return closed.TryGetValue(day, out d) ? d : null;
        }

        // A resumed link (M7 DD-W12): the host replays everything from the applied position, so whatever this client held
        // beyond it goes - queued entries, objects, and the closes of days not yet crossed (a close with LastSeq at or
        // above the applied seq is replayed with them; one below it belongs to a day this client has left).
        public void TrimToApplied()
        {
            entries.Clear();
            groups.Clear();
            pieceCount = 0;
            accepted = applied;
            long firstDropped = long.MaxValue;
            var drop = new List<long>();
            foreach (KeyValuePair<long, DayClosed> kv in closed)
                if (kv.Value.LastSeq >= applied) { drop.Add(kv.Key); if (kv.Key < firstDropped) firstDropped = kv.Key; }
            foreach (long d in drop) closed.Remove(d);
            if (firstDropped != long.MaxValue) closedThrough = firstDropped - 1;
            if (promised > applied) promised = applied;
        }

        // Days the client has left are not needed again; closes come one day at a time, so they go one at a time.
        void Forget(long localDay)
        {
            while (closed.Count > 0 && nextToForget < localDay) closed.Remove(nextToForget++);
        }
    }
}
