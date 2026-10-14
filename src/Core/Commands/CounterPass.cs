using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Where a client's counters live: the game side reads and writes them (CounterSync). Each answers null, or why it
    // cannot (a counter this game cannot read or set is out of step, never a guess).
    public interface ICounterStore
    {
        string Read(CounterRow r, out long value);
        string Write(CounterRow r, long value);
        // An item this game made (a landed ship's facility, ItemIdGaps) given another item id.
        string Renumber(object item, long id);
    }

    // The counters around a client's apply (DD3, DD11): each counter an entry names is set to the host's "before",
    // the entry is applied, and each must then stand at the host's "after"; a state's counters are set outright. The
    // planted faults "counter" and "counterafter" act here, so the checks they prove are the real ones.
    public sealed class CounterPass
    {
        readonly ICounterStore store;
        bool skipOne, spoilOne;
        CounterRow printSpoil;   // the counter fault's row, spoiled at its agency's next builds print
        // an item counter row whose landings here were moved past the entry: the counter to stand at after its check
        CounterRow shiftRow;
        long shiftTo;

        public CounterPass(ICounterStore store)
        {
            if (store == null) throw new System.ArgumentNullException("store");
            this.store = store;
        }

        // -semp-command-fault counter: the item counter the next apply carries is noted, and set one past this game's
        // value when its agency's builds print (which carries the counter) is next taken for the day's compare, so
        // the print, not an after-check, must find the difference and the restore mend it. At the print, after the
        // day's last apply: an apply later the same day sets the counter to the host's "before" again, so a counter
        // spoiled at its own apply showed nothing; and one wrong before an apply would give the new item another
        // id, which no restore mends.
        public void SkipNext() { skipOne = true; }

        // -semp-command-fault counterafter: after the next apply that carries a counter, that counter is set one past
        // the host's value before the check, which must then find it.
        public void SpoilNext() { spoilOne = true; }

        // The item ids the host spent ahead of an entry on landings this game has still to run (ItemIdGaps).
        public readonly ItemIdGaps Gaps = new ItemIdGaps();

        // Before the apply: every counter to the host's "before". Null, or why not.
        public string Before(CounterRow[] rows) { return Before(rows, long.MinValue); }

        // atTicks: the game time the host applied the entry at; an item counter this game holds below the host's
        // "before" is noted as a gap until then (long.MinValue: none is noted).
        public string Before(CounterRow[] rows, long atTicks)
        {
            string bad = CounterRules.Problem(rows);
            if (bad != null) return bad;
            shiftRow = null;
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                CounterRow r = rows[i];
                if (r.Which == CounterKind.ProductionCountId && atTicks != long.MinValue)
                {
                    long mine;
                    string unread = store.Read(r, out mine);
                    if (unread != null) return unread;
                    string moved = MoveAhead(r, mine);
                    if (moved != null) return moved;
                    Gaps.Note(r.Agency, mine, r.Before, atTicks);
                }
                if (skipOne && r.Which == CounterKind.ProductionCountId)
                {
                    skipOne = false;
                    printSpoil = r;
                    Log.Warn("command: fault counter: " + CounterRules.Name(r) + " to be set past the host's at its "
                        + "next builds print");
                }
                string why = store.Write(r, r.Before);
                if (why != null) return why;
            }
            return null;
        }

        // The counter fault, at a print about to be taken: when it is of the noted agency's builds, that agency's item
        // counter is set one past what this game has (the host's, after every apply of the day). Null, or why not.
        public string BeforeBuildsPrint(string agency)
        {
            CounterRow r = printSpoil;
            if (r == null || r.Agency != agency) return null;
            printSpoil = null;
            long now;
            string why = store.Read(r, out now);
            if (why != null) return why;
            Log.Warn("command: fault counter: " + CounterRules.Name(r) + " set past the host's at the day's print");
            return store.Write(r, now + 1);
        }

        // This game's own landings that took the ids between the host's "before" and this game's counter: the host
        // applied the entry first, so they are moved to the ids the host gave them after it (ItemIdGaps.Ahead).
        string MoveAhead(CounterRow r, long mine)
        {
            List<KeyValuePair<object, long>> moves;
            if (mine <= r.Before || !Gaps.Ahead(r.Agency, mine, r.Before, r.After, out moves)) return null;
            foreach (KeyValuePair<object, long> m in moves)
            {
                string why = store.Renumber(m.Key, m.Value);
                if (why != null) return why;
            }
            shiftRow = r;
            shiftTo = r.After + (mine - r.Before);
            Log.Info("command: " + moves.Count + " item(s) of " + r.Agency + " landed here ahead of an entry the host "
                + "applied first move to ids from " + r.After + "; the counter goes to " + shiftTo + " after it");
            return null;
        }

        // After the apply: every counter at the host's "after". Null, or what differs.
        public string After(CounterRow[] rows)
        {
            if (spoilOne && rows != null && rows.Length > 0)
            {
                spoilOne = false;
                Log.Warn("command: fault counterafter: " + CounterRules.Name(rows[0]) + " set past the host's");
                string bad = store.Write(rows[0], rows[0].After + 1);
                if (bad != null) return bad;
            }
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                CounterRow r = rows[i];
                long now;
                string why = store.Read(r, out now);
                if (why != null) return why;
                why = CounterRules.AfterProblem(r, now);
                if (why != null) return why;
                if (ReferenceEquals(r, shiftRow))
                {
                    shiftRow = null;
                    why = store.Write(r, shiftTo);
                    if (why != null) return why;
                }
            }
            return null;
        }

        // A state's counters (AgencyState, a restored site): each to the host's value.
        public string Set(CounterRow[] rows)
        {
            string bad = CounterRules.Problem(rows);
            if (bad != null) return bad;
            Gaps.Clear();   // a state starts the stream again: nothing before it is owed
            shiftRow = null;
            for (int i = 0; rows != null && i < rows.Length; i++)
            {
                string why = store.Write(rows[i], rows[i].After);
                if (why != null) return why;
            }
            return null;
        }
    }
}
