using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // M7 DD-W12: the host's recent stream (entries and DayClosed, in send order), kept for a player that dropped and
    // can come back without a reload. Bounded by game days and bytes; whichever runs out first drops the oldest.
    public sealed class ReplayBuffer
    {
        sealed class Slot { public Message M; public int Bytes; public long Day; public uint Seq; public bool Close; }

        readonly int days, bytes;
        readonly List<Slot> slots = new List<Slot>();
        long total;
        uint lastSeq;
        // Every item after a client's position is held when its seq is at least this: a dropped entry S needs the
        // client to have S; a dropped close of L may be one the client lacks, so it needs L + 1.
        uint droppedThrough;

        public ReplayBuffer(int days, int bytes)
        {
            if (days < 1 || bytes < 1) throw new ArgumentOutOfRangeException("days");
            this.days = days;
            this.bytes = bytes;
        }

        public int Count { get { return slots.Count; } }
        public long Bytes { get { return total; } }
        public uint LastSeq { get { return lastSeq; } }

        // Only entries and DayClosed belong in the stream; anything else is ignored.
        public void Add(Message m, int size, long day)
        {
            var r = m as ActionResult;
            var c = m as DayClosed;
            if (r == null && c == null) return;
            var s = new Slot { M = m, Bytes = size, Day = day, Seq = r != null ? r.Seq : c.LastSeq, Close = r == null };
            if (r != null && r.Seq > lastSeq) lastSeq = r.Seq;
            slots.Add(s);
            total += size;
            // checked after the add, so one item bigger than the cap leaves nothing but itself
            while (slots.Count > 1 && (total > bytes || day - slots[0].Day >= days)) DropFirst();
        }

        void DropFirst()
        {
            Slot s = slots[0];
            slots.RemoveAt(0);
            total -= s.Bytes;
            uint need = s.Close ? s.Seq + 1 : s.Seq;
            if (need > droppedThrough) droppedThrough = need;
        }

        // Still holds everything a client that has applied entry fromSeq is missing.
        public bool Covers(uint fromSeq) { return fromSeq <= lastSeq && fromSeq >= droppedThrough; }

        // The entries after fromSeq and the closes that follow entry fromSeq, in send order.
        public IEnumerable<Message> From(uint fromSeq)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                if (s.Close ? s.Seq >= fromSeq : s.Seq > fromSeq) yield return s.M;
            }
        }

        public long BytesFrom(uint fromSeq)
        {
            long n = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                if (s.Close ? s.Seq >= fromSeq : s.Seq > fromSeq) n += s.Bytes;
            }
            return n;
        }

        // A reset sent to every recipient: a new stream starts at seq 1. (A per-player drop never calls this.)
        public void Reset()
        {
            slots.Clear();
            total = 0;
            lastSeq = 0;
            droppedThrough = 0;
        }
    }
}
