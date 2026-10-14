using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The Steam lobby, owned by SteamNet (no statics). A host's session is a lobby its friends see and are invited to;
    // a joiner stays in the host's lobby for the whole session, because membership is what the host's door admits.
    // Host: Host() creates it when the session starts hosting, the data keys follow the session (only the owner writes
    // them), EndHosting() leaves it. Joiner: a join request (Steam's GameLobbyJoinRequested while the game runs, or
    // "+connect_lobby <id>" when an invite started it) goes through LobbyJoinFlow to JoinReady, which the menus turn
    // into a session (SessionPlan.JoinSteamLobby). Every Steamworks call is on the main thread, after Steam is up, and
    // none after the game's Steam has shut down; the game's own pump delivers the callbacks. Never blocks: every
    // answer from Steam comes back as a callback. Lobby and Steam ids go to the log in SteamNet.Short form only.
    public sealed class SteamLobby : ILobbySteam, ISteamLobbyView, ISteamMembers
    {
        const float HostWatchSeconds = 2f, FlushEverySeconds = 1f, JoinerWatchSeconds = 3f, SnapshotEverySeconds = 0.5f;

        readonly SteamNet net;
        LobbyJoinFlow flow;
        Callback<GameLobbyJoinRequested_t> joinRequestCb;
        Callback<LobbyDataUpdate_t> dataCb;
        Callback<LobbyChatUpdate_t> chatCb;
        CallResult<LobbyCreated_t> createCall;
        CallResult<LobbyEnter_t> enterCall;
        float nextRegister, nextFlush, nextSnapshot, hostGoneSince = -1f, joinerGoneSince = -1f;
        bool registered, coldStartChecked, menuSeen, joinerSessionSeen;

        // ---- hosting ----
        bool wantHosting, creating;
        CSteamID hosted = CSteamID.Nil;
        LobbyAccess access = LobbyAccess.FriendsOnly;
        int maxMembers;
        bool open = true;
        Func<int> playersNow;
        string sessionId;
        readonly Dictionary<string, string> written = new Dictionary<string, string>();
        bool? writtenJoinable;
        LobbyAccess? writtenAccess;
        readonly HashSet<ulong> askedNames = new HashSet<ulong>();
        // The hosted lobby's members and their Steam names as last read on the main thread, for the link's threads
        // (the Hello check runs off the main thread, where no Steamworks call is made).
        readonly object snapLock = new object();
        Dictionary<ulong, string> memberSnap = new Dictionary<ulong, string>();

        public SteamLobby(SteamNet net) { this.net = net; }

        // ---- what the screens (B3) and the session (B2) use ----

        // Joiner: a friend's invite or Join Game arrived (for the screens), a "Leave this game to join X?" question, a
        // checked lobby to connect to, and every failure with the reason (the screens word each LobbyJoinProblem).
        public event Action<LobbyJoinRequest> JoinRequested;
        public event Action<LobbyJoinRequest> LeaveGameFirst;
        public event Action<LobbyJoinTarget> JoinReady;
        public event Action<LobbyJoinProblem, string> JoinFailed;
        // Host: the lobby exists (its id), or could not be made (why, for the screen).
        public event Action<ulong> HostLobbyCreated;
        public event Action<string> HostLobbyFailed;

        public bool Hosting { get { return hosted.IsValid(); } }
        public ulong HostedLobby { get { return hosted.m_SteamID; } }
        public LobbyAccess Access { get { return access; } }
        public ulong JoinedLobby { get { return flow != null ? flow.JoinedLobby : 0; } }
        public LobbyJoinState JoinState { get { return flow != null ? flow.State : LobbyJoinState.Idle; } }

        // Host, main thread: a lobby for this session. seats = humans in all, the host included; players = how many
        // are in the session now (polled). False (and a HostLobbyFailed) when Steam can't make one now.
        public bool Host(LobbyAccess how, int seats, Func<int> players)
        {
            if (wantHosting && (creating || Hosting)) return true;
            wantHosting = true;
            access = how; maxMembers = Math.Max(1, Math.Min(seats, 250)); playersNow = players; open = true;
            sessionId = HostGate.Hex(HostGate.SecureRandom(4));
            if (!Alive()) { HostFailed("Steam can't be used right now"); wantHosting = false; return false; }
            Register();
            try
            {
                SteamAPICall_t call = SteamMatchmaking.CreateLobby(LobbyType(how), maxMembers);
                if (call == SteamAPICall_t.Invalid)
                {
                    wantHosting = false;
                    HostFailed("Steam would not make a lobby");
                    return false;
                }
                if (createCall == null) createCall = CallResult<LobbyCreated_t>.Create(OnCreated);
                createCall.Set(call);
                creating = true;
                Log.Info("steam lobby: create requested (" + Words(how) + ", " + maxMembers + " seats)");
                return true;
            }
            catch (Exception e)
            {
                wantHosting = false;
                HostFailed("asking Steam threw " + e.GetType().Name);
                return false;
            }
        }

        // Host: who may find the lobby; "Who can join" on the screens. Applied at once when the lobby exists.
        public void SetAccess(LobbyAccess how)
        {
            if (access == how) return;
            access = how;
            Log.Info("steam lobby: access set to " + Words(how));
            Flush();
        }

        // Host: whether the session takes players at all (joinable also goes off by itself when the seats are full).
        public void SetOpen(bool takesPlayers)
        {
            if (open == takesPlayers) return;
            open = takesPlayers;
            Flush();
        }

        // Host: the session is over (or never started): out of the lobby, so it closes for everyone.
        public void EndHosting(string why)
        {
            wantHosting = false;
            hostGoneSince = -1f;
            if (!Hosting) return;
            CSteamID l = hosted;
            hosted = CSteamID.Nil;
            written.Clear(); writtenJoinable = null; writtenAccess = null;
            lock (snapLock) memberSnap = new Dictionary<ulong, string>();
            LeaveNative(l, why);
        }

        // Whether Steam's overlay is up in this game (it is not in a game started directly rather than through Steam).
        public bool OverlayOn
        {
            get
            {
                try { return Alive() && SteamUtils.IsOverlayEnabled(); }
                catch (Exception) { return false; }
            }
        }

        // Host or member: Steam's invite dialog for the lobby this game hosts, else the one it joined (any member may
        // invite). null = asked; else why not (for the screen; the Steam join code is the way round it). The overlay is
        // off in a game started directly rather than through Steam.
        public string OpenInviteOverlay()
        {
            string why;
            try
            {
                bool alive = Alive();
                ulong lobby;
                why = InviteOverlay.Pick(alive, Hosting ? hosted.m_SteamID : 0, JoinedLobby,
                                         alive && SteamUtils.IsOverlayEnabled(), out lobby);
                if (why == null)
                {
                    SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(lobby));
                    Log.Info("steam lobby: invite overlay requested for " + net.Short(lobby)
                        + (Hosting ? "" : " (as a member)"));
                    return null;
                }
            }
            catch (Exception e) { why = "asking Steam threw " + e.GetType().Name; }
            Log.Warn("steam lobby: invite overlay not opened: " + why);
            return why;
        }

        // The members of the lobby this game is in (hosted or joined); empty when none. Main thread.
        public List<ulong> Members()
        {
            var list = new List<ulong>();
            CSteamID l = Hosting ? hosted : new CSteamID(JoinedLobby);
            if (!l.IsValid() || !Alive()) return list;
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(l);
                for (int i = 0; i < n && i < 256; i++) list.Add(SteamMatchmaking.GetLobbyMemberByIndex(l, i).m_SteamID);
            }
            catch (Exception e) { Log.Warn("steam lobby: members not read: " + e.GetType().Name); }
            return list;
        }

        // Host's door, main thread: whether that account is in this host's lobby right now.
        public bool IsMember(ulong steamId)
        {
            if (!Hosting || steamId == 0) return false;
            foreach (ulong m in Members()) if (m == steamId) return true;
            return false;
        }

        // Steam's name for an account (the roster's name in a Steam game), cleaned as every player name is; null when
        // Steam does not know it yet (it is asked for, and a later call has it). Main thread.
        public string MemberName(ulong steamId) { return Name(steamId); }

        // Any thread: the hosted lobby's membership and a member's Steam name as of the last main-thread read (at most
        // half a second old, and fresh after every member change Steam reports).
        public bool IsMemberNow(ulong steamId) { lock (snapLock) return memberSnap.ContainsKey(steamId); }
        public string MemberNameNow(ulong steamId)
        {
            string n;
            lock (snapLock) return memberSnap.TryGetValue(steamId, out n) ? n : null;
        }

        // The player backed out of the join screen before the session began: out of the lobby.
        public void CancelJoin(string why)
        {
            if (flow != null) flow.Leave(why ?? "the player backed out");
        }

        // The answer to LeaveGameFirst: yes once the game has been left (false while it is still on), or no.
        public bool ConfirmJoin() { return flow != null && flow.Confirm(Now); }
        public void DeclineJoin() { if (flow != null) flow.Decline(); }

        // ---- the Steam rules' ISteamLobbyView (the door's view): the host's own lobby only ----

        public bool HasLobby { get { return Hosting; } }
        public ulong Owner { get { return Hosting ? OwnerOf(hosted.m_SteamID) : 0; } }

        // null when absent (Steam gives "" for a key never set)
        public string GetData(string key)
        {
            if (!Hosting) return null;
            string v = Data(hosted.m_SteamID, key);
            return v.Length == 0 ? null : v;
        }

        public bool SetData(string key, string value)
        {
            if (!Hosting || !Alive() || key == null || value == null) return false;
            try { return SteamMatchmaking.SetLobbyData(hosted, key, value); }
            catch (Exception) { return false; }
        }

        // ---- ILobbySteam (the join flow's view of Steam) ----

        public bool Usable { get { net.Ready.Refresh(); return net.Ready.Usable && SteamReady.NativeAlive; } }
        public bool Settled { get { return menuSeen; } }
        public bool InGame { get { return Session.Host != null || Session.Live || !Session.AtMenu; } }
        public bool LobbyOnly { get { return InGame && Session.LobbyOnly; } }

        public bool RequestData(ulong lobby)
        {
            if (!Alive()) return false;
            try { return SteamMatchmaking.RequestLobbyData(new CSteamID(lobby)); }
            catch (Exception e) { Log.Warn("steam lobby: data request threw " + e.GetType().Name); return false; }
        }

        public string Data(ulong lobby, string key)
        {
            if (!Alive()) return "";
            try { return SteamMatchmaking.GetLobbyData(new CSteamID(lobby), key) ?? ""; }
            catch (Exception) { return ""; }
        }

        public bool Join(ulong lobby)
        {
            if (!Alive()) return false;
            try
            {
                SteamAPICall_t call = SteamMatchmaking.JoinLobby(new CSteamID(lobby));
                if (call == SteamAPICall_t.Invalid) return false;
                if (enterCall == null) enterCall = CallResult<LobbyEnter_t>.Create(OnEntered);
                enterCall.Set(call);
                Log.Info("steam lobby: joining " + net.Short(lobby));
                return true;
            }
            catch (Exception e) { Log.Warn("steam lobby: join threw " + e.GetType().Name); return false; }
        }

        public ulong OwnerOf(ulong lobby)
        {
            if (!Alive()) return 0;
            try { return SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID; }
            catch (Exception) { return 0; }
        }

        public void Leave(ulong lobby, string why) { LeaveNative(new CSteamID(lobby), why); }

        public string Name(ulong steamId)
        {
            if (steamId == 0 || !Alive()) return null;
            try
            {
                var id = new CSteamID(steamId);
                string raw = SteamFriends.GetFriendPersonaName(id);
                string name = TextSanitiser.Clean(raw, PlayerNames.MaxChars, null);
                if (string.IsNullOrEmpty(name) || raw == "[unknown]")
                {
                    // Steam fetches it; the next call has it (askedNames keeps this to one request per account)
                    if (askedNames.Add(steamId)) SteamFriends.RequestUserInformation(id, true);
                    return null;
                }
                return name;
            }
            catch (Exception) { return null; }
        }

        // ---- the root's calls ----

        // Every frame from SteamNet.Update: callbacks registered once Steam is up, the cold start's lobby once, the
        // flow's deadlines, the host's data, and the watches that leave a lobby whose session is gone.
        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (Session.AtMenu) menuSeen = true;
            if (!registered)
            {
                if (now < nextRegister) return;
                nextRegister = now + 1f;
                net.Ready.Refresh();
                if (!net.Ready.Up || !SteamReady.NativeAlive) return;
                Register();
            }
            if (!SteamReady.NativeAlive) return;
            if (!coldStartChecked) { coldStartChecked = true; ColdStart(); }
            flow.Update(now);
            if (Hosting && now >= nextFlush) { nextFlush = now + FlushEverySeconds; Flush(); }
            if (Hosting && now >= nextSnapshot) { nextSnapshot = now + SnapshotEverySeconds; Snapshot(); }
            WatchHost(now);
            WatchJoiner(now);
        }

        // The game is closing (or Steam is): out of every lobby while Steam can still be told.
        public void Stop()
        {
            EndHosting("the game is closing");
            if (flow != null && Alive()) flow.Leave("the game is closing");
            Unregister();
        }

        // Steam has shut down under us: nothing may be called; Steam dropped our lobbies with it.
        public void Forget()
        {
            hosted = CSteamID.Nil;
            lock (snapLock) memberSnap = new Dictionary<ulong, string>();
            wantHosting = false; creating = false;
            Unregister();
            flow = null;
        }

        // ---- inside ----

        static float Now { get { return Time.realtimeSinceStartup; } }

        bool Alive()
        {
            if (!SteamReady.NativeAlive) return false;
            net.Ready.Refresh();
            return net.Ready.Up;
        }

        void Register()
        {
            if (registered) return;
            registered = true;
            flow = new LobbyJoinFlow(this, ModInfo.Protocol, ModInfo.Version, Session.GameBuildId, net.Short);
            flow.Received += r => Raise(JoinRequested, r);
            flow.LeaveFirst += r => Raise(LeaveGameFirst, r);
            flow.Ready += t =>
            {
                joinerSessionSeen = false; joinerGoneSince = -1f;
                if (JoinReady != null) { Raise(JoinReady, t); return; }
                // no screen takes it (the menus wire JoinReady): out again, not a member of a lobby for nothing
                Log.Warn("steam lobby: nothing takes a lobby join in this build");
                flow.Leave("nothing takes the join");
            };
            flow.Failed += (p, w) =>
            {
                var a = JoinFailed;
                if (a == null) return;
                try { a(p, w); }
                catch (Exception e) { Log.Error("steam lobby: a JoinFailed handler threw " + e.GetType().Name); }
            };
            joinRequestCb = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            dataCb = Callback<LobbyDataUpdate_t>.Create(OnData);
            chatCb = Callback<LobbyChatUpdate_t>.Create(OnChat);
            Log.Info("steam lobby: listening for join requests");
        }

        void Unregister()
        {
            try
            {
                if (joinRequestCb != null) joinRequestCb.Dispose();
                if (dataCb != null) dataCb.Dispose();
                if (chatCb != null) chatCb.Dispose();
                if (createCall != null) createCall.Dispose();
                if (enterCall != null) enterCall.Dispose();
            }
            catch (Exception) { }
            joinRequestCb = null; dataCb = null; chatCb = null; createCall = null; enterCall = null;
            registered = false;
        }

        // Steam started this game for a friend's invite: "+connect_lobby <id>" in the arguments (or in Steam's own
        // launch line). The flow holds it until the menu is up.
        void ColdStart()
        {
            ulong id = 0;
            try { id = SteamLobbyIds.FromArgs(Environment.GetCommandLineArgs()); }
            catch (Exception) { }
            if (id == 0)
            {
                try
                {
                    string line;
                    if (SteamApps.GetLaunchCommandLine(out line, 1024) > 0) id = SteamLobbyIds.FromLine(line);
                }
                catch (Exception) { }
            }
            if (id == 0) return;
            flow.Request(new LobbyJoinRequest { LobbyId = id, Source = LobbyJoinSource.ColdStart }, Now);
        }

        void OnJoinRequested(GameLobbyJoinRequested_t cb)
        {
            try
            {
                ulong friend = cb.m_steamIDFriend.m_SteamID;
                flow.Request(new LobbyJoinRequest
                {
                    LobbyId = cb.m_steamIDLobby.m_SteamID, FriendId = friend, FriendName = Name(friend),
                    Source = LobbyJoinSource.Invite,
                }, Now);
            }
            catch (Exception e)
            {
                Log.Error("steam lobby: a join request failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        void OnData(LobbyDataUpdate_t cb)
        {
            try
            {
                if (cb.m_ulSteamIDLobby != cb.m_ulSteamIDMember) return;   // one member's data, not the lobby's
                if (flow != null) flow.OnData(cb.m_ulSteamIDLobby, cb.m_bSuccess != 0, Now);
            }
            catch (Exception e) { Log.Error("steam lobby: lobby data failed: " + e.GetType().Name); }
        }

        void OnEntered(LobbyEnter_t cb, bool ioFailure)
        {
            try
            {
                var response = (EChatRoomEnterResponse)cb.m_EChatRoomEnterResponse;
                bool ok = !ioFailure && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
                string why = ioFailure ? "Steam lost the request" : ok ? null : Words(response);
                if (flow != null) flow.OnEntered(cb.m_ulSteamIDLobby, ok, why, Now);
                else if (ok) LeaveNative(new CSteamID(cb.m_ulSteamIDLobby), "nobody waits for it");
            }
            catch (Exception e) { Log.Error("steam lobby: entering failed: " + e.GetType().Name); }
        }

        // Members coming and going, for the log (the door asks Steam directly, so nothing is kept here). The host
        // leaving a joined lobby ends nothing by itself: the session's link drop does that.
        void OnChat(LobbyChatUpdate_t cb)
        {
            try
            {
                bool ours = cb.m_ulSteamIDLobby == hosted.m_SteamID
                    || (flow != null && cb.m_ulSteamIDLobby == flow.JoinedLobby);
                if (!ours) return;
                var change = (EChatMemberStateChange)cb.m_rgfChatMemberStateChange;
                bool entered = (change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
                if (cb.m_ulSteamIDLobby == hosted.m_SteamID) Snapshot();
                Log.Info("steam lobby: " + net.Short(cb.m_ulSteamIDUserChanged) + (entered ? " entered " : " left ")
                    + net.Short(cb.m_ulSteamIDLobby));
            }
            catch (Exception) { }
        }

        void OnCreated(LobbyCreated_t cb, bool ioFailure)
        {
            creating = false;
            try
            {
                if (ioFailure || cb.m_eResult != EResult.k_EResultOK)
                {
                    wantHosting = false;
                    HostFailed(ioFailure ? "Steam lost the request" : "Steam said " + cb.m_eResult);
                    return;
                }
                var id = new CSteamID(cb.m_ulSteamIDLobby);
                if (!wantHosting)
                {
                    LeaveNative(id, "the session ended before Steam made the lobby");
                    return;
                }
                hosted = id;
                written.Clear(); writtenJoinable = null; writtenAccess = null;
                Log.Info("steam lobby: created " + net.Short(id.m_SteamID) + " (" + Words(access) + ")");
                Flush();
                Raise(HostLobbyCreated, id.m_SteamID);
            }
            catch (Exception e) { HostFailed("the answer could not be used (" + e.GetType().Name + ")"); }
        }

        // The host's data and settings, written only when they changed and only while Steam says we own the lobby.
        void Flush()
        {
            if (!Hosting || !Alive()) return;
            try
            {
                ulong me = net.OwnId();
                if (me == 0 || SteamMatchmaking.GetLobbyOwner(hosted).m_SteamID != me) return;
                int players = 1;
                try { if (playersNow != null) players = Math.Max(1, playersNow()); } catch (Exception) { }
                var facts = new LobbyHostFacts
                {
                    HostId = me, Protocol = ModInfo.Protocol, Mod = ModInfo.Version, Build = Session.GameBuildId,
                    SessionId = sessionId, Access = access, Players = players, Max = maxMembers, Open = open,
                };
                var keys = new List<string>();
                foreach (KeyValuePair<string, string> kv in LobbyData.Changed(written, facts.ToData().ToPairs()))
                {
                    if (!SetData(kv.Key, kv.Value)) continue;   // tried again at the next flush
                    written[kv.Key] = kv.Value;
                    keys.Add(kv.Key);
                }
                if (writtenAccess != access && SteamMatchmaking.SetLobbyType(hosted, LobbyType(access)))
                    writtenAccess = access;
                if (writtenJoinable != facts.Joinable && SteamMatchmaking.SetLobbyJoinable(hosted, facts.Joinable))
                    writtenJoinable = facts.Joinable;
                if (keys.Count > 0)
                    Log.Info("steam lobby: data set (" + string.Join(", ", keys.ToArray()) + "; " + players + "/"
                        + maxMembers + ", joinable " + facts.Joinable + ")");
            }
            catch (Exception e) { Log.Warn("steam lobby: data not written: " + e.GetType().Name); }
        }

        void Snapshot()
        {
            var snap = new Dictionary<ulong, string>();
            foreach (ulong m in Members()) snap[m] = Name(m);
            lock (snapLock) memberSnap = snap;
        }

        // A hosted lobby outlives no host session: every way a host session ends should call EndHosting, and this
        // catches one that did not.
        void WatchHost(float now)
        {
            if (!Hosting && !creating) { hostGoneSince = -1f; return; }
            if (Session.Host != null) { hostGoneSince = -1f; return; }
            if (hostGoneSince < 0) { hostGoneSince = now; return; }
            if (now - hostGoneSince >= HostWatchSeconds) EndHosting("the host session is gone");
        }

        // A joiner's lobby is left once the session it was joined for has come and gone.
        void WatchJoiner(float now)
        {
            if (flow == null || flow.JoinedLobby == 0) return;
            if (Session.Live) { joinerSessionSeen = true; joinerGoneSince = -1f; return; }
            if (!joinerSessionSeen) return;   // the join screen is still up: CancelJoin covers backing out
            if (joinerGoneSince < 0) { joinerGoneSince = now; return; }
            if (now - joinerGoneSince >= JoinerWatchSeconds)
            {
                joinerSessionSeen = false;
                joinerGoneSince = -1f;
                flow.Leave("the session ended");
            }
        }

        void LeaveNative(CSteamID lobby, string why)
        {
            if (!lobby.IsValid()) return;
            try
            {
                if (SteamReady.NativeAlive) SteamMatchmaking.LeaveLobby(lobby);
                Log.Info("steam lobby: left " + net.Short(lobby.m_SteamID) + " (" + TextSanitiser.OneLine(why) + ")");
            }
            catch (Exception e) { Log.Warn("steam lobby: leaving threw " + e.GetType().Name); }
        }

        void HostFailed(string why)
        {
            Log.Warn("steam lobby: no lobby for this session: " + why);
            Raise(HostLobbyFailed, why);
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

        static ELobbyType LobbyType(LobbyAccess a)
        {
            // invite-only and code-only lobbies are private (a code-only one is also never joinable)
            return a == LobbyAccess.FriendsOnly ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePrivate;
        }

        static string Words(LobbyAccess a)
        {
            return a == LobbyAccess.FriendsOnly ? "friends only" : a == LobbyAccess.InviteOnly ? "invites only"
                : "join code only";
        }

        static string Words(EChatRoomEnterResponse r)
        {
            switch (r)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist: return "the lobby no longer exists";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed: return "not allowed in";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull: return "the lobby is full";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned: return "banned from it";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited: return "a limited Steam account";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseMemberBlockedYou:
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseYouBlockedMember: return "blocked on Steam";
                default: return "Steam said " + r;
            }
        }
    }
}
