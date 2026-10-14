using System;
using HarmonyLib;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    [HarmonyPatch(typeof(MenuSceneUI), "Start")]
    public static class MenuMarker
    {
        // Before Start: it asks LoadSaveManager.HasSaves(), so the test save must already be there. HasSaves also deletes
        // unpaired save files in whatever folder the game resolves, so a test profile that fails its self-check exits here,
        // before Start runs (Prefix returns false first). A normal player (no -semp-profile) is never touched.
        static bool Prefix()
        {
            if (!Boot.ProfileFlagGiven) return true;
            string problem = null;
            try
            {
                if (DataIsolation.Paths == null) problem = "test profile is not set up";
                else
                {
                    SelfCheck();
                    if (!GameHooks.IsolationOk || DataIsolation.Unmatched) problem = "isolation self-check failed";
                    else DataIsolation.SeedTestSave();
                }
            }
            catch (Exception e)
            {
                GameHooks.IsolationOk = false;
                problem = "isolation self-check / seeding failed: " + e.Message;
            }
            if (problem == null) return true;
            Log.Error("QUIT: " + problem + " (before the main menu starts)");
            Environment.Exit(3);
            return false;
        }

        // The patched folders must really be inside the profile (the translation folder needs a started game, so it is skipped).
        static void SelfCheck()
        {
            Check("saves", Manager.LoadSaveManager.FullSaveDirectory());
            Check("end-game data", Manager.LoadSaveManager.FullEndGameToStartGameDataDirectory());
            Check("custom maps", Game.SolarSystemLoader.SolarLoader.FullCustomMapsDirectory());
        }

        static void Check(string what, string path)
        {
            if (DataIsolation.IsInProfile(path)) return;
            GameHooks.IsolationOk = false;
            Log.Error("isolation self-check: " + what + " folder is not in the profile: " + path);
        }

        static void Postfix()
        {
            try
            {
                string reason = IsolationGate.Check(Boot.ProfileFlagGiven, Boot.Profile, Boot.GameRootDir,
                                                    GameHooks.IsolationOk && !DataIsolation.Unmatched);
                if (reason != null)
                {
                    Log.Error("QUIT: " + reason);
                    if (Boot.ProfileFlagGiven) Application.Quit();
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error("QUIT: gate failed: " + e.Message);
                if (Boot.ProfileFlagGiven) Application.Quit();
                return;
            }
            try
            {
                if (SempRoot.Instance == null)
                {
                    var go = new GameObject("SolarExpanseMultiplayer");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.AddComponent<SempRoot>();
                }
                Log.Info("main menu ready");
            }
            catch (Exception e) { Log.Error("could not create the mod object: " + e); return; }
            Session.OnMenuReady();   // a joiner's session starts at the menu, and a full rejoin goes on from it
        }
    }
}
