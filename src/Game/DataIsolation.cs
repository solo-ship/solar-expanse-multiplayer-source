using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // Dev test profiles: the game's own data folders (saves, end-game data, custom maps) move into <game>\UserData\<profile>.
    // The four methods patched here are every use of Application.persistentDataPath in the game's code
    // (checked with tools\dump Refs.exe, 2026-10-09), apart from the demo-save folder, which is only read.
    public static class DataIsolation
    {
        public static ProfilePaths Paths;
        static string realBase;
        static readonly HashSet<string> Logged = new HashSet<string>();

        public static void Init()
        {
            Paths = new ProfilePaths(Boot.GameRootDir, Boot.Profile);
            Log.Info("isolation: test profile data in " + Paths.Root);
        }

        // Set when any path had to go to the _unmatched fallback; the menu gate then refuses to carry on.
        public static volatile bool Unmatched;
        static readonly object Lock = new object();

        // Null when the game's data folder can't be found: everything then goes to the fallback.
        static string RealBase()
        {
            lock (Lock)
            {
                if (realBase == null)
                {
                    try { realBase = Application.persistentDataPath; }
                    catch (System.Exception e) { Log.Error("isolation: persistentDataPath failed: " + e.Message); }
                }
                return realBase;
            }
        }

        static void Redirect(ref string __result)
        {
            string original = __result;
            string moved = null;
            try { moved = PathRedirect.Rewrite(original, RealBase(), Paths.Root); }
            catch (System.Exception e) { Log.Error("isolation: redirect failed: " + e.Message); }
            if (moved == null)
            {
                moved = IsolationFallback.Unmatched(Paths.Root, original);
                Unmatched = true;
                Log.Error("isolation: path not under the data folder, sent to " + moved + " instead: " + original);
            }
            Directory.CreateDirectory(moved);
            bool first;
            lock (Lock) first = Logged.Add(moved);
            if (first) Log.Info("isolation: " + original + " -> " + moved);
            __result = moved;
        }

        // True when the path is inside the profile folder (case and slash style don't matter, no .. allowed).
        public static bool IsInProfile(string path)
        {
            if (string.IsNullOrEmpty(path) || Paths == null) return false;
            string p = path.Replace('/', '\\'), r = Paths.Root.Replace('/', '\\').TrimEnd('\\');
            if (!p.StartsWith(r + "\\", System.StringComparison.OrdinalIgnoreCase) && !p.Equals(r, System.StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string part in p.Split('\\')) if (part == "..") return false;
            return true;
        }

        public static void SeedTestSave()
        {
            Directory.CreateDirectory(Paths.Saves);
            string rb = RealBase();
            if (rb == null) throw new System.InvalidOperationException("no data folder to seed from");
            string realSaves = Path.Combine(rb, "Saves");
            foreach (var copy in SaveSeed.Plan(realSaves, Paths.Saves, Boot.Seed))
            {
                File.Copy(copy.Key, copy.Value, false);
                Log.Info("isolation: seeded " + Path.GetFileName(copy.Value));
            }
        }

        [HarmonyPatch(typeof(Manager.LoadSaveManager), nameof(Manager.LoadSaveManager.FullSaveDirectory))]
        public static class SaveDirectory
        {
            static void Postfix(ref string __result) { Redirect(ref __result); }
        }

        [HarmonyPatch(typeof(Manager.LoadSaveManager), nameof(Manager.LoadSaveManager.FullEndGameToStartGameDataDirectory))]
        public static class EndGameDirectory
        {
            static void Postfix(ref string __result) { Redirect(ref __result); }
        }

        [HarmonyPatch(typeof(Game.SolarSystemLoader.SolarLoader), nameof(Game.SolarSystemLoader.SolarLoader.FullCustomMapsDirectory))]
        public static class CustomMapsDirectory
        {
            static void Postfix(ref string __result) { Redirect(ref __result); }
        }

        [HarmonyPatch(typeof(Game.SolarSystemLoader.SolarLoader), "FullCustomMapsTranslationDirectory")]
        public static class CustomMapsTranslationDirectory
        {
            static void Postfix(ref string __result) { Redirect(ref __result); }
        }

        // Settings > "clear save data" deletes everything in Application.persistentDataPath - the REAL folder.
        [HarmonyPatch(typeof(Game.UI.Windows.SettingsWindow), "OnClearSaveDataButtonClick")]
        public static class NoClearSaveData
        {
            static bool Prefix()
            {
                Log.Warn("isolation: 'clear save data' blocked in a test profile");
                return false;
            }
        }

        // Settings > "clear data" asks to confirm, then calls PlayerPrefs.DeleteAll - the registry key the user's real game shares.
        [HarmonyPatch(typeof(Game.UI.Windows.SettingsWindow), "OnClearDataButtonClick")]
        public static class NoClearData
        {
            static bool Prefix()
            {
                Log.Warn("isolation: 'clear data' blocked in a test profile");
                return false;
            }
        }
    }
}
