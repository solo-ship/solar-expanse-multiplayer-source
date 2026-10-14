namespace SolarExpanseMultiplayer
{
    // M7 DD-W7: what one 92 completed, compared between the host's entry and this game's apply. Both sides record it
    // the same way (ContractRule.BeginChain/TakeChain around the root MarkAsComplete: every objective and contract the
    // call completed, in the order the game did it), so any difference means the games had parted before the entry.
    public static class ChainCheck
    {
        // null = the same rows in the same order; else what differs. A missing chain is an empty one.
        public static string Differs(ChainRow[] host, ChainRow[] mine)
        {
            int nh = host == null ? 0 : host.Length, nm = mine == null ? 0 : mine.Length;
            for (int i = 0; i < nh && i < nm; i++)
                if (!Same(host[i], mine[i]))
                    return "chain row " + i + " is " + Text(mine[i]) + " here, " + Text(host[i]) + " on the host";
            if (nh != nm) return "the chain has " + nm + " rows here, " + nh + " on the host";
            return null;
        }

        static bool Same(ChainRow a, ChainRow b)
        {
            if (a == null || b == null) return a == b;
            if (a.Agency != b.Agency || a.Finished != b.Finished) return false;
            if (a.Done == null || b.Done == null) return a.Done == b.Done;
            return a.Done.Contract == b.Done.Contract && a.Done.Objective == b.Done.Objective;
        }

        static string Text(ChainRow r)
        {
            if (r == null || r.Done == null) return "empty";
            return r.Agency + " " + r.Done.Contract + (r.Finished ? " finished" : " objective " + r.Done.Objective);
        }
    }
}
