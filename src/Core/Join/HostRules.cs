using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's join rules that need no game (M4 Task 10), so they get real tests; the game glue
    // (src/Game/JoinHost.cs, ClockHost.cs, SnapshotSource.cs, Session.cs) only reads the game and acts on the answers.

    // ------------------------------------------------------------------ the pause at a day (I-11)

    public enum PauseStep { None, StepDown, Pause, Paused, Passed }

    // -semp-pause-at-day N and the host's dev trigger "pause-at-day N" (the same meaning; join-run arms the trigger for
    // every join after the first): the game pauses at origin + N days + PauseDayFraction day, as a pause the host
    // player chose. One game day before it the speed is stepped down to 25000 (when faster), so the pause lands within
    // one 25000 fixed step (about 0.03 day) of N + 0.4 and a join point is never on a day boundary. Arming again
    // replaces the target.
    public sealed class PauseAtDay
    {
        public const float StepDownSpeed = 25000f;
        // a pause the game refused (a planner, the countdown) is asked again this often
        public const long RepeatMs = 1000;

        int day;
        bool armed, checkedStart, stepped, pausing;
        long lastAskMs;

        public int Day { get { return day; } }
        public bool Armed { get { return armed; } }

        // 1..800, as DevFlags allows for the flag; false (and nothing armed) otherwise.
        public bool Arm(int targetDay)
        {
            if (targetDay < 1 || targetDay > 800) return false;
            day = targetDay;
            armed = true;
            checkedStart = stepped = pausing = false;
            lastAskMs = 0;
            return true;
        }

        public static long TargetTicks(long originTicks, int day)
        {
            return originTicks + day * TimeSpan.TicksPerDay
                + (long)(JoinLimits.PauseDayFraction * TimeSpan.TicksPerDay);
        }

        // Once a frame. originTicks: the host's day-0 key (its load moment); nowTicks: its CurrentTime; scale: its
        // speed; nowMs: a monotonic clock. StepDown: set 25000 now. Pause: ask for speed 0 now. Paused: the pause
        // holds, the target is done. Passed: the target was already behind when armed, so nothing is done (a pause
        // there would be a join point the run did not ask for).
        public PauseStep Step(long originTicks, long nowTicks, float scale, long nowMs)
        {
            if (!armed || originTicks < 1) return PauseStep.None;
            long target = TargetTicks(originTicks, day);
            if (!checkedStart)
            {
                checkedStart = true;
                if (nowTicks >= target) { armed = false; return PauseStep.Passed; }
            }
            if (nowTicks >= target || pausing)
            {
                pausing = true;
                if (scale == 0f) { armed = false; return PauseStep.Paused; }
                if (lastAskMs != 0 && nowMs - lastAskMs < RepeatMs) return PauseStep.None;
                lastAskMs = nowMs;
                return PauseStep.Pause;
            }
            if (!stepped && nowTicks >= target - TimeSpan.TicksPerDay && scale > StepDownSpeed)
            {
                stepped = true;
                return PauseStep.StepDown;
            }
            return PauseStep.None;
        }

        // The trigger's text: a day number 1..800, or -1.
        public static int ParseDay(string text)
        {
            int d;
            if (text == null || text.Length == 0 || text.Length > 3) return -1;
            foreach (char c in text) if (c < '0' || c > '9') return -1;
            if (!int.TryParse(text, out d)) return -1;
            return d >= 1 && d <= 800 ? d : -1;
        }
    }

    // ------------------------------------------------------------------ the clock hears only players in game (I-12)

    // ClockHost asks this before it acts on a player's SpeedRequest (its speed, its vote and its planner flag) and
    // before it uses up the player's "fresh" mark, so a joiner that has not loaded can neither steer the clock nor
    // spend the restore its first request after Loaded must be. The first ignored request per player is logged (once
    // more after the player was in game in between). inGame null: every player counts (no join service).
    public sealed class ClockAdmission
    {
        readonly Func<int, bool> inGame;
        readonly HashSet<int> logged = new HashSet<int>();

        public ClockAdmission(Func<int, bool> inGame) { this.inGame = inGame; }

        public bool InGame(int player)
        {
            if (inGame == null) return true;
            try { return inGame(player); }
            catch (Exception) { return false; }   // unknown: the request is not acted on
        }

        // True: act on the request. False: ignore it; logNow says whether this is the first ignored one for the player.
        public bool Admit(int player, out bool logNow)
        {
            if (InGame(player)) { logged.Remove(player); logNow = false; return true; }
            logNow = logged.Add(player);
            return false;
        }

        // A player's link closed: only a player who has loaded holds everyone with a drop hold (DESIGN "Only a player
        // who has loaded into the running game pauses it by dropping"). Asked before the join service hears of the
        // leave, because the leave ends the player's in-game state there.
        public bool DropHolds(int player) { return InGame(player); }
    }

    // ------------------------------------------------------------------ the supported game builds

    public static class BuildSupport
    {
        // null when this mod version supports the build (KnownBuilds); otherwise the line the player sees. The save
        // security data (the allowlist, SaveAliases) is audited only for the known builds, so neither a host nor a
        // joiner runs on another.
        public static string Problem(string gameBuild)
        {
            return KnownBuilds.Find(gameBuild) != null ? null : JoinText.UnsupportedGame;
        }

        // The same, where a test profile's dev "fake-build" trigger (null when there is none) stands in for the build,
        // so the refusal can be seen in the real game.
        public static string Problem(string gameBuild, string fakeBuild)
        {
            return Problem(fakeBuild ?? gameBuild);
        }
    }

    // ------------------------------------------------------------------ kicks

    public static class HostKick
    {
        // The kick takes the player's rejoin keys when the player must not come back on them this session: its joins
        // failed too often, it broke the protocol (else a modified client could rejoin, break a rule and be kicked
        // without end), or the host removed it (removed, or the host's own kick code). A join that merely ended
        // (a refusal, a deadline) leaves them, so the player may try again.
        public static bool RevokesKeys(int code, bool removed)
        {
            return removed || code == JoinCodes.TooManyFailures || code == JoinCodes.Protocol
                || code == DevHostCommands.KickCode;
        }

        // The host's own removal (a kick, or a ban, which the Steam door enforces): the keys go, but the account may
        // come straight back in through the Steam lobby (the user's decision: only a ban keeps a player out). Any
        // other key-taking kick is a broken rule, and the gate then refuses that account's lobby Hello for the
        // session, else a modified client could loop Hello, break a rule and take a new player id without end.
        public static bool ByTheHost(int code)
        {
            return code == JoinCodes.KickedFromLobby || code == JoinCodes.Banned || code == DevHostCommands.KickCode;
        }
    }

    // ------------------------------------------------------------------ the agencies players have joined as

    // The host switches off the AI of every agency a player has loaded as (AiOffOne, never BlockEnableAI), and again
    // after each of its own loads, since a load's SetupPlayerAndRivalAIs may switch them on again. The set is the
    // session's: a host load never empties it. Agencies are kept in the wire form and mapped back to the game's ids by
    // AgencyWire.
    public sealed class JoinedAgencies
    {
        readonly List<string> wire = new List<string>();

        public int Count { get { return wire.Count; } }

        public void Add(string agency)
        {
            if (!string.IsNullOrEmpty(agency) && !wire.Contains(agency)) wire.Add(agency);
        }

        // The game ids to switch off now (after a player loaded, or after a host load): every joined agency the game
        // has, by AgencyWire's unique case-insensitive match. unknown: the joined agencies the game no longer resolves.
        public List<string> GameIds(IEnumerable<string> gameIds, out List<string> unknown)
        {
            var list = new List<string>();
            unknown = new List<string>();
            var ids = new List<string>(gameIds ?? new string[0]);
            foreach (string w in wire)
            {
                string id = AgencyWire.Resolve(w, ids);
                if (id != null) list.Add(id); else unknown.Add(w);
            }
            return list;
        }
    }

    // ------------------------------------------------------------------ a print that may be compared

    public static class PrintCheck
    {
        // The state reader is prepared once per load: only when nobody (Drift, or an earlier print) has prepared it
        // since this load began (preparesAtLoad: its Prepare count then, -1 before any load), or it is not prepared at
        // all.
        public static bool NeedsPrepare(int preparesAtLoad, int preparesNow, bool prepared)
        {
            return !prepared || preparesAtLoad < 0 || preparesNow <= preparesAtLoad;
        }

        // The wire print of one visit, or null when the visit cannot vouch for the state: an area that failed during it
        // (its block is missing or partial) or a snapshot that ran out of room. Null means "no print", which
        // RejoinRules answers with a full rejoin, never a quick one on a partial picture.
        public static Fingerprint From(DaySnapshot s, long originTicks, int areaFailuresBefore, int areaFailuresAfter,
                                       bool overflow)
        {
            if (areaFailuresAfter != areaFailuresBefore || overflow) return null;
            return FingerprintBuild.From(s, originTicks);
        }
    }

    // -semp-join-fault fingerprint: one block's Discrete hash flipped in every print this joiner takes, so its rejoin
    // offer differs from the host's at Discrete (FingerprintDiffers) while its own self-check still holds (both of its
    // prints carry the same flip). A copy; any other fault leaves the print as it is.
    public static class JoinFaultPrint
    {
        public static Fingerprint Apply(Fingerprint p, string fault)
        {
            if (p == null || fault != "fingerprint" || p.Blocks == null || p.Blocks.Length == 0) return p;
            var blocks = new FingerprintBlock[p.Blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                FingerprintBlock b = p.Blocks[i];
                blocks[i] = b == null ? null : new FingerprintBlock
                {
                    Area = b.Area, Agency = b.Agency, Count = b.Count, Exact = b.Exact, Rounded = b.Rounded,
                    Discrete = b.Discrete, Capped = b.Capped,
                };
            }
            if (blocks[0] != null) blocks[0].Discrete = JoinFaultSummary.Flip(blocks[0].Discrete);
            return new Fingerprint
            {
                OriginTicks = p.OriginTicks, DayDiscrete = p.DayDiscrete, DayRounded = p.DayRounded,
                    DayExact = p.DayExact,
                Blocks = blocks,
            };
        }
    }

    // ------------------------------------------------------------------ the host's configuration in the wire forms

    // The host's StartGameConfiguration as the joiner reads it back (src/Game/SnapshotLoad.cs, StartConfigMap):
    // agencies as their lower-cased game ids (AgencyWire.From), the epoch as its index among every StartGameEpoch id
    // sorted ordinally (the glue passes StartConfigMap.EpochIndex, which is AgencyWire.EpochIndex over the game's
    // epochs, the list the joiner's AgencyWire.EpochAt reads), the gameplay id lower-cased when it is hex (else none).
    // One code path for both sides, so the two forms cannot drift apart.
    public static class HostStartConfig
    {
        public static StartConfig From(string playerGameId, IList<string> rivalGameIds, int difficulty, string systemId,
                                       int epochIndex, bool setupAis, string gameplayId)
        {
            string company = AgencyWire.From(playerGameId);
            var rivals = new List<string>();
            if (rivalGameIds != null)
                foreach (string r in rivalGameIds)
                {
                    string w = AgencyWire.From(r);
                    if (string.IsNullOrEmpty(w) || w == company || rivals.Contains(w)) continue;
                    rivals.Add(w);
                }
            return new StartConfig
            {
                Company = company, Rivals = rivals.ToArray(), Difficulty = difficulty, PlanetarySystem = systemId,
                Epoch = epochIndex, SetupAis = setupAis, GameplayId = Gameplay(gameplayId),
            };
        }

        static string Gameplay(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32) return null;
            string lower = id.ToLowerInvariant();
            foreach (char c in lower) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
            return lower;
        }

        // The agencies a joiner may claim, in the wire form: active (the GameObject), enabled, not the World Government
        // and not the host's own. The arrays run in parallel, one entry per company the game has.
        public static List<string> Playable(IList<string> gameIds, IList<bool> active, IList<bool> enabled,
                                            IList<bool> worldGovernment, string playerGameId)
        {
            var list = new List<string>();
            string host = AgencyWire.From(playerGameId);
            for (int i = 0; i < gameIds.Count; i++)
            {
                string w = AgencyWire.From(gameIds[i]);
                if (string.IsNullOrEmpty(w) || w == host || !active[i] || !enabled[i] || worldGovernment[i]) continue;
                if (!list.Contains(w)) list.Add(w);
            }
            return list;
        }
    }

    // ------------------------------------------------------------------ the snapshot's sizes

    public static class GzipFacts
    {
        // The uncompressed size a single-member gzip stream declares in its trailer (ISIZE, the size mod 2^32), or -1
        // when the bytes are too short to be gzip. The host's own self-check gunzips against it, so a wrong value is
        // caught there.
        public static long DeclaredSize(byte[] gz)
        {
            if (gz == null || gz.Length < 18 || gz[0] != 0x1f || gz[1] != 0x8b) return -1;
            int n = gz.Length;
            return (long)(uint)(gz[n - 4] | (gz[n - 3] << 8) | (gz[n - 2] << 16) | (gz[n - 1] << 24));
        }
    }

    // ------------------------------------------------------------------ the planted content faults (test profiles)

    // -semp-join-fault build|version|plugin|content|managed perturbs a joiner's own content summary (and, for content,
    // one item of the first category) so that each refusal path is exercised in the real game. Copies: the scan's own
    // summary is kept.
    public static class JoinFaultSummary
    {
        public const string FakePluginId = "semp.fault.plugin";

        public static ContentSummary Apply(ContentSummary s, string fault)
        {
            if (s == null) return null;
            var c = new ContentSummary
            {
                GameBuild = s.GameBuild, GameVersion = s.GameVersion, ModVersion = s.ModVersion, Loader = s.Loader,
                ManagedHash = s.ManagedHash, ManagedCount = s.ManagedCount, Plugins = Copy(s.Plugins),
                    Assemblies = Copy(s.Assemblies),
                Categories = Copy(s.Categories), Overall = s.Overall,
            };
            switch (fault)
            {
                case "build":
                case "version":
                    // exactly what HelloFault does to the Hello, so the two never disagree
                    var h = new Hello { GameBuild = c.GameBuild, GameVersion = c.GameVersion };
                    HelloFault.Apply(h, fault);
                    c.GameBuild = h.GameBuild;
                    c.GameVersion = h.GameVersion;
                    break;
                case "managed": c.ManagedHash = Flip(c.ManagedHash); break;
                case "plugin":
                    var list = new List<PluginEntry>(c.Plugins ?? new PluginEntry[0]);
                    list.Add(new PluginEntry { Id = FakePluginId, Name = "Planted plugin", Version = "1.0",
                        Sha256 = new string('a', 64), Where = "other" });
                    c.Plugins = list.ToArray();
                    break;
                case "content":
                    if (c.Categories != null && c.Categories.Length > 0
                        && c.Categories[0] != null) c.Categories[0].Hash = Flip(c.Categories[0].Hash);
                    c.Overall = Flip(c.Overall);
                    break;
            }
            return c;
        }

        // For the "content" fault: the first category's first item hash flipped, so the detail report names one item.
        public static ItemList ApplyItems(ItemList items, int category, string fault)
        {
            if (items == null || fault != "content" || category != 0 || items.Hashes == null
                || items.Hashes.Length == 0) return items;
            var hashes = (string[])items.Hashes.Clone();
            hashes[0] = Flip(hashes[0]);
            return new ItemList { Ids = items.Ids, Hashes = hashes, Complete = items.Complete };
        }

        // The last hex digit changed (0 <-> 1, anything else to 0): still well formed, never equal.
        internal static string Flip(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return hex;
            char last = hex[hex.Length - 1];
            return hex.Substring(0, hex.Length - 1) + (last == '0' ? '1' : '0');
        }

        static PluginEntry[] Copy(PluginEntry[] a) { return a == null ? null : (PluginEntry[])a.Clone(); }

        static CategoryHash[] Copy(CategoryHash[] a)
        {
            if (a == null) return null;
            var r = new CategoryHash[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] == null ? null : new CategoryHash { Category = a[i].Category,
                Count = a[i].Count, Hash = a[i].Hash };
            return r;
        }
    }
}
