using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The human set after the host loads a save mid-session (the host may now play another agency, and the save may
    // lack an agency a joiner holds). The truth is the ledger's seats; the set follows them.
    public sealed class HumanReconcile
    {
        public readonly List<string> Drop = new List<string>();      // human, but no player holds it any more
        public readonly List<string> Add = new List<string>();       // held by a player, not human yet
        public readonly List<int> Unseated = new List<int>();        // joiners whose agency the save does not have

        // current: the set now; ledger: who holds what; inSave: the loaded game has that agency.
        public static HumanReconcile Plan(string[] current, AgencyLedger ledger, Func<string, bool> inSave)
        {
            return Plan(current, ledger.Seats(null), inSave);
        }

        public static HumanReconcile Plan(string[] current, AgencySeat[] seats, Func<string, bool> inSave)
        {
            var plan = new HumanReconcile();
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (AgencySeat s in seats)
            {
                if (s == null || string.IsNullOrEmpty(s.Agency)) continue;
                if (s.Player > 0 && !inSave(s.Agency)) { plan.Unseated.Add(s.Player); continue; }
                held.Add(s.Agency);
            }
            foreach (string a in current) if (!held.Contains(a)) plan.Drop.Add(a);
            var have = new HashSet<string>(current, StringComparer.Ordinal);
            foreach (string a in held) if (!have.Contains(a)) plan.Add.Add(a);
            plan.Add.Sort(StringComparer.Ordinal);
            return plan;
        }
    }
}
