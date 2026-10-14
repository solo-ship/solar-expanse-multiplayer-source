using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Z-1: what an acceptance's schedule step removed, made or not. The step's chained removals stay made when a later
    // one fails (and MoneyController.RemoveMoney deducts even when it then returns false), so a refusal must give back
    // exactly what was taken, newest first. Task 9 feeds it from prefixes on the game's removal methods.
    public sealed class RemovalJournal
    {
        sealed class Entry { public RemovalRow Row; public bool Made; }

        readonly List<Entry> entries = new List<Entry>();

        // made: the amount really left the stock or the purse (for money, even when the game then answered false).
        public void Record(int what, string agency, int body, string resource, double amount, bool made)
        {
            if (what != RemovalKinds.Resource && what != RemovalKinds.Money)
                throw new ArgumentException("not a removal kind", "what");
            // a NaN or negative amount would poison the purse when added back
            if (double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0)
                throw new ArgumentException("an amount is finite and not negative", "amount");
            var row = new RemovalRow
            {
                What = what, Agency = agency, Body = body, Resource = resource, Amount = amount
            };
            entries.Add(new Entry { Row = row, Made = made });
        }

        // Every made removal, newest first, as amounts to add back exactly. The journal is emptied, so a second
        // rollback cannot add the same amounts again.
        public IList<RemovalRow> Rollback()
        {
            var back = new List<RemovalRow>();
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Made) back.Add(entries[i].Row);
            entries.Clear();
            return back;
        }

        // Every row recorded, made or not.
        public int Count { get { return entries.Count; } }

        public void Clear() { entries.Clear(); }
    }
}
