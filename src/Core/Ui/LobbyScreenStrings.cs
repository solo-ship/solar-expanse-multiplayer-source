using System;

namespace SolarExpanseMultiplayer
{
    // M8 Task 26 / Beta B3: the lines the capabilities turn on (kept apart from UiStrings.cs, which other branches edit).
    public static class LobbyScreenStrings
    {
        public static void Add(Action<string, string> P)
        {
            P("lobby.opt.late", "Late joiners");
        }
    }
}
