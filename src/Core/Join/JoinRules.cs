using System;

namespace SolarExpanseMultiplayer
{
    // TimeController's six private boundary fields (the game's TimeController), in ticks. The host sends them in the JoinHeader and the
    // joiner writes them once, in the save-loaded handler, after BoundaryRules.Check (Design decision 12 step 2). Without them the
    // joiner's boundaries are its own load moment and its day events fire at a different game time than the host's (S4's
    // negative control: 10 h 18 m off).
    public struct DayBoundaries { public long LastDay, NextDay, LastMonth, NextMonth, LastYear, NextYear; }

    // The sanity rules on those six values (plan "Interfaces, Rules, ledger, batcher"; docs/research/join.md S4). They stop a bad
    // or hostile header from setting the joiner's clock keys to anything: every value is a real date, the last-keys are near the
    // header's date, the keys are in order, and each span is no longer than the calendar allows. All arithmetic is on values already range-checked
    // to the calendar, so no subtraction can overflow.
    public static class BoundaryRules
    {
        const long Day = TimeSpan.TicksPerDay;
        const long MaxDaySpan = Day, MaxMonthSpan = 31 * Day, MaxYearSpan = 366 * Day;
        // How far behind the date each last-key may be (M6): the day key 2 days, the month key 32, the year key 367, a day more than
        // the longest span of each kind. The real S4 snapshots sit at 0.4, 9.3 and 100.4 days (day 100.4) and 0.0, 3.0 and 33.9 (day 400).
        const long DayWindow = 2 * Day, MonthWindow = 32 * Day, YearWindow = 367 * Day;

        public static DayBoundaries Of(JoinHeader h)
        {
            if (h == null) throw new ArgumentNullException("h");
            return new DayBoundaries
            {
                LastDay = h.LastDay, NextDay = h.NextDay, LastMonth = h.LastMonth, NextMonth = h.NextMonth,
                LastYear = h.LastYear, NextYear = h.NextYear,
            };
        }

        // null = ok; otherwise the first rule that failed, in plain words for the log:
        //   every date and key is a real date (1..DateTime.MaxValue.Ticks);
        //   OriginTicks <= date, and NextDay - Origin is a whole number of days (the day keys are Origin + N days);
        //   lastDay, lastMonth and lastYear are not after the date (a next-key MAY be before it: the date can have run past a
        //   key the game still owes an event for, a backlog, which the spans and the windows below keep bounded);
        //   each last-key is strictly before its next-key;
        //   nextDay - lastDay <= 1 day, nextMonth - lastMonth <= 31 days, nextYear - lastYear <= 366 days;
        //   lastDay is at most 2 days behind the date, lastMonth 32 days, lastYear 367 days.
        // The next-keys need no window of their own: each is after its last-key (so no further behind the date) and at most one
        // span past it (so no further ahead of the date than that span).
        public static string Check(long dateTicks, long originTicks, DayBoundaries b)
        {
            string e = Real(dateTicks, "date") ?? Real(originTicks, "origin") ?? Real(b.LastDay, "lastDay") ?? Real(b.NextDay, "nextDay")
                ?? Real(b.LastMonth, "lastMonth") ?? Real(b.NextMonth, "nextMonth") ?? Real(b.LastYear, "lastYear") ?? Real(b.NextYear, "nextYear");
            if (e != null) return e;
            if (originTicks > dateTicks) return "origin is after the date";
            if (b.LastDay > dateTicks) return "lastDay is after the date";
            if (b.LastMonth > dateTicks) return "lastMonth is after the date";
            if (b.LastYear > dateTicks) return "lastYear is after the date";
            if (b.LastDay >= b.NextDay) return "lastDay is not before nextDay";
            if (b.LastMonth >= b.NextMonth) return "lastMonth is not before nextMonth";
            if (b.LastYear >= b.NextYear) return "lastYear is not before nextYear";
            if (b.NextDay - b.LastDay > MaxDaySpan) return "nextDay is more than a day after lastDay";
            if (b.NextMonth - b.LastMonth > MaxMonthSpan) return "nextMonth is more than 31 days after lastMonth";
            if (b.NextYear - b.LastYear > MaxYearSpan) return "nextYear is more than 366 days after lastYear";
            if (dateTicks - b.LastDay > DayWindow) return "lastDay is more than 2 days behind the date";
            if (dateTicks - b.LastMonth > MonthWindow) return "lastMonth is more than 32 days behind the date";
            if (dateTicks - b.LastYear > YearWindow) return "lastYear is more than 367 days behind the date";
            if ((b.NextDay - originTicks) % Day != 0) return "nextDay is not a whole number of days from the origin";
            return null;
        }

        // (date - origin) mod one day, in days: 0 on a day key, 0.4 at the -semp-pause-at-day moment, just under 1 before the next
        // key. Join-compare uses it to refuse a join too close to a boundary (I-11). A date before the origin wraps into 0..1.
        public static double DayFraction(long dateTicks, long originTicks)
        {
            long rem = (dateTicks - originTicks) % Day;
            if (rem < 0) rem += Day;
            return (double)rem / Day;
        }

        // The one text form of a day fraction in the logs. Host and joiner must print it alike: join-compare reads both
        // boundary lines and compares the fraction to 1e-6, so a shorter form on one side reads as a real difference.
        public static string FractionText(double fraction)
        {
            return fraction.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string Real(long ticks, string what)
        {
            return ticks < 1 || ticks > DateTime.MaxValue.Ticks ? what + " is not a date" : null;
        }
    }

    // The joiner's checks on a JoinHeader that need no game (plan "Interfaces, Rules, ledger, batcher"): sizes, counts, seats and the
    // shape of the start configuration. The wire decode has already run JoinHeader.Validate; this does not rely on it (the host's
    // own header never went through a decode), and it adds what only the joiner knows: its own save version and player id. The
    // dates are BoundaryRules' and are not judged here, so each failure keeps its own JoinCodes value (HeaderInvalid,
    // BoundariesInvalid). null = ok.
    public static class HeaderRules
    {
        public const int MaxDifficulty = 2;
        public static string Check(JoinHeader h, string ownSaveVersion, int ownPlayer)
        {
            if (h == null) return "the header is missing";
            if (h.Format != 1) return "the header format is not 1";
            // the host's version went through ClipGameVersion and the sanitiser before it travelled; compare with ours the same way
            if (ownSaveVersion == null || h.SaveVersion != TextSanitiser.Clean(ModInfo.ClipGameVersion(ownSaveVersion), 32, "?"))
                return "the host's save version differs from this game's";
            // (the upper end, the gate's last id, is held by the seat rule below: this player's seat must exist and be in range)
            if (h.YourPlayer < 1) return "YourPlayer is not a joiner's id (the host is player 0)";
            if (h.YourPlayer != ownPlayer) return "the header is for another player";

            if (h.CompressedSize < 1 || h.CompressedSize > JoinLimits.MaxCompressed) return "CompressedSize is outside 1.." + JoinLimits.MaxCompressed;
            if (h.UncompressedSize < 1 || h.UncompressedSize > JoinLimits.MaxUncompressed) return "UncompressedSize is outside 1.." + JoinLimits.MaxUncompressed;
            if (h.ChunkCount != (h.CompressedSize + JoinLimits.ChunkSize - 1) / JoinLimits.ChunkSize) return "ChunkCount does not match CompressedSize";
            if (!Hex(h.Sha256, 64)) return "Sha256 is not 64 lower-case hex digits";
            if (h.SaveId != null && !Hex(h.SaveId, 16)) return "SaveId is not 16 lower-case hex digits";

            string e = Seats(h);
            if (e != null) return e;
            e = Config(h);
            if (e != null) return e;
            // a session's host always names the human agencies; a header without them is not one this mod sent
            if (h.HumanAgencies == null) return "HumanAgencies are missing";
            return PostLoad(h.PostLoad);
        }

        static bool Hex(string s, int length)
        {
            if (s == null || s.Length != length) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        // At most 5 seats (the host and up to four more), the host as player 0 (so never none), each player and agency once, and a
        // seat for this joiner.
        static string Seats(JoinHeader h)
        {
            AgencySeat[] seats = h.Seats;
            if (seats == null) return "Seats are missing";
            if (seats.Length > JoinLimits.MaxPlayers) return "there are more than " + JoinLimits.MaxPlayers + " seats";
            bool host = false, mine = false;
            for (int i = 0; i < seats.Length; i++)
            {
                AgencySeat s = seats[i];
                if (s == null) return "a seat is empty";
                if (s.Player < 0 || s.Player > HostGate.MaxPlayerIds) return "a seat's player is out of range";
                if (!JoinCheck.IsAgencyId(s.Agency)) return "a seat's agency is not an agency id";
                for (int j = 0; j < i; j++)
                {
                    if (seats[j].Player == s.Player) return "a player is seated twice";
                    if (seats[j].Agency == s.Agency) return "an agency is seated twice";
                }
                if (s.Player == 0) host = true;
                if (s.Player == h.YourPlayer) mine = true;
            }
            if (!host) return "the host has no seat";
            return mine ? null : "this player has no seat";
        }

        // Company = this joiner's seat (so it is a valid agency id, the seat's being checked); the rivals hold the host's agency and
        // every other human agency, never the joiner's own (Design decision 12), each once, at most 16.
        //
        // These Config rules follow load mode A (I-10, signed off 2026-10-11): the joiner loads AS its claimed agency, so that agency
        // is never a rival and every other human is. AgencyLedger.ConfigFor builds configs that pass them.
        static string Config(JoinHeader h)
        {
            StartConfig c = h.Config;
            if (c == null) return "Config is missing";
            if (c.Rivals == null) return "Config.Rivals is missing";
            if (c.Rivals.Length > StartConfig.MaxRivals) return "Config has more than " + StartConfig.MaxRivals + " rivals";
            for (int i = 0; i < c.Rivals.Length; i++)
            {
                if (!JoinCheck.IsAgencyId(c.Rivals[i])) return "a rival is not an agency id";
                if (c.Rivals[i] == c.Company) return "the joiner's own agency is also a rival";
                for (int j = 0; j < i; j++)
                    if (c.Rivals[j] == c.Rivals[i]) return "a rival is listed twice";
            }
            foreach (AgencySeat s in h.Seats)
            {
                if (s.Player == h.YourPlayer)
                {
                    if (s.Agency != c.Company) return "Config.Company is not this player's seat";
                }
                else if (Array.IndexOf(c.Rivals, s.Agency) < 0)
                    return s.Player == 0 ? "the host's agency is not among the rivals" : "another human agency is not among the rivals";
            }
            // DD25: one of the game's three difficulties (EGameDifficulty: Explorer 0, Pioneer 1, Veteran 2)
            if (c.Difficulty < 0 || c.Difficulty > MaxDifficulty) return "Config.Difficulty is not one of the three";
            if (c.Epoch < 0 || c.Epoch > 100) return "Config.Epoch is out of range";
            if (string.IsNullOrEmpty(c.PlanetarySystem) || c.PlanetarySystem.Length > 64) return "Config.PlanetarySystem is missing or too long";
            if (c.GameplayId != null && (c.GameplayId.Length > 32 || !HexAnyLength(c.GameplayId))) return "Config.GameplayId is not up to 32 lower-case hex digits";
            return null;
        }

        static bool HexAnyLength(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        static string PostLoad(CategoryHash[] list)
        {
            if (list == null) return "PostLoad is missing";
            if (list.Length > JoinLimits.MaxCategories) return "PostLoad has more than " + JoinLimits.MaxCategories + " entries";
            long seen = 0;
            foreach (CategoryHash c in list)
            {
                if (c == null) return "a PostLoad entry is empty";
                if (c.Category < 0 || c.Category >= ContentCategory.Count) return "a PostLoad category is unknown";
                if (c.Count < 0) return "a PostLoad count is negative";
                if (!Hex(c.Hash, 64)) return "a PostLoad hash is not 64 lower-case hex digits";
                long bit = 1L << c.Category;
                if ((seen & bit) != 0) return "a PostLoad category is listed twice";
                seen |= bit;
            }
            return null;
        }
    }
}
