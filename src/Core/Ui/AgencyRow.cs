using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public enum AgencyStanding { Human = 1, AiRival = 2, Idle = 3, NotInGame = 4 }

    // One agency as the lobby and the agency picker show it: its state, whether a player may claim it, who holds it.
    public sealed class AgencyRow
    {
        public string Id; public AgencyStanding State; public bool Claimable;
        public bool Playable;                         // the agency can be played at all in this game
        public bool EpochAllows;                      // the chosen epoch allows it
        public List<int> Holders = new List<int>();   // player ids holding it now
        public string LastHolder;                     // the name of the last player who held it, or null
    }
}
