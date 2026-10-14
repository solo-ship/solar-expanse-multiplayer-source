using System;

namespace SolarExpanseMultiplayer
{
    // The host's lobby options (plan "Lobby service"). Validate is the gate for anything that came from a screen or a network field.
    public sealed class LobbyOptions
    {
        public int MaxPlayers = SessionLimits.PlayerLimitNoCoop;   // 1..SessionLimits.PlayerLimitNoCoop
        public bool NewGame;                                       // false = a save
        public string SaveId;                                      // 16 hex (a save), "" or null for a new game
        public string Difficulty, Epoch, StarSystem;               // content ids; new game only
        public long StartingMoney;                                 // 0 = the difficulty's
        public bool AiRivals = true;                               // the master switch (new game)
        public bool LateJoin = true;

        public const long MaxStartingMoney = 1000000000000000L;    // 1e15: far above any difficulty, far below overflow

        // null = ok, else a fixed reason (never peer text).
        public string Validate()
        {
            if (MaxPlayers < 1 || MaxPlayers > SessionLimits.PlayerLimitNoCoop) return "the player limit is outside 1 to " + SessionLimits.PlayerLimitNoCoop;
            if (NewGame)
            {
                if (!string.IsNullOrEmpty(SaveId)) return "a new game has no save id";
                if (Difficulty != null && !LobbyIds.IsContentId(Difficulty)) return "the difficulty is not a content id";
                if (Epoch != null && !LobbyIds.IsContentId(Epoch)) return "the epoch is not a content id";
                if (StarSystem != null && !LobbyIds.IsContentId(StarSystem)) return "the star system is not a content id";
                if (StartingMoney < 0 || StartingMoney > MaxStartingMoney) return "the starting money is outside its range";
            }
            else
            {
                if (!SaveNames.IsSaveId(SaveId)) return "a save needs its 16 digit save id";
                if (Difficulty != null || Epoch != null || StarSystem != null || StartingMoney != 0) return "a save keeps its own difficulty, epoch, star system and money";
            }
            return null;
        }

        public LobbyOptionsData ToData()
        {
            return new LobbyOptionsData
            {
                MaxPlayers = MaxPlayers, NewGame = NewGame, SaveId = SaveId ?? "", Difficulty = Difficulty, Epoch = Epoch,
                StarSystem = StarSystem, StartingMoney = StartingMoney, AiRivals = AiRivals, LateJoin = LateJoin,
            };
        }
    }
}
