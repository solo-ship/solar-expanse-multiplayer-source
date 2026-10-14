using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The observatory's dice and the asteroid spawns of a removal, in a session (plan DD14, DD15). The game draws them
    // from UnityEngine.Random, which every game would draw differently, so a session takes them from the mod's own
    // generator instead: the host records what it drew into the entry, and every client replays exactly that,
    // checking each draw against the call it comes from. Pure and single-threaded; the Harmony patches that call into
    // it live in src/Game and keep no state of their own.
    public static class ObservatoryDraws
    {
        // the five call sites of an observatory draw (R1-R5): which deposit, the step's 30/60/90 days, which body of
        // a group, which asteroid, the discovery's 680-779 days
        public const int SiteDeposit = 0, SiteStepDays = 1, SiteGroupBody = 2, SiteAsteroid = 3, SiteDiscoveryDays = 4;

        // The id a draw is checked against when the call is about a resource: the low 31 bits of an FNV-1a 32 hash of
        // the id's UTF-8 bytes. The content check makes the ids equal on every game; a collision only weakens the
        // check, so the hash need not be strong. 0 for no id.
        public static int RefOf(string id)
        {
            if (id == null) return 0;
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(id))
            {
                h ^= b;
                h *= 16777619;
            }
            return (int)(h & 0x7FFFFFFF);
        }
    }

    // The mod's own generator (SplitMix64): the same seed gives the same draws on every machine, and nothing else in
    // the game shares its state, so observatories cannot split UnityEngine.Random between the games.
    public sealed class ModRandom
    {
        ulong state;

        public ModRandom(long seed) { state = unchecked((ulong)seed); }

        ulong NextBits()
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        // In min..maxExclusive - 1, like UnityEngine.Random.Range(int, int); an empty range gives min. The span is
        // under 2^32 and the draw has 64 bits, so the remainder's bias is below 2^-32.
        public int Next(int min, int maxExclusive)
        {
            if (maxExclusive <= min) return min;
            ulong span = (ulong)((long)maxExclusive - min);
            return (int)(min + (long)(NextBits() % span));
        }

        // A new star system's seed, 0..999,999,999 (inside ActionRequest.Value's range).
        public int NextSeed() { return Next(0, ActionLimits.MaxSeed + 1); }

        // In min..max, upper bound exclusive like UnityEngine.Random.Range(float, float): 53 random bits make the
        // fraction, and a float that rounds up to max is brought back to min.
        public float NextFloat(float min, float max)
        {
            double fraction = (NextBits() >> 11) * (1.0 / 9007199254740992.0);
            float v = (float)(min + (max - min) * fraction);
            return v >= max ? min : v;
        }
    }

    // One session's observatory dice. The host opens a record around each game call that may draw, and the entry
    // carries what was recorded; a client opens a replay around the apply of that entry. A draw outside both is not
    // allowed in a session, except while a load rebuilds a probe observatory (a client never finishes a step itself,
    // so what the load draws is harmless).
    public sealed class ObservatoryDrawState
    {
        readonly ModRandom rng;
        readonly List<ObsDraw> record = new List<ObsDraw>();
        bool recording, replaying;
        ObsDraw[] queue;
        int next;
        string replayProblem, stray;

        public ObservatoryDrawState(ModRandom rng) { this.rng = rng; }

        public bool Recording { get { return recording; } }
        public bool Replaying { get { return replaying; } }
        public bool Loading { get; set; }

        // One record or replay at a time: false when one is open already.
        public bool BeginRecord()
        {
            if (recording || replaying) return false;
            recording = true;
            record.Clear();
            return true;
        }

        // The draws made since BeginRecord, or null for none. Never more than MaxDraws + 1, so a runaway loop cannot
        // grow the list and the entry still fails Validate.
        public ObsDraw[] EndRecord()
        {
            if (!recording) return null;
            recording = false;
            ObsDraw[] draws = record.Count == 0 ? null : record.ToArray();
            record.Clear();
            return draws;
        }

        public bool BeginReplay(ObsDraw[] draws)
        {
            if (recording || replaying) return false;
            replaying = true;
            queue = draws ?? new ObsDraw[0];
            next = 0;
            replayProblem = null;
            return true;
        }

        // null = every recorded draw was taken, none was missing or different; else the first difference (the client
        // is then out of step).
        public string EndReplay()
        {
            if (!replaying) return "no replay was open";
            replaying = false;
            string problem = replayProblem;
            if (problem == null && next < queue.Length)
                problem = (queue.Length - next) + " recorded draws left over";
            queue = null;
            return problem;
        }

        // The seam the game's five call sites go through: UnityEngine.Random.Range(min, max), max exclusive.
        public int Range(int min, int max, int site, int reference)
        {
            if (recording)
            {
                int v = rng.Next(min, max);
                if (record.Count <= ActionLimits.MaxDraws)
                    record.Add(new ObsDraw { Site = site, Value = v, Ref = reference });
                return v;
            }
            if (replaying) return Replay(min, max, site, reference);
            if (!Loading && stray == null)
                stray = "an observatory draw (site " + site + ") outside any record or replay";
            return rng.Next(min, max);
        }

        // The value is checked against everything the client knows of the call; on any difference the game still
        // gets a number in range (its minimum) and the replay remembers the first problem.
        int Replay(int min, int max, int site, int reference)
        {
            if (next >= queue.Length)
            {
                Problem("the game drew more than the host recorded");
                return min;
            }
            ObsDraw d = queue[next++];
            if (d == null) { Problem("an empty recorded draw"); return min; }
            if (d.Site != site)
            {
                Problem("draw " + next + " came from site " + site + ", recorded " + d.Site);
                return min;
            }
            if (d.Ref != reference) { Problem("draw " + next + " was about another id than the host's"); return min; }
            bool inRange = max > min ? d.Value >= min && d.Value < max : d.Value == min;
            if (!inRange) { Problem("draw " + next + " is outside the range the game asked for"); return min; }
            return d.Value;
        }

        void Problem(string what) { if (replayProblem == null) replayProblem = what; }

        // The first draw made outside any record or replay (null for none); read once.
        public string TakeStray()
        {
            string s = stray;
            stray = null;
            return s;
        }

        // A load or a new session: nothing is open and nothing is remembered.
        public void Reset()
        {
            recording = false;
            replaying = false;
            Loading = false;
            record.Clear();
            queue = null;
            next = 0;
            replayProblem = null;
            stray = null;
        }
    }

    // The same for the asteroids a removal makes the game spawn: the host records each spawn the game's own creation
    // makes, a client replays them in order when it applies the entry, and outside an apply a client spawns nothing.
    public sealed class AsteroidSpawnState
    {
        readonly List<AsteroidSpawn> record = new List<AsteroidSpawn>();
        bool recording, replaying;
        AsteroidSpawn[] queue;
        int next;
        string replayProblem, stray;

        public bool Recording { get { return recording; } }
        public bool Replaying { get { return replaying; } }

        public bool BeginRecord()
        {
            if (recording || replaying) return false;
            recording = true;
            record.Clear();
            return true;
        }

        // False when no record is open: the host then opens one of its own and raises the spawn as AsteroidSpawned.
        public bool Add(AsteroidSpawn s)
        {
            if (!recording) return false;
            if (record.Count <= ActionLimits.MaxSpawns) record.Add(s);   // one over the cap is kept so Validate fails
            return true;
        }

        public AsteroidSpawn[] EndRecord()
        {
            if (!recording) return null;
            recording = false;
            AsteroidSpawn[] spawns = record.Count == 0 ? null : record.ToArray();
            record.Clear();
            return spawns;
        }

        public bool BeginReplay(AsteroidSpawn[] spawns)
        {
            if (recording || replaying) return false;
            replaying = true;
            queue = spawns ?? new AsteroidSpawn[0];
            next = 0;
            replayProblem = null;
            return true;
        }

        // The entry's next spawn, after checking the group and sub-type the game is spawning into; null and a problem
        // for anything else (the game then spawns nothing for this call).
        public AsteroidSpawn Take(int group, string subType, out string problem)
        {
            problem = null;
            if (!replaying) { problem = "a spawn outside any replay"; return null; }
            if (next >= queue.Length)
            {
                problem = Remember("the game spawned more than the host recorded");
                return null;
            }
            AsteroidSpawn s = queue[next++];
            if (s == null) { problem = Remember("an empty recorded spawn"); return null; }
            if (s.Group != group || s.SubType != subType)
            {
                problem = Remember("spawn " + next + " is for another group or kind than the host's");
                return null;
            }
            return s;
        }

        string Remember(string what)
        {
            if (replayProblem == null) replayProblem = what;
            return what;
        }

        public string EndReplay()
        {
            if (!replaying) return "no replay was open";
            replaying = false;
            string problem = replayProblem;
            if (problem == null && next < queue.Length)
                problem = (queue.Length - next) + " recorded spawns left over";
            queue = null;
            return problem;
        }

        // A client's spawn outside any apply, which the prefix refused: the tripwire reads it.
        public void NoteStray(string what) { if (stray == null) stray = what; }

        // The game made an asteroid that is not the one the entry names (another id or name): the replay's problem.
        public void Mismatch(string what) { Remember(what); }

        public string TakeStray()
        {
            string s = stray;
            stray = null;
            return s;
        }

        public void Reset()
        {
            recording = false;
            replaying = false;
            record.Clear();
            queue = null;
            next = 0;
            replayProblem = null;
            stray = null;
        }

        // The sub-type count is checked once, at session start: the list is content (equal on every game), and over
        // the cap a removal could spawn more than an entry may carry. null = fine; else the player's line.
        public static string KindsProblem(int count)
        {
            return count > ActionLimits.MaxSpawnSubTypes
                ? CommandText.TooManyAsteroidKinds(count, ActionLimits.MaxSpawnSubTypes) : null;
        }
    }

    // The widest numbers an entry of a kind can carry, for the host's encode-before-apply check.
    static class ObservatoryWorst
    {
        public static ObsAfter After()
        {
            return new ObsAfter { Mode = ObsMode.Discovery, Target = ActionLimits.MaxBodyId,
                Group = ActionLimits.MaxBodyId, Days = int.MaxValue };
        }

        // a negative value and the largest id are the widest the writer makes of a draw
        public static ObsDraw[] Draws(int count)
        {
            var draws = new ObsDraw[count];
            for (int i = 0; i < count; i++)
                draws[i] = new ObsDraw { Site = ActionLimits.MaxDrawSite, Value = int.MinValue, Ref = int.MaxValue };
            return draws;
        }

        // every number as wide as the writer can make it (a negative float at its longest, a 96-byte sub-type)
        public static AsteroidSpawn[] Spawns(int count)
        {
            var spawns = new AsteroidSpawn[count];
            const float wide = -1.2345679E-30f;
            for (int i = 0; i < count; i++)
                spawns[i] = new AsteroidSpawn { Group = ActionLimits.MaxBodyId, SubType = CounterRows.WideId,
                    Id = ActionLimits.MaxBodyId, Mass = int.MinValue, A = wide, Ecc = wide, P = wide, OmegaUc = wide,
                    OmegaLc = wide, Incl = wide, Name = "ZZ9-9999", Seed = ActionLimits.MaxSeed };
            return spawns;
        }
    }
}
