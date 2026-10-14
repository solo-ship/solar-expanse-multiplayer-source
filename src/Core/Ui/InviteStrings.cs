using System;

namespace SolarExpanseMultiplayer
{
    // B3 "invite": the lines of the lobby's invite strip, the "Leave this game to join?" question, the Steam join
    // problems and the join screen's Steam code note. Added to the table by UiStrings.Build. Plain English
    // (Australian/British); a name from Steam goes in as an argument, cleaned by the caller (PeerText on screen).
    public static class InviteStrings
    {
        public static void Add(Action<string, string> P)
        {
            // the strip
            P("lobby.invite.title", "Steam");
            P("lobby.invite.button", "Invite friends");
            P("lobby.invite.steamDown", "Steam isn't available, so only the LAN code works.");
            P("lobby.invite.notReady", "The Steam lobby isn't ready yet. Try again in a moment.");
            P("lobby.invite.notReadyJoiner", "You can invite friends once you're in the lobby.");
            P("lobby.invite.overlayOff", "Steam's overlay is off. Use the Steam join code below, or ask your friend " +
                "to use Join Game in the Steam friends list.");
            P("lobby.invite.overlayFailed", "Steam's invite window didn't open ({0}). Use the Steam join code below, " +
                "or ask your friend to use Join Game in the Steam friends list.");
            P("lobby.invite.codeOnlyNote", "Code only: friends can't join from Steam. Share the Steam join code.");
            P("lobby.invite.asked", "Steam's invite window is open.");
            P("lobby.invite.who", "Who can join");
            P("lobby.invite.who.friends", "Friends");
            P("lobby.invite.who.invite", "Invite only");
            P("lobby.invite.who.code", "Code only");
            P("lobby.invite.who.friends.hint", "Your Steam friends can join from the friends list.");
            P("lobby.invite.who.invite.hint", "Only people you invite can join.");
            P("lobby.invite.who.code.hint", "Only people with the Steam join code can join.");
            P("lobby.invite.code", "Steam join code");
            P("lobby.invite.copy", "Copy");
            P("lobby.invite.friendsList", "Friends can also join from your Steam friends list: right-click your name > Join Game.");
            P("lobby.invite.copied", "The Steam join code is copied.");
            P("lobby.invite.codeNone", "The Steam join code isn't ready yet.");
            P("lobby.invite.privacy", "Send to a player who is not on your friends list.");

            // "Leave this game to join <name>?"
            P("dialog.leaveFirst.title", "Leave this game?");
            P("dialog.leaveFirst.body", "Leave this game to join {0}?");
            P("dialog.leaveFirst.bodyUnknown", "Leave this game to join a friend's game?");
            P("dialog.leaveFirst.host", "You are hosting this game. Leaving ends it for everyone in it.");
            P("dialog.leaveFirst.player", "You leave this game, and your seat stays yours to come back to.");
            P("dialog.leaveFirst.yes", "Leave and join");
            P("dialog.leaveFirst.no", "Stay here");

            // a Steam join that did not go ahead
            P("dialog.steamJoin.title", "Couldn't join that game");
            P("dialog.steamJoin.ok", "OK");
            P("join.steam.SteamDown", "Steam isn't available right now, so the invitation can't be used. Check that " +
                "Steam is running and online.");
            P("join.steam.NotALobby", "That invitation isn't for a Solar Expanse game.");
            P("join.steam.OwnLobby", "That's the game you are hosting.");
            P("join.steam.NotOurGame", "That invitation isn't for a Solar Expanse Multiplayer game.");
            P("join.steam.OtherProtocol", "That game uses a different multiplayer version from yours. Both players " +
                "need the same version of the mod.");
            P("join.steam.OtherVersion", "That game runs a different version of the mod. Both players need the same " +
                "version.");
            P("join.steam.OtherBuild", "That game is a different version of Solar Expanse from yours. Both players " +
                "need the same game version.");
            P("join.steam.Closed", "The host has closed that game to new players.");
            P("join.steam.Full", "That game is full.");
            P("join.steam.Gone", "That game isn't there any more. Ask your friend to invite you again.");
            P("join.steam.EnterRefused", "Steam wouldn't let you into that game. Try again, or ask your friend to " +
                "invite you again.");
            P("join.steam.HostMismatch", "That invitation doesn't match the host's game. Ask the host to invite you " +
                "again.");
            P("join.steam.TimedOut", "Steam took too long to answer. Try again.");
            P("join.steam.StillInGame", "Leave your current game first, then use the invitation again.");
            P("join.steam.Replaced", "A newer invitation replaced that one.");
            P("join.steam.unknown", "That game couldn't be joined.");

            // the Multiplayer and Join screens' Steam lines (replacing the "coming later" ones)
            P("menu.mp.steamInvite", "Invited on Steam? Accept it, or right-click the host in your Steam friends " +
                "list > Join Game. Friends can join you the same way.");
            P("join.screen.steamNote", "A Steam join code works here too, or accept an invitation in Steam.");
            P("join.screen.kind.home", "This is a LAN code.");
            P("join.screen.kind.steam", "This is a Steam join code.");
            P("host.setup.lateJoin", "Late joiners");
            P("host.setup.on", "On");
            P("host.setup.off", "Off");
        }
    }
}
