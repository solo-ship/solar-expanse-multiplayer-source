using System;
using System.Collections.Generic;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // Test profiles only: Steam lobby joins with no screens, so a two-PC invite test can run. The dev trigger
    // "agency <id>" sets the agency a lobby join plays (the test tools write it before the start): the menus' session
    // root (M4SessionAdapter, when the profile has one) joins the host's lobby and claims and readies it there; with
    // no session root a checked join request (an invite, Join Game in the friends list, "+connect_lobby") becomes a
    // session join as that agency here. "Leave this game to join X?" is answered yes once the player is back at the
    // main menu. Dev triggers: "agency <id>",
    // "invite" (Steam's invite dialog for the hosted lobby) and "lobby" (the lobby's id, access, members and data).
    // Every step logs with the "steam lobby:" prefix. Owned by SteamNet; main thread.
    public sealed class LobbyDevJoin
    {
        readonly SteamNet net;
        readonly SteamLobby lobby;
        string agency;
        float nextTrigger;
        bool askedToLeave;

        public LobbyDevJoin(SteamNet net)
        {
            this.net = net;
            lobby = net.Lobby;
            lobby.JoinReady += OnReady;
            lobby.LeaveGameFirst += r =>
            {
                askedToLeave = true;
                Log.Info("steam lobby: dev join: a game is on; the join goes on by itself once this game is back at "
                    + "the main menu");
            };
            lobby.JoinFailed += (p, words) => Log.Info("steam lobby: dev join: no join (" + p + ")");
            lobby.HostLobbyCreated += id => Log.Info("steam lobby: dev: hosting " + net.Short(id)
                + "; dev trigger \"invite\" opens Steam's invite dialog, \"lobby\" lists it");
            Log.Info("steam lobby: dev join ready (test profile): lobby joins start a session with no screens");
        }

        public void Update()
        {
            if (askedToLeave && lobby.JoinState == LobbyJoinState.AskingToLeave && !lobby.InGame && lobby.ConfirmJoin())
            {
                askedToLeave = false;
                Log.Info("steam lobby: dev join: back at the menu; \"leave this game first\" answered yes");
            }
            if (lobby.JoinState != LobbyJoinState.AskingToLeave) askedToLeave = false;
            if (!DevTriggers.Every(ref nextTrigger, Time.realtimeSinceStartup)) return;
            string a = DevTriggers.Take("agency");
            if (a != null) SetAgency(a.Trim());
            if (DevTriggers.Take("invite") != null) Invite();
            if (DevTriggers.Take("lobby") != null) Describe();
        }

        void SetAgency(string a)
        {
            if (!JoinCheck.IsAgencyId(a))
            {
                Log.Warn("steam lobby: dev agency: not an agency id (a-z, 0-9 and _)");
                return;
            }
            agency = a;
            Log.Info("steam lobby: waiting for an invite (or Join Game in the Steam friends list); a lobby join "
                + "claims " + a);
        }

        // The agency the dev trigger set (null before): the session root claims it in the host's lobby and readies.
        public string Agency { get { return agency; } }

        void OnReady(LobbyJoinTarget t)
        {
            // the menus' session root takes every lobby join (it waits in the host's lobby, with this agency)
            if (Session.Early != null) return;
            string host = net.Short(t.HostId);
            if (agency == null)
            {
                Log.Warn("steam lobby: dev join: no agency set (dev trigger \"agency <id>\"); leaving the lobby");
                lobby.CancelJoin("no agency to join as");
                return;
            }
            SessionPlan plan = SessionPlan.JoinSteamLobby(M4SessionAdapter.OwnName(), t.HostId);
            Log.Info("steam lobby: dev join: starting the session with host " + host + " as " + agency);
            string why = null;
            if (Session.Client != null) why = Session.JoinAgain(plan, agency);
            else
            {
                Session.StartFromPlan(plan, agency);
                if (Session.Client == null) why = "the session did not start (the session lines say why)";
            }
            if (why == null) return;
            Log.Warn("steam lobby: dev join: not started: " + why);
            lobby.CancelJoin("the session did not start");
        }

        void Invite()
        {
            bool overlay = lobby.OverlayOn;
            string why = lobby.OpenInviteOverlay();
            if (why == null) { Log.Info("steam lobby: dev invite: Steam's invite dialog asked for"); return; }
            if (!overlay)
                Log.Warn("steam lobby: dev invite: overlay off. Invite from the Steam friends list instead (the "
                    + "friend's name > Invite to Game), or the friend uses Join Game on this player");
            else Log.Warn("steam lobby: dev invite: not opened: " + why);
        }

        void Describe()
        {
            ulong id = lobby.Hosting ? lobby.HostedLobby : lobby.JoinedLobby;
            if (id == 0)
            {
                Log.Info("steam lobby: dev lobby: in no lobby (join state " + lobby.JoinState + ")");
                return;
            }
            var members = new List<string>();
            foreach (ulong m in lobby.Members())
                members.Add(net.Short(m) + " " + (lobby.MemberName(m) ?? "(name not known yet)"));
            var data = new List<string>();
            foreach (string k in LobbyData.AllKeys)
                data.Add(k + "=" + (k == LobbyData.KeyHost ? net.Short(Parse(lobby.Data(id, k))) : lobby.Data(id, k)));
            Log.Info("steam lobby: dev lobby: " + net.Short(id) + (lobby.Hosting ? " (hosting, " + lobby.Access + ")"
                : " (joined)") + "; overlay " + (lobby.OverlayOn ? "on" : "off") + "; " + members.Count
                + " member(s): " + string.Join(", ", members.ToArray()));
            Log.Info("steam lobby: dev lobby data: " + TextSanitiser.OneLine(string.Join(" ", data.ToArray())));
        }

        static ulong Parse(string s)
        {
            ulong v;
            return ulong.TryParse(s, out v) ? v : 0;
        }
    }
}
