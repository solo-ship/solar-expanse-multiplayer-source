using System;
using System.Diagnostics;
using Opencoding.CommandHandlerSystem;
using Opencoding.Console;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The game's developer console in a session (rules: ConsoleRules, docs/TESTING.md "The developer console").
    // The console (Opencoding's DebugConsole) opens with ` or ~ and runs the [CommandHandler] methods of
    // DevConsoleCommands straight on this game, outside every click capture, so nothing here blocks it. This class
    // hooks the console's one seam, CommandHandlers.BeforeCommandParsedHook (a single delegate the game leaves empty;
    // BeforeCommandExecutedHook is combined by the game itself and the last handler's answer wins, so it is not used):
    //  - on a joiner a command that is not read-only is refused with a note, and the line never reaches the game;
    //  - on the host every command runs as in vanilla, is logged ("console: ..."), and a change the joiners cannot
    //    heal gets a note saying to rejoin them;
    //  - outside a session (solo, menu) it does nothing.
    // The tripwire asks InConsole() so a change made by a console command is tagged "console", not "unrouted".
    public static class ConsoleGuard
    {
        static Func<string, bool> previous;
        static bool installed, seen, wasOpen;

        // Each frame (SempRoot.Update): one log line when the game's console shows up, and one for each open and close,
        // so a run's log says whether the console was there and used.
        public static void Poll()
        {
            try
            {
                if (DebugConsole.Instance == null) return;
                if (!seen) { seen = true; Log.Info("console: the game's developer console is present (open it with the back-tick key)"); }
                bool open = DebugConsole.IsVisible;
                if (open != wasOpen) { wasOpen = open; Log.Info("console: " + (open ? "opened" : "closed")); }
            }
            catch (Exception e) { seen = true; Log.Warn("console: could not read the console's state: " + e.Message); }
        }

        public static void Hook()
        {
            if (installed) return;
            try
            {
                previous = CommandHandlers.BeforeCommandParsedHook;
                CommandHandlers.BeforeCommandParsedHook = Before;
                installed = true;
            }
            catch (Exception e) { Log.Error("console: the guard could not be hooked: " + e.Message); }
        }

        // True: the line is handled here and the game skips it.
        static bool Before(string line)
        {
            try
            {
                if (previous != null && previous(line)) return true;
                string name = ConsoleRules.Name(line);
                if (name == null) return false;
                CommandSession commands = Session.Commands;
                bool joiner = Session.Client != null || (commands != null && !commands.IsHost);
                bool host = !joiner && commands != null && commands.IsHost && commands.Active;
                if (!joiner && !host) return false;
                // a name the console does not know is the game's own "could not be found" to answer
                if (CommandHandlers.GetCommandHandler(name) == null) return false;
                string safe = TextSanitiser.OneLine(name);
                if (joiner)
                {
                    if (ConsoleRules.JoinerMayRun(name))
                    {
                        Log.Info("console: joiner ran " + safe + " (read-only)");
                        return false;
                    }
                    Log.Info("console: joiner refused " + safe + ": " + ConsoleRules.JoinerRefusal);
                    Tell(commands, ConsoleRules.JoinerRefusal, true);
                    return true;
                }
                Log.Info("console: host ran " + safe + " (" + ConsoleRules.Kind(name) + ")");
                string note = ConsoleRules.HostNote(name);
                if (note != null) Tell(commands, note, false);
                return false;
            }
            catch (Exception e)
            {
                // fail closed on a joiner would need the side; a broken guard must not take the console down
                Log.Warn("console: the guard failed: " + e.Message);
                return false;
            }
        }

        // The dev trigger "console": "open" / "close" show or hide the console, anything else is a command line run the way
        // Enter runs it (CommandHandlers.HandleCommand, so the guard and the tripwire see it). For runs that cannot type.
        public static void Dev(string arg)
        {
            if (DebugConsole.Instance == null) { Log.Warn("dev: console: the game has no console"); return; }
            string a = (arg ?? "").Trim();
            if (a == "open") DebugConsole.IsVisible = true;
            else if (a == "close") DebugConsole.IsVisible = false;
            else CommandHandlers.HandleCommand(a);   // the call the console's input box makes on Enter
            Log.Info("dev: console: " + TextSanitiser.OneLine(a.Length > 40 ? a.Substring(0, 40) : a));
        }

        // In the console's own log (a warning shows there) and on the status line.
        static void Tell(CommandSession commands, string text, bool warn)
        {
            if (warn) UnityEngine.Debug.LogWarning(text); else UnityEngine.Debug.Log(text);
            ICaptureSeam seam = commands != null ? ((ICaptureOwner)commands).Capture : null;
            if (seam != null) seam.Note(text);
        }

        // True while a console command is running: HandleCommand is on the stack. (CommandHandlers.CurrentExecutingCommand
        // names it, but a command that throws leaves that name set, so the stack is the proof.)
        public static bool InConsole(out string command)
        {
            command = CommandHandlers.CurrentExecutingCommand;
            if (command == null) return false;
            foreach (StackFrame f in new StackTrace(2, false).GetFrames() ?? new StackFrame[0])
            {
                var m = f.GetMethod();
                if (m != null && m.Name == "HandleCommand" && m.DeclaringType != null &&
                    m.DeclaringType.Name == "CommandHandlers") return true;
            }
            command = null;
            return false;
        }
    }
}
