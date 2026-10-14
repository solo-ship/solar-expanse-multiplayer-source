using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's Ban on the PLAYERS screen and its list of banned players (Unban). Added to the table by
    // UiStrings.Build. A banned player's name goes in as an argument, as peer text; the Steam id never shows.
    public static class BanStrings
    {
        public static void Add(Action<string, string> P)
        {
            P("hud.players.ban", "Ban");
            P("hud.players.banTitle", "Ban {0}?");
            P("hud.players.banLine", "They stay in the Steam lobby (Steam doesn't let a host remove members), but " +
                "this game turns them away.");
            P("hud.players.banHint", "You can lift the ban later under Banned players on this screen.");
            P("hud.players.banYes", "Ban {0}");
            P("hud.bans.title", "Banned players");
            P("hud.bans.none", "Nobody is banned.");
            P("hud.bans.unban", "Unban");
            P("hud.bans.unknown", "Unknown player");
        }

        // The unban list's names, in the list's order: the name kept with the ban, or "Unknown player".
        public static List<string> Names(IList<BannedPlayer> bans)
        {
            var names = new List<string>();
            if (bans == null) return names;
            foreach (BannedPlayer b in bans)
                names.Add(b != null && !string.IsNullOrEmpty(b.Name) ? b.Name : UiText.Get("hud.bans.unknown"));
            return names;
        }
    }
}
