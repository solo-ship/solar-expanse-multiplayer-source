using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Who joins together (Design decisions 8, 9; spec section 1 "Several joiners"): ONE snapshot and ONE pause for everyone who
    // was ready when the batch started. A player who is ready while a batch runs waits for the next one. Each member has its own
    // no-progress deadline AND a total deadline from the start of its batch, then from its header (I2: a joiner
    // that sends a heartbeat stage every 29.9 s never trips the first one, and would hold everyone's pause for ever);
    // one that misses either, fails a check or drops leaves the batch without touching the others, and the batch is Done when every member has loaded or left. The host ends it (End) and only then does the pause lift.
    //
    // Pure and single-threaded: JoinService calls it from the main thread. The idle paths (Expired with nothing due, Start with
    // nobody waiting) allocate nothing (Tasks 9a/9b: 10,000 idle updates, at most 16 bytes); the arrays they return then are one
    // shared empty array, which callers must not expect to be fresh (it has no elements to change).
    public sealed class JoinBatcher
    {
        sealed class Member { public int Player; public long StartMs, LastMs, TotalMs; public bool Loaded; }

        static readonly int[] None = new int[0];

        readonly int noProgressMs, totalMs;
        readonly List<int> waiting = new List<int>();
        readonly List<Member> batch = new List<Member>();
        bool running;

        // noProgressMs: a member with no progress for this long has missed its deadline (JoinLimits.NoProgressMs, 30 s).
        // totalMs: a member still pending this long after its batch started has missed its total deadline, whatever progress it
        // sent (JoinLimits.JoinerTotalMs, 300 s); it may not be shorter than noProgressMs. HeaderSent restarts one
        // member's total from its header, with the time its snapshot's size needs.
        public JoinBatcher(int noProgressMs, int totalMs)
        {
            if (noProgressMs < 1) throw new ArgumentOutOfRangeException("noProgressMs", "the deadline must be positive");
            if (totalMs < noProgressMs) throw new ArgumentOutOfRangeException("totalMs", "the total deadline must be positive and at least the no-progress one");
            this.noProgressMs = noProgressMs;
            this.totalMs = totalMs;
        }

        // The player passed the content check and the claim (or is on a full rejoin): it joins the next batch. Queuing a player
        // who is already waiting or is a member of the running batch changes nothing. A waiting player has no deadline here (it is
        // told to wait, "Waiting for the current join to finish"), so the time is not kept; the list is in arrival order.
        public void Queue(int player, long nowMs)
        {
            if (Find(player) >= 0 || waiting.Contains(player)) return;
            waiting.Add(player);
        }

        public bool Running { get { return running; } }

        // Everyone queued joins this batch, in arrival order, each with its deadline running from nowMs. Nothing happens (an empty
        // array) when a batch is already running or nobody is waiting.
        public int[] Start(long nowMs)
        {
            if (running || waiting.Count == 0) return None;
            var ids = new int[waiting.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = waiting[i];
                batch.Add(new Member { Player = waiting[i], TotalMs = totalMs, StartMs = nowMs, LastMs = nowMs });
            }
            waiting.Clear();
            running = true;
            return ids;
        }

        // An ack or a stage from a member: its deadline restarts. Never moves a deadline backwards. (A loaded member has no deadline
        // to move.)
        public void Progress(int player, long nowMs)
        {
            int i = Find(player);
            if (i < 0) return;
            Member m = batch[i];
            if (nowMs > m.LastMs) m.LastMs = nowMs;
        }

        // The member's header went out at nowMs: from then its total deadline is totalMs (JoinLimits.TotalMsFor the
        // header's compressed size, so a big snapshot on a slow link is not cut off), counted from the header as the
        // joiner counts it from receiving it. Never shorter than the no-progress deadline; never moves the start
        // backwards. Ignored for a player who is not in the batch.
        public void HeaderSent(int player, int totalMs, long nowMs)
        {
            if (totalMs < noProgressMs)
                throw new ArgumentOutOfRangeException("totalMs", "the total must be at least the no-progress deadline");
            int i = Find(player);
            if (i < 0) return;
            batch[i].TotalMs = totalMs;
            if (nowMs > batch[i].StartMs) batch[i].StartMs = nowMs;
        }

        // The member has loaded. Ignored for a player who is not in the batch (it cannot come back by loading).
        public void Loaded(int player)
        {
            int i = Find(player);
            if (i >= 0) batch[i].Loaded = true;
        }

        // A check failed, the link dropped, or a deadline passed: the player is out of this batch (or off the waiting list). A
        // member that had loaded is removed too, so that it can queue again for a full rejoin.
        public void Fail(int player)
        {
            int i = Find(player);
            if (i >= 0) batch.RemoveAt(i);
            waiting.Remove(player);
        }

        // The members that have not loaded and either have made no progress for noProgressMs or started their batch totalMs ago, in
        // batch order. Each is reported once: it leaves the batch here, as if Fail had been called, so it can never hold the pause.
        // A clock that steps back reports nobody.
        public int[] Expired(long nowMs)
        {
            int due = 0;
            for (int i = 0; i < batch.Count; i++)
                if (IsDue(batch[i], nowMs)) due++;
            if (due == 0) return None;
            var ids = new int[due];
            int n = 0;
            for (int i = 0; i < batch.Count; )
            {
                if (IsDue(batch[i], nowMs)) { ids[n++] = batch[i].Player; batch.RemoveAt(i); }
                else i++;
            }
            return ids;
        }

        bool IsDue(Member m, long nowMs)
        {
            // m.LastMs <= nowMs - noProgressMs, written so a long elapsed time cannot overflow
            return !m.Loaded && (m.LastMs <= nowMs - noProgressMs || m.StartMs <= nowMs - m.TotalMs);
        }

        // A batch is running and every member has loaded or left (a batch whose members all left is Done at once).
        public bool Done
        {
            get
            {
                if (!running) return false;
                for (int i = 0; i < batch.Count; i++)
                    if (!batch[i].Loaded) return false;
                return true;
            }
        }

        // The pause may lift. Anyone still pending is dropped from the batch and returned (in batch order) so the caller can tell
        // them and clear their joining pause; members that loaded, failed or expired are not. The waiting list is untouched, and the
        // next Start takes it. An empty answer is the shared empty array.
        public int[] End()
        {
            int pending = 0;
            for (int i = 0; i < batch.Count; i++)
                if (!batch[i].Loaded) pending++;
            int[] dropped = None;
            if (pending > 0)
            {
                dropped = new int[pending];
                int n = 0;
                for (int i = 0; i < batch.Count; i++)
                    if (!batch[i].Loaded) dropped[n++] = batch[i].Player;
            }
            running = false;
            batch.Clear();
            return dropped;
        }

        public int Waiting { get { return waiting.Count; } }

        // A member of the running batch, loaded or not (a member that failed, expired or was ended is not).
        public bool InBatch(int player) { return Find(player) >= 0; }

        public bool Queued(int player) { return waiting.Contains(player); }

        int Find(int player)
        {
            for (int i = 0; i < batch.Count; i++)
                if (batch[i].Player == player) return i;
            return -1;
        }
    }
}
