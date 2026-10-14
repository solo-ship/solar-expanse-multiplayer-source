using System;
using System.Collections.Generic;
using UnityEngine;
using Data.ScriptableObject;
using ScriptableObjectScripts;

namespace SolarExpanseMultiplayer
{
    // The screens' one session root (ISessionActions, SessionView), over the existing session (Session, SessionHost,
    // SessionClient): the beta adapts it rather than replacing it (DECISIONS 2026-10-14). Hosting opens the lobby at
    // the main menu (Session.OpenLobby) with a save from the game's own load dialog or a new game from the game's own
    // pages, both kept by the setup's hooks with nothing loaded; the lobby's claims, Ready, Start and Start anyway,
    // kick, ban and the agency rules go to the host's room (HostLobby), whose roster every player's view is built
    // from. A joiner (a code, or a friend's Steam invite or Join Game) waits in the host's lobby until let in
    // (ClientLobby), then joins through M4. Leave says goodbye with the leave code; End session ends it for everyone.
    // A host with no lobby (the dev flags' host) is still joined the M4 way. The view is rebuilt twice a second and
    // on every event; a field the session cannot fill keeps its unknown value. Owned by MenuSessions; main thread only.
    public sealed partial class M4SessionAdapter : ISessionActions, IMpSaves, IHostOptions
    {
        public const string NotYet = "menu.notYet";
        const float RebuildEverySeconds = 0.5f;
        const Capability EarlyCapabilities = Capability.HostSave | Capability.HostNewGame | Capability.JoinByCode |
                                 Capability.PickAgency | Capability.Players;
        // The lobby at the menu (the beta): the host's room and the joiner's hold make these real.
        const Capability LobbyCapabilities = EarlyCapabilities | Capability.LobbyAtMenu | Capability.Ready |
            Capability.Start | Capability.StartAnyway | Capability.Kick | Capability.Ban | Capability.AgencyStates |
            Capability.CarryOn | Capability.LeaveNoPause | Capability.EndSession | Capability.NamesEverywhere |
            Capability.MpSaves | Capability.SoloPlay;
        // Not in the beta: SaveCopies (no handler sends or takes kinds 38-41 yet) and HostMpSave (HostMpSave answers
        // "not yet"); a control behind either would offer a feature that does nothing.

        readonly MenuSessions owner;
        readonly NoticeQueue notices = new NoticeQueue();
        readonly PauseTracker pauses = new PauseTracker();
        SessionView view = new SessionView();
        SessionPhase phase = SessionPhase.Idle;
        float nextRebuild;

        List<string> ownAgencies;           // this game's playable agencies (its own content), read once per world
        readonly HashSet<string> refused = new HashSet<string>();   // agencies the host refused this join
        string hostedSave;
        bool hostedNewGame;

        // What this side heard about other players, by id (a client knows only what notices and seats said).
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        readonly Dictionary<int, string> agencies = new Dictionary<int, string>();
        readonly Dictionary<int, int> states = new Dictionary<int, int>();
        Clock clientClock;
        SessionHost wiredHost;
        SessionClient wiredClient;
        HostStartProgress hostStart = new HostStartProgress();   // the host's start, from Start until all are in

        public event Action Changed;

        public M4SessionAdapter(MenuSessions owner) { this.owner = owner; }

        // The host's own agency for a new game (picked before the game's pages) and the setup chosen; read by the
        // setup hooks, which arm the host intent at the pages' last step.
        public string HostAgencyChoice { get; private set; }
        public NewGameSetup PendingNewGame { get; private set; }

        public Capability Capabilities
        {
            get
            {
                // every row's ping: the host measures each link; a joiner hears them in the host's roster
                SessionClient c = Session.Client;
                bool pings = Session.Host != null || c != null && Session.Live && c.Lobby.Follow.Last != null;
                return pings ? LobbyCapabilities | Capability.PingAll : LobbyCapabilities;
            }
        }

        public SessionView View { get { return view; } }
        public NoticeQueue Notices { get { return notices; } }

        // This player's own name: the one saved for the screens (PlayerNames.Default), cleaned, never empty.
        public static string OwnName()
        {
            string raw = null;
            try { raw = PlayerNames.Default != null ? PlayerNames.Default() : null; }
            catch (Exception) { }
            return PlayerNames.Clean(raw);
        }

        // ---- hosting ----

        // Why this game cannot host now, as a key; null when it can. A session that ended at the menu is reset here
        // (Session.ResetMenuSession); after a session whose world loaded, hosting needs a restart (SessionLifecycle).
        internal static string HostBusy()
        {
            string why = Session.HostBlock();
            if (why != null) return why;
            Session.ResetMenuSession("hosting again");
            return Session.Started || Session.Client != null ? "menu.restartFirst" : null;
        }

        // An own save's lobby at the menu (the dev trigger "menu-host <save>", the screens' test session); the screens'
        // own route is the game's load dialog, whose kept save Update takes.
        // The host setup screen's options for the lobby about to open (taken once by OpenLobby).
        int hostMaxPlayers = SessionLimits.PlayerLimitNoCoop;
        bool hostLateJoin = true;

        public void SetHostOptions(int maxPlayers, bool lateJoin)
        {
            hostMaxPlayers = HostOptionRules.ClampPlayers(maxPlayers);
            hostLateJoin = lateJoin;
        }

        public string HostFromSave(string ownSaveName)
        {
            string busy = HostBusy();
            if (busy != null) return busy;
            string why;
            LobbySetup setup = LobbyWorld.ForSave(ownSaveName, out why);
            return setup == null ? why : OpenLobby(setup);
        }

        // The game's own pages run the new game's setup; their last Next is kept (nothing loads) and Update opens the
        // lobby with it. The reference agency only decides the money line on the difficulty page.
        public string HostNewGame(NewGameSetup setup)
        {
            string busy = HostBusy();
            if (busy != null) return busy;
            List<string> own = OwnAgencies();
            string reference = HostAgencyChoice ?? (own.Count > 0 ? own[0] : null);
            if (reference == null) return "join.badAgency";
            string open = owner.Pages.Open(reference, true);
            if (open != null) return open;
            PendingNewGame = setup ?? new NewGameSetup();
            if (PendingNewGame.MaxPlayers > 0 || PendingNewGame.NoLateJoin)
                SetHostOptions(PendingNewGame.MaxPlayers, !PendingNewGame.NoLateJoin);
            hostedSave = null;
            hostedNewGame = true;
            Log.Info("menu: hosting a new game: the game's pages come next, then the lobby");
            return null;
        }

        // A new game with the game's own defaults and no pages (the dev trigger "menu-newgame", the device kit).
        public string HostDefaultNewGame(string hostPick)
        {
            string busy = HostBusy();
            if (busy != null) return busy;
            List<string> ai, off;
            string why;
            var config = owner.Pages.DefaultNewGame(out ai, out off, out why);
            if (config == null)
            {
                Log.Error("menu: no default new game: " + why);
                return "host.couldNotStart";
            }
            return OpenLobby(LobbyWorld.ForNewGame(config, ai, off, hostPick ?? HostAgencyChoice));
        }

        string OpenLobby(LobbySetup setup)
        {
            string why = owner.PrepareHost();
            if (why != null) return why;
            if (setup.Options != null)
            {
                setup.Options.MaxPlayers = hostMaxPlayers;
                setup.Options.LateJoin = hostLateJoin;
            }
            hostMaxPlayers = SessionLimits.PlayerLimitNoCoop;   // taken once
            hostLateJoin = true;
            why = Session.OpenLobby(owner.TakeHostPlan(), setup);
            if (why != null) return why;
            hostedSave = setup.NewGame ? null : setup.Title;
            hostedNewGame = setup.NewGame;
            HostLobby lobby = Session.Lobby;
            if (lobby != null) lobby.Changed += () => Safely(Rebuild);
            Rebuild();
            Action opened = LobbyOpened;
            if (opened != null) Safely(opened);
            return null;
        }

        // The host's lobby is open at the menu (the menu's own screens make way for the lobby screen).
        public event Action LobbyOpened;

        // The setup's kept choice (the load dialog's save, the pages' new game), taken once and hosted.
        void TakeKeptSetup()
        {
            if (!Session.AtMenu || Session.Host != null) return;
            string save = owner.Dialog.TakeKeptSave();
            if (save != null) Refusal("host a save", HostFromSave(save));
            List<string> ai, off;
            var kept = owner.Pages.TakeKept(out ai, out off);
            if (kept != null && Session.Host == null)
                Refusal("host a new game", OpenLobby(LobbyWorld.ForNewGame(kept, ai, off, HostAgencyChoice)));
        }

        // A refusal no screen asked for: a notice with its line, and the log.
        void Refusal(string what, string key)
        {
            if (key == null) return;
            Log.Warn("menu: could not " + what + " (" + key + "): " + UiText.Get(key));
            notices.Push(key, null, NoticeQueue.Warning, NetClock.NowMs);
        }

        public string HostMpSave(string mpSaveName) { return NotYet; }
        // DD24: a Multiplayer save played alone, from the main menu (SoloLoad). The screens offer it behind SoloPlay.
        public string PlaySolo(string mpSaveName, string agencyId) { return owner.Saves.Solo.Start(mpSaveName, agencyId); }

        public IList<MpSaveRow> MpSaves { get { return owner.Saves.Folder.MpSaves; } }
        public string DeleteMpSave(string name) { return owner.Saves.Folder.DeleteMpSave(name); }

        public string NewCode()
        {
            SessionHost h = Session.Host;
            if (h == null) return "host.notHosting";
            h.NewCode();
            Rebuild();
            return null;
        }

        // ---- joining ----

        // The code is checked here, then the session connects at once with no agency, as a friend's Steam invite does:
        // the player waits in the host's lobby and picks from the host's roster there, so the agencies already taken
        // show as taken (a picker before the roster offered them as free).
        public string Join(string joinCodeText, string playerName)
        {
            string busy = Session.JoinBlock();
            if (busy != null) return busy;
            if (string.IsNullOrWhiteSpace(joinCodeText)) return "join.noCode";
            string text = joinCodeText.Trim();
            string error;
            JoinCode lan;
            SteamCode steam;   // a Steam join code is as good as a home-network one
            if (text.Length > JoinCode.MaxTextLength || !JoinRoute.Parse(text, out lan, out steam, out error))
                return "join.badCode";
            SessionPlan plan = SessionPlan.JoinFromMenu(playerName ?? OwnName(), text);
            if (plan.Check() != null) return "join.badCode";
            if (!Session.AtMenu) return "menu.notAtMenu";
            Log.Info("menu: join code accepted; connecting, the host's lobby comes next");   // never the code itself
            return StartJoin(plan, null);
        }

        // Every join from the menus (a code, a friend's Steam invite or Join Game): the session connects now and waits
        // in the host's lobby, where the player picks from the host's roster. preset: a test profile's dev agency,
        // claimed and readied by itself; else null. null = connecting; else a UiText key.
        string StartJoin(SessionPlan plan, string preset)
        {
            ForgetSession();
            string why = null;
            if (Session.Client != null) why = Session.JoinAgain(plan, preset);
            else
            {
                Session.StartFromPlan(plan, preset);
                if (Session.Client == null) why = "the session did not start (the session lines say why)";
            }
            if (why != null)
            {
                Log.Warn("menu: the join did not start: " + why);
                Rebuild();
                return "host.couldNotStart";
            }
            Session.Client.Lobby.Preset = preset;
            Session.Client.Lobby.AutoReady = preset != null;
            Rebuild();
            return null;
        }

        // A pick: in the host's own lobby its claim; a joiner's claim in the host's lobby (the join is already
        // connected); after a refusal the next one is claimed on the flow's next update (JoinerFlow), never the same
        // agency again and never past M4's claims per join. Before any session, the host's own agency for a new game.
        public string PickAgency(string agencyId)
        {
            if (!JoinCheck.IsAgencyId(agencyId)) return "join.badAgency";
            List<string> own = OwnAgencies();
            if (own.Count > 0 && !own.Contains(agencyId)) return "join.badAgency";
            // the host's own claim in its lobby (a save's host keeps the save's agency)
            HostLobby lobby = Session.Lobby;
            if (lobby != null)
            {
                int code = lobby.Room.Claim(lobby.Room.HostId, agencyId, NetClock.NowMs);
                Rebuild();
                return code == 0 ? null : RefusedKey(code);
            }
            // a joiner waiting in the host's lobby claims through it; a host with no lobby takes the pick as M4's claim
            SessionClient held = Session.Client;
            if (held != null && Session.Live && held.Lobby.Holding)
            {
                if (held.Lobby.Follow.Legacy) return held.Lobby.PickWithoutLobby(agencyId) ? null : "join.badAgency";
                if (!held.Send(new LobbyClaim { Agency = agencyId })) return NotYet;
                Log.Info("menu: claiming " + agencyId + " in the host's lobby");
                return null;
            }
            SessionClient c = Session.Client;
            if (c != null && Session.Live)
            {
                JoinerFlow f = c.Flow;
                if (!f.ClaimRefused) return "join.claimPending";
                if (f.ClaimsLeft <= 0) return "join.noClaimsLeft";
                if (f.LastClaim != null) refused.Add(f.LastClaim);
                if (refused.Contains(agencyId)) return "join.badAgency";   // never an agency refused in this join
                c.World.SetAgency(agencyId);
                Log.Info("menu: claiming " + agencyId + " after a refusal");
                Rebuild();
                return null;
            }
            if (Session.Host == null && Session.AtMenu && HostBusy() == null)
            {
                HostAgencyChoice = agencyId;
                return null;
            }
            return NotYet;
        }

        // The session that ended at the menu was reset (Session.ResetMenuSession): nothing of it stays in the view.
        internal void SessionReset()
        {
            ForgetSession();
            pickerAsked = false;
            hostStart = new HostStartProgress();
            Rebuild();
        }

        // A new join: nothing heard in an earlier session (names, states, agencies, the last Clock, refusals) carries
        // over into its view.
        void ForgetSession()
        {
            names.Clear();
            states.Clear();
            agencies.Clear();
            refused.Clear();
            clientClock = null;
        }

        // ---- the lobby ----

        // A joiner's Ready in the host's lobby (the host itself is never waited for).
        public void SetReady(bool ready)
        {
            SessionClient c = Session.Client;
            if (c == null || !Session.Live || !c.Lobby.Holding || !c.Send(new LobbyReady { Ready = ready }))
            {
                Unavailable("ready");
                return;
            }
            Log.Info("menu: " + (ready ? "ready" : "not ready") + " in the host's lobby");
        }

        // The host's Start (anyway: "Start anyway", after its confirmation): the host's world loads, and once it has
        // every member of the start set joins with its lobby claim. A refusal shows as a notice.
        public void Start(bool anyway)
        {
            HostLobby lobby = Session.Lobby;
            if (lobby == null) { Unavailable("start"); return; }
            Refusal("start", lobby.Start(anyway));
            Rebuild();
        }

        // The host's kick and ban. In the lobby the seat is freed at once and the player told "The host removed you
        // from the lobby."; in the game M4's kick frees the seat too. Either way the player's keys go, but a kicked
        // player may come straight back in (the Steam lobby, or the current join code); a banned Steam account is
        // refused at the Steam door for good.
        public void Kick(int player)
        {
            SessionHost h = Session.Host;
            if (h == null) { Unavailable("kick"); return; }
            HostLobby lobby = Session.Lobby;
            bool inLobby = lobby != null && lobby.Room.InLobby(player);
            if (lobby != null) lobby.Room.Kick(player);
            if (inLobby) h.Kick(player, JoinCodes.KickedFromLobby, true);
            else if (Session.Join != null) Session.Join.KickPlayer(player, DevHostCommands.KickCode);
            else Session.Host.Kick(player, DevHostCommands.KickCode, true);
            Rebuild();
        }

        public void Ban(int player)
        {
            if (Session.Host == null) { Unavailable("ban"); return; }
            HostLobby lobby = Session.Lobby;
            if (lobby != null) lobby.Room.Ban(player);
            Session.Host.Ban(player);   // the Steam account first, while the link still names it
            if (Session.Join != null) Session.Join.KickPlayer(player, JoinCodes.Banned);   // and its seat freed
            Rebuild();
        }

        // The host's choice for a free agency in a new game's lobby: an AI rival or not in the game.
        public void SetAgencyRule(string agencyId, AgencyStanding rule)
        {
            HostLobby lobby = Session.Lobby;
            if (lobby == null || !lobby.Room.SetAgencyRule(agencyId, rule)) { Unavailable("agency rule"); return; }
            Rebuild();
        }

        static string RefusedKey(int code)
        {
            string key = "join.refused." + code;
            return UiText.Has(key) ? key : "join.badAgency";
        }

        // The host's Carry on: the drop hold ends and the session runs at the slowest remaining request (the dropped
        // player's own request went with the drop).
        public void CarryOnWithout(int player)
        {
            if (Session.HostClock == null) { Unavailable("carry on"); return; }
            Log.Info("menu: the host carries on without player " + player);
            Session.HostClock.CarryOn();
        }
        // A joiner's Leave (DD18): its goodbye carries the leave code, so the host keeps its seat with no drop pause;
        // the game goes back to the menu (the pause screen's exit runs its own order around this).
        public void Leave()
        {
            SessionClient c = Session.Client;
            if (c == null || !Session.Live) { Unavailable("leave"); return; }
            c.Leave();
            Rebuild();
        }

        // The host's End session (DD16): everyone gets the host's goodbye and goes back to the menu. In the lobby it
        // ends at once; in a running game the session's final save is written and offered first (the pause screen's
        // exit does that itself; anywhere else StepEnding does), then the host's game goes to the menu.
        public void EndSession()
        {
            if (Session.Host == null) { Unavailable("end session"); return; }
            HostLobby lobby = Session.Lobby;
            if (lobby == null || !lobby.HasWorld)
            {
                Session.EndHostSession("the host ended the session");
                Rebuild();
                return;
            }
            lobby.End();
            ending = true;
            endSaved = false;
            Log.Info("menu: the host ends the session");
            Rebuild();
        }

        bool ending, endSaved;
        long endingSinceMs;

        void StepEnding()
        {
            if (!ending) return;
            if (Session.Host == null) { ending = false; return; }
            if (owner.Saves.Exits.Active) return;   // the pause screen's exit: it saves, then the menu ends the session
            long now = NetClock.NowMs;
            if (!endSaved)
            {
                endSaved = true;
                endingSinceMs = now;
                try { owner.Saves.Host.FinalSave(); }
                catch (Exception e) { Log.Error("menu: the final save failed: " + e.Message); }
                return;
            }
            if (!owner.Saves.Host.Idle && now - endingSinceMs < owner.Saves.Host.EndDeadlineMs) return;
            ending = false;
            Session.EndHostSession("the host ended the session");
            Session.ToMenu();
        }

        static void Unavailable(string what) { Log.Warn("menu: " + what + " is not available on this session yet"); }

        // ---- the world and the frame ----

        // A save loaded or the menu came back: this world's agency list goes; the session's own state stays.
        public void OnWorldChanged()
        {
            ownAgencies = null;
            Rebuild();
        }

        public void Update()
        {
            WireUp();
            WireSteam();
            TakeKeptSetup();
            StepEnding();
            StepLeaveForSteam();
            // nothing to show outside a session (a solo game): the view stays as it is, and allocates nothing
            bool quiet = Session.Host == null && Session.Client == null &&
                         !owner.Intents.HostIntent && phase == SessionPhase.Idle;
            if (!quiet && Time.realtimeSinceStartup >= nextRebuild) Rebuild();
        }

        // Listens to the session's host or client once each (M4 makes one of each per process at most). The view is a
        // listener after M4's own: whatever goes wrong in it is caught here, so it never reaches M4's event handling
        // (where a throw is a failed event).
        void WireUp()
        {
            SessionHost h = Session.Host;
            if (h != null && h != wiredHost)
            {
                wiredHost = h;
                h.Confirmed += (id, name, rejoin) => Safely(() => HostConfirmed(id, name, rejoin));
                h.Left += id => Safely(() => { states.Remove(id); agencies.Remove(id); Rebuild(); });
                h.LeftForGood += id => Safely(() => HostLeft(id));
                h.Received += (id, m) => Safely(() => HostReceived(id, m));
                h.Sent += HostSent;   // every chunk goes through Send: no allocation, its own catch
            }
            SessionClient c = Session.Client;
            if (c != null && c != wiredClient)
            {
                wiredClient = c;
                c.Received += ClientHeard;   // the host's Clock four times a second: no allocation
            }
        }

        int listenerErrors;

        void ClientHeard(Message m)
        {
            try { ClientReceived(m); }
            catch (Exception e) { ListenerFailed(e); }
        }

        void ListenerFailed(Exception e)
        {
            listenerErrors++;
            if (listenerErrors <= 5) Log.Error("menu: the session view's listener failed: " + e);
        }

        void Safely(Action a)
        {
            try { a(); }
            catch (Exception e) { ListenerFailed(e); }
        }

        void HostConfirmed(int id, string name, bool rejoin)
        {
            names[id] = Named(name, id);
            states[id] = rejoin ? PlayerRow.Rejoining : PlayerRow.Joining;
            Push(rejoin ? "join.notice.rejoined" : "join.notice.joining", names[id], NoticeQueue.Routine);
        }

        // A player's goodbye on purpose (the Leave button): the notice; its seat is kept (away), no drop pause.
        void HostLeft(int id)
        {
            string name;
            names.TryGetValue(id, out name);
            Push("join.notice.left", name ?? Named(null, id), NoticeQueue.Routine);
            // the other players hear it too: their notice stacks and PLAYERS (the leaver's own link is gone already)
            SessionHost h = Session.Host;
            if (h != null)
                h.Broadcast(new Notice { Kind = NoticeKind.Left, Player = id,
                                         Name = TextBudget.FitBytes(name ?? "", 96) });
        }

        void HostReceived(int id, Message m)
        {
            var stage = m as JoinStage;
            if (stage == null) return;
            string name;
            names.TryGetValue(id, out name);
            if (stage.Stage == JoinStageKind.Loaded)
            {
                states[id] = PlayerRow.InGame;
                Push("join.notice.joined", name ?? Named(null, id), NoticeQueue.Routine);
            }
            else if (stage.Stage == JoinStageKind.Failed)
                Push("join.notice.couldNotJoin", name ?? Named(null, id), NoticeQueue.Warning);
            else states[id] = PlayerRow.Joining;
            Rebuild();
        }

        // The host's own answer to a claim is the only place the host glue sees which agency a player holds.
        void HostSent(int id, Message m)
        {
            var claim = m as ClaimResult;
            if (claim == null || !claim.Ok) return;
            try { if (JoinCheck.IsAgencyId(claim.Agency)) agencies[id] = claim.Agency; }
            catch (Exception) { }
        }

        void ClientReceived(Message m)
        {
            var clock = m as Clock;
            if (clock != null) { clientClock = clock; return; }
            if (m is Roster) { Rebuild(); return; }   // the host's lobby changed (at most 4 a second)
            var n = m as Notice;
            if (n == null) return;
            string name = Named(n.Name, n.Player);
            if (!string.IsNullOrEmpty(TextSanitiser.Clean(n.Name, PlayerNames.MaxChars, ""))) names[n.Player] = name;
            switch (n.Kind)
            {
                case NoticeKind.Joining:
                    states[n.Player] = PlayerRow.Joining;
                    Push("join.notice.joining", name, NoticeQueue.Routine);
                    break;
                case NoticeKind.Waiting:
                    states[n.Player] = PlayerRow.Joining;
                    Push("join.notice.waiting", name, NoticeQueue.Routine);
                    break;
                case NoticeKind.Joined:
                    states[n.Player] = PlayerRow.InGame;
                    Push("join.notice.joined", name, NoticeQueue.Routine);
                    break;
                case NoticeKind.Rejoined:
                    states[n.Player] = PlayerRow.InGame;
                    Push("join.notice.rejoined", name, NoticeQueue.Routine);
                    break;
                case NoticeKind.CouldNotJoin:
                    states.Remove(n.Player);
                    Push("join.notice.couldNotJoin", name, NoticeQueue.Warning);
                    break;
                case NoticeKind.Left:
                    states[n.Player] = PlayerRow.Away;
                    Push("join.notice.left", name, NoticeQueue.Routine);
                    break;
                case NoticeKind.CopySaved:
                    Push("join.notice.copySaved", name, NoticeQueue.Routine);
                    break;
            }
            Rebuild();
        }

        void Push(string key, string name, int kind)
        {
            notices.Push(key, new object[] { name }, kind, NetClock.NowMs);
        }

        // A name from another game, cleaned; "Player N" when it cleans to nothing (the view's unknown name).
        static string Named(string raw, int id)
        {
            string n = TextSanitiser.Clean(raw, PlayerNames.MaxChars, "");
            return n.Length > 0 ? n : PlayerRow.Unknown(id).Name;
        }

        // ---- the view ----

        void Rebuild()
        {
            nextRebuild = Time.realtimeSinceStartup + RebuildEverySeconds;
            try
            {
                MoveTo(WantedPhase());
                view = Build();
            }
            catch (Exception e) { Log.Error("menu: the session view failed: " + e.Message); return; }
            Action changed = Changed;
            if (changed == null) return;
            try { changed(); }
            catch (Exception e) { Log.Error("menu: a listener of the session view failed: " + e); }
        }

        SessionPhase WantedPhase()
        {
            HostLobby lobby = Session.Lobby;
            if (lobby != null) return lobby.Room.Phase == SessionPhase.Idle ? SessionPhase.Lobby : lobby.Room.Phase;
            if (Session.Host != null) return SessionPhase.InGame;
            if (owner.Intents.HostIntent) return SessionPhase.Starting;
            SessionClient c = Session.Client;
            if (c != null && Session.Live)
            {
                if (c.Lobby.Holding) return SessionPhase.Lobby;   // in the host's lobby at the menu
                if (c.Flow.InGame) return SessionPhase.InGame;
                if (c.World.Loading || phase == SessionPhase.InGame || phase == SessionPhase.Loading)
                    return SessionPhase.Loading;
                return SessionPhase.Starting;
            }
            return SessionPhase.Idle;
        }

        // Walks to the phase M4's state says, one legal move at a time (SessionPhases), logging each.
        void MoveTo(SessionPhase want)
        {
            for (int i = 0; i < 8 && phase != want; i++)
            {
                SessionPhase next;
                bool rejoinLoad = want == SessionPhase.Loading && phase == SessionPhase.InGame;
                if (want == SessionPhase.Idle || want < phase && !rejoinLoad)
                    next = phase == SessionPhase.Ending ? SessionPhase.Idle : SessionPhase.Ending;
                else if (rejoinLoad) next = SessionPhase.Loading;
                else next = phase + 1;
                if (!SessionPhases.Allowed(phase, next)) return;
                Log.Info("session: phase " + phase + " -> " + next);
                phase = next;
            }
        }

        SessionView Build()
        {
            var v = new SessionView { Phase = phase };
            var rows = new List<PlayerRow>();
            Clock clock = null;
            SessionHost h = Session.Host;
            SessionClient c = Session.Client;
            HostLobby lobby = Session.Lobby;
            if (h != null && lobby != null)
            {
                // the host's lobby: the same roster every player sees, plus what only the host has
                RosterView.Fill(v, lobby.Room.Peek(h.PingMs), lobby.Room.HostId);
                v.JoinCode = h.JoinCodeText;
                v.NewGame = lobby.Setup.NewGame;
                v.SaveTitle = lobby.Setup.Title;
                v.SaveDate = lobby.HasWorld ? GameDate() : lobby.Setup.Date;
                v.StartBlockedWhy = lobby.Room.StartBlocked();
                v.StartLeavesOut = lobby.Room.Lobby.LeftOut();
                v.RoomPhase = lobby.Room.Phase;
                v.Progress = hostStart.Next(lobby.Room.Phase, v.Players);
                v.ApplyClock(phase == SessionPhase.InGame ? h.LastClock : null, pauses, NetClock.NowMs);
                return v;
            }
            if (c != null && Session.Live && c.Lobby.Follow.Last != null && !c.Lobby.Follow.Legacy)
            {
                // a joiner of a lobby host: the host's roster, in the lobby and in the game alike
                RosterView.Fill(v, c.Lobby.Follow.Last, c.PlayerId);
                v.Status = c.Lobby.Holding ? null : ClientStatus(c);
                v.Progress = JoinProgress.ForJoiner(JoinerState(c));
                v.ApplyClock(phase == SessionPhase.InGame ? clientClock : null, pauses, NetClock.NowMs);
                return v;
            }
            if (c != null && !Session.Live && c.Lobby.EndCode > 0)
            {
                // the host's lobby ended for this player before the join began (a refusal at the door, a kick, the
                // host's goodbye)
                v.EndedWhy = SessionEnds.Key(c.Lobby.EndCode, false, c.Lobby.EndedByHost);
                v.EndedReport = c.Lobby.EndLines;
                v.EndedHost = c.HostName;
                return v;
            }
            if (h != null)
            {
                v.IsHost = true;
                v.MyId = 0;
                v.JoinCode = h.JoinCodeText;
                v.SaveTitle = hostedSave;
                v.NewGame = hostedNewGame;
                v.SaveDate = GameDate();
                rows.Add(new PlayerRow
                {
                    Id = 0, Name = PlanName(), Agency = HostAgency(), Host = true, Me = true, State = PlayerRow.InGame,
                });
                foreach (KeyValuePair<int, string> p in h.Players())
                {
                    rows.Add(new PlayerRow
                    {
                        Id = p.Key, Name = Named(p.Value, p.Key), Agency = Get(agencies, p.Key) ?? "",
                        State = states.ContainsKey(p.Key) ? states[p.Key] : PlayerRow.Joining, PingMs = h.PingMs(p.Key),
                    });
                }
                clock = h.LastClock;
            }
            else if (c != null && Session.Live)
            {
                ClientRows(c, rows);
                v.MyId = c.PlayerId > 0 ? c.PlayerId : -1;
                clock = clientClock;
                v.Status = ClientStatus(c);
                v.Progress = JoinProgress.ForJoiner(JoinerState(c));
            }
            else if (c != null && c.Label.Shows && (c.Flow.BackCode > 0 || c.Flow.EndCode > 0))
            {
                // the session's end in the words its status gave: the host left, a kick, the link lost, a refusal
                JoinerFlow f = c.Flow;
                v.EndedWhy = f.BackCode > 0 ? SessionEnds.Key(f.BackCode, f.BackInGame, f.BackByHost)
                                            : SessionEnds.Key(f.EndCode, false, false);
                v.EndedReport = c.Flow.Report;
                v.EndedHost = c.HostName;
            }
            v.Players = SessionView.GroupByAgency(rows);
            v.ApplyClock(phase == SessionPhase.InGame ? clock : null, pauses, NetClock.NowMs);
            v.Agencies = Agencies(rows, c);
            return v;
        }

        void ClientRows(SessionClient c, List<PlayerRow> rows)
        {
            int me = c.PlayerId;
            int ping = c.RttMs >= 0 ? (int)Math.Round(c.RttMs) : -1;   // only this link's own round trip is known
            rows.Add(new PlayerRow
            {
                Id = me, Name = PlanName(), Agency = c.Flow.ClaimedAgency ?? "", Me = true, PingMs = ping,
                State = c.Flow.InGame ? PlayerRow.InGame : PlayerRow.Joining,
            });
            var seen = new HashSet<int> { me };
            AgencySeat[] seats = c.World.Seats;
            string hostName = c.HostName;
            string hostAgency = null;
            if (seats != null)
                foreach (AgencySeat s in seats)
                {
                    if (s == null) continue;
                    if (s.Player == 0) { hostAgency = s.Agency; if (!string.IsNullOrEmpty(s.Name)) hostName = s.Name; }
                    else if (seen.Add(s.Player))
                        rows.Add(new PlayerRow
                        {
                            Id = s.Player, Name = Get(names, s.Player) ?? Named(s.Name, s.Player),
                            Agency = s.Agency ?? "",
                            State = states.ContainsKey(s.Player) ? states[s.Player] : PlayerRow.InGame,
                        });
                }
            if (me > 0)
                rows.Add(new PlayerRow
                {
                    Id = 0, Name = Named(hostName, 0), Agency = hostAgency ?? "", Host = true, PingMs = ping,
                    State = PlayerRow.InGame,
                });
            seen.Add(0);
            foreach (KeyValuePair<int, int> s in states)
                if (seen.Add(s.Key))
                {
                    PlayerRow r = PlayerRow.Unknown(s.Key);
                    string n = Get(names, s.Key);
                    if (n != null) r.Name = n;
                    r.State = s.Value;
                    rows.Add(r);
                }
        }

        // The joiner's state for the progress view (JoinProgress.ForJoiner).
        static JoinerProgressState JoinerState(SessionClient c)
        {
            JoinerFlow f = c.Flow;
            LobbyFollow follow = c.Lobby.Follow;
            RosterPlayer mine = follow.Last != null ? follow.Mine : null;
            return new JoinerProgressState
            {
                Holding = c.Lobby.Holding, Room = follow.Last != null ? follow.Last.Phase : SessionPhase.Idle,
                MyState = mine != null ? mine.State : -1, LinkUp = c.Connected, Playing = f.Playing,
                Dropped = f.Dropped || f.Rejoining, OutOfStep = f.OutOfStepRejoin, ReloadCode = f.ReloadCode,
                Step = f.Step, Pct = f.ReceivedPct, Ahead = f.Ahead,
            };
        }

        static StatusLine ClientStatus(SessionClient c)
        {
            JoinerFlow f = c.Flow;
            var candidates = new List<StatusLine>();
            if (f.ClaimRefused) candidates.Add(StatusLine.Make("join.refused." + f.ClaimRefusedCode));
            // a full rejoin reloads the world (its reason rides along for the dialog); a drop waits to reconnect
            if (f.ReloadCode > 0 && !f.Playing) candidates.Add(StatusLine.Make(StatusLine.OutOfStep, f.ReloadCode));
            if (f.Rejoining) candidates.Add(StatusLine.Make(StatusLine.Rejoining));
            if (f.Dropped) candidates.Add(StatusLine.Make(StatusLine.Dropped));
            if (!f.InGame) candidates.Add(StatusLine.Make(StatusLine.Joining));
            return StatusLine.Pick(candidates);
        }

        // The picker's list while a join needs an agency, or the host picks its own before a new game's pages (this
        // game's own playable agencies, the refused ones not claimable); otherwise the agencies players hold.
        List<AgencyRow> Agencies(List<PlayerRow> rows, SessionClient c)
        {
            var list = new List<AgencyRow>();
            bool picking = PickerMode.Picking(c != null && Session.Live && c.Flow.ClaimRefused,
                                              Session.AtMenu, Session.Host != null, c != null, Session.Started);
            // a joiner whose host runs no lobby picks from its own agencies too
            if (c != null && Session.Live && c.Lobby.Holding && c.Lobby.Follow.Legacy) picking = true;
            if (picking)
            {
                if (c != null && c.Flow.ClaimRefused && c.Flow.LastClaim != null) refused.Add(c.Flow.LastClaim);
                foreach (string id in OwnAgencies())
                    list.Add(new AgencyRow
                    {
                        Id = id, State = AgencyStanding.AiRival, Claimable = !refused.Contains(id), Playable = true,
                        EpochAllows = true,
                    });
                return list;
            }
            foreach (PlayerRow r in rows)
            {
                if (string.IsNullOrEmpty(r.Agency)) continue;
                AgencyRow a = null;
                foreach (AgencyRow x in list) if (x.Id == r.Agency) a = x;
                if (a == null)
                {
                    a = new AgencyRow { Id = r.Agency, State = AgencyStanding.Human, Playable = true, EpochAllows = true };
                    list.Add(a);
                }
                a.Holders.Add(r.Id);
            }
            return list;
        }

        // This game's playable agencies in the wire form: every agency of its content but the World Government.
        List<string> OwnAgencies()
        {
            if (ownAgencies != null) return ownAgencies;
            var ids = new List<string>();
            try
            {
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                if (all != null && all.AllMyIDScriptableObjects != null)
                    foreach (MyIDScriptableObject o in all.AllMyIDScriptableObjects)
                    {
                        var d = o as CompanyDefinition;
                        string w = d != null && !d.IsWorldGovernment ? AgencyWire.From(d.ID) : null;
                        if (JoinCheck.IsAgencyId(w) && !ids.Contains(w)) ids.Add(w);
                    }
            }
            catch (Exception e) { Log.Warn("menu: the agency list could not be read: " + e.Message); }
            ids.Sort(StringComparer.Ordinal);
            ownAgencies = ids;
            return ids;
        }

        static string PlanName()
        {
            SessionPlan p = Session.Plan;
            return p != null ? p.PlayerName : OwnName();
        }

        static string HostAgency()
        {
            try { return AgencyWire.From(MonoBehaviourSingleton<Manager.GameManager>.Instance.Player.ID) ?? ""; }
            catch (Exception) { return ""; }
        }

        static string GameDate()
        {
            try { return MonoBehaviourSingleton<Manager.TimeController>.Instance.CurrentTime.ToString("d MMM yyyy",
                System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        static string Get(Dictionary<int, string> d, int id)
        {
            string s;
            return d.TryGetValue(id, out s) ? s : null;
        }

        // The view as log lines (the PLAYERS button with no screen yet, and the dev trigger). Names are peer text:
        // cleaned again for the one-line log.
        public void LogView(string why)
        {
            Rebuild();
            SessionView v = view;
            Log.Info("session view (" + why + "): " + v + ", host " + v.IsHost + ", me " + v.MyId +
                     (v.Status != null ? ", status " + v.Status.Key : "") +
                     (v.EndedWhy != null ? ", ended " + v.EndedWhy : ""));
            foreach (PlayerRow r in v.Players)
                Log.Info("session view: player " + r.Id + LogPrivacy.ViewName(r.Name, Boot.Profile == null)
                         + " agency " +
                         (r.Agency.Length > 0 ? r.Agency : "-") + ", state " + r.State + ", ping " +
                         (r.PingMs >= 0 ? UiText.Get("hud.players.ping", r.PingMs) : "-") + (r.Me ? " (me)" : ""));
            int claimable = 0;
            foreach (AgencyRow a in v.Agencies) if (a.Claimable) claimable++;
            Log.Info("session view: " + v.Agencies.Count + " agencies (" + claimable + " claimable), " +
                     v.Pauses.Count + " pause reasons, " + notices.History().Count + " notices");
        }
    }
}
