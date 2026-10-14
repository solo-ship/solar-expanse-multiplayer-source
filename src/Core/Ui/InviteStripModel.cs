namespace SolarExpanseMultiplayer
{
    // What the invite strip reads from the game, gathered by the screen's source (live Steam, or a test's numbers).
    public sealed class InviteInputs
    {
        public bool IsHost;            // this game hosts the lobby (only the host sets who can join and has the code)
        public bool SteamUsable;       // Steam up, signed in
        public bool LobbyReady;        // host: its lobby exists; member: it is in the host's lobby
        public bool OverlayOn;         // Steam's overlay is up in this game
        public LobbyAccess Access;     // the host's setting
        public string SteamCode;       // host only; null or empty when none
    }

    // The strip's state, by role, access and what Steam offers: which controls show, which are on, and the lines.
    // Any lobby member may invite (the user's decision); only the host chooses who can join and holds the code.
    public sealed class InviteStrip
    {
        public bool InviteShown, InviteEnabled;
        public string InviteHintKey;          // under the button, or null
        public bool WhoShown, WhoEnabled;
        public LobbyAccess Selected;
        public string WhoHintKey;             // the selected choice's line; null when the host's choice is hidden
        public bool CodeShown, CopyEnabled;
        public string CodeNoteKey;            // "isn't ready yet" when the host has no code yet, else null

        public static readonly LobbyAccess[] Choices =
            { LobbyAccess.FriendsOnly, LobbyAccess.InviteOnly, LobbyAccess.CodeOnly };

        public static string ChoiceKey(LobbyAccess a)
        {
            return a == LobbyAccess.InviteOnly ? "lobby.invite.who.invite"
                : a == LobbyAccess.CodeOnly ? "lobby.invite.who.code" : "lobby.invite.who.friends";
        }

        public static string ChoiceHintKey(LobbyAccess a) { return ChoiceKey(a) + ".hint"; }

        public static InviteStrip Build(InviteInputs i)
        {
            var s = new InviteStrip { InviteShown = true, Selected = i.Access };
            if (!i.SteamUsable)
            {
                s.InviteHintKey = "lobby.invite.steamDown";   // nothing of Steam works; the home-network code stays
                return s;
            }
            bool codeOnly = i.IsHost && i.Access == LobbyAccess.CodeOnly;
            s.InviteEnabled = i.LobbyReady && !codeOnly;
            s.InviteHintKey = !i.LobbyReady ? (i.IsHost ? "lobby.invite.notReady" : "lobby.invite.notReadyJoiner")
                : codeOnly ? "lobby.invite.codeOnlyNote"
                : !i.OverlayOn ? "lobby.invite.overlayOff" : null;
            if (!i.IsHost) return s;
            s.WhoShown = true;
            s.WhoEnabled = i.LobbyReady;
            s.WhoHintKey = ChoiceHintKey(i.Access);
            s.CodeShown = true;
            bool has = !string.IsNullOrEmpty(i.SteamCode);
            s.CopyEnabled = has;
            s.CodeNoteKey = has ? null : "lobby.invite.codeNone";
            return s;
        }

        // What shows after a click on "Invite friends" once OpenInviteOverlay answered (null = it opened).
        public static string OverlayLine(string why)
        {
            return string.IsNullOrEmpty(why) ? UiText.Get("lobby.invite.asked")
                : UiText.Get("lobby.invite.overlayFailed", why);
        }
    }

    // The "Leave this game to join <name>?" question (or "Leave your lobby to join <name>?" while this game is only
    // in a lobby at the menu) and the Steam join problems, as dialog specs for DialogBox.
    public static class InviteDialogs
    {
        public static DialogSpec LeaveFirst(LobbyJoinRequest r, bool isHost) { return LeaveFirst(r, isHost, false); }

        // At the menu with a lobby (hosted, or joined and not let in yet) or a join that has not loaded: no world of
        // the session is loaded, so what the player leaves is a lobby.
        public static bool LobbyOnly(SessionPhase phase, bool atMenu)
        {
            return atMenu && (phase == SessionPhase.Lobby || phase == SessionPhase.Starting);
        }

        public static DialogSpec LeaveFirst(LobbyJoinRequest r, bool isHost, bool lobbyOnly)
        {
            string name = r != null ? r.FriendName : null;
            string k = lobbyOnly ? "dialog.leaveLobby." : "dialog.leaveFirst.";
            var d = new DialogSpec
            {
                Kind = DialogKind.ConfirmLeave, Id = "leavefirst", TitleKey = k + "title",
                OkKey = "dialog.leaveFirst.yes", OkAction = DialogAction.Leave,
                OtherKey = "dialog.leaveFirst.no", OtherAction = DialogAction.Close,
            };
            d.Lines.Add(string.IsNullOrEmpty(name) ? DialogLine.Of(k + "bodyUnknown")
                : new DialogLine { Key = k + "body", Args = new object[] { name }, Peer = true });
            DialogLine second = DialogLine.Of(k + (isHost ? "host" : "player"));
            second.Small = true;
            d.Lines.Add(second);
            return d;
        }

        public static DialogSpec JoinFailed(LobbyJoinProblem p)
        {
            var d = new DialogSpec
            {
                Kind = DialogKind.Refused, Id = "steamjoin." + p, TitleKey = "dialog.steamJoin.title",
                OkKey = "dialog.steamJoin.ok", OkAction = DialogAction.Close,
            };
            d.Lines.Add(DialogLine.Of(JoinProblemKey(p)));
            return d;
        }

        // The table line for a problem: every problem but None has its own.
        public static string JoinProblemKey(LobbyJoinProblem p)
        {
            string key = "join.steam." + p;
            return p != LobbyJoinProblem.None && UiText.Has(key) ? key : "join.steam.unknown";
        }
    }
}
