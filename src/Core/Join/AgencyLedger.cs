using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Which human holds which agency (Design decision 8; spec section 1 "Several joiners"). Pure and single-threaded: the host's
    // JoinService calls it from the main thread.
    //
    // First come, first served. A claim is refused before any game data moves, with the reason, and the joiner may try another
    // agency. A seat is held from a successful claim; it is released when its joiner never loads, and once the player has loaded
    // it stays reserved for the session, so the same player's rejoin (quick or full) gets the same agency back. At most
    // maxPlayers humans in all, the host included (JoinLimits.MaxPlayers = 5).
    //
    // Player 0 is the host. The host's seat exists once SetPlayable has been given its agency.
    //
    // ConfigFor (Task 8b) is built on what the rest of this class exposes and keeps no state of its own.
    public sealed class AgencyLedger
    {
        sealed class Seat { public string Agency; public bool Loaded; }

        readonly int maxPlayers;
        readonly HashSet<string> playable = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> unavailable = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<int, Seat> joiners = new Dictionary<int, Seat>();
        string hostAgency;

        // maxPlayers counts the host: 5 means the host and four joiners.
        public AgencyLedger(int maxPlayers)
        {
            if (maxPlayers < 1) throw new ArgumentOutOfRangeException("maxPlayers", "a game has at least its host");
            this.maxPlayers = maxPlayers;
        }

        // The host's active, enabled, non-World-Government agencies, and the host's own agency (player 0). Claims already made
        // are kept whatever the new list says. Returns false (and changes nothing) when the host's agency is already held by a
        // joiner: an agency is never seated twice (M3), and the caller treats it as a bug in the host's setup.
        public bool SetPlayable(IList<string> agencies, string hostAgency)
        {
            return SetPlayable(agencies, hostAgency, null);
        }

        // As above, plus the agencies the game has but nobody may play (the World Government, an inactive or disabled one), so a
        // refusal can say "not playable" instead of "no such agency". An id on both lists is NOT playable (M1, fail-safe: the
        // host's word that an agency is unavailable wins).
        public bool SetPlayable(IList<string> agencies, string hostAgency, IList<string> notPlayable)
        {
            string host = string.IsNullOrEmpty(hostAgency) ? null : hostAgency;
            if (host != null)
                foreach (KeyValuePair<int, Seat> kv in joiners)
                    if (kv.Value.Agency == host)
                    {
                        Log.Warn("agency ledger: the host's agency " + TextSanitiser.OneLine(host) + " is already held by player " + kv.Key + "; the list was not changed");
                        return false;
                    }
            playable.Clear();
            unavailable.Clear();
            if (notPlayable != null)
                foreach (string a in notPlayable)
                    if (!string.IsNullOrEmpty(a)) unavailable.Add(a);
            if (agencies != null)
                foreach (string a in agencies)
                    if (!string.IsNullOrEmpty(a) && !unavailable.Contains(a)) playable.Add(a);
            this.hostAgency = host;
            return true;
        }

        // 0 = the player now holds the agency (a repeat of its own claim, or a rejoin with its own agency, is 0 too); otherwise a
        // JoinCodes value. The order of the checks: no usable id or player; a player that already holds a seat; the table being
        // full; the agency being held by anyone (the host included); the id being unknown; the agency not being playable.
        public int TryClaim(int player, string agency)
        {
            if (string.IsNullOrEmpty(agency) || player < 1 || player > HostGate.MaxPlayerIds) return JoinCodes.NoAgency;
            Seat mine;
            if (joiners.TryGetValue(player, out mine))
                return mine.Agency == agency ? 0 : JoinCodes.AgencyTaken;
            if (Humans >= maxPlayers) return JoinCodes.Full;
            if (HeldBySomeone(agency)) return JoinCodes.AgencyTaken;
            if (!playable.Contains(agency)) return unavailable.Contains(agency) ? JoinCodes.AgencyNotPlayable : JoinCodes.AgencyUnknown;
            joiners[player] = new Seat { Agency = agency };
            return 0;
        }

        bool HeldBySomeone(string agency)
        {
            if (agency == hostAgency) return true;
            foreach (KeyValuePair<int, Seat> kv in joiners)
                if (kv.Value.Agency == agency) return true;
            return false;
        }

        // null if the player holds none. Player 0 is the host.
        public string AgencyOf(int player)
        {
            if (player == 0) return hostAgency;
            Seat s;
            return joiners.TryGetValue(player, out s) ? s.Agency : null;
        }

        // The joiner never loaded: the seat and the agency are free again. A player that has loaded keeps both (a failed full
        // rejoin must not give its agency away), and the host's seat is never released.
        public void Release(int player)
        {
            Seat s;
            if (joiners.TryGetValue(player, out s) && !s.Loaded) joiners.Remove(player);
        }

        // The host removed the player: its seat and agency are free again, loaded or not.
        public void Forget(int player)
        {
            if (player > 0) joiners.Remove(player);
        }

        // The player has loaded: its agency is reserved for it for the rest of the session.
        public void InGame(int player)
        {
            Seat s;
            if (joiners.TryGetValue(player, out s)) s.Loaded = true;
        }

        // The host and every joiner that holds a seat, loaded or not (the header lists every human agency), the host first and the
        // joiners by player id. nameOf may be null, and may return null; names are cleaned and capped at 24 characters.
        public AgencySeat[] Seats(Func<int, string> nameOf)
        {
            var ids = new List<int>(joiners.Keys);
            ids.Sort();
            var seats = new List<AgencySeat>(ids.Count + 1);
            if (hostAgency != null) seats.Add(MakeSeat(0, hostAgency, nameOf));
            foreach (int id in ids) seats.Add(MakeSeat(id, joiners[id].Agency, nameOf));
            return seats.ToArray();
        }

        static AgencySeat MakeSeat(int player, string agency, Func<int, string> nameOf)
        {
            string name = nameOf == null ? null : TextSanitiser.Clean(nameOf(player), 24, null);
            return new AgencySeat { Player = player, Agency = agency, Name = name };
        }

        // The start configuration this joiner loads with (Design decision 12, load mode A, signed off as I-10): the host's, but with
        // Company = the joiner's own agency and the rivals the host's own minus that agency, then the host's agency and every other
        // human's (Seats lists the host first, then the joiners by player id), each once. It depends only on the seats, so a full
        // rejoin gets an equal config again. A new object with a new rival array: the host's config is never changed or shared.
        // null when this player has no seat (the host, player 0, included: its config is IJoinHostWorld.HostConfig) or the host's own
        // agency is not known, as no header can be made then (HeaderRules wants the host's seat).
        public StartConfig ConfigFor(int player, StartConfig host)
        {
            if (host == null) throw new ArgumentNullException("host");
            string company = player < 1 ? null : AgencyOf(player);
            if (company == null || hostAgency == null) return null;
            var rivals = new List<string>();
            if (host.Rivals != null)
                foreach (string r in host.Rivals) AddRival(rivals, r, company);
            foreach (AgencySeat s in Seats(null)) AddRival(rivals, s.Agency, company);
            return new StartConfig
            {
                Company = company, Rivals = rivals.ToArray(), Difficulty = host.Difficulty, PlanetarySystem = host.PlanetarySystem,
                Epoch = host.Epoch, SetupAis = host.SetupAis, GameplayId = host.GameplayId,
            };
        }

        static void AddRival(List<string> rivals, string agency, string company)
        {
            if (string.IsNullOrEmpty(agency) || agency == company || rivals.Contains(agency)) return;
            rivals.Add(agency);
        }

        // The humans in the game: the host (always, even before its agency is known: the cap of maxPlayers must hold regardless, M2)
        // and every joiner holding a seat.
        public int Humans { get { return 1 + joiners.Count; } }
    }
}
