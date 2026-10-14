using System;

namespace SolarExpanseMultiplayer
{
    // Protocol 2's fourteen join messages (plan "Interfaces, Protocol 2"), kinds 9-22 in Envelope. Every one is a plain class
    // with a fixed schema. Validate() refuses each out-of-range field, and text from the peer comes out sanitised
    // (TextSanitiser) and length-capped. SnapshotChunk is the one binary kind: a 4-byte index and the raw bytes.

    // ---- shared field checks (the JSON messages all use these) ----

    static class JoinCheck
    {
        public static readonly long MaxTicks = DateTime.MaxValue.Ticks;

        // [a-z0-9_]{1,64}: an agency id. A value that is not a plain id never reaches the game.
        public static bool IsAgencyId(string s)
        {
            if (s == null || s.Length == 0 || s.Length > 64) return false;
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        public static string Agency(string s, string field, bool required)
        {
            if (s == null) return required ? field + " missing" : null;
            return IsAgencyId(s) ? null : field + " is not an agency id (a-z, 0-9, _; 1-64 characters)";
        }

        // A date as DateTime ticks: 1..DateTime.MaxValue.Ticks (zeroOk also allows 0, meaning "none").
        public static string Ticks(long v, string field, bool zeroOk)
        {
            if (v == 0 && zeroOk) return null;
            return v < 1 || v > MaxTicks ? field + " is not a date" : null;
        }

        public static string Code(int v, string field) { return v < 0 || v > 99 ? field + " outside 0..99" : null; }

        public static string Category(int c, string field) { return c < 0 || c >= ContentCategory.Count ? field + " is not a content category" : null; }

        public static string Lower(string s, int max, string field, bool required)
        {
            if (s == null) return required ? field + " missing" : null;
            if (s.Length > max) return field + " longer than " + max;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return field + " is not lower-case hex";
            return null;
        }
    }

    // ---- code tables ----

    public static class JoinCodes
    {
        // host refusals
        public const int BuildDiffers = 1, ModDiffers = 2, PluginsDiffer = 3, ContentDiffers = 4, ManagedDiffers = 5,
            AgencyTaken = 10, AgencyUnknown = 11, AgencyNotPlayable = 12, Full = 13, NoAgency = 14, TooManyClaims = 15,
            HostGameOver = 16, HostSnapshotFailed = 17, ClaimDeadline = 18, Deadline = 20, HostLeft = 21,
            TooManyFailures = 22, Protocol = 23, TooMuchDetail = 24;
        // joiner failures (JoinStage.Failed), each a wall
        public const int OverCap = 30, SizeMismatch = 31, HashMismatch = 32, Decompress = 33, TypeRefused = 34,
            Malformed = 35, RefUnresolved = 36, GameOver = 37, AgencyMissing = 38, Declined = 39, LoadThrew = 40,
            PostLoadContent = 41, BoundariesInvalid = 42, HeaderInvalid = 43, DateMismatch = 44, StrictDeserialise = 45,
            JoinerTimeout = 46, SanityFailed = 47, SummaryTooBig = 48, Aborted = 49,
            QueueWait = 50,   // waited JoinLimits.JoinerQueueWaitMs for a batch and gave up (never counted against it)
            ContentUnavailable = 51,   // the joiner's own content scan could not run: refused locally, nothing sent
            Reconnects = 52,  // the link was lost over JoinLimits.MaxReconnects times in one join attempt (local only)
            RejoinRefused = 53,   // the host answered a reconnect with Reject (local only; its own code is in the log)
            LoadsDifferently = 54,   // this game build loads differently than the mod knows (the habitability warm-up)
            // M5: a joiner's command stream broke (a gap, a day going back, a reset, a failed restore) and it rejoins
            // in full. Local only: never a failed join and never a violation, though it counts as a reconnect.
            OutOfStep = 55,
            // a first connection that ended before the host let it in (local only, never on the wire): the host
            // refused its Hello (a spent or wrong code, a full game, other versions; the reason is in the report), or
            // no connection came at all
            NotLetIn = 56, NoConnection = 57,
            // protocol 7: the lobby's door and claims, and the code of a Bye when a player leaves on purpose (LobbyCodes)
            LobbyFull = 60, LateJoinOff = 61, Banned = 62, EpochNotAllowed = 63, NotClaimable = 64, KickedFromLobby = 65,
            BadKey = 66, Left = 67;
    }

    public static class ContentCategory
    {
        public const int Facilities = 0, ProductionItems = 1, Research = 2, SpacecraftParts = 3,
            Resources = 4, Bodies = 5, Contracts = 6, Other = 7, Count = 8;

        // The words a report uses. Other is "Other game data": every definition outside the seven named kinds.
        public static string Name(int c)
        {
            switch (c)
            {
                case Facilities: return "Facilities";
                case ProductionItems: return "Production items";
                case Research: return "Research";
                case SpacecraftParts: return "Spacecraft parts";
                case Resources: return "Resources";
                case Bodies: return "Bodies";
                case Contracts: return "Contracts";
                case Other: return "Other game data";
                default: return "Unknown data";
            }
        }
    }

    public static class JoinStageKind { public const int Checking = 1, Loading = 2, Loaded = 3, Failed = 4; }

    public static class NoticeKind
    {
        public const int Joining = 1, Joined = 2, CouldNotJoin = 3, Waiting = 4, Rejoined = 5;
        // protocol 7: a player left the session on purpose, a save copy was written, the host ran a console command
        public const int Left = 6, CopySaved = 7, HostConsole = 8;
    }

    // Why a rejoin is full: the FIRST failing condition of Design decision 16.
    public static class RejoinCodes
    {
        public const int Quick = 0, TimeMoved = 1, NotInSession = 2, DropTooLong = 3, BoundaryDiffers = 4, FingerprintDiffers = 5,
            NotInGameBefore = 6, NoDropRecord = 7, BadOffer = 8, HostReloaded = 9, ClientChangedDuringDrop = 10,
            HostChangedDuringDrop = 11, NoPrint = 12,
            CommandsMissed = 13,   // M5: the client has not applied the last command result the host sent it
            Resume = 14;           // M7: a short drop the host will replay: RejoinResult carries ResumeNextSeq and ResumePausedTicks
    }

    // ---- parts ----

    public sealed class PluginEntry
    {
        // Id: a plugin GUID, an assembly's file name, or a full name when it has no file. Where: mod | bepinex-core |
        // bepinex-plugin | other | memory. Sha256 is the file's hash (null when there is no file: only for memory). Mvid is
        // the module version id of an assembly loaded from bytes (I-9): present exactly when Where is memory.
        public string Id;
        public string Name;
        public string Version;
        public string Sha256;
        public string Where;
        public string Mvid;

        internal string Validate()
        {
            string e = Message.Text(Id, "Id", 128, true) ?? Message.Text(Name, "Name", 64, false) ?? Message.Text(Version, "Version", 32, false);
            if (e != null) return e;
            bool memory = Where == "memory";
            if (Where == null) return "Where missing";
            if (!memory && Where != "mod" && Where != "bepinex-core" && Where != "bepinex-plugin" && Where != "other") return "Where is not a known kind";
            if (memory)
            {
                e = Message.Hex(Mvid, "Mvid", 32, true) ?? Message.Hex(Sha256, "Sha256", 64, false);
            }
            else
            {
                if (Mvid != null) return "Mvid on an entry that has a file";
                e = Message.Hex(Sha256, "Sha256", 64, true);
            }
            if (e != null) return e;
            Id = TextSanitiser.Clean(Id, 128, "?");
            if (Name != null) Name = TextSanitiser.Clean(Name, 64, "");
            if (Version != null) Version = TextSanitiser.Clean(Version, 32, "");
            return null;
        }
    }

    public sealed class CategoryHash
    {
        public int Category;
        public int Count;
        public string Hash;   // 64 hex

        internal string Validate()
        {
            return JoinCheck.Category(Category, "Category") ?? (Count < 0 ? "Count is negative" : null) ?? Message.Hex(Hash, "Hash", 64, true);
        }

        // 0..MaxCategories entries, each valid, no category twice.
        internal static string ValidateList(CategoryHash[] list, string field, bool required)
        {
            if (list == null) return required ? field + " missing" : null;
            if (list.Length > JoinLimits.MaxCategories) return "more than " + JoinLimits.MaxCategories + " " + field;
            long seen = 0;
            foreach (CategoryHash c in list)
            {
                if (c == null) return "an empty entry in " + field;
                string e = c.Validate();
                if (e != null) return field + ": " + e;
                long bit = 1L << c.Category;
                if ((seen & bit) != 0) return field + ": category " + c.Category + " twice";
                seen |= bit;
            }
            return null;
        }
    }

    public sealed class AgencySeat
    {
        public int Player;      // 0 = the host
        public string Agency;
        public string Name;

        internal string Validate()
        {
            string e = Player < 0 || Player > HostGate.MaxPlayerIds ? "Seat.Player out of range" : null;
            e = e ?? JoinCheck.Agency(Agency, "Seat.Agency", true) ?? Message.Text(Name, "Seat.Name", 24, false);
            if (e != null) return e;
            if (Name != null) Name = TextSanitiser.Clean(Name, 24, "");
            return null;
        }
    }

    public sealed class StartConfig
    {
        public string Company;            // in a JoinHeader: THIS joiner's claimed agency; in IJoinHostWorld.HostConfig: the host's own player agency
        public string[] Rivals;           // in a JoinHeader: the host's rivals minus this joiner's agency, plus the host's agency and every other human agency; at most 16
        public int Difficulty;            // GameManager.EGameDifficulty as an int, range-checked again by the joiner
        public string PlanetarySystem;    // PlanetarySystemDescriptor id
        public int Epoch;                 // StartGameEpoch as an int
        public bool SetupAis;
        public string GameplayId;         // at most 32, [0-9a-f]

        public const int MaxRivals = 16;

        internal string Validate()
        {
            string e = JoinCheck.Agency(Company, "Config.Company", true);
            if (e != null) return e;
            if (Rivals == null) return "Config.Rivals missing";
            if (Rivals.Length > MaxRivals) return "more than " + MaxRivals + " rivals";
            foreach (string r in Rivals)
            {
                e = JoinCheck.Agency(r, "Config.Rivals entry", true);
                if (e != null) return e;
            }
            if (Difficulty < 0 || Difficulty > 100) return "Config.Difficulty out of range";
            if (Epoch < 0 || Epoch > 100) return "Config.Epoch out of range";
            e = Message.Text(PlanetarySystem, "Config.PlanetarySystem", 64, true) ?? JoinCheck.Lower(GameplayId, 32, "Config.GameplayId", false);
            if (e != null) return e;
            PlanetarySystem = TextSanitiser.Clean(PlanetarySystem, 64, "?");
            return null;
        }
    }

    public sealed class FingerprintBlock
    {
        public int Area;
        public int Agency;
        public int Count;
        public string Exact, Rounded, Discrete;   // 16 hex each
        public bool Capped;

        internal string Validate()
        {
            if (Area < 0 || Area >= DriftAreas.All.Length) return "Block.Area is not one of M3's areas";
            if (Agency < DriftAreas.WorldAgency || Agency > 255) return "Block.Agency out of range";
            if (Count < 0) return "Block.Count is negative";
            return Message.Hex(Exact, "Block.Exact", 16, true) ?? Message.Hex(Rounded, "Block.Rounded", 16, true) ?? Message.Hex(Discrete, "Block.Discrete", 16, true);
        }
    }

    public sealed class Fingerprint
    {
        public long OriginTicks;
        public string DayDiscrete, DayRounded, DayExact;   // Fnv64 over every block's hash at that level, in block order (16 hex)
        public FingerprintBlock[] Blocks;                  // at most JoinLimits.MaxFingerprintBlocks, in M3's block order

        internal string Validate()
        {
            string e = JoinCheck.Ticks(OriginTicks, "Print.OriginTicks", false) ?? Message.Hex(DayDiscrete, "Print.DayDiscrete", 16, true)
                ?? Message.Hex(DayRounded, "Print.DayRounded", 16, true) ?? Message.Hex(DayExact, "Print.DayExact", 16, true);
            if (e != null) return e;
            if (Blocks == null) return "Print.Blocks missing";
            if (Blocks.Length > JoinLimits.MaxFingerprintBlocks) return "more than " + JoinLimits.MaxFingerprintBlocks + " blocks";
            foreach (FingerprintBlock b in Blocks)
            {
                if (b == null) return "an empty block";
                e = b.Validate();
                if (e != null) return e;
            }
            return null;
        }
    }

    // ---- the messages ----

    // Joiner -> host, straight after the Welcome. Its content, summarised: same build, versions, plugin and assembly lists
    // and category hashes mean "same". At most 60 KiB (a joiner whose summary would not fit refuses locally).
    public sealed class ContentSummary : Message
    {
        public string GameBuild;        // at most 96
        public string GameVersion;      // at most 32
        public string ModVersion;       // at most 32
        public string Loader;           // doorstop | bepinex
        public string ManagedHash;      // 64 hex: one SHA-256 over every file name and hash in the game's Managed folder
        public int ManagedCount;
        public PluginEntry[] Plugins;       // at most 256
        public PluginEntry[] Assemblies;    // at most 256
        public CategoryHash[] Categories;   // at most 16
        public string Overall;          // 64 hex

        public override string Validate()
        {
            string e = Text(GameBuild, "GameBuild", 96, true) ?? Text(GameVersion, "GameVersion", 32, true) ?? Text(ModVersion, "ModVersion", 32, true);
            if (e != null) return e;
            if (Loader != "doorstop" && Loader != "bepinex") return "Loader is not doorstop or bepinex";
            e = Hex(ManagedHash, "ManagedHash", 64, true) ?? (ManagedCount < 0 ? "ManagedCount is negative" : null) ?? Hex(Overall, "Overall", 64, true);
            if (e != null) return e;
            e = Entries(Plugins, "Plugins", JoinLimits.MaxPlugins) ?? Entries(Assemblies, "Assemblies", JoinLimits.MaxAssemblies) ?? CategoryHash.ValidateList(Categories, "Categories", true);
            if (e != null) return e;
            GameBuild = TextSanitiser.Clean(GameBuild, 96, "?");
            GameVersion = TextSanitiser.Clean(GameVersion, 32, "?");
            ModVersion = TextSanitiser.Clean(ModVersion, 32, "?");
            return null;
        }

        static string Entries(PluginEntry[] list, string field, int max)
        {
            if (list == null) return field + " missing";
            if (list.Length > max) return "more than " + max + " " + field;
            foreach (PluginEntry p in list)
            {
                if (p == null) return "an empty entry in " + field;
                string e = p.Validate();
                if (e != null) return field + ": " + e;
            }
            return null;
        }
    }

    // Host -> joiner: "send me the items of these categories" (only the differing ones).
    public sealed class ContentRequest : Message
    {
        public int[] Categories;   // at most 16, each a ContentCategory, none twice

        public override string Validate()
        {
            if (Categories == null) return "Categories missing";
            if (Categories.Length > JoinLimits.MaxCategories) return "more than " + JoinLimits.MaxCategories + " categories";
            long seen = 0;
            foreach (int c in Categories)
            {
                string e = JoinCheck.Category(c, "Categories entry");
                if (e != null) return e;
                long bit = 1L << c;
                if ((seen & bit) != 0) return "a category twice";
                seen |= bit;
            }
            return null;
        }
    }

    // Joiner -> host: one part of one category's items, sorted ids with a 16-hex item hash. Several parts per category,
    // at most 128 KiB in all (the host counts).
    public sealed class ContentDetail : Message
    {
        public int Category;
        public int Part;        // 0..7
        public bool Last;
        public string[] Ids;    // at most 1024, each at most 128
        public string[] Hashes; // as many as Ids, 16 hex each

        public override string Validate()
        {
            string e = JoinCheck.Category(Category, "Category");
            if (e != null) return e;
            if (Part < 0 || Part >= JoinLimits.MaxDetailParts) return "Part outside 0.." + (JoinLimits.MaxDetailParts - 1);
            if (Ids == null || Hashes == null) return "Ids or Hashes missing";
            if (Ids.Length > JoinLimits.MaxDetailItemsPerPart) return "more than " + JoinLimits.MaxDetailItemsPerPart + " items";
            if (Hashes.Length != Ids.Length) return "Hashes and Ids differ in length";
            for (int i = 0; i < Ids.Length; i++)
            {
                e = Text(Ids[i], "Ids entry", 128, true) ?? Hex(Hashes[i], "Hashes entry", 16, true);
                if (e != null) return e;
            }
            for (int i = 0; i < Ids.Length; i++) Ids[i] = TextSanitiser.Clean(Ids[i], 128, "?");
            return null;
        }
    }

    // Host -> joiner: the join is refused, with a short report of what differs. The lines are budgeted in UTF-8 bytes (I-13).
    public sealed class JoinRefused : Message
    {
        public int Code;          // JoinCodes
        public string[] Lines;    // at most 40, each at most 160 characters, at most JoinLimits.ReportMaxBytes in all

        public override string Validate()
        {
            string e = JoinCheck.Code(Code, "Code");
            if (e != null) return e;
            if (Lines == null) return "Lines missing";
            if (Lines.Length > JoinLimits.MaxReportLines) return "more than " + JoinLimits.MaxReportLines + " lines";
            foreach (string l in Lines)
            {
                e = Text(l, "Line", JoinLimits.MaxReportLineChars, true);
                if (e != null) return e;
            }
            int bytes = 0;
            for (int i = 0; i < Lines.Length; i++)
            {
                Lines[i] = TextSanitiser.Clean(Lines[i], JoinLimits.MaxReportLineChars, "");
                bytes += TextBudget.Utf8Bytes(Lines[i]);
            }
            return bytes > JoinLimits.ReportMaxBytes ? "the lines are " + bytes + " bytes, over " + JoinLimits.ReportMaxBytes : null;
        }
    }

    // Joiner -> host: "I want this agency".
    public sealed class AgencyClaim : Message
    {
        public string Agency;   // 1-64, [a-z0-9_]
        public override string Validate() { return JoinCheck.Agency(Agency, "Agency", true); }
    }

    // Host -> joiner: the answer to a claim (or, on a full rejoin, the agency it keeps).
    public sealed class ClaimResult : Message
    {
        public bool Ok;
        public int Code;        // JoinCodes when refused
        public string Agency;   // required when Ok

        public override string Validate()
        {
            return JoinCheck.Code(Code, "Code") ?? JoinCheck.Agency(Agency, "Agency", Ok);
        }
    }

    // Host -> joiner: "N joiners are ahead of you" (a batch is in progress).
    public sealed class JoinWait : Message
    {
        public int Ahead;
        public override string Validate() { return Ahead < 0 || Ahead > HostGate.MaxPlayerIds ? "Ahead outside 0.." + HostGate.MaxPlayerIds : null; }
    }

    // Host -> joiner: what the snapshot is, before its chunks. Every size is checked against the caps (the joiner checks them
    // again before allocating anything).
    public sealed class JoinHeader : Message
    {
        public int Format;                                       // 1
        public string SaveVersion;                               // the host's Application.version; must equal the joiner's
        public long DateTicks;                                   // the host's CurrentTime at the snapshot (paused)
        public long OriginTicks;                                 // the host's day-0 key (its own load moment); day N's key = Origin + N days
        public long LastDay, NextDay, LastMonth, NextMonth, LastYear, NextYear;   // TimeController's private fields, ticks
        public int YourPlayer;
        public AgencySeat[] Seats;                               // at most 5: every human agency, the host's included
        public StartConfig Config;
        public long CompressedSize, UncompressedSize;            // under the JoinLimits caps
        public int ChunkCount;                                   // the chunks CompressedSize needs
        public string Sha256;                                    // 64 hex, of the compressed bytes
        // the host's content hashes at the snapshot, at most 16
        public CategoryHash[] PostLoad;
        public string SaveId;                                    // the host's save hash (16 hex) or null (drift runs)
        // M5: the host's human agencies right after the batch's SeatClaimed entries (never the players still waiting
        // for the next batch); at most 16, each an agency id, no repeats. Null from a host that sent none.
        public string[] HumanAgencies;

        public override string Validate()
        {
            if (Format != 1) return "Format is not 1";
            string e = Text(SaveVersion, "SaveVersion", 32, true);
            if (e != null) return e;
            e = JoinCheck.Ticks(DateTicks, "DateTicks", false) ?? JoinCheck.Ticks(OriginTicks, "OriginTicks", false)
                ?? JoinCheck.Ticks(LastDay, "LastDay", false) ?? JoinCheck.Ticks(NextDay, "NextDay", false)
                ?? JoinCheck.Ticks(LastMonth, "LastMonth", false) ?? JoinCheck.Ticks(NextMonth, "NextMonth", false)
                ?? JoinCheck.Ticks(LastYear, "LastYear", false) ?? JoinCheck.Ticks(NextYear, "NextYear", false);
            if (e != null) return e;
            if (YourPlayer < 1 || YourPlayer > HostGate.MaxPlayerIds) return "YourPlayer outside 1.." + HostGate.MaxPlayerIds;
            e = ValidateSeats();
            if (e != null) return e;
            if (Config == null) return "Config missing";
            e = Config.Validate();
            if (e != null) return e;
            if (CompressedSize < 1 || CompressedSize > JoinLimits.MaxCompressed) return "CompressedSize outside 1.." + JoinLimits.MaxCompressed;
            if (UncompressedSize < 1 || UncompressedSize > JoinLimits.MaxUncompressed) return "UncompressedSize outside 1.." + JoinLimits.MaxUncompressed;
            if (ChunkCount != (CompressedSize + JoinLimits.ChunkSize - 1) / JoinLimits.ChunkSize) return "ChunkCount does not match CompressedSize";
            e = Hex(Sha256, "Sha256", 64, true) ?? Hex(SaveId, "SaveId", 16, false)
                ?? CategoryHash.ValidateList(PostLoad, "PostLoad", true) ?? ValidateHumans();
            if (e != null) return e;
            SaveVersion = TextSanitiser.Clean(SaveVersion, 32, "?");
            return null;
        }

        string ValidateHumans()
        {
            if (HumanAgencies == null) return null;
            if (HumanAgencies.Length > StartConfig.MaxRivals) return "more than " + StartConfig.MaxRivals
                + " human agencies";
            for (int i = 0; i < HumanAgencies.Length; i++)
            {
                string e = JoinCheck.Agency(HumanAgencies[i], "HumanAgencies entry", true);
                if (e != null) return e;
                for (int k = 0; k < i; k++) if (HumanAgencies[k] == HumanAgencies[i]) return "an agency listed twice";
            }
            return null;
        }

        string ValidateSeats()
        {
            if (Seats == null) return "Seats missing";
            if (Seats.Length < 1 || Seats.Length > JoinLimits.MaxPlayers) return "Seats outside 1.." + JoinLimits.MaxPlayers;
            bool mine = false;
            for (int i = 0; i < Seats.Length; i++)
            {
                AgencySeat s = Seats[i];
                if (s == null) return "an empty seat";
                string e = s.Validate();
                if (e != null) return e;
                for (int j = 0; j < i; j++)
                {
                    if (Seats[j].Player == s.Player) return "a player seated twice";
                    if (Seats[j].Agency == s.Agency) return "an agency seated twice";
                }
                if (s.Player == YourPlayer) mine = true;
            }
            return mine ? null : "YourPlayer has no seat";
        }
    }

    // Host -> joiner: one chunk of the compressed snapshot. The one BINARY message: on the wire it is the 4-byte big-endian
    // index and then the raw bytes, with no JSON and no base64 (Envelope encodes and decodes it itself).
    public sealed class SnapshotChunk : ChunkMessage
    {
        protected override int MaxData { get { return JoinLimits.ChunkSize; } }
    }

    // Joiner -> host: "I have received this many chunks". The sink sends these itself, on the link's reader thread.
    public sealed class SnapshotAck : Message
    {
        public int Next;   // chunks received so far
        public override string Validate()
        {
            int most = (int)(JoinLimits.MaxCompressed / JoinLimits.ChunkSize);
            return Next < 0 || Next > most ? "Next outside 0.." + most : null;
        }
    }

    // Joiner -> host: where the joiner is, for the host's 30 s deadline, and how it ended.
    public sealed class JoinStage : Message
    {
        public int Stage;       // JoinStageKind
        public int Code;        // JoinCodes (a joiner failure for Failed)
        public long DateTicks;  // Loaded: the date the joiner's game is at

        public override string Validate()
        {
            if (Stage < JoinStageKind.Checking || Stage > JoinStageKind.Failed) return "Stage unknown";
            return JoinCheck.Code(Code, "Code") ?? JoinCheck.Ticks(DateTicks, "DateTicks", true);
        }
    }

    // Host -> joiners: a short status for the on-screen line ("<name> is joining").
    public sealed class Notice : Message
    {
        public const int MaxTextChars = 120, MaxTextBytes = 240;

        public int Kind;        // NoticeKind
        public int Player;
        public string Name;     // at most 24, sanitised
        public int Code;
        public string Command;  // HostConsole only: the command the host ran, cleaned, at most MaxTextChars

        public override string Validate()
        {
            if (Kind < NoticeKind.Joining || Kind > NoticeKind.HostConsole) return "Kind unknown";
            if (Player < 0 || Player > HostGate.MaxPlayerIds) return "Player out of range";
            string e = Text(Name, "Name", 24, false) ?? JoinCheck.Code(Code, "Code")
                ?? Text(Command, "Command", MaxTextChars, false);
            if (e != null) return e;
            if (Command != null && Kind != NoticeKind.HostConsole) return "only a console notice carries a command";
            if (Name != null) Name = TextSanitiser.Clean(Name, 24, "");
            if (Command != null) Command = TextBudget.FitBytes(TextSanitiser.Clean(Command, MaxTextChars, ""), MaxTextBytes);
            return null;
        }
    }

    // Client -> host, first message on a rejoin by a client that says it is still in the session. The fingerprint never
    // leaves the session (it is sent only after the credential has passed).
    public sealed class RejoinOffer : Message
    {
        public bool InSession;
        public bool SelfCheckHeld;
        public long DateTicks;
        public long NextDay;
        public Fingerprint Print;   // null when the client has none (the host answers with RejoinCodes.NoPrint)
        // M5: the seq of the last command result the client applied (0 = none). A quick rejoin needs it to equal the
        // last seq the host sent that player, else the rejoin is full (RejoinCodes.CommandsMissed). Absent reads as 0.
        public uint LastResult;

        public override string Validate()
        {
            string e = JoinCheck.Ticks(DateTicks, "DateTicks", true) ?? JoinCheck.Ticks(NextDay, "NextDay", true);
            if (e != null) return e;
            return Print == null ? null : Print.Validate();
        }
    }

    // Host -> client: quick (resume without a reload) or full (back to the menu and the join path), and why.
    public sealed class RejoinResult : Message
    {
        public bool Quick;
        public int Code;   // RejoinCodes
        // M7 (set only with Code Resume): the stream continues at ResumeNextSeq, and the host's clock is paused at
        // ResumePausedTicks for the catch-up. Both 0 otherwise.
        public uint ResumeNextSeq; public long ResumePausedTicks;

        public override string Validate()
        {
            string e = JoinCheck.Code(Code, "Code");
            if (e != null) return e;
            if (Code != RejoinCodes.Resume) return ResumeNextSeq != 0 || ResumePausedTicks != 0 ? "only a Resume carries the resume fields" : null;
            return ResumeNextSeq < 1 ? "ResumeNextSeq is 0" : JoinCheck.Ticks(ResumePausedTicks, "ResumePausedTicks", false);
        }
    }
}
