using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // The joiner's load rules that need no game (M4 Task 11), so they get real tests; the game glue
    // (src/Game/SnapshotLoad.cs, RefusingBinder.cs, JoinClient.cs) only gathers the facts and acts on the verdicts.

    // ------------------------------------------------------------------ agency ids on the wire

    // The protocol's agency ids are [a-z0-9_] (JoinCheck, -semp-agency); the game's are "NASA", "CNSA", "Roscosmos". An
    // agency travels as its CompanyDefinition id in lower case, and is resolved back by a unique case-insensitive
    // match: two ids that differ only in case would make the wire form ambiguous, so they resolve to none and the join
    // is refused.
    public static class AgencyWire
    {
        public static string From(string gameId) { return gameId == null ? null : gameId.ToLowerInvariant(); }

        // The game id the wire id names, or null (none, or more than one).
        public static string Resolve(string wire, IEnumerable<string> gameIds)
        {
            if (wire == null || gameIds == null) return null;
            string found = null;
            foreach (string id in gameIds)
            {
                if (id == null || From(id) != wire) continue;
                if (found != null && found != id) return null;
                found = id;
            }
            return found;
        }

        // The epoch travels as an int: its index among every StartGameEpoch id, sorted ordinally (both games hold the
        // same content, so the same list). -1 when it is not there.
        public static int EpochIndex(IEnumerable<string> epochIds, string id)
        {
            List<string> ids = SortedDistinct(epochIds);
            return id == null ? -1 : ids.IndexOf(id);
        }

        public static string EpochAt(IEnumerable<string> epochIds, int index)
        {
            List<string> ids = SortedDistinct(epochIds);
            return index >= 0 && index < ids.Count ? ids[index] : null;
        }

        static List<string> SortedDistinct(IEnumerable<string> ids)
        {
            var list = new List<string>();
            if (ids != null)
                foreach (string s in ids)
                    if (s != null && !list.Contains(s)) list.Add(s);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }

    // ------------------------------------------------------------------ the refusing binder's decision

    // What the strict deserialise's binder decides for each `$type` name, without Odin (docs/research/save-allowlist.md
    // 10.2):
    //   1. the whole name goes through SaveTypePolicy FIRST, so the default binder (which can call Assembly.Load and
    //      Type.GetType on the name) only ever sees a name the policy allowed;
    //   2. the allowed name is bound once, whole: Odin binds generic arguments inside its own default binder, never
    //      through an override, so the check has to be of the whole name;
    //   3. the bound type's name (as Odin writes it) must equal the name exactly, so a name that binds somewhere else
    //      (an assembly-wide [BindTypeNameToType] mapping, a type found by searching every assembly) is refused too.
    // Null means refused. The caller must ABORT the deserialise on a refusal: Odin treats a null type as "use the
    // slot's declared type" and goes on silently, so returning null alone would let the node through.
    public sealed class TypeBinding
    {
        readonly Func<string, Type> bindWhole;
        readonly Func<Type, string> nameOf;

        public TypeBinding(Func<string, Type> bindWhole, Func<Type, string> nameOf)
        {
            if (bindWhole == null) throw new ArgumentNullException("bindWhole");
            if (nameOf == null) throw new ArgumentNullException("nameOf");
            this.bindWhole = bindWhole;
            this.nameOf = nameOf;
        }

        public bool Refused { get; private set; }
        public string FirstRefused { get; private set; }   // printable and clipped, never the raw peer text
        public string Why { get; private set; }
        public int Calls { get; private set; }

        public Type Bind(string name)
        {
            Calls++;
            string part;
            if (!SaveTypePolicy.Allowed(name, out part)) return Refuse(part, "not on the list");
            Type t;
            try { t = bindWhole(name); }
            catch (Exception) { t = null; }
            if (t == null) return Refuse(SaveTypePolicy.Report(name), "does not bind in this game");
            string back;
            try { back = nameOf(t); }
            catch (Exception) { back = null; }
            if (!string.Equals(back, name, StringComparison.Ordinal))
                return Refuse(SaveTypePolicy.Report(name), "binds to " + SaveTypePolicy.Report(back));
            return t;
        }

        public static bool Contains(string name)
        {
            string part;
            return name != null && SaveTypePolicy.Allowed(name, out part);
        }

        Type Refuse(string part, string why)
        {
            if (!Refused)
            {
                Refused = true;
                FirstRefused = part ?? "(unknown)";
                Why = why;
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ wall 7: the snapshot's sanity

    // The values of the deserialised snapshot the joiner checks against the header (plan Design decision 2 step 7 and
    // 11).
    public sealed class SnapshotFacts
    {
        public long DateTicks;
        public bool DateReadable = true;
        public float Scale;
        public bool StartGameSave;
        public string SaveVersion;
        public bool EndGame;                          // object 533 (the asteroid) destroyed from Earth (66)
        public ICollection<string> AgenciesInSave;     // wire ids of every companyDataSave entry
        public Func<string, bool> InContent;           // this game's content has a CompanyDefinition for the wire id
    }

    public static class SnapshotSanity
    {
        // 0, or SanityFailed (the field named), GameOver or AgencyMissing. ownVersion: this game's Application.version.
        public static int Check(SnapshotFacts f, JoinHeader h, string ownAgency, string ownVersion, out string error)
        {
            error = null;
            const int Bad = JoinCodes.SanityFailed;
            if (f == null || h == null || h.Config == null) { error = "no snapshot or no header"; return Bad; }
            if (!f.DateReadable) { error = "currentTime is unreadable"; return JoinCodes.SanityFailed; }
            long diff = f.DateTicks - h.DateTicks;
            if (diff < 0) diff = -diff;
            if (diff > JoinLimits.DateToleranceTicks)
            {
                error = "currentTime (" + f.DateTicks + ") is not the header's date (" + h.DateTicks + ")";
                return JoinCodes.SanityFailed;
            }
            if (f.Scale != 0f)
            {
                error = "currentTimeScale is " + f.Scale + ", not 0 (the host snapshots a paused game)";
                return JoinCodes.SanityFailed;
            }
            if (f.StartGameSave) { error = "startGameSave is true (a new-game save, not a running game)"; return Bad; }
            if (!string.Equals(f.SaveVersion, h.SaveVersion, StringComparison.Ordinal))
            {
                error = "saveVersion '" + TextSanitiser.Clean(f.SaveVersion, 32, "?") + "' is not the header's";
                return JoinCodes.SanityFailed;
            }
            if (ModInfo.ClipGameVersion(f.SaveVersion) != ModInfo.ClipGameVersion(ownVersion))
            {
                error = "saveVersion is not this game's version";
                return JoinCodes.SanityFailed;
            }
            if (f.EndGame) { error = "the snapshot is a finished game (the impact)"; return JoinCodes.GameOver; }

            StartConfig c = h.Config;
            if (ownAgency == null || !string.Equals(c.Company, ownAgency, StringComparison.Ordinal))
            {
                error = "Config.Company is not the agency this joiner claimed";
                return JoinCodes.SanityFailed;
            }
            bool seated = false;
            if (h.Seats != null)
                foreach (AgencySeat s in h.Seats)
                    if (s != null && s.Player == h.YourPlayer && s.Agency == c.Company) seated = true;
            if (!seated) { error = "the claimed agency is not this player's seat"; return JoinCodes.SanityFailed; }

            var named = new List<string> { c.Company };
            if (c.Rivals != null) named.AddRange(c.Rivals);
            if (h.Seats != null) foreach (AgencySeat s in h.Seats) if (s != null) named.Add(s.Agency);
            foreach (string id in named)
            {
                string shown = TextSanitiser.Clean(id, 64, "?");
                if (f.AgenciesInSave == null || id == null || !f.AgenciesInSave.Contains(id))
                {
                    error = "agency " + shown + " is not in the snapshot";
                    return JoinCodes.AgencyMissing;
                }
                bool known;
                try { known = f.InContent != null && f.InContent(id); }
                catch (Exception) { known = false; }
                if (!known)
                {
                    error = "agency " + shown + " is not in this game's content";
                    return JoinCodes.AgencyMissing;
                }
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ step 1: the Unity log during the load

    // The values are UnityEngine.LogType's (LogKinds.FromUnity maps them; tests pin both).
    public enum LogKind { Error = 0, Assert = 1, Warning = 2, Log = 3, Exception = 4 }

    public struct LoadErrors
    {
        public int Errors, Benign;
        public string First;
        public bool FirstOffMain;
    }

    // Counts the Unity log's Error, Exception and Assert lines between Begin (the load starts) and End (the extract
    // returned), from any thread: the game's load can fail on a worker thread too, and Unity reports those on that
    // thread. The texts every clean load logs (measured over every clean load of the spike, docs/research/join.md "The
    // benign Unity error lines") are not counted. Thread-safe: Note runs on whatever thread logged.
    public sealed class LoadErrorCounter
    {
        public static readonly string[] Benign =
        {
            "check me offer.ID two time the same",
            "Error: No variable exists with name contract",
            "Error: No variable exists with name offer",
            "Error: No variable exists with name research",
        };

        // Benign texts that end in an agency id (1-64 of A-Z, a-z, 0-9, '_', '-'), the prefix exact.
        // ResearchManager.SetDataFromSave logs this one, and loads on, for an agency whose research queue is not empty
        // while its slot is. A non-player agency keeps its finished topic at the head of the queue (AutoPickResearch is
        // player-only), so every load of a world past such an agency's first finished topic logs it, the host's own
        // world included; the joiner's world then holds exactly the host's state (docs/research/join.md).
        public static readonly string[] BenignWithAgency =
        {
            "check me slot1 is null this.company.ID=",
        };

        public const int MaxAgencyIdLength = 64;

        public static bool IsBenign(string line)
        {
            if (Array.IndexOf(Benign, line) >= 0) return true;
            foreach (string prefix in BenignWithAgency)
                if (line.StartsWith(prefix, StringComparison.Ordinal) && IsAgencyIdText(line, prefix.Length))
                    return true;
            return false;
        }

        static bool IsAgencyIdText(string s, int from)
        {
            int n = s.Length - from;
            if (n < 1 || n > MaxAgencyIdLength) return false;
            for (int i = from; i < s.Length; i++)
            {
                char ch = s[i];
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')
                          || ch == '_' || ch == '-';
                if (!ok) return false;
            }
            return true;
        }

        readonly object gate = new object();
        bool counting;
        int errors, benign, mainThread;
        string first;
        bool firstOffMain;

        public void Begin(int mainThreadId)
        {
            lock (gate)
            {
                errors = 0; benign = 0; first = null; firstOffMain = false;
                mainThread = mainThreadId;
                counting = true;
            }
        }

        public int ErrorsSoFar { get { lock (gate) return errors; } }

        public LoadErrors End()
        {
            lock (gate)
            {
                counting = false;
                return new LoadErrors { Errors = errors, Benign = benign, First = first, FirstOffMain = firstOffMain };
            }
        }

        public void Note(LogKind kind, string condition, int threadId)
        {
            if (kind == LogKind.Log || kind == LogKind.Warning) return;
            string line = condition ?? "";
            int nl = line.IndexOf('\n');
            if (nl >= 0) line = line.Substring(0, nl);
            line = line.Trim();
            bool isBenign = kind == LogKind.Error && IsBenign(line);
            lock (gate)
            {
                if (!counting) return;
                if (isBenign) { benign++; return; }
                errors++;
                if (first == null)
                {
                    first = kind + " " + TextSanitiser.Clean(line, 200, "");
                    firstOffMain = threadId != mainThread;
                }
            }
        }
    }

    // Step 1 of the apply (plan Design decision 12): ExtractAllFromSaveData swallows a provider's exception and returns
    // false, so a load is good only when every signal agrees. The first failing signal names the failure; null means
    // the load is good.
    public static class LoadSignals
    {
        public const string Cancelled = "cancelled", ExtractFalse = "extract false", SuccessFalse = "success false",
            UnityError = "unity error";

        public static string Decide(bool cancelled, bool extract, string threw, bool successSeen, bool success,
                                    LoadErrors errors, out string detail)
        {
            detail = null;
            if (cancelled) { detail = "the load was abandoned during the extract"; return Cancelled; }
            if (!extract)
            {
                detail = "ExtractAllFromSaveData " + (threw != null ? "threw " + threw : "returned false");
                return ExtractFalse;
            }
            if (!successSeen || !success)
            {
                detail = successSeen ? "the save-loaded event said false" : "no save-loaded event";
                return SuccessFalse;
            }
            if (errors.Errors > 0)
            {
                detail = errors.Errors + " beyond the benign list, first " +
                         (errors.FirstOffMain ? "on a worker thread" : "on the main thread") + ": " + errors.First;
                return UnityError;
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ the settle before Loaded

    // A loaded game re-plans its missions over the following frames (the plan queue, one a frame once both porkchop
    // windows are done) and registers them a frame later still, so a joiner that reported "loaded" at once would differ
    // from the host's own reload of the same snapshot. Settled = the queue empty and both windows done for Frames
    // frames in a row AND for at least QuietSeconds (the spike saw one mission arrive after the queue had emptied; by
    // 3 s every mission matched the host's); a ceiling keeps a stuck queue from holding the join forever (the caller
    // logs that it was reached). An unreadable queue (-1) is never quiet.
    public sealed class SettleWatch
    {
        public const int Frames = 5;
        public const double QuietSeconds = 3, CeilingSeconds = 30;

        readonly double start;
        int quiet;
        double quietSince;
        bool settled;

        public SettleWatch(double startSeconds) { start = startSeconds; }

        public bool Settled { get { return settled; } }
        public bool CeilingReached { get; private set; }
        public int Steps { get; private set; }

        // Once a frame. True when done (settled, or the ceiling reached).
        public bool Step(int planQueue, bool porkchopsDone, double nowSeconds)
        {
            Steps++;
            if (planQueue == 0 && porkchopsDone)
            {
                if (quiet == 0) quietSince = nowSeconds;
                quiet++;
            }
            else quiet = 0;
            settled = quiet >= Frames && nowSeconds - quietSince >= QuietSeconds;
            if (settled) return true;
            if (nowSeconds - start > CeilingSeconds) { CeilingReached = true; return true; }
            return false;
        }
    }

    // ------------------------------------------------------------------ in session (plan Design decision 16 (b))

    // In session: this process completed a join (or a quick rejoin kept it), and since then no other save loaded and it
    // did not go back to the menu. Only a join made in this process counts, so a process restarted with -semp-rejoin
    // starts outside until its own (full) rejoin has loaded.
    public sealed class JoinSessionState
    {
        int loads;
        int joinedAt = -1;

        public bool InSession { get { return joinedAt >= 0 && joinedAt == loads; } }

        // Every save load: the join's own load is counted before Joined, so only a LATER load ends the session.
        public void SaveLoaded() { loads++; }
        public void Joined() { joinedAt = loads; }
        public void LeftToMenu() { joinedAt = -1; }
    }

    // ------------------------------------------------------------------ which messages the joiner's flow hears

    public static class JoinMessageRoute
    {
        // Only the join's own messages: JoinerFlow counts every message it hears as the host's progress, so the host's
        // Clock (sent every frame) must not reach it, or a stalled join would never time out. A Bye is not one: the
        // link ends on it, and the flow hears it from the closing event.
        public static bool ForTheFlow(Message m)
        {
            return m is JoinRefused || m is ContentRequest || m is ClaimResult || m is JoinWait ||
                   m is JoinHeader || m is RejoinResult;
        }
    }

    // ------------------------------------------------------------------ the Hello's planted mismatch (test profiles)

    // -semp-join-fault build|version also changes what the joiner says about itself in its Hello, so the host's version
    // refusal at the Hello (before any content is compared) can be seen in a test. Any other fault leaves it alone.
    public static class HelloFault
    {
        public static void Apply(Hello h, string fault)
        {
            if (h == null) return;
            const string build = "acs-ffffffffffffffff", version = "0.0.0 FAULT";
            if (fault == "build") h.GameBuild = h.GameBuild == build ? "acs-eeeeeeeeeeeeeeee" : build;
            else if (fault == "version") h.GameVersion = h.GameVersion == version ? "0.0.1 FAULT" : version;
        }
    }

    // ------------------------------------------------------------------ the kept rejoin key (test profiles)

    public static class RejoinKeyText
    {
        public const int MaxFileBytes = 64 + 8;   // a key is 64 hex digits; a little slack for a newline
        static readonly Regex Form = new Regex(@"^[0-9a-fA-F]{64}\z");

        public static bool IsKey(string s) { return s != null && Form.IsMatch(s); }

        // The key in a file's text, or null (empty, too long, or not a key). Never echoes the text.
        public static string Parse(string fileText)
        {
            if (fileText == null || fileText.Length > MaxFileBytes) return null;
            string k = fileText.Trim();
            return IsKey(k) ? k : null;
        }
    }

    // ------------------------------------------------------------------ the dev feed's header (test profiles)

    // A JoinHeader built from a dev feed's key=value file (src/Game/JoinClient.cs, JoinDevFeed), so a snapshot copy can
    // go through the joiner's real walls with no network. Keys: date, origin, lastDay, nextDay, lastMonth, nextMonth,
    // lastYear, nextYear (ticks); hostAgency, agency (the claimed one), rivals (the HOST's, comma-separated),
    // difficulty, system, setupAis, gameplayId; optional hostName, yourPlayer (1), saveVersion (this game's). The
    // joiner's rivals follow load mode A: the host's minus the claimed agency, plus the host's agency.
    public static class DevFeedHeader
    {
        static readonly string[] Required =
        {
            "date", "origin", "lastDay", "nextDay", "lastMonth", "nextMonth", "lastYear", "nextYear", "hostAgency",
            "agency", "rivals", "difficulty", "system", "setupAis",
        };

        public static JoinHeader Parse(IDictionary<string, string> kv, long compressed, long uncompressed,
                                       string sha256, string ownVersion, int epoch, string ownName, out string error)
        {
            error = null;
            foreach (string k in Required)
                if (!kv.ContainsKey(k)) { error = "the header file has no " + k; return null; }
            long[] t = new long[8];
            string[] tk = { "date", "origin", "lastDay", "nextDay", "lastMonth", "nextMonth", "lastYear", "nextYear" };
            for (int i = 0; i < tk.Length; i++)
                if (!long.TryParse(kv[tk[i]], NumberStyles.None, CultureInfo.InvariantCulture, out t[i]))
                { error = tk[i] + " is not a tick count"; return null; }
            int difficulty, you = 1;
            if (!int.TryParse(kv["difficulty"], out difficulty)) { error = "difficulty is not a number"; return null; }
            if (kv.ContainsKey("yourPlayer") && !int.TryParse(kv["yourPlayer"], out you))
            {
                error = "yourPlayer is not a number";
                return null;
            }
            string claimed = kv["agency"], host = kv["hostAgency"];
            var rivals = new List<string>();
            foreach (string r in kv["rivals"].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (r.Trim() != claimed && !rivals.Contains(r.Trim())) rivals.Add(r.Trim());
            if (!rivals.Contains(host)) rivals.Add(host);
            string gameplay;
            kv.TryGetValue("gameplayId", out gameplay);
            string hostName;
            if (!kv.TryGetValue("hostName", out hostName)) hostName = "Dev host";
            string version;
            if (!kv.TryGetValue("saveVersion", out version)) version = ModInfo.ClipGameVersion(ownVersion);
            return new JoinHeader
            {
                Format = 1, SaveVersion = version, DateTicks = t[0], OriginTicks = t[1],
                LastDay = t[2], NextDay = t[3], LastMonth = t[4], NextMonth = t[5], LastYear = t[6], NextYear = t[7],
                YourPlayer = you,
                Seats = new[]
                {
                    new AgencySeat { Player = 0, Agency = host, Name = hostName },
                    new AgencySeat { Player = you, Agency = claimed, Name = ownName },
                },
                Config = new StartConfig
                {
                    Company = claimed, Rivals = rivals.ToArray(), Difficulty = difficulty,
                    PlanetarySystem = kv["system"], Epoch = epoch, SetupAis = kv["setupAis"] == "true",
                    GameplayId = string.IsNullOrEmpty(gameplay) ? null : gameplay,
                },
                CompressedSize = compressed, UncompressedSize = uncompressed, ChunkCount = ChunkMath.Count(compressed),
                Sha256 = sha256, PostLoad = new CategoryHash[0], HumanAgencies = new string[0],
            };
        }
    }

    // ------------------------------------------------------------------ step 4: the content after the load

    public static class PostLoadContent
    {
        // The host's categories after its own load against the joiner's: the same set, each with the same count and
        // hash.
        public static bool Same(CategoryHash[] host, CategoryHash[] mine)
        {
            if (host == null || mine == null || host.Length != mine.Length) return false;
            foreach (CategoryHash x in host)
            {
                if (x == null) return false;
                CategoryHash match = null;
                int seen = 0;
                foreach (CategoryHash y in mine)
                    if (y != null && y.Category == x.Category) { match = y; seen++; }
                if (seen != 1 || match.Count != x.Count || match.Hash != x.Hash) return false;
            }
            return true;
        }
    }
}
