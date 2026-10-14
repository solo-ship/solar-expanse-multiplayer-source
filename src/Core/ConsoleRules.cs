using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The game's own developer console (Opencoding's DebugConsole, opened with ` or ~; its commands are the
    // [CommandHandler] methods of DevConsoleCommands) in a session. The rule for a test session (docs/TESTING.md,
    // "The developer console"): the host's console works as in vanilla and the joiners heal or rejoin from it; a
    // joiner's world-changing command is refused with a note, because the heal would only undo it (and spend the
    // heal budget doing so); read-only and camera commands work everywhere. Unlisted names are treated as
    // world-changing, so a command a game update adds is refused on a joiner until someone classifies it.
    public enum ConsoleKind
    {
        ReadOnly,   // no world change: display and engine settings
        Heals,      // changes money, stock or population, which M7's heals mend on a joiner
        Rejoin,     // changes something no heal covers (ships, buildings, speed cheats, offers...): rejoin the joiners
        Unknown     // not in the table: a joiner may not run it
    }

    public static class ConsoleRules
    {
        // Names are the game's command names, lower-case (the console matches them case-insensitively).
        static readonly Dictionary<string, ConsoleKind> Kinds = new Dictionary<string, ConsoleKind>
        {
            { "setfpslimit", ConsoleKind.ReadOnly },
            { "setvsync", ConsoleKind.ReadOnly },
            { "hideui", ConsoleKind.ReadOnly },
            { "reportbug", ConsoleKind.ReadOnly },
            { "unlockstartgameepoch", ConsoleKind.ReadOnly },   // main menu only: a setting of the player, not of a world
            { "givemethemony", ConsoleKind.Heals },
            { "addmony", ConsoleKind.Heals },
            { "addresourcestoplanet", ConsoleKind.Heals },
            { "addresourcestoplanetall", ConsoleKind.Heals },
            { "addcrewtoplanet", ConsoleKind.Heals },
            { "addspacecrafttoplanet", ConsoleKind.Rejoin },
            { "addlvtoplanet", ConsoleKind.Rejoin },
            { "addgroundfacilitydescriptorplanet", ConsoleKind.Rejoin },
            { "addmodulestoplanet", ConsoleKind.Rejoin },
            { "createmarketoffers", ConsoleKind.Rejoin },
            { "speedresearch", ConsoleKind.Rejoin },
            { "speedobservatory", ConsoleKind.Rejoin },
            { "unlockcontentuicyclicalmission", ConsoleKind.Rejoin },
            { "markobjectivedone", ConsoleKind.Rejoin }
        };

        public const int MaxNameLength = 64;

        // The command name a console line starts with, lower-case, or null for a blank line. The game splits the line
        // on whitespace and honours quotes; a leading quote is dropped here so a quoted name is the same name.
        public static string Name(string line)
        {
            if (line == null) return null;
            string t = line.Trim();
            if (t.Length == 0) return null;
            int end = 0;
            while (end < t.Length && !char.IsWhiteSpace(t[end])) end++;
            string first = t.Substring(0, end).Trim('"', '\'');
            if (first.Length == 0) return null;
            if (first.Length > MaxNameLength) first = first.Substring(0, MaxNameLength);
            return first.ToLowerInvariant();
        }

        public static ConsoleKind Kind(string name)
        {
            ConsoleKind k;
            return name != null && Kinds.TryGetValue(name, out k) ? k : ConsoleKind.Unknown;
        }

        // A joiner (a game following a host's session) may run only the commands that change no world.
        public static bool JoinerMayRun(string name) { return Kind(name) == ConsoleKind.ReadOnly; }

        // The note for the host after it ran a command, or null when nothing needs saying.
        public static string HostNote(string name)
        {
            return Kind(name) == ConsoleKind.Heals ? null :
                   Kind(name) == ConsoleKind.ReadOnly ? null :
                   "Console change on the host. The joiners cannot heal this one: rejoin them from the host.";
        }

        public static string JoinerRefusal
        {
            get { return "Console changes on a joiner are undone by the host; use the host's console."; }
        }

        public static IEnumerable<string> KnownNames { get { return Kinds.Keys; } }
    }
}
