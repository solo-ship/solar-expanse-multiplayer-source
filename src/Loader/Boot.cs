using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    public static class Boot
    {
        const string GuardKey = "SolarExpanseMultiplayer.Started";

        public static string Mode { get; private set; }
        public static string ModDir { get; private set; }
        public static string GameRootDir { get; private set; }
        public static string Profile { get; private set; }
        public static bool ProfileFlagGiven { get; private set; }
        public static string Seed { get; private set; }
        public static string AutoLoad { get; private set; }
        public static string DataDir { get; private set; }          // the mod's own data for this profile (ModData.Dir); null if unusable
        public static string InstallProblem { get; private set; }   // BepInEx mode: main DLL not beside the shim
        public static DevFlags Dev { get; private set; }            // M2 session flags (test profiles only)

        // Plain flags (no Harmony types), set by InstallHooks and read by the watchdog.
        static volatile bool hooksReady, hooksOk;
        const int WatchdogSeconds = 120;

        public static void FromDoorstop() { Start("Doorstop"); }
        public static void FromBepInEx() { Start("BepInEx", null); }
        public static void FromBepInEx(string shimDllPath) { Start("BepInEx", shimDllPath); }

        static void Start(string mode, string shimDllPath = null)
        {
            if (!OnceGuard.TryClaim(GuardKey, mode)) return;   // already started the other way
            try
            {
                Mode = mode;
                ModDir = Path.GetDirectoryName(typeof(Boot).Assembly.Location);
                // -semp- flags count only in a test copy (DevMarker): Steam can start a player's game with a command
                // line someone else chose
                int dropped;
                string[] args = DevMarker.Args(Environment.GetCommandLineArgs(), ModDir, out dropped);
                ProfileFlagGiven = LaunchArgs.Has(args, LaunchArgs.ProfileFlag);
                GameRootDir = GameRoot.FromModDir(ModDir);
                Profile = LaunchArgs.Profile(args);
                Seed = LaunchArgs.Seed(args);
                AutoLoad = LaunchArgs.AutoLoad(args);
                DataDir = ProfileFlagGiven && Profile == null ? null : ModData.Dir(ModDir, Profile);   // never the player's data in a test run
                Log.Init(Path.Combine(Path.Combine(ModDir, "logs"), Profile == null ? "semp.log" : "semp-" + Profile + ".log"));
                Log.Info(ModInfo.Name + " " + ModInfo.Version + " starting via " + mode + "; game "
                         + (GameRootDir != null ? LogPrivacy.Path(GameRootDir, null) : "(not found)") +
                         "; profile " + (Profile ?? (ProfileFlagGiven ? "(rejected name)" : "(none)")));
                if (dropped > 0)
                    Log.Warn(dropped + " multiplayer test flag(s) ignored: this game folder is not a test copy");
                Dev = DevFlags.From(args, Profile);
                if (Dev.Ignored) Log.Warn("multiplayer dev flags ignored: they need a test profile");
                if (Dev.Problem != null) Log.Error("dev flags refused: " + Dev.Problem);
                if (mode == "BepInEx")
                {
                    InstallProblem = InstallCheck.Mismatch(typeof(Boot).Assembly.Location, shimDllPath);
                    if (InstallProblem != null)
                    {
                        Log.Error(InstallProblem);
                        if (ProfileFlagGiven) { Quit(InstallProblem); return; }
                    }
                }
                if (mode == "Doorstop") AppDomain.CurrentDomain.AssemblyResolve += ResolveFromModDir;
                if (ProfileFlagGiven) StartWatchdog();
                WhenGameCodeLoads(InstallHooks);
            }
            catch (Exception e)
            {
                Log.Error("boot failed: " + e);
                if (ProfileFlagGiven || ProfileFlagInArgs()) Quit("boot failed: " + e.Message);
            }
        }

        static bool ProfileFlagInArgs()
        {
            try
            {
                int dropped;
                string mod = ModDir ?? Path.GetDirectoryName(typeof(Boot).Assembly.Location);
                return LaunchArgs.Has(DevMarker.Args(Environment.GetCommandLineArgs(), mod, out dropped),
                                      LaunchArgs.ProfileFlag);
            }
            catch (Exception) { return false; }
        }

        // A test profile must never carry on without its protections: stop the whole game.
        static void Quit(string reason)
        {
            Log.Error("QUIT: " + reason);
            Environment.Exit(3);
        }

        static void StartWatchdog()
        {
            var t = new Thread(() =>
            {
                Thread.Sleep(WatchdogSeconds * 1000);
                if (!(hooksReady && hooksOk)) Quit("hooks never installed within " + WatchdogSeconds + " s");
            });
            t.IsBackground = true;
            t.Name = "SEMP watchdog";
            t.Start();
        }

        // Doorstop starts us before Unity: only the libraries we ship are resolved from our folder.
        static Assembly ResolveFromModDir(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            if (name != "0Harmony") return null;
            string file = Path.Combine(ModDir, name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        }

        static void WhenGameCodeLoads(Action go)
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Assembly-CSharp")) { go(); return; }
            AssemblyLoadEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.LoadedAssembly.GetName().Name != "Assembly-CSharp") return;
                AppDomain.CurrentDomain.AssemblyLoad -= handler;
                // go() can throw before InstallHooks' own try runs (the JIT fails on a missing 0Harmony.dll while compiling the
                // method that touches GameHooks), and Mono may swallow what escapes an AssemblyLoad handler: log it, and in a
                // test profile stop, as every other boot failure does (cloud review G-3).
                try { go(); }
                catch (Exception ex)
                {
                    Log.Error("starting the hooks failed: " + ex);
                    if (ProfileFlagGiven) Quit("starting the hooks failed: " + ex.Message);
                }
            };
            AppDomain.CurrentDomain.AssemblyLoad += handler;
            Log.Info("waiting for the game's code to load");
        }

        // Separate method so Harmony and game types are only touched once the game's code is loaded.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void InstallHooks()
        {
            try
            {
                GameHooks.Install();
                hooksOk = GameHooks.IsolationOk;
                hooksReady = GameHooks.Ready;
                if (ProfileFlagGiven && !(hooksReady && hooksOk)) Quit("test-profile protections are not all in place");
            }
            catch (Exception e)
            {
                Log.Error("hooks failed: " + e);
                if (ProfileFlagGiven) Quit("hooks failed: " + e.Message);
            }
        }
    }
}
