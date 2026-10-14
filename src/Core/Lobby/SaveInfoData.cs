using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Every serialised SaveInfo field with its real type (D 83113-83132), as plain checked data: the mod never deserialises an
    // .info.gz, and a host's info travels as these fields (SaveCopyHeader). Validate is the gate: null = ok, else a fixed
    // reason (never peer text). Fails closed: a missing list or object is invalid.
    public sealed class SaveInfoData
    {
        public const int MaxVersionChars = 40, MaxHistory = 64, RequiredVersionCode = 6;
        // "a real date within the calendar the game uses": the game runs from the 1950s start options to the 2200s; outside is no save.
        public const int FirstYear = 1950, LastYear = 2300;

        public string SaveVersion;
        public DateTime GameDate;
        public StartConfigData StartGameConfiguration;
        public List<string> SaveVersionsHistory = new List<string>();
        public int SaveVersionCode;
        public bool CodeUse;                      // carried as is (the cheat flag)
        public bool IsModded;                     // carried; a rebuilt info file always writes true
        public bool CreatedInDemo, FromCreatedInDemo;

        // Application.version's grammar: digits, dots, spaces and letters, 1..40 characters.
        public static bool IsVersion(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxVersionChars) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '.' || c == ' ';
                if (!ok) return false;
            }
            return true;
        }

        public string Validate()
        {
            if (!IsVersion(SaveVersion)) return "the save's game version is not valid";
            if (GameDate.Year < FirstYear || GameDate.Year > LastYear) return "the save's date is outside the game's calendar";
            if (SaveVersionCode != RequiredVersionCode) return "the save's version code is not " + RequiredVersionCode;
            if (CreatedInDemo || FromCreatedInDemo) return "a demo save is not accepted";
            if (SaveVersionsHistory == null || SaveVersionsHistory.Count > MaxHistory) return "the save's version history is missing or too long";
            foreach (string v in SaveVersionsHistory) if (!IsVersion(v)) return "a version in the save's history is not valid";
            if (StartGameConfiguration == null) return "the save has no start configuration";
            return StartGameConfiguration.Validate();
        }

        public SaveInfoData Clone()
        {
            return new SaveInfoData
            {
                SaveVersion = SaveVersion, GameDate = GameDate, SaveVersionCode = SaveVersionCode, CodeUse = CodeUse, IsModded = IsModded,
                CreatedInDemo = CreatedInDemo, FromCreatedInDemo = FromCreatedInDemo,
                SaveVersionsHistory = SaveVersionsHistory == null ? null : new List<string>(SaveVersionsHistory),
                StartGameConfiguration = StartGameConfiguration == null ? null : StartGameConfiguration.Clone()
            };
        }
    }

    // GameManager.StartGameConfiguration's serialised fields (D 80720-80734).
    public sealed class StartConfigData
    {
        public const int MaxRivals = 64;

        public string GameplayId = "";            // 16 lower-case hex, or "" (DD9's fallback)
        public bool StartWithTutorial;            // false in a session save; a solo load writes false
        public bool SetupAIs;
        public List<string> EnabledRivalCorporations = new List<string>();
        public string Company;
        public int GameDifficulty;                // 0..2 (EGameDifficulty)
        public string PlanetarySystemDescriptor;  // a built-in descriptor id (no custom map in a session, DD6)
        public string StartGameEpoch;

        public string Validate()
        {
            if (GameplayId == null || (GameplayId.Length != 0 && !SaveNames.IsSaveId(GameplayId))) return "the save's gameplay id is not valid";
            if (StartWithTutorial) return "a session save never starts with the tutorial";
            if (EnabledRivalCorporations == null || EnabledRivalCorporations.Count > MaxRivals) return "the save's rival list is missing or too long";
            foreach (string r in EnabledRivalCorporations) if (!LobbyIds.IsContentId(r)) return "a rival in the save is not a content id";
            if (!LobbyIds.IsContentId(Company)) return "the save's company is not a content id";
            if (GameDifficulty < 0 || GameDifficulty > 2) return "the save's difficulty is outside 0..2";
            if (!LobbyIds.IsContentId(PlanetarySystemDescriptor)) return "the save's star system is not a content id";
            if (!LobbyIds.IsContentId(StartGameEpoch)) return "the save's epoch is not a content id";
            return null;
        }

        public StartConfigData Clone()
        {
            return new StartConfigData
            {
                GameplayId = GameplayId, StartWithTutorial = StartWithTutorial, SetupAIs = SetupAIs, Company = Company,
                GameDifficulty = GameDifficulty, PlanetarySystemDescriptor = PlanetarySystemDescriptor, StartGameEpoch = StartGameEpoch,
                EnabledRivalCorporations = EnabledRivalCorporations == null ? null : new List<string>(EnabledRivalCorporations)
            };
        }
    }
}
