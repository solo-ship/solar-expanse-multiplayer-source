using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // M2's dev-only session flags, M3's drift-test flags and M4's join-test flags. They work only together with a test profile: a normal player's
    // game ignores them. Any bad value means no session and no M3 switch at all (fail closed) and a Problem to log.
    // A test host listens on this PC's loopback, or on its home-network address for the two-device test, and nowhere
    // else (BindAddress): across the internet waits for the Steam relay (M9).
    public sealed class DevFlags
    {
        public const string HostFlag = "-semp-host", JoinFlag = "-semp-join", BindFlag = "-semp-bind", PortFlag = "-semp-port",
            SimLagFlag = "-semp-sim-lag", SimJitterFlag = "-semp-sim-jitter", SimLossFlag = "-semp-sim-loss",
            SimRateFlag = "-semp-sim-rate", JoinFileFlag = "-semp-join-file";
        // M3: record daily checksums, and the world switches of a drift measurement (docs/superpowers/plans/2026-10-10-m3-drift.md)
        public const string DriftFlag = "-semp-drift", AiOffFlag = "-semp-ai-off", NoAutosaveFlag = "-semp-no-autosave",
            RandomSeedFlag = "-semp-random-seed", DriftSpeedFlag = "-semp-drift-speed", DriftDaysFlag = "-semp-drift-days";
        // M4: a joiner's agency and test switches, and the host's pause at a day (docs/superpowers/plans/2026-10-10-m4-join.md)
        public const string AgencyFlag = "-semp-agency", PauseAtDayFlag = "-semp-pause-at-day", NoBoundaryCopyFlag = "-semp-no-boundary-copy",
            JoinFaultFlag = "-semp-join-fault", RejoinFlag = "-semp-rejoin", KeepPrewarmFlag = "-semp-keep-prewarm",
            DumpSummaryFlag = "-semp-dump-summary";
        // M8 Task 11: the test window's size, the player-path profile, the consoles kept usable in a session
        public const string WindowFlag = "-semp-window", PlayerPathFlag = "-semp-player-path",
            ConsolesFlag = "-semp-consoles";
        // M5: the command flags (docs/superpowers/plans/2026-10-12-m5-commands.md "Dev flags, triggers and faults"):
        // session flags
        public const string ActionsFlag = "-semp-actions", CommandFaultFlag = "-semp-command-fault",
            NoHumanRulesFlag = "-semp-no-human-rules", NoDayGateFlag = "-semp-no-day-gate",
            ObservatorySpeedFlag = "-semp-observatory-speed";
        // M6: the flight flags (plan "Dev flags, triggers and faults")
        public const string FlightsFlag = "-semp-flights", FlightFaultFlag = "-semp-flight-fault",
            NoFlightRulesFlag = "-semp-no-flight-rules", NoAlignedBoundaryFlag = "-semp-no-aligned-boundary";
        // M7: the shared-world flags (plan "Dev flags, triggers and faults")
        public const string WorldFaultFlag = "-semp-world-fault", NoWorldRulesFlag = "-semp-no-world-rules",
            NoHealFlag = "-semp-no-heal", NoResumeFlag = "-semp-no-resume";
        // M9: force how Steam looks (notrunning, offline, notloggedon, relaydown, relayslow) to see every refusal in one game
        public const string SteamStateFlag = "-semp-steam-state";
        static readonly string[] All =
        {
            HostFlag, JoinFlag, JoinFileFlag, BindFlag, PortFlag, SimLagFlag, SimJitterFlag, SimLossFlag, SimRateFlag,
            DriftFlag, AiOffFlag, NoAutosaveFlag, RandomSeedFlag, DriftSpeedFlag, DriftDaysFlag,
            AgencyFlag, PauseAtDayFlag, NoBoundaryCopyFlag, JoinFaultFlag, RejoinFlag, KeepPrewarmFlag,
            ActionsFlag, CommandFaultFlag, NoHumanRulesFlag, NoDayGateFlag, ObservatorySpeedFlag, DumpSummaryFlag,
            FlightsFlag, FlightFaultFlag, NoFlightRulesFlag, NoAlignedBoundaryFlag,
            WorldFaultFlag, NoWorldRulesFlag, NoHealFlag, NoResumeFlag, SteamStateFlag
        };
        // What no script path may hold: the invalid path characters and the ones that make a path a command or a pipe.
        static readonly char[] PathBadChars = ActionsPathChars();
        static char[] ActionsPathChars()
        {
            var bad = new System.Collections.Generic.List<char>(System.IO.Path.GetInvalidPathChars());
            bad.AddRange(new[] { '<', '>', '|', '"' });
            return bad.ToArray();
        }
        // The faults of the command protocol a test can plant (the game side decides which side each one acts on).
        static readonly string[] CommandFaults =
        {
            "print", "counter", "lateday", "seqgap", "payfail", "gameover", "dropentry", "hook", "fetchlost",
            "counterafter", "restorestruct"
        };
        // The faults of the shared world a test can plant (the game side decides which side each one acts on).
        static readonly string[] WorldFaults =
        {
            "money", "stock", "crew", "offer", "research", "deposit", "fleet", "nogrowth", "extrabody", "aihuman",
            "healloop", "badprint", "doublepay", "resumebad", "nonui", "catchupdrop", "earlycompletion", "nosaveflush",
            "noclientsave", "latestart", "earlyactivate"
        };
        // The faults of the flight protocol a test can plant (the game side decides which side each one acts on).
        static readonly string[] FlightFaults =
        {
            "figures", "removal", "creation", "cancelrace", "insane", "creationfail", "chainfail", "targetgone",
            "launchlost", "pulllost", "unwatched", "trailing", "nocut", "presetflood", "fakeleft", "noga", "a4drop"
        };
        static readonly string[] Task11 = { WindowFlag, PlayerPathFlag, ConsolesFlag };
        // The faults a joiner can plant: the first five perturb the Hello or the summary, fingerprint flips a block of the
        // rejoin offer, the last four arm the dev-only load faults (JoinTestFaults).
        static readonly string[] JoinFaults =
        {
            "build", "version", "plugin", "content", "managed", "fingerprint", "extract", "success", "unityerror",
            "apply", "prewarm-patch", "prewarm-count"
        };
        static readonly Regex AgencyId = new Regex(@"^[a-z0-9_]{1,64}\z");
        static readonly Regex CodeChars = new Regex(@"^[A-Za-z0-9-]{1,200}\z");

        public bool Host;                       // -semp-host
        public string JoinCode;                 // -semp-join <code> (characters checked here, the code itself by JoinCode.Parse)
        public string JoinFile;                 // -semp-join-file <path>: a file holding the code, so the secret is not on the command line
        public IPAddress Bind = IPAddress.Loopback;   // -semp-bind <BindAddress.Rule>: where the host listens
        public int Port;                        // -semp-port <1024-65535>; 0 = any free port
        public int SimLagMs, SimJitterMs, SimLossPercent;
        public int SimRateKiBps;                // -semp-sim-rate <16-100000>: this side's upload, KiB/s; 0 = off
        public bool Drift;                      // -semp-drift: record daily checksums (with or without a session)
        public bool AiOff;                      // -semp-ai-off: rivals' AI off after load
        public bool NoAutosave;                 // -semp-no-autosave: the yearly autosave off
        public int RandomSeed = -1;             // -semp-random-seed <0..999999>: UnityEngine.Random.InitState after load; -1 = not given
        // -semp-drift-speed <5000|25000|50000|100000|200000>: only without a session; 0 = not given
        public float DriftSpeed;
        public int DriftDays;                   // -semp-drift-days <1..800>: stop after this many days (host or solo); 0 = no stop
        public string Agency;                   // -semp-agency <id>: the agency a joiner claims; null = not given (joiners only)
        public int PauseAtDay;                  // -semp-pause-at-day <1-800>: pause at origin + N days + 0.4 day (host or solo); 0 = not given
        public bool NoBoundaryCopy;             // -semp-no-boundary-copy: the negative control, a joiner does not copy the host's day boundaries
        public string JoinFault;                // -semp-join-fault <one of JoinFaults>: a planted fault (joiners only); null = none
        public bool Rejoin;                     // -semp-rejoin: connect with the rejoin key kept in the test profile (joiners only)
        // -semp-keep-prewarm: the negative control, a joiner's load runs the game's habitability warm-up (joiners only)
        public bool KeepPrewarm;
        // -semp-actions <file>: the run's action script (ActionScript), session only; null = none
        public string ActionsFile;
        // -semp-command-fault <one of CommandFaults>: a planted fault, session only; null = none
        public string CommandFault;
        // -semp-no-human-rules: the negative control, the human-agency rules stay off (session only)
        public bool NoHumanRules;
        // -semp-no-day-gate: the negative control, the day gate is always open (session only)
        public bool NoDayGate;
        // -semp-observatory-speed: the game's observatory speed cheat on, since its consoles are off in a session
        public bool ObservatorySpeed;
        // -semp-flights <file>: the run's flight script (FlightScript), session only; null = none
        public string FlightsFile;
        // -semp-flight-fault <one of FlightFaults>: a planted fault, session only; null = none
        public string FlightFault;
        // -semp-no-flight-rules: the negative control, the flight rules (Tasks 7 and 8) stay off (session only)
        public bool NoFlightRules;
        // -semp-no-aligned-boundary: the negative control, the clients' unconditional cut is off (session only)
        public bool NoAlignedBoundary;
        // -semp-world-fault <one of WorldFaults>: a planted fault, session only; null = none
        public string WorldFault;
        // -semp-no-world-rules: the negative control, the world rules (Tasks 8-10) stay off (session only)
        public bool NoWorldRules;
        // -semp-no-heal: the negative control, heals are never asked (session only)
        public bool NoHeal;
        // -semp-no-resume: the negative control, a drop always takes M4's path (session only)
        public bool NoResume;
        // -semp-dump-summary: write the scanned content summary to the profile's data folder (a host or a joiner)
        public SteamState? ForcedSteam;         // -semp-steam-state <name>: SteamReady reports this state (works with or without a session)
        public bool DumpSummary;
        // -semp-window <W>x<H> (640x360 to 7680x4320): the window's size, so screenshots have fixed sizes; 0 = none
        public int WindowWidth, WindowHeight;
        // -semp-player-path: a test profile that runs as a player would (TestSeams.Open is false): isolation stays, the
        // session starts only from the menus, and every flag but -semp-window is refused
        public bool PlayerPath;
        public bool Consoles;                   // -semp-consoles (TestSeams.Open only): consoles stay usable
        public bool Ignored;                    // flags without a test profile
        public string Problem;                  // a bad value: no session, no M3 switch

        public bool SessionWanted { get { return Host || JoinCode != null || JoinFile != null; } }
        public bool DriftWanted { get { return Drift; } }   // a world flag without -semp-drift is allowed: the switches apply, nothing is recorded

        public bool HasWindow { get { return WindowWidth > 0; } }

        public static DevFlags From(string[] args, string profile)
        {
            var f = new DevFlags();
            bool any = false;
            foreach (string flag in All) any |= LaunchArgs.Has(args, flag);
            foreach (string flag in Task11) any |= LaunchArgs.Has(args, flag);
            if (profile == null) { f.Ignored = any; return f; }
            bool playerPath = LaunchArgs.Has(args, PlayerPathFlag);
            if (!TestSeams.Open(profile, playerPath))
            {
                // Every seam asks TestSeams.Open and nothing else. The player-path profile keeps its isolation and the
                // window size (screenshots need fixed sizes) and refuses every other flag; anything else is ignored.
                if (!playerPath || !LaunchArgs.SafeName(profile)) { f.Ignored = any; return f; }
                return PlayerPathFlags(args);
            }
            f.Host = LaunchArgs.Has(args, HostFlag);
            if (LaunchArgs.Has(args, JoinFlag))
            {
                string v = LaunchArgs.Value(args, JoinFlag);
                if (v != null && CodeChars.IsMatch(v) && !LooksLikeFlag(v)) f.JoinCode = v;
                else f.Problem = JoinFlag + " needs a join code";
            }
            if (LaunchArgs.Has(args, JoinFileFlag))
            {
                string v = LaunchArgs.Value(args, JoinFileFlag);
                if (v != null && v.Length > 0 && v.Length <= 260 && v.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0 && !LooksLikeFlag(v)) f.JoinFile = v;
                else f.Problem = JoinFileFlag + " needs the path of a file holding a join code";
            }
            if (f.Host && (f.JoinCode != null || f.JoinFile != null)) f.Problem = "use " + HostFlag + " or a join flag, not both";
            if (f.JoinCode != null && f.JoinFile != null) f.Problem = "use " + JoinFlag + " or " + JoinFileFlag + ", not both";
            if (LaunchArgs.Has(args, BindFlag))
            {
                IPAddress a;
                if (BindAddress.TryParse(LaunchArgs.Value(args, BindFlag), out a)) f.Bind = a;
                else f.Problem = BindFlag + " needs " + BindAddress.Rule + "; public and other addresses are refused";
            }

            f.Consoles = LaunchArgs.Has(args, ConsolesFlag);
            ParseWindow(args, f);
            f.Port = Int(args, PortFlag, 1024, 65535, 0, f);
            f.SimLagMs = Int(args, SimLagFlag, 0, 5000, 0, f);
            f.SimJitterMs = Int(args, SimJitterFlag, 0, 5000, 0, f);
            f.SimLossPercent = Int(args, SimLossFlag, 0, 50, 0, f);
            f.SimRateKiBps = Int(args, SimRateFlag, 16, 100000, 0, f);

            f.Drift = LaunchArgs.Has(args, DriftFlag);
            f.AiOff = LaunchArgs.Has(args, AiOffFlag);
            f.NoAutosave = LaunchArgs.Has(args, NoAutosaveFlag);
            f.RandomSeed = Int(args, RandomSeedFlag, 0, 999999, -1, f);
            f.DriftDays = Int(args, DriftDaysFlag, 1, 800, 0, f);
            if (LaunchArgs.Has(args, DriftSpeedFlag))
            {
                switch (LaunchArgs.Value(args, DriftSpeedFlag))
                {
                    case "5000": f.DriftSpeed = 5000f; break;
                    case "25000": f.DriftSpeed = 25000f; break;
                    case "50000": f.DriftSpeed = 50000f; break;
                    case "100000": f.DriftSpeed = 100000f; break;
                    case "200000": f.DriftSpeed = 200000f; break;
                    default: f.Problem = DriftSpeedFlag + " needs one of 5000, 25000, 50000, 100000, 200000"; break;
                }
            }
            bool joins = LaunchArgs.Has(args, JoinFlag) || LaunchArgs.Has(args, JoinFileFlag);
            if (LaunchArgs.Has(args, DriftSpeedFlag) && (joins || LaunchArgs.Has(args, HostFlag)))
                f.Problem = DriftSpeedFlag + " works only without a session (a game in a session follows the host)";
            if (LaunchArgs.Has(args, DriftDaysFlag) && joins)
                f.Problem = DriftDaysFlag + " works only on a host or a solo game (a client follows the host)";

            // M4. The joiner flags need a join; the pause-at-day flag does not work on one; a joiner starts at the menu.
            const string joinerOnly = " works only on a joiner (it needs -semp-join or -semp-join-file)";
            if (LaunchArgs.Has(args, AgencyFlag))
            {
                string v = LaunchArgs.Value(args, AgencyFlag);
                if (v != null && AgencyId.IsMatch(v)) f.Agency = v;
                else f.Problem = AgencyFlag + " needs an agency id (a-z, 0-9 and _, up to 64 characters)";
                if (!joins) f.Problem = AgencyFlag + joinerOnly;
            }
            f.PauseAtDay = Int(args, PauseAtDayFlag, 1, 800, 0, f);
            if (LaunchArgs.Has(args, PauseAtDayFlag) && joins)
                f.Problem = PauseAtDayFlag + " works only on a host or a solo game (a joiner follows the host)";
            f.NoBoundaryCopy = LaunchArgs.Has(args, NoBoundaryCopyFlag);
            if (f.NoBoundaryCopy && !joins) f.Problem = NoBoundaryCopyFlag + joinerOnly;
            f.Rejoin = LaunchArgs.Has(args, RejoinFlag);
            if (f.Rejoin && !joins) f.Problem = RejoinFlag + joinerOnly;
            f.KeepPrewarm = LaunchArgs.Has(args, KeepPrewarmFlag);
            if (f.KeepPrewarm && !joins) f.Problem = KeepPrewarmFlag + joinerOnly;
            if (LaunchArgs.Has(args, SteamStateFlag))
            {
                SteamState st;
                if (SteamStates.TryParse(LaunchArgs.Value(args, SteamStateFlag), out st)) f.ForcedSteam = st;
                else f.Problem = SteamStateFlag + " needs one of " + string.Join(", ", SteamStates.Names);
            }
            f.DumpSummary = LaunchArgs.Has(args, DumpSummaryFlag);
            if (f.DumpSummary && !joins && !f.Host)
                f.Problem = DumpSummaryFlag + " works only in a session (only a host or a joiner scans a summary)";
            if (LaunchArgs.Has(args, JoinFaultFlag))
            {
                string v = LaunchArgs.Value(args, JoinFaultFlag);
                if (v != null && Array.IndexOf(JoinFaults, v) >= 0) f.JoinFault = v;
                else f.Problem = JoinFaultFlag + " needs one of " + string.Join(", ", JoinFaults);
                if (!joins) f.Problem = JoinFaultFlag + joinerOnly;
            }
            // M5. All of them need a session: the rules, the gate and the stream exist only in one.
            const string sessionOnly = " works only in a session (-semp-host, -semp-join or -semp-join-file)";
            if (LaunchArgs.Has(args, ActionsFlag))
            {
                string v = LaunchArgs.Value(args, ActionsFlag);
                if (v != null && v.Length > 0 && v.Length <= 260 && v.IndexOfAny(PathBadChars) < 0 && !LooksLikeFlag(v))
                    f.ActionsFile = v;
                else f.Problem = ActionsFlag + " needs the path of an action script file";
                if (!f.SessionWanted) f.Problem = ActionsFlag + sessionOnly;
            }
            if (LaunchArgs.Has(args, CommandFaultFlag))
            {
                string v = LaunchArgs.Value(args, CommandFaultFlag);
                if (v != null && Array.IndexOf(CommandFaults, v) >= 0) f.CommandFault = v;
                else f.Problem = CommandFaultFlag + " needs one of " + string.Join(", ", CommandFaults);
                if (!f.SessionWanted) f.Problem = CommandFaultFlag + sessionOnly;
            }
            f.NoHumanRules = LaunchArgs.Has(args, NoHumanRulesFlag);
            if (f.NoHumanRules && !f.SessionWanted) f.Problem = NoHumanRulesFlag + sessionOnly;
            f.NoDayGate = LaunchArgs.Has(args, NoDayGateFlag);
            if (f.NoDayGate && !f.SessionWanted) f.Problem = NoDayGateFlag + sessionOnly;
            f.ObservatorySpeed = LaunchArgs.Has(args, ObservatorySpeedFlag);
            if (f.ObservatorySpeed && !f.SessionWanted) f.Problem = ObservatorySpeedFlag + sessionOnly;
            if (LaunchArgs.Has(args, FlightsFlag))
            {
                string v = LaunchArgs.Value(args, FlightsFlag);
                if (v != null && v.Length > 0 && v.Length <= 260 && v.IndexOfAny(PathBadChars) < 0 && !LooksLikeFlag(v))
                    f.FlightsFile = v;
                else f.Problem = FlightsFlag + " needs the path of a flight script file";
                if (!f.SessionWanted) f.Problem = FlightsFlag + sessionOnly;
            }
            if (LaunchArgs.Has(args, FlightFaultFlag))
            {
                string v = LaunchArgs.Value(args, FlightFaultFlag);
                if (v != null && Array.IndexOf(FlightFaults, v) >= 0) f.FlightFault = v;
                else f.Problem = FlightFaultFlag + " needs one of " + string.Join(", ", FlightFaults);
                if (!f.SessionWanted) f.Problem = FlightFaultFlag + sessionOnly;
            }
            f.NoFlightRules = LaunchArgs.Has(args, NoFlightRulesFlag);
            if (f.NoFlightRules && !f.SessionWanted) f.Problem = NoFlightRulesFlag + sessionOnly;
            f.NoAlignedBoundary = LaunchArgs.Has(args, NoAlignedBoundaryFlag);
            if (f.NoAlignedBoundary && !f.SessionWanted) f.Problem = NoAlignedBoundaryFlag + sessionOnly;
            if (LaunchArgs.Has(args, WorldFaultFlag))
            {
                string v = LaunchArgs.Value(args, WorldFaultFlag);
                if (v != null && Array.IndexOf(WorldFaults, v) >= 0) f.WorldFault = v;
                else f.Problem = WorldFaultFlag + " needs one of " + string.Join(", ", WorldFaults);
                if (!f.SessionWanted) f.Problem = WorldFaultFlag + sessionOnly;
            }
            f.NoWorldRules = LaunchArgs.Has(args, NoWorldRulesFlag);
            if (f.NoWorldRules && !f.SessionWanted) f.Problem = NoWorldRulesFlag + sessionOnly;
            f.NoHeal = LaunchArgs.Has(args, NoHealFlag);
            if (f.NoHeal && !f.SessionWanted) f.Problem = NoHealFlag + sessionOnly;
            f.NoResume = LaunchArgs.Has(args, NoResumeFlag);
            if (f.NoResume && !f.SessionWanted) f.Problem = NoResumeFlag + sessionOnly;
            if (f.CommandFault == "lateday" && !f.NoDayGate) f.Problem = CommandFaultFlag
                + " lateday works only with " + NoDayGateFlag;
            if (joins && LaunchArgs.Has(args, LaunchArgs.AutoLoadFlag))
                f.Problem = LaunchArgs.AutoLoadFlag + " works only without a join (a joiner starts at the main menu)";

            if (f.Problem != null)
            {
                f.Host = false; f.JoinCode = null; f.JoinFile = null;
                f.Bind = IPAddress.Loopback; f.Port = 0; f.SimLagMs = 0; f.SimJitterMs = 0; f.SimLossPercent = 0;
                f.SimRateKiBps = 0;
                f.Drift = false; f.AiOff = false; f.NoAutosave = false; f.RandomSeed = -1; f.DriftSpeed = 0; f.DriftDays = 0;
                f.Agency = null; f.PauseAtDay = 0; f.NoBoundaryCopy = false; f.JoinFault = null; f.Rejoin = false;
                f.KeepPrewarm = false;
                f.ActionsFile = null; f.CommandFault = null; f.NoHumanRules = false; f.NoDayGate = false;
                f.ObservatorySpeed = false; f.DumpSummary = false; f.ForcedSteam = null;
                f.FlightsFile = null; f.FlightFault = null; f.NoFlightRules = false; f.NoAlignedBoundary = false;
                f.WorldFault = null; f.NoWorldRules = false; f.NoHeal = false; f.NoResume = false;
                f.Consoles = false; f.WindowWidth = 0; f.WindowHeight = 0;
            }
            return f;
        }

        // The player-path profile: only -semp-window (and the flag itself) is taken; any other dev flag is a Problem.
        static DevFlags PlayerPathFlags(string[] args)
        {
            var f = new DevFlags { PlayerPath = true };
            foreach (string flag in All)
            {
                if (!LaunchArgs.Has(args, flag)) continue;
                f.Problem = flag + " is refused with " + PlayerPathFlag + " (the profile runs as a player would)";
                break;
            }
            if (f.Problem == null && LaunchArgs.Has(args, ConsolesFlag))
                f.Problem = ConsolesFlag + " is refused with " + PlayerPathFlag
                    + " (the profile runs as a player would)";
            if (f.Problem == null && LaunchArgs.Has(args, LaunchArgs.AutoLoadFlag))
                f.Problem = LaunchArgs.AutoLoadFlag + " is refused with " + PlayerPathFlag
                    + " (the session starts from the menus)";
            if (f.Problem == null) ParseWindow(args, f);   // a bad size is a Problem and leaves the size at 0
            return f;
        }

        static readonly Regex WindowSize = new Regex(@"^([0-9]{3,4})x([0-9]{3,4})\z");

        static void ParseWindow(string[] args, DevFlags f)
        {
            if (!LaunchArgs.Has(args, WindowFlag)) return;
            int w, h;
            if (TryWindowSize(LaunchArgs.Value(args, WindowFlag), out w, out h)) { f.WindowWidth = w; f.WindowHeight = h; }
            else f.Problem = WindowFlag + " needs <width>x<height>, from 640x360 to 7680x4320";
        }

        // "<W>x<H>" within 640x360 to 7680x4320 (the flag's and the `window` dev trigger's one rule); else false and 0.
        public static bool TryWindowSize(string v, out int w, out int h)
        {
            w = 0; h = 0;
            Match m = v == null ? null : WindowSize.Match(v);
            if (m == null || !m.Success
                || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out w)
                || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out h)
                || w < 640 || w > 7680 || h < 360 || h > 4320)
            {
                w = 0; h = 0;
                return false;
            }
            return true;
        }

        // A test profile's window: the -semp-window size when given, else 1280x720 (two games fit on one screen).
        public const int DefaultTestWidth = 1280, DefaultTestHeight = 720;

        public void TestWindow(out int w, out int h)
        {
            bool own = HasWindow && Problem == null;
            w = own ? WindowWidth : DefaultTestWidth;
            h = own ? WindowHeight : DefaultTestHeight;
        }

        // A value that starts with '-' is the next flag, which the missing value swallowed ("-semp-join -semp-drift"). It is a
        // Problem, never a value: otherwise the code check, which allows '-', would take "-semp-drift" as the join code and
        // leave the drift switch on.
        static bool LooksLikeFlag(string v) { return v.Length > 0 && v[0] == '-'; }

        static int Int(string[] args, string flag, int min, int max, int absent, DevFlags f)
        {
            if (!LaunchArgs.Has(args, flag)) return absent;
            string v = LaunchArgs.Value(args, flag);
            int n;
            if (v != null && Regex.IsMatch(v, @"^\d{1,6}\z") && int.TryParse(v, out n) && n >= min && n <= max) return n;
            f.Problem = flag + " needs a whole number from " + min + " to " + max;
            return absent;
        }
    }
}
