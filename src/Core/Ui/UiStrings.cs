using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Every player-facing line of the mod, English (Australian/British), by key (Design decision 12). A key is
    // "area.name"; a line holds positional arguments {0}..{3}, each written exactly {n}, and never a '<' (no rich
    // text).
    // M4's join lines (JoinText) are here, word for word as they were; M5-M7's and M8's lines are added by their own
    // tasks. Text that came from another player is cleaned by TextSanitiser before it is an argument.
    public static class UiStrings
    {
        public static readonly Dictionary<string, string> English = Build();

        static Dictionary<string, string> Build()
        {
            var t = new Dictionary<string, string>(StringComparer.Ordinal);
            Action<string, string> p = (key, text) => t.Add(key, text);
            AddNames(p);
            AddStatus(p);
            AddJoin(p);
            AddRefused(p);
            AddReloads(p);
            AddEarly(p);
            AddMenuScreens(p);
            AddLobby(p);
            AddPicker(p);
            AddHud(p);
            AddDialogs(p);
            AddLobbyService(p);
            AddSaveGlue(p);
            AddCommands(p);
            AddSteam(p);
            LobbyScreenStrings.Add(p);
            InviteStrings.Add(p);
            BanStrings.Add(p);
            LifecycleStrings.Add(p);
            return t;
        }

        // M8 Task 18: the main menu's multiplayer screens (boards 1-4 and 12)
        static void AddMenuScreens(Action<string, string> P)
        {
            P("menu.needsUpdate", "Multiplayer needs a mod update");
            P("menu.back", "Back");
            P("menu.button", "MULTIPLAYER");   // the main menu button (the game's own says "Multiplayer game")
            P("menu.mp.title", "Multiplayer");
            P("menu.mp.intro", "Play one solar system together, with up to five players.");
            P("menu.mp.host", "Host a game");
            P("menu.mp.hostHint", "Pick a save or a new game, then invite friends.");
            P("menu.mp.join", "Join a game");
            P("menu.mp.joinHint", "Enter the join code the host gave you.");
            P("menu.mp.network", "On the same network you can also join with the host's LAN code.");

            P("host.setup.crumb", "Multiplayer ► Host a game");   // the game's own arrow (its page lines)
            P("host.setup.tab.mp", "Multiplayer saves");
            P("host.setup.tab.new", "New game");
            P("host.setup.tab.own", "My saves");
            P("host.setup.newGame", "New game");
            P("host.setup.newGameText", "Next come the game's own new-game pages: star system, start date, and " +
                "difficulty with the rival corporations. Players pick their agencies in the lobby.");
            P("host.setup.newGameEarly", "Pick your agency below. Next come the game's own new-game pages: star " +
                "system, start date and difficulty. Every other agency is an AI rival.");
            P("host.setup.saveKeeps", "A loaded save keeps its own world, date and rivals.");
            P("host.setup.saveClaims", "Every player picks an agency again in the lobby.");
            P("host.setup.saveDate", "Game date: {0}");
            P("host.setup.saveAgencies", "Agencies: {0}");
            P("host.setup.saveHost", "Hosted by {0}");
            P("host.setup.saveAge", "Last played: {0}");
            P("host.setup.ownSaves", "Choose one of my saves");
            P("host.setup.ownSavesText", "Next opens the game's own Load Game window. Pick one of your saves there: " +
                "the session starts once it has loaded, and you play its own agency.");
            P("host.setup.options", "Host options:");
            P("host.setup.name", "Your name");
            P("host.setup.agency", "Your agency");
            P("host.setup.players", "Players");
            P("host.setup.fewer", "-");
            P("host.setup.more", "+");
            P("host.setup.next", "Next");
            P("host.setup.pickOne", "Pick a new game or a save to host.");
            P("host.setup.pickAgency", "Pick your agency first.");

            P("join.screen.title", "Join a game");
            P("join.screen.name", "Your name");
            P("join.screen.code", "Join code");
            P("join.screen.paste", "Paste");
            P("join.screen.codeHint", "The host's lobby shows the code. Paste all of it: it's long because it also " +
                "keeps the connection private.");
            P("join.screen.network", "Paste the host's LAN code or their Steam join code.");
            P("join.screen.join", "Join");
            P("join.screen.nothingToPaste", "The clipboard doesn't hold a join code. Copy it again from the host.");

            P("menu.saves.title", "Multiplayer saves");
            P("menu.saves.button", "Multiplayer saves");
            P("menu.saves.empty", "No multiplayer saves yet. A session you host is saved here.");
            P("menu.saves.session", "Session");
            P("menu.saves.date", "Game date");
            P("menu.saves.agencies", "Agencies");
            P("menu.saves.lastPlayed", "Last played");
            P("menu.saves.players", "{0} (players)");
            P("menu.saves.ai", "{0} (AI)");
            P("menu.saves.idle", "{0} (idle)");
            P("menu.saves.today", "Today");
            P("menu.saves.yesterday", "Yesterday");
            P("menu.saves.days", "{0} days ago");
            P("menu.saves.oneWeek", "A week ago");
            P("menu.saves.weeks", "{0} weeks ago");
            P("menu.saves.playAs", "Play {0} alone, as:");
            P("menu.saves.soloHint", "The other agencies in this save run as AI rivals. Playing alone doesn't " +
                "change the multiplayer save: your solo game is saved under My saves.");
            P("menu.saves.pickSave", "Pick a save to play.");
            P("menu.saves.pickAgency", "Pick the agency to play.");
            P("menu.saves.load", "Load");
            P("menu.saves.deleteTitle", "Delete a multiplayer save");
            P("menu.saves.deleteAsk", "Delete {0}? This can't be undone.");
            P("menu.saves.delete", "Delete");
        }

        // M8 Task 19: the lobby screen (LobbyBoard, src/Game/Ui/Lobby)
        static void AddLobby(Action<string, string> P)
        {
            P("lobby.plain", "{0}");
            P("lobby.title", "{0}'s game");
            P("lobby.titlePlain", "Lobby");
            P("lobby.players.count", "Players ({0} of {1})");
            P("lobby.players.countOpen", "Players ({0})");
            P("lobby.untested", "More than five players is untested.");
            P("lobby.col.player", "Player");
            P("lobby.col.agency", "Agency");
            P("lobby.col.readyHead", "Ready");
            P("lobby.col.status", "Status");
            P("lobby.col.conn", "Connection");
            P("lobby.col.host", "Host");
            P("lobby.col.ready", "Ready");
            P("lobby.col.notReady", "Not ready");
            P("lobby.group.none", "No agency yet");
            P("lobby.group.one", "1 player");
            P("lobby.group.many", "{0} players, sharing");
            P("lobby.role.youHost", "You, the host");
            P("lobby.role.host", "The host");
            P("lobby.role.you", "You");
            P("lobby.you", "You");
            P("lobby.state.lobby", "In the lobby");
            P("lobby.state.joining", "Joining");
            P("lobby.state.joiningPct", "Joining ({0}%)");
            P("lobby.state.inGame", "Playing");
            P("lobby.state.dropped", "Dropped");
            P("lobby.state.away", "Away");
            P("lobby.state.rejoining", "Rejoining");
            P("lobby.conn.thisPc", "This PC");
            P("lobby.conn.none", "-");
            P("lobby.waitingSlot", "Waiting for a player");
            P("lobby.agency.nasa", "NASA");
            P("lobby.agency.esa", "ESA");
            P("lobby.agency.cnsa", "CNSA");
            P("lobby.agency.roscosmos", "Roscosmos");
            P("lobby.agency.solex", "Solex");
            P("lobby.agency.none", "No agency yet");
            P("lobby.names.list", "{0}, {1}");
            P("lobby.names.last", "{0} and {1}");
            P("lobby.section.agencies", "Agencies");
            P("lobby.card.ai", "AI rival");
            P("lobby.card.idle", "Idle");
            P("lobby.card.out", "Not in the game");
            P("lobby.card.open", "Free");
            P("lobby.card.last", "Last played by {0}");
            P("lobby.card.leaveOut", "Leave out");
            P("lobby.card.makeAi", "Make AI rival");
            P("lobby.section.code", "LAN code");
            P("lobby.code.show", "Show");
            P("lobby.code.hide", "Hide");
            P("lobby.code.copy", "Copy code");
            P("lobby.code.copied", "The join code is copied.");
            P("lobby.code.new", "New code");
            P("lobby.code.newDone", "There's a new join code. The old one no longer works.");
            P("lobby.code.privacy", "Anyone with this code can join, and it shows your network address. Share it " +
                "only with the players you invite.");
            P("lobby.section.options", "Host options");
            P("lobby.section.game", "This game");
            P("lobby.opt.game", "Game");
            P("lobby.opt.newGame", "A new game");
            P("lobby.opt.save", "Save");
            P("lobby.opt.date", "Game date");
            P("lobby.opt.players", "Players");
            P("lobby.opt.upTo", "Up to {0}");
            P("lobby.opt.noLimit", "No limit");
            P("lobby.opt.share", "Share an agency");
            P("lobby.opt.on", "On");
            P("lobby.opt.off", "Off");
            P("lobby.opt.ai", "AI rivals");
            P("lobby.opt.none", "None");
            P("lobby.opt.who", "Who can join");
            P("lobby.opt.anyoneWithCode", "Anyone with the code");
            P("lobby.opt.noteJoiner", "Only {0} can change these and invite players.");
            P("lobby.opt.noteShare", "Players who share an agency share its money, research and ships.");
            P("lobby.leave", "Leave lobby");
            P("lobby.close", "Close");
            P("lobby.ready", "Ready");
            P("lobby.unready", "Not ready");
            P("lobby.start", "Start game");
            P("lobby.startAnyway", "Start anyway");
            P("lobby.cancel", "Cancel");
            P("lobby.blocked.notReady", "Everyone needs to be ready before the game starts.");
            P("lobby.status.waiting", "Waiting for {0} to be ready.");
            P("lobby.status.waitingAnyway", "Waiting for {0} to be ready. You can start anyway.");
            P("lobby.status.allReady", "Everyone is ready.");
            P("lobby.status.pickFirst", "Pick your agency, then press Ready.");
            P("lobby.status.pressReady", "Press Ready when you're set.");
            P("lobby.status.youReady", "You're ready. Waiting for {0} to start the game.");
            P("lobby.status.youReadyPlain", "You're ready. Waiting for the host to start the game.");
            P("lobby.status.hostOpen", "Players join with the code while you play. Close this to carry on.");
            P("lobby.status.joinerOpen", "You're in {0}'s game.");
            P("lobby.status.joinerOpenPlain", "You're in the host's game.");
            P("lobby.anyway.title", "Start without everyone?");
            P("lobby.anyway.body", "{0} will stay in the lobby and can join the game once it's running.");
            P("lobby.kick", "Remove");
            P("lobby.kick.title", "Remove {0}?");
            P("lobby.kick.body", "{0} goes back to the main menu and their agency is free again. They can come " +
                "straight back in; only a ban keeps them out.");
        }

        // M8 Task 19: the agency picker (PickerBoard)
        static void AddPicker(Action<string, string> P)
        {
            P("lobby.pick.title", "Pick your agency");
            P("lobby.pick.hint", "Agencies nobody picks stay as they are: an AI rival, or not in the game.");
            P("lobby.pick.hintShare", "Pick a free agency, or share one with the players already in it.");
            P("lobby.pick.none", "There's no agency to pick right now.");
            P("lobby.pick.free", "Free");
            P("lobby.pick.freeAi", "Free (AI rival)");
            P("lobby.pick.freeIdle", "Free (idle)");
            P("lobby.pick.freeOut", "Free (not in the game)");
            P("lobby.pick.yours", "Yours");
            P("lobby.pick.yoursWith", "Yours, with {0}");
            P("lobby.pick.share", "Share with {0}");
            P("lobby.pick.takenBy", "Taken by {0}");
            P("lobby.pick.taken", "Taken");
            P("lobby.pick.notInHost", "Not in the host's game");
            P("lobby.pick.unplayable", "Can't be played");
            P("lobby.pick.refused", "Refused");
            P("lobby.pick.epoch", "Not at this start date");
            P("lobby.pick.epochWhy", "The chosen start date doesn't allow this agency. Pick another one.");
            P("lobby.pick.notInGame", "Not in the game");
            P("lobby.pick.notInGameWhy", "This agency isn't in this game. Pick another one.");
            P("lobby.pick.unavailable", "Not available");
            P("lobby.pick.alreadyYours", "That's already your agency.");
        }

        // M8 Task 20: the in-game strip under the speed buttons (HudModel) and the PLAYERS screen (PlayersModel)
        static void AddHud(Action<string, string> P)
        {
            // the game's own labels for its six speed buttons
            P("hud.speed.0", "Pause");
            P("hud.speed.1", "x1/5");
            P("hud.speed.2", "x1");
            P("hud.speed.3", "x2");
            P("hud.speed.4", "x4");
            P("hud.speed.5", "x8");
            P("hud.chip.you", "You");
            P("hud.chip.more", "+{0}");
            P("hud.chip.slowest", "Slowest");
            // the pause line: "Paused: Sam is planning a mission (1:12); <name> dropped."
            P("hud.paused", "Paused: {0}.");
            P("hud.paused.sep", "; ");
            P("hud.paused.head", "Paused:");
            P("hud.paused.rest", "{0}.");
            P("hud.paused.plain", "Paused.");
            P("hud.why.planning", "{0} is planning a mission ({1})");
            P("hud.why.youPlanning", "you are planning a mission ({0})");
            P("hud.why.dropped", "{0} dropped");
            P("hud.why.joining", "Waiting for {0} to join");
            P("hud.why.joiningPct", "{0} is joining ({1}%)");
            P("hud.why.joiningEta", "{0} is joining ({1}%, about {2} s left)");
            P("hud.why.youJoining", "you are joining");
            P("hud.why.youJoiningPct", "you are joining ({0}%)");
            P("hud.why.paused", "{0} paused");
            P("hud.why.youPaused", "you paused");
            P("hud.why.other", "{0} is busy");
            // the speed play runs at, and whose choice it is
            P("hud.run.now", "Running at {0}: {1}");
            P("hud.run.resume", "When play resumes: {0}, {1}");
            P("hud.run.leadNow", "Running at");
            P("hud.run.leadResume", "When play resumes:");
            P("hud.who.everyone", "everyone's choice.");
            P("hud.who.you", "your choice.");
            P("hud.who.youSlowest", "your choice, the slowest.");
            P("hud.who.one", "{0}'s choice.");
            P("hud.who.oneSlowest", "{0}'s choice, the slowest.");
            P("hud.who.youName", "you");
            P("hud.who.two", "chosen by {0} and {1}.");
            P("hud.who.twoSlowest", "chosen by {0} and {1}, the slowest.");
            P("hud.who.many", "chosen by {0}, {1} and {2} more.");
            P("hud.who.manySlowest", "chosen by {0}, {1} and {2} more, the slowest.");
            P("hud.locked", "The game is ending, so its speed is locked.");
            P("hud.carryOn", "Carry on without {0}");
            P("hud.carryOn.hint", "{0}'s seat is kept for when {0} is back.");

            // the PLAYERS screen
            P("hud.players.title", "Players");
            P("hud.players.titleOf", "Players: {0}'s game");
            P("hud.players.count", "Players ({0})");
            P("hud.players.untested", "More than five players is untested.");
            P("hud.players.pingNote", "Ping in milliseconds beside each signal.");
            P("hud.players.noAgency", "No agency yet");
            P("hud.players.one", "1 player");
            P("hud.players.sharing", "{0} players, sharing");
            P("hud.players.you", "{0} (you)");
            P("hud.players.host", "{0} (the host)");
            P("hud.players.youHost", "{0} (you, the host)");
            P("hud.players.thisPc", "This PC");
            P("hud.players.noPing", "-");
            P("hud.players.playing", "Playing");
            P("hud.players.planning", "Planning a mission");
            P("hud.players.paused", "Paused");
            P("hud.players.inLobby", "In the lobby");
            P("hud.players.joining", "Joining");
            P("hud.players.joiningPct", "Joining ({0}%)");
            P("hud.players.joiningEta", "Joining ({0}%, about {1} s left)");
            P("hud.players.dropped", "Dropped, reconnecting");
            P("hud.players.away", "Away");
            P("hud.players.rejoining", "Rejoining");
            P("hud.players.remove", "Remove from session");
            P("hud.players.removeShort", "Remove");
            P("hud.players.colPlayer", "Player");
            P("hud.players.colStatus", "Status");
            P("hud.players.colConnection", "Connection");
            P("hud.players.carryOn", "Carry on");
            P("hud.players.back", "Back");
            P("hud.players.end", "End session");
            P("hud.players.no", "No");
            P("hud.players.log", "Session log");
            P("hud.players.logEmpty", "Nothing has happened in this session yet.");
            P("hud.players.logRepeat", "{0} (x{1})");
            P("hud.players.removeTitle", "Remove {0} from the session?");
            P("hud.players.removeYes", "Remove {0}");
            P("hud.players.removeHint", "{0} can come straight back in; only a ban keeps them out.");
            P("hud.players.removeNoAgency", "{0} goes back to the main menu.");
            P("hud.players.removeFrees", "{0} goes back to the main menu. {1}'s seat is freed: another player can " +
                "join as {1}, and until then the AI runs it.");
            P("hud.players.removeMate", "{0} goes back to the main menu. {1} carries on running {2}.");
            P("hud.players.removeMates", "{0} goes back to the main menu. {1} and {2} more carry on running {3}.");
        }

        // M8 Task 21: the session dialogs (refusals, the session over, a drop, a reload, carry on, leave, end) and the
        // notice cards
        static void AddDialogs(Action<string, string> P)
        {
            P("dialog.ok", "OK");
            P("dialog.refused.title", "Couldn't join {0}'s game");
            P("dialog.refused.titleNoHost", "Couldn't join the game");
            P("dialog.title.left", "You left {0}'s game");
            P("dialog.title.leftNoHost", "You left the game");
            P("dialog.title.cancelled", "Joining cancelled");
            P("dialog.title.removed", "Removed from {0}'s lobby");
            P("dialog.title.removedNoHost", "Removed from the lobby");
            P("dialog.title.banned", "Banned from {0}'s game");
            P("dialog.title.bannedNoHost", "Banned from the game");
            P("dialog.refused.inLog", "The full report is also in your log.");
            P("dialog.refused.copy", "Copy the list");
            P("dialog.agency.title", "Pick another agency");
            P("dialog.agency.back", "Back to the agencies");
            P("dialog.ended.title", "Session over");
            P("dialog.ended.copy", "Your copy of the game is under Load Game, in Multiplayer saves.");
            P("dialog.ended.mpSaves", "Multiplayer saves");
            P("dialog.dropped.title", "Connection lost");
            P("dialog.dropped.body", "Your game is paused while it reconnects to the host.");
            P("dialog.reload.title", "Reloading your world");
            P("dialog.reload.body", "Your game fell out of step with the host's, so your world is being reloaded.");
            P("dialog.reload.progress", "Downloading the host's game: {0}%, about {1} s left");
            P("dialog.reload.progressNoTime", "Downloading the host's game: {0}%");
            P("dialog.reload.waiting", "Waiting for the host's game");
            P("dialog.reload.loading", "Loading the host's game");
            P("dialog.reload.keep", "You keep your agency, as it is in the host's game. The others are paused " +
                "until you're back.");
            P("dialog.carryOn.title", "Carry on without {0}?");
            P("dialog.carryOn.body", "The game is paused until {0} is back. If you carry on, {0}'s seat is kept " +
                "for when {0} is back.");
            P("dialog.carryOn.yes", "Carry on");
            P("dialog.carryOn.no", "Wait");
            P("dialog.leave.title", "Leave the session?");
            P("dialog.leave.body", "You can rejoin while the session runs.");
            P("dialog.leave.button", "Leave session");
            P("dialog.leave.stay", "Stay");
            P("dialog.end.title", "End the session?");
            P("dialog.end.body", "Everyone goes back to the main menu.");
            P("dialog.end.bodyCopy", "The game is saved and copied to {0}, then everyone goes back to the main menu.");
            P("dialog.end.later", "You can host it again later from Multiplayer saves.");
            P("dialog.end.yes", "End session");
            P("dialog.end.no", "No");
            P("dialog.names.two", "{0} and {1}");
            P("dialog.names.more", "{0}, {1}");
            P("notice.dropped", "{0} dropped: waiting to reconnect");
            P("notice.carryOn", "Click to carry on without {0}.");
            P("notice.more", "+{0} more");
        }

        // M8's early track: the answers of the session actions over M4 (M4SessionAdapter) and the pause screen's
        // PLAYERS entry
        static void AddEarly(Action<string, string> P)
        {
            P("menu.notYet", "This isn't available yet.");
            P("menu.notAtMenu", "Go back to the main menu first.");
            P("menu.restartFirst", "You've already played a multiplayer game since Solar Expanse started. Restart " +
                "the game to start another one.");
            P("host.alreadyHosting", "You're already hosting a session.");
            P("host.notHosting", "You aren't hosting a session.");
            P("host.noSave", "Pick a save to host.");
            P("host.loadFailed", "The game couldn't load that save.");
            P("host.noLocalNetwork", "This PC has no local network address to host on.");
            P("host.couldNotStart", "The session couldn't start. The mod's log says why.");
            P("host.demoSave", "A save made in the demo can't be hosted. Pick another save.");
            P("join.noCode", "Enter the host's join code first.");
            P("join.badCode", "That join code isn't valid. Copy it again from the host.");
            P("join.alreadyJoining", "You're already in a session.");
            P("join.badAgency", "That isn't an agency you can pick.");
            P("join.claimPending", "Wait for the host to answer your last pick.");
            P("join.noClaimsLeft", "You can't pick another agency. Try joining again.");
            P("hud.players.button", "Players");
            P("hud.players.ping", "~{0} ms");
        }

        static void AddNames(Action<string, string> P)
        {
            // a player whose name cleans to nothing
            P("name.fallback", "A player");
        }

        // the one status line a screen shows (StatusLine); none of these takes an argument
        static void AddStatus(Action<string, string> P)
        {
            P("status.rejoining", "Rejoining the session...");
            P("status.outOfStep", "Out of step with the host");
            P("status.waitingClock", "Waiting for the host's clock");
            P("status.joining", "Joining...");
            P("status.dropped", "Connection lost: waiting to reconnect");
            P("status.planning", "Paused while a mission is planned");
        }

        static void AddJoin(Action<string, string> P)
        {
            P("join.unknown", "unknown");
            P("join.unsupportedGame", "This game version isn't supported by this version of the mod yet.");

            // version refusals
            P("join.version.game", "The host's game is version {0}, yours is {1}. Both players need the same version.");
            P("join.version.mod", "The host's mod is version {0}, yours is {1}. Both players need the same version.");
            P("join.version.build", "Your game is a different build from the host's " +
                "(version {0}: host {1}, yours {2}). Both players need the same version.");

            // a game in progress ends
            P("join.ended.hostLeft", "The host left, so the session is over.");
            P("join.ended.agencyTaken", "The host took over your agency, so you're no longer in the session.");
            P("join.ended.kicked", "The host removed you from the session.");
            P("join.ended.reconnects", "The connection to the host was lost, so the session is over.");
            P("join.ended.rejoinRefused", "The host didn't let you back in, so the session is over.");
            P("join.ended.protocol",
                "Your game and the host's couldn't understand each other, so the session is over.");

            // the link's words
            P("join.link.kicked", "kicked by the host ({0})");
            P("join.link.joinEnded", "the join ended ({0})");
            P("join.link.stoppedHere", "your game stopped joining (code {0})");
            P("join.link.refusedByHost", "refused by the host (code {0})");
            P("join.moreInLog", "... and {0} more (see the log)");

            // the content report
            P("join.report.managed", "The game's files differ from the host's: verify the game files in Steam.");
            P("join.report.overall",
                "Your game data doesn't match the host's. Both players need the same game and mods.");
            P("join.report.versionPart", " (version {0})");
            P("join.report.bracketed", " ({0})");
            P("join.report.onlyHost", "{0} {1}: the host has it{2}, you don't.");
            P("join.report.onlyJoiner", "{0} {1}: you have it{2}, the host doesn't.");
            P("join.report.versions", "{0} {1}: host {2}, you {3}");
            P("join.report.sameVersion", "{0} {1}: same version{2} but a different file.");
            P("join.report.moreDifferences", "... and {0} more {1} differences");
            P("join.report.categoryDiffers", "{0}: the data differs (the host has {1}, you have {2})");
            P("join.report.missing", "{0}: you're missing {1} that the host has{2}");
            P("join.report.missingAtLeast", "{0}: you're missing at least {1} that the host has{2}");
            P("join.report.extra", "{0}: you have {1} that the host doesn't{2}");
            P("join.report.extraAtLeast", "{0}: you have at least {1} that the host doesn't{2}");
            P("join.report.differ", "{0}: {1} differ{2}");
            P("join.report.differAtLeast", "{0}: at least {1} differ{2}");

            // the host's notices
            P("join.notice.joining", "{0} is joining");
            P("join.notice.joined", "{0} joined");
            P("join.notice.couldNotJoin", "{0} couldn't join");
            P("join.notice.waiting", "{0} is waiting to join");
            P("join.notice.rejoined", "{0} is back");
            P("join.notice.left", "{0} left the game");
            P("join.notice.copySaved", "{0} saved a copy of this game");
            P("join.notice.console", "{0} ran a console command: {1}");

            // the joiner's status line
            P("join.status.checking", "Checking your game against the host's...");
            P("join.status.loading", "Loading the host's world...");
            P("join.status.loaded", "You're in the host's game.");
            P("join.status.failed", "Joining failed.");
            P("join.downloading", "Downloading the host's world: {0} of {1} parts");
            P("join.waiting.one", "Waiting for {0} other player to finish joining...");
            P("join.waiting.many", "Waiting for {0} other players to finish joining...");
            P("join.summary", "Joining {0}'s game on {1} as {2}.");
            P("join.summary.host", "the host");
            P("join.summary.unknownDate", "an unknown date");
            P("join.summary.unknownAgency", "an unknown agency");

            // rejoining
            P("join.rejoin.quick", "You're back in the game.");
            P("join.rejoin.yourAgency", "your agency");
            P("join.rejoin.holds", "You hold {0} in this session. Rejoin as {0}.");
            P("join.rejoin.keptSeat", "You hold {0} in this session, so you are rejoining as {0}.");
            P("join.rejoin.reload.default", "Your world is being reloaded to catch up.");
        }

        // why a join was refused or failed, by JoinCodes number; the number goes to the log
        static void AddRefused(Action<string, string> P)
        {
            P("join.refused.unknown", "Joining failed (code {0}).");
            P("join.refused.1", "Your game is a different build from the host's. Update or verify the game in Steam " +
                "so both players have the same version.");
            P("join.refused.2", "Your multiplayer mod is a different version from the host's. Both players need the " +
                "same version.");
            P("join.refused.3", "Your mods differ from the host's. Both players need the same mods.");
            P("join.refused.4", "Your game data differs from the host's. Both players need the same mods and " +
                "settings.");
            P("join.refused.5", "Your game files differ from the host's. Verify the game files in Steam.");
            P("join.refused.10", "That agency has already been taken. Pick another one.");
            P("join.refused.11", "The host's game doesn't have that agency. Pick another one.");
            P("join.refused.12", "That agency can't be played. Pick another one.");
            P("join.refused.13", "The host's game is full. It holds five players at most.");
            P("join.refused.14", "There's no free agency left for you to play.");
            P("join.refused.15", "Too many of your agency picks were refused, so the host closed the connection. " +
                "Try joining again.");
            P("join.refused.16", "The host's game has ended, so nobody can join it.");
            P("join.refused.17", "The host couldn't prepare its game for you. Ask the host to check their log, then " +
                "try again.");
            P("join.refused.18", "You took too long to pick an agency. Try joining again.");
            P("join.refused.20", "Joining took too long and was stopped. Try joining again.");
            P("join.refused.21", "The host left before you finished joining.");
            P("join.refused.22", "Joining has failed too many times this session, so you can't rejoin. Ask the host " +
                "to invite you again.");
            P("join.refused.23", "Your game and the host's couldn't understand each other, so joining was stopped. " +
                "Check you both have the same version of the multiplayer mod.");
            P("join.refused.24", "Your game data is too different from the host's to list. Both players need the " +
                "same mods.");
            P("join.refused.30", "The host's world is bigger than a join allows, so it wasn't downloaded.");
            P("join.refused.31", "The download didn't match what the host announced, so it was thrown away. Try " +
                "joining again.");
            P("join.refused.32", "The download was damaged on the way, so it was thrown away. Try joining again.");
            P("join.refused.33", "The downloaded world couldn't be unpacked. Try joining again.");
            P("join.refused.34", "The host's world holds data your game doesn't accept, so it wasn't loaded.");
            P("join.refused.35", "The host's world was damaged or malformed, so it wasn't loaded.");
            P("join.refused.36", "The host's world refers to game content you don't have. Check that you have the " +
                "same mods.");
            P("join.refused.37", "The host's game has already ended, so there's nothing to join.");
            P("join.refused.38", "The agency you picked isn't in the host's world.");
            P("join.refused.39", "You declined to join.");
            P("join.refused.40", "Your game ran into an error while loading the host's world. Try joining again.");
            P("join.refused.41", "After loading, your game data didn't match the host's. Check that you have the " +
                "same mods.");
            P("join.refused.42", "The host's clock details were invalid, so the join was stopped.");
            P("join.refused.43", "The host sent invalid join details, so the join was stopped.");
            P("join.refused.44", "Your game loaded a different date from the host's, so the join was stopped.");
            P("join.refused.45", "The host's world failed the safety check, so it wasn't loaded.");
            P("join.refused.46", "The host stopped responding. Try joining again.");
            P("join.refused.47", "The host's world failed a sanity check, so it wasn't loaded.");
            P("join.refused.48", "Your list of mods and game data is too big to send to the host. Remove some mods " +
                "and try again.");
            P("join.refused.49", "The join was cancelled.");
            P("join.refused.50", "You waited too long for other players to finish joining. Try joining again.");
            P("join.refused.51", "Your game couldn't check its own mods and game data, so it didn't join. Restart " +
                "the game and try again.");
            P("join.refused.52", "The connection to the host kept dropping, so joining was stopped. Try again " +
                "later.");
            P("join.refused.53", "The host didn't let you back in. Ask the host to invite you again.");
            P("join.refused.54", "This game build loads differently; the multiplayer mod needs an update.");
            P("join.refused.55", "Your game fell out of step with the host's, so it is rejoining from a fresh copy.");
            P("join.refused.56", "The host's game didn't let you in. Ask the host for a new invite.");
            P("join.refused.57", "Couldn't connect to the host. Check that the host's game is still open, then try " +
                "again.");
            P("join.refused.60", "The lobby is full.");
            P("join.refused.61", "The host has closed this game to new players.");
            P("join.refused.62", "The host has banned you from this game.");
            P("join.refused.63", "That agency isn't available in the epoch the host chose.");
            P("join.refused.64", "That agency can't be claimed.");
            P("join.refused.65", "The host removed you from the lobby.");
            P("join.refused.66", "Your player key wasn't accepted. Restart the game and try joining again.");
            P("join.refused.67", "You can join again while the host's game is open.");
        }

        // M8 Task 15: the save copy glue, the Multiplayer saves folder and solo play
        static void AddSaveGlue(Action<string, string> P)
        {
            P("notice.saveHostOnly", "Only the host saves. You get a copy of each save.");
            P("notice.saveRefused", "The session save couldn't be written to Multiplayer saves. The mod's log says why.");
            P("notice.saveFailed", "The session save failed. The mod's log says why.");
            P("notice.copyInstalled", "A copy of {0} was added to Multiplayer saves.");
            P("notice.copyRefused", "A copy of the session save was refused and not kept. The mod's log says why.");
            P("menu.saves.gone", "That save isn't in Multiplayer saves any more.");
            P("menu.saves.deleteFailed", "That save couldn't be deleted. The mod's log says why.");
            P("menu.solo.refused", "That save can't be played alone. The mod's log says why.");
            P("menu.solo.busy", "A save is already loading.");
            P("menu.solo.failed", "The save couldn't be loaded. The mod's log says why.");
        }

        // why a rejoin is a full reload, by RejoinCodes number
        static void AddReloads(Action<string, string> P)
        {
            P("join.rejoin.reload.1", "The game moved on while you were away, so your world is being reloaded.");
            P("join.rejoin.reload.2", "Your game was restarted or left the session, so your world is being " +
                "reloaded.");
            P("join.rejoin.reload.3", "You were away for a while, so your world is being reloaded.");
            P("join.rejoin.reload.4", "Your game's clock was out of step with the host's, so your world is being " +
                "reloaded.");
            P("join.rejoin.reload.5", "Your game no longer matches the host's, so your world is being reloaded.");
            P("join.rejoin.reload.6", "You hadn't finished joining before you dropped, so your world is being " +
                "loaded again.");
            P("join.rejoin.reload.7", "The host has no record of your drop, so your world is being reloaded.");
            P("join.rejoin.reload.8", "The host couldn't check your game, so your world is being reloaded.");
            P("join.rejoin.reload.9", "The host reloaded its game while you were away, so your world is being " +
                "reloaded.");
            P("join.rejoin.reload.10", "Your game changed while it was paused, so your world is being reloaded.");
            P("join.rejoin.reload.11", "The host's game changed while you were away, so your world is being " +
                "reloaded.");
            P("join.rejoin.reload.12", "The host couldn't compare your game with its own, so your world is being " +
                "reloaded.");
            P("join.rejoin.reload.13", "Some of the host's actions didn't reach you while you were away, so your " +
                "world is being reloaded.");
        }

        // M8 Task 6: why the host's Start is off (LobbyHost.CanStart's keys; lobby.blocked.notReady and .alone are above)
        static void AddLobbyService(Action<string, string> P)
        {
            P("lobby.blocked.hostAgency", "Pick your own agency before the game starts.");
            P("lobby.blocked.noAgency", "Every player needs to pick an agency before the game starts.");
            P("lobby.blocked.check", "Every player's game needs to match yours before the game starts.");
        }

        // M8 Task 7b: the lines M5-M7 show (CommandText, PauseWhy), by key, reworded for players. The refusal lines are
        // cmd.refused.<code> (RefuseCodes), cmd.refused.unknown for any other code.
        static void AddCommands(Action<string, string> P)
        {
            P("cmd.refused.1", "You haven't joined the game yet.");
            P("cmd.refused.2", "That belongs to another agency.");
            P("cmd.refused.3", "That agency is away, so nothing can be done for it right now.");
            P("cmd.refused.4", "You're doing that too quickly. Wait a moment and try again.");
            P("cmd.refused.5", "A newer change replaced that one.");
            P("cmd.refused.6", "The host is busy right now. Try again in a moment.");
            P("cmd.refused.7", "That's too big to send. Try something smaller.");
            P("cmd.refused.8", "That can't be done in multiplayer.");
            P("cmd.refused.10", "That isn't a valid choice.");
            P("cmd.refused.11", "That list changed while you were looking at it. Try again.");
            P("cmd.refused.12", "You don't have the money or resources for that.");
            P("cmd.refused.13", "That isn't unlocked yet.");
            P("cmd.refused.14", "That has already been done.");
            P("cmd.refused.15", "That can't be done here.");
            P("cmd.refused.16", "That's more units than are allowed.");
            P("cmd.refused.17", "That's in use right now.");
            P("cmd.refused.18", "That isn't available in multiplayer yet.");
            P("cmd.refused.19", "The game is over, so nothing can be changed.");
            P("cmd.refused.20", "Your agency has stopped, so nothing can be changed.");
            P("cmd.refused.21", "That's too close to the launch time to change.");
            P("cmd.refused.22", "That flight has already launched.");
            P("cmd.refused.23", "That craft is already on a flight.");
            P("cmd.refused.24", "That flight can't be flown as planned. Check the plan and try again.");
            P("cmd.refused.25", "The destination of that flight is gone.");
            P("cmd.refused.26", "That agency has no room for more presets.");
            P("cmd.refused.27", "Too many flights already use that asteroid, so it can't be crashed into a planet " +
                "now.");
            P("cmd.refused.30", "The price changed. Look at it again.");
            P("cmd.refused.31", "That offer is gone.");
            P("cmd.refused.32", "That offer belongs to another agency.");
            P("cmd.refused.33", "That contract can't be changed right now.");
            P("cmd.refused.34", "That isn't a bomb.");
            P("cmd.refused.35", "That can only be done on your home planet.");
            P("cmd.refused.36", "Flights are due to leave today, so that can't be done yet.");
            P("cmd.refused.37", "That resource isn't traded on the market.");
            P("cmd.refused.38", "That's your own offer.");
            P("cmd.refused.39", "Your agency already has too many offers open.");
            P("cmd.refused.40", "That offer has fewer units left now. Look at it again.");
            P("cmd.refused.unknown", "The host refused that action.");
            P("cmd.flightRefused", "The host didn't accept that flight. {0}");
            P("cmd.planning", "{0} is planning ({1}:{2})");
            P("cmd.waiting", "Waiting for the host...");
            P("cmd.notYet", "Not available in multiplayer yet.");
            P("cmd.bankrupt.yours", "Your agency has gone bankrupt and has stopped.");
            P("cmd.bankrupt.other", "{0} has gone bankrupt and has stopped.");
            P("cmd.catchingUp", "{0} is catching up");
            P("cmd.resumeFailed", "Your game couldn't catch up with the host's, so it's reloading the whole world.");
            P("cmd.contractReward", "Contract complete: {0}. The reward has been paid.");
            P("cmd.outOfStep", "Your game no longer matches the host's, so it's rejoining...");
            P("cmd.keptOutOfStep", "Your game kept going out of sync with the host's, so it stopped trying to " +
                "rejoin. Join again from the menu.");
            P("cmd.asteroidKinds", "Your mods add too many asteroid kinds for multiplayer ({0}; the limit is {1}). " +
                "Remove some mods.");
            P("cmd.pause.prefix", "Paused: {0}");
            P("cmd.pause.host", "The host");
            P("cmd.pause.player", "Player {0}");
            P("cmd.pause.planning", "{0} is planning a mission");
            P("cmd.pause.dropped", "{0} lost connection");
            P("cmd.pause.joining", "{0} is joining");
            P("cmd.pause.catchingUp", "{0} is catching up");
            P("cmd.pause.other", "{0} paused the game");
        }

        // M8 Task 7b: what the Steam door and the lobby reader tell a player who is turned away
        static void AddSteam(Action<string, string> P)
        {
            P("steam.door.not_individual", "Only a person's own Steam account can join this game.");
            P("steam.door.not_secure", "The connection wasn't secure enough, so it was turned away. Try joining " +
                "again.");
            P("steam.door.banned", "The host has banned you from this game.");
            P("steam.door.busy", "Too many people are trying to join at once. Wait a moment and try again.");
            P("steam.door.closed", "The host has closed this game to new players.");
            P("steam.door.not_invited", "You need an invitation from the host to join this game.");
            P("steam.lobby.bad_data", "That isn't a lobby for this game.");
            P("steam.lobby.version", "That game is a different version from yours. Both players need the same " +
                "version.");
            P("steam.lobby.closed", "The host has closed this game to new players.");
            P("steam.lobby.full", "That game is full.");
            P("steam.lobby.not_host", "That invitation doesn't match the host's game. Ask the host to invite you " +
                "again.");
        }
    }
}
