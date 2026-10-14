using System;

namespace SolarExpanseMultiplayer
{
    // The joiner's side of the host's lobby (M8 DD5), over SessionClient. A join from the menus or a Steam invite
    // (SessionPlan.Lobby) holds its Welcome: the player waits at the main menu in the host's lobby, sends its content
    // summary for the door's check, claims and readies through the lobby's messages, and sees the host's Roster. When
    // the roster lets this player in, M4's join (JoinerFlow) starts with the lobby claim, from the same link. A host
    // that sends no roster (a dev host) is joined the M4 way after LobbyFollow.LegacyAfterMs with the agency picked
    // before, or the one picked then. Owned by its SessionClient; main thread only.
    public sealed class ClientLobby
    {
        readonly SessionClient client;
        LobbyFollow follow = new LobbyFollow();
        bool on;
        int heldPlayer;
        Func<Message, bool> heldSend;
        Action<IBulkSink> heldAttach;

        public ClientLobby(SessionClient client) { this.client = client; }

        public LobbyFollow Follow { get { return follow; } }
        // An agency picked before connecting (the join code's picker, a test profile's dev trigger), claimed in the
        // lobby once the roster shows it free; AutoReady also readies with it (a test profile's dev join, which has no
        // screens).
        public string Preset;
        public bool AutoReady;
        // Why the lobby ended for this player before the join began (the host's refusal or goodbye), for the screens;
        // 0 while none.
        public int EndCode { get; private set; }
        public string[] EndLines { get; private set; }
        public bool EndedByHost { get; private set; }

        // A held Welcome: connected, in the host's lobby at the menu.
        public bool Holding { get { return on && follow.Held; } }

        // A new join (or none): whether this one waits in a lobby, and nothing kept from the last.
        public void NewJoin(bool lobby)
        {
            on = lobby;
            follow = new LobbyFollow();
            Preset = null;
            AutoReady = false;
            heldSend = null; heldAttach = null;
            EndCode = 0; EndLines = null; EndedByHost = false;
        }

        // The Welcome: true when it is held here (the lobby's first link, before the player was let in); false hands it
        // to M4's join at once (a dev join, or a rejoin of a player already let in).
        public bool Hold(int player, Func<Message, bool> send, Action<IBulkSink> attach)
        {
            if (!on || follow.ReleasedWith != null) return false;
            heldPlayer = player; heldSend = send; heldAttach = attach;
            follow.Welcomed(player, NetClock.NowMs);
            EndCode = 0; EndLines = null; EndedByHost = false;
            summarySent = false;
            Log.Info("lobby: connected as player " + player + "; waiting for the host's lobby");
            return true;
        }

        bool summarySent;

        // The door's content check, once the host has shown it runs a lobby (its first roster): a host with no lobby
        // takes a summary as its join's own, and the join's second one later would break its protocol.
        void SendDoorSummary()
        {
            if (summarySent) return;
            summarySent = true;
            ContentSummary own = null;
            try { own = LobbyDoor.Clipped(client.World.OwnSummary); }
            catch (Exception e)
            {
                Log.Warn("lobby: this game's content summary could not be read: " + e.GetType().Name);
            }
            if (own != null) client.Send(own);   // M4's join checks again once let in
            Log.Info("lobby: in the host's lobby; content summary sent for its door");
        }

        // A message from the host (before the join's own handling).
        public void Received(Message m)
        {
            var roster = m as Roster;
            if (roster != null)
            {
                if (follow.OnRoster(roster) && Holding) SendDoorSummary();
                return;
            }
            var refused = m as JoinRefused;
            if (refused != null && Holding)
            {
                EndCode = refused.Code;
                EndLines = null;
                if (refused.Lines != null)
                {
                    // the host's words: cleaned and cut again, as the join's own refusal report is
                    var lines = new System.Collections.Generic.List<string>();
                    foreach (string l in refused.Lines)
                    {
                        string c = TextSanitiser.Clean(l, JoinLimits.MaxReportLineChars, "");
                        if (c.Length > 0 && lines.Count < JoinLimits.MaxReportLines) lines.Add(c);
                    }
                    EndLines = lines.ToArray();
                }
                Log.Info("lobby: the host refused this player at its door (" + refused.Code + ")");
            }
        }

        // The player left the lobby on purpose (Leave): nothing to report when the link closes.
        public void LeftOnPurpose() { follow.Clear(); }

        // The held link closed (the host's goodbye, a kick, a refusal, a lost link): the lobby is over for this player.
        public void Closed(bool bye, int byeCode)
        {
            if (!Holding) return;
            follow.Clear();
            if (EndCode == 0) EndCode = bye ? (byeCode > 0 ? byeCode : JoinCodes.HostLeft) : JoinCodes.Reconnects;
            EndedByHost = bye;
            Log.Info("lobby: left the host's lobby (" + EndCode + ")");
        }

        // Every frame: the release when it is due, else the next message a preset agency calls for.
        public void Update()
        {
            if (!Holding) return;
            string agency = follow.Due(NetClock.NowMs, Preset);
            if (agency != null) { Release(agency); return; }
            Message next = follow.Auto(Preset, AutoReady);
            if (next != null) client.Send(next);
        }

        // A host with no lobby: the player's pick (after the wait) starts the join. False when it cannot now.
        public bool PickWithoutLobby(string agency)
        {
            string a = Holding ? follow.PickedWithoutLobby(agency) : null;
            if (a == null) return false;
            Release(a);
            return true;
        }

        void Release(string agency)
        {
            Log.Info("lobby: let into the game as " + agency + (follow.Legacy ? " (the host runs no lobby)" : ""));
            client.World.SetAgency(agency);
            Func<Message, bool> send = heldSend;
            Action<IBulkSink> attach = heldAttach;
            heldSend = null; heldAttach = null;
            if (send == null || attach == null) return;
            client.Flow.Welcomed(heldPlayer, send, attach, false);
        }
    }
}
