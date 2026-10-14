using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The seat facts the ownership check needs. M8b (several holders for one agency) changes LedgerSeats, never
    // AgencyAccess or this interface.
    public interface ISeats
    {
        bool Holds(int player, string agency);   // the player holds a seat on that agency (player 0 = the host)
        bool InGame(int player);                 // loaded and connected (JoinService.InGame; always true for player 0)
        int[] HoldersOf(string agency);
    }

    // Seats as the AgencyLedger keeps them. inGame says whether a joiner is loaded and connected right now.
    public sealed class LedgerSeats : ISeats
    {
        readonly AgencyLedger ledger;
        readonly Func<int, bool> inGame;

        public LedgerSeats(AgencyLedger ledger, Func<int, bool> inGame)
        {
            if (ledger == null) throw new ArgumentNullException("ledger");
            if (inGame == null) throw new ArgumentNullException("inGame");
            this.ledger = ledger;
            this.inGame = inGame;
        }

        public bool Holds(int player, string agency)
        {
            return !string.IsNullOrEmpty(agency) && ledger.AgencyOf(player) == agency;
        }

        public bool InGame(int player) { return player == 0 || inGame(player); }

        public int[] HoldersOf(string agency)
        {
            var found = new List<int>();
            if (string.IsNullOrEmpty(agency)) return found.ToArray();
            foreach (AgencySeat s in ledger.Seats(null))
                if (s.Agency == agency) found.Add(s.Player);
            return found.ToArray();
        }
    }

    // THE ownership check. Every player action passes MayAct on the host before any other check, and nothing else
    // reads ISeats.Holds (a source test pins it), so widening who may act for an agency is one change.
    public static class AgencyAccess
    {
        // 0 = may act; otherwise a RefuseCodes value: NotInGame (the player is not loaded and connected), AgencyAway
        // (the agency is held but none of its holders is in game), or NotYourAgency (anything else: another player's
        // agency, an unheld one, the World Government, an unknown or empty id). The checks run in that order.
        public static int MayAct(ISeats seats, int player, string agency)
        {
            if (seats == null) throw new ArgumentNullException("seats");
            if (!seats.InGame(player)) return RefuseCodes.NotInGame;
            if (seats.Holds(player, agency)) return 0;
            return Away(seats, agency) ? RefuseCodes.AgencyAway : RefuseCodes.NotYourAgency;
        }

        // A held agency none of whose holders is in game carries on without a player (D6): its queues run, nothing new
        // is ordered. An agency nobody holds is not "away": it was never a player's.
        public static bool Away(ISeats seats, string agency)
        {
            if (seats == null) throw new ArgumentNullException("seats");
            int[] holders = seats.HoldersOf(agency);
            if (holders == null || holders.Length == 0) return false;
            foreach (int p in holders)
                if (seats.InGame(p)) return false;
            return true;
        }
    }
}
