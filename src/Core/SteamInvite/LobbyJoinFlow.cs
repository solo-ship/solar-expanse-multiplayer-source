using System;

namespace SolarExpanseMultiplayer
{
    // Where a join request came from: Steam's GameLobbyJoinRequested (a friend's invite accepted, or Join Game in the
    // friends list, while the game runs) or "+connect_lobby <id>" on the command line (the same, with the game closed).
    public enum LobbyJoinSource { Invite, ColdStart }

    public enum LobbyJoinState { Idle, Queued, AskingToLeave, ReadingData, Entering, Ready }

    // Why a lobby join did not go ahead; the screens turn each into their own text (Words is the log's).
    public enum LobbyJoinProblem
    {
        None, SteamDown, NotALobby, OwnLobby, NotOurGame, OtherProtocol, OtherVersion, OtherBuild, Closed, Full, Gone,
        EnterRefused, HostMismatch, TimedOut, StillInGame, Replaced,
    }

    public sealed class LobbyJoinRequest
    {
        public ulong LobbyId;
        public ulong FriendId;        // the friend whose invite or game it was; 0 for a cold start
        public string FriendName;     // Steam's name for them, cleaned; null when unknown
        public LobbyJoinSource Source;
    }

    // A lobby joined and checked: the session may now connect to HostId through Steam's relay with no join code (the
    // host's door admits lobby members).
    public sealed class LobbyJoinTarget
    {
        public ulong LobbyId;
        public ulong HostId;          // the lobby's owner, equal to its semp_host data: the pin
        public string HostName;       // Steam's name for the host, cleaned; null when unknown
        public string SessionId;
    }

    // What the join flow needs from Steam (the real one is SteamLobby, a fake in the tests). Main thread only.
    public interface ILobbySteam
    {
        bool Usable { get; }          // Steam up, signed in, its relay reachable
        bool Settled { get; }         // the game has reached its main menu once: a request waits until then
        bool InGame { get; }          // a session or a loaded game: joining means leaving it first
        bool LobbyOnly { get; }       // of InGame: only a lobby at the menu, no world of the session loaded
        ulong HostedLobby { get; }    // the lobby this game hosts; 0 = none
        bool RequestData(ulong lobby);              // false = Steam would not ask; else OnData follows
        string Data(ulong lobby, string key);       // "" when missing
        bool Join(ulong lobby);                     // false = Steam would not try; else OnEntered follows
        ulong OwnerOf(ulong lobby);                 // known once we are a member
        void Leave(ulong lobby, string why);
        string Name(ulong steamId);                 // cleaned; null when Steam does not know it yet
    }

    // A join request, from Steam's word to a lobby we are in and whose host we trust: ask first when a game is on,
    // read the lobby's data (ours? same versions? open?), join it, check its owner is the host its data names, then
    // hand over (Ready). Every refusal is one Failed with the reason. Never blocks: Steam's answers come back through
    // OnData/OnEntered, and Update enforces the deadlines. One request at a time: a newer one replaces it.
    public sealed class LobbyJoinFlow
    {
        public const double DataTimeoutSeconds = 10, EnterTimeoutSeconds = 15;

        readonly ILobbySteam steam;
        readonly int myProto;
        readonly string myMod, myBuild;
        readonly Func<ulong, string> shortId;
        double deadline;

        public LobbyJoinState State { get; private set; }
        public LobbyJoinRequest Current { get; private set; }
        public ulong JoinedLobby { get; private set; }   // the lobby this game is in as a joiner; 0 = none

        public event Action<LobbyJoinRequest> Received;     // every request, for the screens
        public event Action<LobbyJoinRequest> LeaveFirst;   // a game or a lobby is on: "Leave ... to join X?"
        public event Action<LobbyJoinTarget> Ready;         // joined and checked: start the session
        public event Action<LobbyJoinProblem, string> Failed;

        public LobbyJoinFlow(ILobbySteam steam, int myProto, string myMod, string myBuild, Func<ulong, string> shortId)
        {
            this.steam = steam; this.myProto = myProto; this.myMod = myMod; this.myBuild = myBuild;
            this.shortId = shortId ?? (id => "?");
        }

        public void Request(LobbyJoinRequest r, double now)
        {
            if (r == null) return;
            Log.Info("steam lobby: join request received (" + (r.Source == LobbyJoinSource.Invite
                ? "invite or Join Game from " + shortId(r.FriendId) : "+connect_lobby at start-up") + ", lobby "
                + shortId(r.LobbyId) + ")");
            Raise(Received, r);
            if (!SteamLobbyIds.IsLobby(r.LobbyId)) { Fail(LobbyJoinProblem.NotALobby, null); return; }
            if (r.LobbyId == steam.HostedLobby) { Fail(LobbyJoinProblem.OwnLobby, null); return; }
            if (r.LobbyId == JoinedLobby)
            {
                Log.Info("steam lobby: already in that lobby; nothing to do");
                return;
            }
            if (Current != null && State != LobbyJoinState.Ready) Fail(LobbyJoinProblem.Replaced, null);
            Current = r;
            State = LobbyJoinState.Queued;
            Advance(now);
        }

        // The player said yes to "Leave this game to join X?" and has left it. False (nothing done) while a game is
        // still on.
        public bool Confirm(double now)
        {
            if (State != LobbyJoinState.AskingToLeave) return false;
            if (steam.InGame) { Log.Info("steam lobby: join not started: a game is still on"); return false; }
            Start(now);
            return true;
        }

        public void Decline()
        {
            if (State != LobbyJoinState.AskingToLeave) return;
            Log.Info("steam lobby: join declined by the player");
            Current = null;
            State = LobbyJoinState.Idle;
        }

        // A queued request goes once the game has settled at its menu; Steam's answers that never came time out.
        public void Update(double now)
        {
            if (State == LobbyJoinState.Queued) { Advance(now); return; }
            if ((State == LobbyJoinState.ReadingData || State == LobbyJoinState.Entering) && now >= deadline)
            {
                if (State == LobbyJoinState.Entering) steam.Leave(Current.LobbyId, "the join timed out");
                Fail(LobbyJoinProblem.TimedOut, null);
            }
        }

        void Advance(double now)
        {
            if (!steam.Settled) return;   // stays Queued: the menu is not up yet
            if (steam.InGame)
            {
                State = LobbyJoinState.AskingToLeave;
                Log.Info("steam lobby: " + (steam.LobbyOnly ? "this game is in a lobby" : "a game is on") +
                         "; asking the player to leave it first");
                Raise(LeaveFirst, Current);
                return;
            }
            Start(now);
        }

        void Start(double now)
        {
            if (JoinedLobby != 0) Leave("joining another lobby");
            if (!steam.Usable) { Fail(LobbyJoinProblem.SteamDown, null); return; }
            State = LobbyJoinState.ReadingData;
            deadline = now + DataTimeoutSeconds;
            if (!steam.RequestData(Current.LobbyId)) Fail(LobbyJoinProblem.Gone, "Steam would not read the lobby");
        }

        // Steam's LobbyDataUpdate for a lobby (not for one member).
        public void OnData(ulong lobby, bool ok, double now)
        {
            if (State != LobbyJoinState.ReadingData || Current == null || lobby != Current.LobbyId) return;
            if (!ok) { Fail(LobbyJoinProblem.Gone, null); return; }
            LobbyData d;
            LobbyJoinProblem p = Check(lobby, out d);
            // not a member yet, so Steam does not tell us the owner: the host the data names stands in until then
            if (p == LobbyJoinProblem.None) p = Compatible(d, d.HostId);
            if (p != LobbyJoinProblem.None) { Fail(p, null); return; }
            State = LobbyJoinState.Entering;
            deadline = now + EnterTimeoutSeconds;
            if (!steam.Join(lobby)) Fail(LobbyJoinProblem.EnterRefused, "Steam would not try");
        }

        // Steam's answer to our JoinLobby. A late answer for a lobby we no longer want is left at once.
        public void OnEntered(ulong lobby, bool ok, string why, double now)
        {
            if (State != LobbyJoinState.Entering || Current == null || lobby != Current.LobbyId)
            {
                if (ok && lobby != JoinedLobby) steam.Leave(lobby, "a join nobody waits for any more");
                return;
            }
            if (!ok) { Fail(LobbyJoinProblem.EnterRefused, why); return; }
            // a member now: Steam tells us the owner, which must be the host the data names (a lobby whose owner
            // changed, the host gone and Steam handing it to another member, names no host anyone may trust)
            LobbyData d;
            LobbyJoinProblem p = Check(lobby, out d);
            if (p == LobbyJoinProblem.None) p = Compatible(d, steam.OwnerOf(lobby));
            ulong host = p == LobbyJoinProblem.None ? d.HostId : 0;
            if (p != LobbyJoinProblem.None)
            {
                steam.Leave(lobby, Words(p));
                Fail(p, null);
                return;
            }
            JoinedLobby = lobby;
            State = LobbyJoinState.Ready;
            var target = new LobbyJoinTarget
            {
                LobbyId = lobby, HostId = host, HostName = steam.Name(host),
                SessionId = d.Session,
            };
            Current = null;
            Log.Info("steam lobby: joined " + shortId(lobby) + "; host " + shortId(host) + " checked, handing over");
            Raise(Ready, target);
        }

        // The joiner's session is over (or the player backed out before it began): out of the lobby.
        public void Leave(string why)
        {
            ulong l = JoinedLobby;
            JoinedLobby = 0;
            if (State == LobbyJoinState.Ready) State = LobbyJoinState.Idle;
            if (l != 0) steam.Leave(l, why);
        }

        // The lobby's data through the Steam rules' strict reader: a stranger's lobby can hold anything.
        LobbyJoinProblem Check(ulong lobby, out LobbyData d)
        {
            string error;
            return LobbyData.TryRead(k => steam.Data(lobby, k), out d, out error) ? LobbyJoinProblem.None
                : LobbyJoinProblem.NotOurGame;
        }

        LobbyJoinProblem Compatible(LobbyData d, ulong owner)
        {
            switch (LobbyData.Compatible(d, owner, myProto, myMod, myBuild))
            {
                case null: return LobbyJoinProblem.None;
                case LobbyData.NotHost: return LobbyJoinProblem.HostMismatch;
                case LobbyData.Closed: return LobbyJoinProblem.Closed;
                case LobbyData.Full: return LobbyJoinProblem.Full;
                case LobbyData.Incompatible:
                    return d.Protocol != myProto ? LobbyJoinProblem.OtherProtocol
                        : d.Mod != myMod ? LobbyJoinProblem.OtherVersion : LobbyJoinProblem.OtherBuild;
                default: return LobbyJoinProblem.NotOurGame;
            }
        }

        void Fail(LobbyJoinProblem p, string detail)
        {
            string words = Words(p) + (detail != null ? " (" + TextSanitiser.OneLine(detail) + ")" : "");
            Log.Warn("steam lobby: join failed: " + words);
            Current = null;
            State = JoinedLobby != 0 ? LobbyJoinState.Ready : LobbyJoinState.Idle;
            var f = Failed;
            if (f == null) return;
            try { f(p, words); }
            catch (Exception e) { Log.Error("steam lobby: a Failed handler threw " + e.GetType().Name); }
        }

        static void Raise<T>(Action<T> a, T arg)
        {
            if (a == null) return;
            try { a(arg); }
            catch (Exception e)
            {
                Log.Error("steam lobby: an event handler threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // For the log (the screens have their own text per problem).
        public static string Words(LobbyJoinProblem p)
        {
            switch (p)
            {
                case LobbyJoinProblem.SteamDown: return "Steam can't be used for an internet game right now";
                case LobbyJoinProblem.NotALobby: return "Steam named something that is not a lobby";
                case LobbyJoinProblem.OwnLobby: return "that is this game's own lobby";
                case LobbyJoinProblem.NotOurGame: return "the lobby is not a Solar Expanse Multiplayer game";
                case LobbyJoinProblem.OtherProtocol: return "the host runs a different version of the mod";
                case LobbyJoinProblem.OtherVersion: return "the host runs a different mod version";
                case LobbyJoinProblem.OtherBuild: return "the host runs a different game build";
                case LobbyJoinProblem.Closed: return "the host is not taking players";
                case LobbyJoinProblem.Full: return "the game is full";
                case LobbyJoinProblem.Gone: return "the lobby is gone";
                case LobbyJoinProblem.EnterRefused: return "Steam did not let this game into the lobby";
                case LobbyJoinProblem.HostMismatch: return "the lobby's owner is not the host that made it";
                case LobbyJoinProblem.TimedOut: return "Steam did not answer in time";
                case LobbyJoinProblem.StillInGame: return "a game is still on";
                case LobbyJoinProblem.Replaced: return "a newer join request replaced it";
                default: return "no problem";
            }
        }
    }
}
