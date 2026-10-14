using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // A ship's landing builds the facilities its type carries (Spacecraft.CreateFacility, productionCountID++ each)
    // on every game at that game's own moment, while a rival's or a person's entry spends ids from the same counter at
    // the host's moment. Where the two fall in a different order on a client than on the host, the landed facility
    // gets another id on each game and the facilities can never be healed. Both orders are mended here:
    //   * the host landed first, the client applied the entry first: the entry's counter row says so (its "before"
    //     is ahead of the client's own counter). Those ids are kept (a gap) for the entry's host time, and a landing
    //     here that arrived by then takes its ids from them, as the host's did.
    //   * the client landed first, the host applied the entry first: the row's "before" is behind the client's own
    //     counter. When every id between was taken by this game's own landings, those facilities are given the ids
    //     the host gives them after the entry (its "after" on), and the counter follows them.
    // One instance per client (CounterPass); main thread.
    public sealed class ItemIdGaps
    {
        sealed class Gap { public string Agency; public long From, To, Until; }
        sealed class Own { public string Agency; public long Id; public object Item; }

        // a landing's few facilities each; more than this many unconfirmed is a stream gone wrong, never a backlog
        const int MaxOwn = 256;
        const long DayTicks = 864000000000L;

        readonly List<Gap> gaps = new List<Gap>();
        readonly List<Own> own = new List<Own>();

        public int Count { get { return gaps.Count; } }
        public int OwnCount { get { return own.Count; } }

        // An entry's item counter row as this game applies it: mine is this game's counter before the row is written,
        // hostBefore the host's, untilTicks the game time the host applied the entry at. Ids below the host's
        // "before" are the host's own from here on: this game's landings that took them are no longer ahead.
        public void Note(string agency, long mine, long hostBefore, long untilTicks)
        {
            if (agency == null) return;
            own.RemoveAll(o => o.Agency == agency && o.Id < hostBefore);
            if (mine >= hostBefore || untilTicks == long.MinValue) return;
            gaps.Add(new Gap { Agency = agency, From = mine, To = hostBefore, Until = untilTicks });
        }

        // The first id of `need` for a landing of this agency's ship, taken from the oldest gap still open. The landing
        // is placed at its arrival (the same on every game), not at this game's step: a client catching up crosses a
        // day in a few long steps, so its step time can be past the entry's host time for a ship that landed well
        // before it on the host. The step's time stands in when the arrival is unknown (long.MinValue), later than
        // the step, or more than a day before it (a ship moved long after its mission ended: the day gate never lets
        // a step run on past a day). A gap whose entry time is before that is gone (the host's landing came after its
        // entry, so this one cannot be it). False when there is none, or it is too narrow (the ids were spent on
        // something else).
        public bool Take(string agency, int need, long arriveTicks, long stepTicks, out long from)
        {
            from = -1;
            bool arrival = arriveTicks != long.MinValue && arriveTicks <= stepTicks
                && stepTicks - arriveTicks <= DayTicks;
            long at = arrival ? arriveTicks : stepTicks;
            gaps.RemoveAll(g => g.Until < at);
            if (need <= 0) return false;
            foreach (Gap g in gaps)
            {
                if (g.Agency != agency) continue;
                if (g.To - g.From < need) return false;
                from = g.From;
                g.From += need;
                if (g.From >= g.To) gaps.Remove(g);
                return true;
            }
            return false;
        }

        // A landing here took ids from this game's own counter, one per item (the facility objects, in id order from
        // `from`). Kept until an entry's "before" passes them, so an entry the host applied before its own landing can
        // move them on (Ahead).
        public void Landed(string agency, long from, IList<object> items)
        {
            if (agency == null || items == null) return;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null) own.Add(new Own { Agency = agency, Id = from + i, Item = items[i] });
            if (own.Count > MaxOwn) own.RemoveRange(0, own.Count - MaxOwn);
        }

        // An entry whose host "before" is behind this game's counter (mine): when every id in [hostBefore, mine) is
        // one of this game's own landings, the host ran the entry first and gives those items the ids from its
        // "after" on, in the same order. Moves lists each item with its new id, highest first (the shift is upward,
        // so no item takes an id another still holds); the records follow. False (nothing moved): an id between was
        // spent on something else, which the facilities compare must find.
        public bool Ahead(string agency, long mine, long hostBefore, long hostAfter,
            out List<KeyValuePair<object, long>> moves)
        {
            moves = null;
            if (agency == null || mine <= hostBefore || hostAfter < hostBefore) return false;
            var found = new List<Own>();
            for (long id = hostBefore; id < mine; id++)
            {
                Own o = own.Find(x => x.Agency == agency && x.Id == id);
                if (o == null) return false;
                found.Add(o);
            }
            moves = new List<KeyValuePair<object, long>>();
            long shift = hostAfter - hostBefore;
            for (int i = found.Count - 1; i >= 0; i--)
            {
                found[i].Id += shift;
                moves.Add(new KeyValuePair<object, long>(found[i].Item, found[i].Id));
            }
            return true;
        }

        public void Clear()
        {
            gaps.Clear();
            own.Clear();
        }
    }
}
