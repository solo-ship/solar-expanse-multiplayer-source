using System;
using System.Collections.Generic;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The host's lobby at the main menu and through the session (M8 DD5), over SessionHost. The room (LobbyRoom) is fed
    // by the links' events and the lobby's messages; the content check runs at the door (LobbyDoor); the Roster goes to
    // everyone on each change, 4 a second at most, with each player's Steam name where Steam gave one (the gate's name
    // for a Steam peer). At Start the host's world loads (LobbyWorld); once it has, Session builds M4's join (JoinHost,
    // told to leave the links' Confirmed to this class) and every member let in goes into it with its lobby claim. A
    // released member's rejoin goes straight to M4's join. Owned by Session (Session.Lobby); main thread.
    public sealed class HostLobby
    {
        const float FollowEverySeconds = 0.5f;

        readonly SessionHost host;
        readonly ContentScan content;
        public readonly LobbySetup Setup;
        public readonly LobbyRoom Room;
        JoinHost join;
        float nextFollow;
        int errors;

        // After the room changed and its roster went out (the screens' view follows).
        public event Action Changed;

        public HostLobby(SessionHost host, LobbySetup setup, ContentScan content, string hostName)
        {
            this.host = host;
            this.content = content;
            Setup = setup;
            Room = new LobbyRoom(setup.Options, setup.Agencies, setup.NewGame, 0, hostName);
            Room.Lobby.SaveTitle = setup.Title;
            Room.Lobby.SaveDate = setup.Date;
            if (setup.HostAgency != null)
            {
                // a save's host plays the save's own agency; a new game's host may change its first pick in the lobby
                int code = setup.NewGame ? Room.Claim(0, setup.HostAgency, NetClock.NowMs)
                                         : Room.FixHostAgency(setup.HostAgency);
                if (code != 0)
                    Log.Warn("lobby: the host's agency " + setup.HostAgency + " was not claimed (" + code + ")");
            }
            host.Confirmed += (id, name, rejoin) => Safely(() => OnConfirmed(id, name, rejoin));
            host.Left += id => Safely(() => Room.Gone(id));
            host.LeftForGood += id => Safely(() => Room.LeftForGood(id));
            host.Received += (id, m) => Safely(() => OnReceived(id, m));
        }

        public bool Starting
        {
            get { return Room.Phase == SessionPhase.Starting || Room.Phase == SessionPhase.Loading; }
        }
        public bool HasWorld { get { return join != null; } }

        void Safely(Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                errors++;
                if (errors <= 5 || errors % 1000 == 0)
                    Log.Error("lobby: handling an event failed (" + errors + "): " + e);
            }
        }

        // ---- the door ----

        void OnConfirmed(int id, string name, bool rejoin)
        {
            if (join != null && (Room.Released(id) || rejoin && !Room.Lobby.Has(id)))
            {
                // a player already let in, back on a new link: M4's rejoin
                join.Confirmed(id, name, rejoin);
                Room.Back(id);
                return;
            }
            ulong steam = host.SteamIdOf(id);
            int code = Room.Arrive(id, name, steam != 0 ? "steam:" + steam : "link");
            if (code != 0)
            {
                Log.Info("lobby: player " + id + " refused at the lobby's door (" + code + ")");
                host.Send(id, Refusal(code, null));
                host.Kick(id, code, false);
                return;
            }
            Log.Info("lobby: player " + id + " is in the lobby" + (steam != 0 ? " (a Steam player)" : ""));
        }

        void OnReceived(int id, Message m)
        {
            var claim = m as LobbyClaim;
            if (claim != null)
            {
                int code = Room.Claim(id, claim.Agency, NetClock.NowMs);
                if (code != 0)
                    Log.Info("lobby: player " + id + "'s claim of " + claim.Agency + " refused (" + code + ")");
                return;
            }
            var ready = m as LobbyReady;
            if (ready != null) { Room.Ready(id, ready.Ready); return; }
            var summary = m as ContentSummary;
            if (summary != null && Room.InLobby(id)) Door(id, summary);
        }

        // The content check at the door: a difference is refused with the join's own code and lines, and the link
        // closed, before the game ever starts.
        void Door(int id, ContentSummary s)
        {
            string[] lines;
            int code = LobbyDoor.Judge(content != null ? content.HostSummary : null, s, out lines);
            if (code == 0)
            {
                Room.Checked(id, true);
                Log.Info("lobby: player " + id + " content same");
                return;
            }
            Room.Checked(id, false);
            Log.Info("lobby: player " + id + "'s content differs (" + code + "); refused at the door");
            host.Send(id, Refusal(code, lines));
            host.Kick(id, code, false);
        }

        static JoinRefused Refusal(int code, string[] lines)
        {
            return new JoinRefused
            {
                Code = code, Lines = lines == null ? null : TextBudget.FitLines(lines, JoinLimits.MaxReportLines,
                    JoinLimits.MaxReportLineChars, JoinLimits.ReportMaxBytes),
            };
        }

        // ---- the start ----

        // The host's Start (DD5). Null = the host's world is loading; else a UiText key.
        public string Start(bool anyway)
        {
            string why = Room.Start(anyway);
            if (why != null) return why;
            Log.Info("lobby: start" + (anyway ? " anyway" : "") + "; human agencies " +
                     string.Join(",", Room.HumanAgencies().ToArray()));
            string load = LobbyWorld.Begin(Setup, Room);
            if (load != null)
            {
                Room.StartFailed();
                return load;
            }
            Room.Loading();
            return null;
        }

        // The host's world has loaded and M4's join exists: the start set goes in.
        public void WorldLoaded(JoinHost j)
        {
            join = j;
            List<int> go = Room.WorldLoaded();
            Log.Info("lobby: the host's world is up; letting in " + (go.Count == 0 ? "nobody yet" :
                     string.Join(",", go.ConvertAll(i => i.ToString()).ToArray())));
        }

        // The world did not come (a failed load, the menu back): everyone waits in the lobby again.
        public void WorldFailed()
        {
            Room.StartFailed();
            Log.Warn("lobby: the host's world did not load; back in the lobby");
        }

        public void End() { Room.End(); }

        // ---- every frame (Session.Update) ----

        public void Update()
        {
            long now = NetClock.NowMs;
            Room.Pump(now);
            // let in first, so M4's join knows the player before the roster that tells it to start
            foreach (int id in Room.TakeReleased()) LetIn(id);
            if (join != null && Time.realtimeSinceStartup >= nextFollow)
            {
                nextFollow = Time.realtimeSinceStartup + FollowEverySeconds;
                foreach (int id in Room.Lobby.Ids())
                {
                    if (!Room.Released(id)) continue;
                    bool connected = host.IsConnected(id);
                    if (!connected && !join.HoldsSeat(id)) { Room.JoinEnded(id); LogEnded(id); continue; }
                    Room.Follow(id, connected, join.InGame(id));
                    if (!connected) Room.StillGone(id, InSteamLobby(id), now);
                }
            }
            if (!Room.RosterDue(now)) return;
            host.Broadcast(Room.TakeRoster(now, host.PingMs));
            Action changed = Changed;
            if (changed != null) changed();
        }

        // The member's game is still in this host's Steam lobby; true for a player not on Steam or with no lobby.
        bool InSteamLobby(int id)
        {
            ulong steam = host.SteamIdOf(id);
            SteamNet net = SempRoot.Instance != null ? SempRoot.Instance.Steam : null;
            if (steam == 0 || net == null || !net.Lobby.Hosting) return true;
            try { return net.Lobby.IsMember(steam); }
            catch (Exception) { return true; }
        }

        static void LogEnded(int id)
        {
            Log.Info("lobby: player " + id + "'s join ended before it loaded; it leaves the room, its agency free");
        }

        void LetIn(int id)
        {
            if (join == null || !host.IsConnected(id)) return;
            string agency = Room.Lobby.ClaimedAgency(id);
            Log.Info("lobby: player " + id + " let into the game as " + agency);
            join.Confirmed(id, Room.Lobby.NameOf(id), false);
        }
    }
}
