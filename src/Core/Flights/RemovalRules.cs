namespace SolarExpanseMultiplayer
{
    // The form of a removal list (a flight entry's Removals): what the host's schedule step took, charged to the acting
    // agency only. A client applies the list as given, so a bad row is refused before it reaches the game.
    public static class RemovalRules
    {
        // null, or why the list is not believable: no list, too many rows, a null row, a kind that is neither resource
        // nor money, another agency charged, a money row with a body or a resource, a resource row with no site or no
        // resource, an amount that is not above 0 and at most MaxMoney.
        public static string Problem(RemovalRow[] rows, string agency)
        {
            if (rows == null) return "removals missing";
            if (rows.Length > ActionLimits.MaxRemovals) return "too many removals";
            for (int i = 0; i < rows.Length; i++)
            {
                RemovalRow r = rows[i];
                if (r == null) return "removal " + i + " missing";
                if (r.What != RemovalKinds.Resource && r.What != RemovalKinds.Money)
                    return "removal " + i + " has no kind";
                if (r.Agency == null || r.Agency != agency) return "removal " + i + " charges another agency";
                if (r.What == RemovalKinds.Money)
                {
                    if (r.Body != 0) return "money removal " + i + " names a body";
                    if (r.Resource != null) return "money removal " + i + " names a resource";
                }
                else
                {
                    if (r.Body < 1 || r.Body > ActionLimits.MaxBodyId) return "removal " + i + " has no site";
                    if (!ActionShapes.IsContentId(r.Resource)) return "removal " + i + " has no resource";
                }
                // written so that NaN fails too
                if (!(r.Amount > 0 && r.Amount <= ActionLimits.MaxMoney)) return "removal " + i + " has a bad amount";
            }
            return null;
        }

        // The rows that take something. The game's schedule step always makes its three fuel removals, and one of them
        // is often 0: the second orbit row when one site gives all the fuel, the launch fuel of a flight with no
        // rocket. Taking 0 changes nothing on any game and refunds nothing at a cancel, so such a row is not sent. A
        // negative row adds stock (RowResourcesData.Remove subtracts it), so it is kept and refused by Problem.
        public static RemovalRow[] WithoutEmpty(System.Collections.Generic.IList<RemovalRow> rows)
        {
            var kept = new System.Collections.Generic.List<RemovalRow>();
            if (rows == null) return kept.ToArray();
            foreach (RemovalRow r in rows)
                if (r == null || r.Amount != 0) kept.Add(r);
            return kept.ToArray();
        }

        // The money total, for checks and logs. Rows that are not money are not counted; null rows are skipped.
        public static double Money(RemovalRow[] rows)
        {
            double total = 0;
            if (rows == null) return total;
            foreach (RemovalRow r in rows)
                if (r != null && r.What == RemovalKinds.Money) total += r.Amount;
            return total;
        }
    }
}
