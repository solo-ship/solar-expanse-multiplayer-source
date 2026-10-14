using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The decisions the observatory rules of src/Game/Rules make, apart from the game's types so they are tested for
    // real (plan DD14; docs/research/observatories.md). The Harmony patches only read the game and call these.
    public static class ObservatoryRules
    {
        // What the game's observatorySpeedCheat is set to by -semp-observatory-speed: the value the console command
        // writes, which finished a 752 day discovery in one game day (observatories.md, Q-O8).
        public const int SpeedCheat = 320;

        // A held survey cost stays this far above zero, so a client's own work can never cross the line by itself.
        public const double HeldFloor = 1e-6;

        // ObsAfter.Days, the same number on every game: -1 when the observatory has nothing to work on (Idle, or the
        // survey, where the game leaves a stale figure), else the game's days left rounded down and clamped to 0 or
        // more (the game leaves a finished step's figure negative).
        public static int DaysAfter(int mode, double days)
        {
            if (mode == ObsMode.Idle || mode == ObsMode.ExoplanetSurvey) return -1;
            if (double.IsNaN(days) || days <= 0) return 0;
            double whole = Math.Floor(days);
            return whole >= int.MaxValue ? int.MaxValue : (int)whole;
        }

        // The id a draw is checked against when the game picks from a list (which deposit, which body of a group,
        // which asteroid): the whole candidate list in order, so a client whose list differs in content or order from
        // the host's is out of step at the draw, before it applies a value that means another candidate. The chosen
        // candidate is not known before the draw, which is why the list stands in for it.
        public static int RefOfIds(IEnumerable<string> ids)
        {
            var parts = new List<string>();
            foreach (string id in ids) parts.Add(id ?? "");
            return ObservatoryDraws.RefOf(parts.Count + ":" + string.Join("\u001f", parts.ToArray()));
        }

        public static int RefOfBodies(IEnumerable<int> bodies)
        {
            var parts = new List<string>();
            foreach (int b in bodies) parts.Add(b.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return RefOfIds(parts);
        }

        // The survey cost a client keeps while it waits for the host: the game's own subtraction, but never to zero.
        public static double HeldSurveyCost(double cost, double power)
        {
            double left = cost - power;
            return left > 0 ? left : HeldFloor;
        }

        // The power that takes a cost to zero or below with the game's own subtraction: the host's crossing, applied.
        public static double CrossingPower(double cost)
        {
            return Math.Max(cost, 0) + 1;
        }

        // Did a re-plan change the observatory? A state that could not be read counts as a change, so the clients are
        // told rather than left to a difference nobody sees.
        // Is a re-plan raised? A listener's is only when it changed something or drew (the game's handler may do
        // nothing); a destroyed target's always is, because the removal's entry already counted it.
        public static bool RaisesReplan(bool onlyIfChanged, ObsDraw[] draws, ObsAfter before, ObsAfter after)
        {
            return !onlyIfChanged || draws != null || Changed(before, after);
        }

        // A listener note (one with a row) is released when its re-plan has run, so a later real one is noted again.
        public static bool ReleasesListener(bool isListenerNote) { return isListenerNote; }

        // How many deposits an observatory has left to examine; the game's list is absent with no target, which
        // counts as "more than none" so no re-plan is noted for it.
        public static int Unexplored(int listCount) { return listCount < 0 ? int.MaxValue : listCount; }

        // A deposit's position among its target's rows (-1 when it is not one of them).
        public static int PositionIn<T>(System.Collections.Generic.IList<T> rows, T row)
        {
            return rows == null ? -1 : rows.IndexOf(row);
        }

        public static bool Changed(ObsAfter before, ObsAfter after)
        {
            if (before == null || after == null) return true;
            return before.Mode != after.Mode || before.Target != after.Target || before.Group != after.Group
                || before.Days != after.Days;
        }
    }

    // A new asteroid as the host's generator makes it (DD15): the values the game's own CreateDynamicAsteroid draws, in
    // its ranges and form, from the mod's generator so the host decides them and every game creates the same one.
    public static class AsteroidMaker
    {
        // the letter pairs the game rejects for a designation
        static readonly string[] Rejected =
        {
            "AB", "AM", "BG", "DE", "EX", "FL", "KB", "KH", "MP", "MP", "PC", "PW", "TJ", "TT", "UT", "ZZ"
        };

        // `massLow`/`massHigh` are the game's asteroidGenerateMinMass/MaxMass (an exclusive upper bound, as its
        // Random.Range(int, int)). `taken` says whether a designation is in use. Id stays 0: the caller fills it in
        // from the object the game made.
        public static AsteroidSpawn Make(ModRandom rng, int group, string subType, int massLow, int massHigh,
            Func<string, bool> taken)
        {
            var s = new AsteroidSpawn { Group = group, SubType = subType };
            s.A = rng.NextFloat(0.9f, 1.1f);
            s.Ecc = rng.NextFloat(0f, 0.25f);
            s.P = rng.NextFloat(0.9f, 1.1f);
            s.OmegaUc = rng.NextFloat(0f, 360f);
            s.OmegaLc = rng.NextFloat(0f, 360f);
            s.Incl = rng.NextFloat(0f, 15f);
            s.Mass = rng.Next(massLow, massHigh);
            s.Name = Name(rng, taken);
            s.Seed = rng.NextSeed();   // drawn last, so the values above are what they were before the seed existed
            return s;
        }

        // The game's designation: two capital letters, a digit 0 to 8 and four digits, with up to ten tries for a pair
        // it accepts and an unused name, then "XX" as its last resort.
        static string Name(ModRandom rng, Func<string, bool> taken)
        {
            int digit = rng.Next(0, 9);
            int number = rng.Next(1000, 9999);
            string letters = "XX";
            string name = letters + digit + "-" + number;
            for (int i = 0; i < 10; i++)
            {
                letters = new string(new[] { (char)rng.Next(65, 90), (char)rng.Next(65, 90) });
                name = letters + digit + "-" + number;
                if (Array.IndexOf(Rejected, letters) >= 0) continue;
                if (!taken(name)) return name;
            }
            return "XX" + digit + "-" + number;
        }
    }

    // Who runs an asteroid impact (FS3: the ship's landing handler). Vanilla runs it on its owner's game only; in a
    // session the host runs it for every human agency and a client only inside the host's entry for that mission
    // (DD15, "Impacts are host-run in a session"). An orbit target (an asteroid pushed into an orbit) keeps the
    // game's own test until M6 (R17-1).
    public static class ImpactRules
    {
        // true = the handler returns without running the branch (the game's `company != local player` result).
        public static bool Skips(bool session, bool ownerIsLocalPlayer, bool isHost, bool ownerIsHuman,
            bool orbitTarget, bool applyingThisMission)
        {
            // M6 (DD-F7): an orbit pull is run like an impact, by the host and, on a client, inside the host's 72;
            // orbitTarget stays for the callers and no longer changes the answer
            if (!session) return !ownerIsLocalPlayer;
            if (isHost) return !(ownerIsHuman || ownerIsLocalPlayer);
            return !applyingThisMission;
        }
    }

    // DD11's build completion, extended to facilities (R19-1).
    public static class CompletionRules
    {
        // A client holds a finishing build of this item until the host's entry: a craft that takes a game-wide id, or
        // any facility.
        public static bool IsHeld(bool takesCraftId, bool facility) { return takesCraftId || facility; }

        // The host raises CraftFinished for it. A craft raises wherever its Build() runs; a facility only when the
        // progress update finished it: a facility built another way (added prebuilt, a probe's own) is inside an
        // applied entry or a load, which every game does the same.
        public static bool Raises(bool takesCraftId, bool facility, bool inBuildUpdate)
        {
            return takesCraftId || (facility && inBuildUpdate);
        }
    }

    // FS2: a flight read from the save keeps the leftover fuel it was saved with (R12-4).
    public static class FuelKeep
    {
        public static bool Keeps(bool session, bool loadingFromSave) { return session && loadingFromSave; }
    }
}

namespace SolarExpanseMultiplayer
{
    // Which observatories already have a listener re-plan noted. The game's listener fires for every change of a
    // row (the explored flag, then the mine flag), at every observatory that examines that body for the agency, and
    // acts only when the row is explored and nothing is left for that observatory to examine. So a re-plan is noted
    // once, and only then; anything else would send the clients a re-plan the host never made, and they would draw
    // dice the host never recorded.
    public sealed class ListenerGate<TKey>
    {
        readonly System.Collections.Generic.HashSet<TKey> noted = new System.Collections.Generic.HashSet<TKey>();

        // true = note this one (the first time its list is empty and the row is explored).
        public bool Note(TKey observatory, bool rowExplored, int remainingCandidates)
        {
            if (!rowExplored || remainingCandidates != 0) return false;
            return noted.Add(observatory);
        }

        public void Release(TKey observatory) { noted.Remove(observatory); }

        public void Clear() { noted.Clear(); }
    }

    // A generated system's names. The game names every planet and moon `<system name><letter>` from its own
    // time-seeded generator, so two games that generate the same seed have the same bodies under different names;
    // every game takes the host's system name for all of them.
    public static class SystemNames
    {
        public static string Rename(string name, string oldSystem, string newSystem)
        {
            if (name == null || string.IsNullOrEmpty(oldSystem) || newSystem == null) return name;
            return name.StartsWith(oldSystem, System.StringComparison.Ordinal)
                ? newSystem + name.Substring(oldSystem.Length) : name;
        }

        // true when a body's name is not one of this system's.
        public static bool Differs(string name, string system)
        {
            return string.IsNullOrEmpty(name) || !name.StartsWith(system, System.StringComparison.Ordinal);
        }
    }
}
