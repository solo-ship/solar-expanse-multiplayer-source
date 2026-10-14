using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Data.ScriptableObject;
using ScriptableObjectScripts;
using ScriptableObjectScripts.StartGameConfiguration;
using LoadSaveManager = Manager.LoadSaveManager;
using GameManager = Manager.GameManager;

namespace SolarExpanseMultiplayer
{
    // SaveInfo (the .info.gz) and the game's start configuration as the plain checked SaveInfoData (SaveInfoData.cs), both
    // ways, by content id against this game's content. Nothing here reads an .info.gz: the host's index is written from the
    // game's live state after a save, and a copy's info file is built from the checked fields with this game's own types.
    public static class SaveInfoMap
    {
        static readonly FieldInfo History = AccessTools.Field(typeof(LoadSaveManager), "saveVersionsHistory");
        static readonly FieldInfo CodeUsePrev = AccessTools.Field(typeof(LoadSaveManager), "codeUsePreviousSave");
        static readonly FieldInfo ModdedPrev = AccessTools.Field(typeof(LoadSaveManager), "isModdedInPreviousSave");
        static readonly FieldInfo DemoPrev = AccessTools.Field(typeof(LoadSaveManager), "fromCreatedInDemoPreviousSave");

        static List<MyIDScriptableObject> All()
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            return all != null ? all.AllMyIDScriptableObjects : null;
        }

        static T ById<T>(string id) where T : MyIDScriptableObject
        {
            List<MyIDScriptableObject> all = All();
            if (id == null || all == null) return null;
            foreach (MyIDScriptableObject o in all)
            {
                var t = o as T;
                if (t != null && string.Equals(t.ID, id, StringComparison.Ordinal)) return t;
            }
            return null;
        }

        public static CompanyDefinition Company(string id) { return ById<CompanyDefinition>(id); }

        // What SaveToFile just wrote into the info file, from the same live sources (D 82345-82385).
        public static SaveInfoData FromLive(LoadSaveManager lsm)
        {
            var history = History != null ? History.GetValue(lsm) as List<string> : null;
            bool codeUse = DevConsoleCommands.CheatUse || Flag(CodeUsePrev);
            bool modded = BepInExDetector.IsModded || Flag(ModdedPrev);
            return new SaveInfoData
            {
                SaveVersion = LoadSaveManager.SaveVersion,
                GameDate = MonoBehaviourSingleton<Manager.TimeController>.Instance.CurrentTime,
                SaveVersionCode = SaveInfoData.RequiredVersionCode,
                SaveVersionsHistory = history != null ? new List<string>(history) : new List<string>(),
                StartGameConfiguration = ConfigData(MonoBehaviourSingleton<GameManager>.Instance.StartGameConfig),
                CodeUse = codeUse, IsModded = modded, CreatedInDemo = false, FromCreatedInDemo = Flag(DemoPrev)
            };
        }

        static bool Flag(FieldInfo f)
        {
            try { return f != null && (bool)f.GetValue(null); } catch (Exception) { return false; }
        }

        static StartConfigData ConfigData(GameManager.StartGameConfiguration c)
        {
            var rivals = new List<string>();
            if (c.enabledRivalCorporations != null)
                foreach (CompanyDefinition r in c.enabledRivalCorporations) if (r != null) rivals.Add(r.ID);
            return new StartConfigData
            {
                GameplayId = c.gameplayID ?? "", StartWithTutorial = c.startWithTutorial, SetupAIs = c.setupAIs,
                EnabledRivalCorporations = rivals, Company = c.Company != null ? c.Company.ID : null,
                GameDifficulty = (int)c.GameDifficulty,
                PlanetarySystemDescriptor = c.PlanetarySystemDescriptor != null ? c.PlanetarySystemDescriptor.ID : null,
                StartGameEpoch = c.StartGameEpoch != null ? c.StartGameEpoch.ID : null
            };
        }

        // The game's configuration for these ids, or null and the reason when any id is not in this game's content.
        public static GameManager.StartGameConfiguration ToGame(StartConfigData c, out string problem)
        {
            problem = null;
            CompanyDefinition company = Company(c.Company);
            if (company == null) { problem = "the save's agency is not in this game's content"; return null; }
            var rivals = new List<CompanyDefinition>();
            foreach (string r in c.EnabledRivalCorporations)
            {
                CompanyDefinition d = Company(r);
                if (d == null) { problem = "a rival in the save is not in this game's content"; return null; }
                if (d != company && !rivals.Contains(d)) rivals.Add(d);
            }
            if (!Enum.IsDefined(typeof(GameManager.EGameDifficulty), c.GameDifficulty))
            { problem = "the save's difficulty is not one of this game's"; return null; }
            PlanetarySystemDescriptor system = ById<PlanetarySystemDescriptor>(c.PlanetarySystemDescriptor);
            if (system == null) { problem = "the save's star system is not in this game's content"; return null; }
            StartGameEpoch epoch = ById<StartGameEpoch>(c.StartGameEpoch);
            if (epoch == null) { problem = "the save's epoch is not in this game's content"; return null; }
            if (system.MapEpochToToStartData == null || !system.MapEpochToToStartData.ContainsKey(epoch))
            { problem = "the save's epoch has no start data in its star system"; return null; }
            return new GameManager.StartGameConfiguration
            {
                gameplayID = c.GameplayId ?? "", startWithTutorial = false, setupAIs = c.SetupAIs,
                enabledRivalCorporations = rivals, Company = company,
                GameDifficulty = (GameManager.EGameDifficulty)c.GameDifficulty,
                PlanetarySystemDescriptor = system, StartGameEpoch = epoch
            };
        }

        // The .info.gz of a copy, in the game's own format, built from the checked fields (null + problem when an id does not
        // resolve). A rebuilt file always says "modded", as the mod's saves are.
        public static byte[] BuildInfoGz(SaveInfoData info, out string problem)
        {
            GameManager.StartGameConfiguration cfg = ToGame(info.StartGameConfiguration, out problem);
            if (cfg == null) return null;
            try
            {
                return new Manager.SaveInfo
                {
                    saveVersion = info.SaveVersion, saveVersionCode = info.SaveVersionCode,
                    saveVersionsHistory = new List<string>(info.SaveVersionsHistory), gameDate = info.GameDate,
                    startGameConfiguration = cfg, codeUse = info.CodeUse, isModded = true,
                    createdInDemo = false, fromCreatedInDemo = info.FromCreatedInDemo
                }.Serialize();
            }
            catch (Exception e) { problem = "the info file could not be made (" + e.GetType().Name + ")"; return null; }
        }

        // The statics a normal load sets (D 82623-82631), from the checked info, before a solo load or a hosted copy loads.
        public static string SetLoadStatics(LoadSaveManager lsm, SaveInfoData info)
        {
            try
            {
                if (History == null || CodeUsePrev == null || ModdedPrev == null || DemoPrev == null)
                    return "a load setting of the game was not found";
                History.SetValue(lsm, new List<string>(info.SaveVersionsHistory));
                CodeUsePrev.SetValue(null, info.CodeUse);
                ModdedPrev.SetValue(null, info.IsModded);
                DemoPrev.SetValue(null, info.FromCreatedInDemo);
                DevConsoleCommands.ResetCheatUse();
                return null;
            }
            catch (Exception e) { return "the load settings could not be set (" + e.GetType().Name + ")"; }
        }
    }
}
