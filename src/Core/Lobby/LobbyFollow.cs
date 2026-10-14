using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The joiner's side of the host's lobby (M8 DD5). A joiner from the menus or a Steam invite holds its Welcome at
    // the main menu: M4's join (JoinerFlow: the content check, the claim, the snapshot) starts only once the host lets
    // this player in, which its row in the host's Roster says (joining, with the agency it claimed in the lobby). A
    // host that sends no Roster at all (a dev host started from a save, the laptop line) is joined the M4 way once
    // LegacyAfterMs has passed, with the agency picked before connecting. Pure; the joiner's main thread.
    public sealed class LobbyFollow
    {
        public const int LegacyAfterMs = 3000;   // well inside a dev host's 10 s for the summary

        bool held, legacy, claimSent, readySent;
        long welcomedMs;
        int me = -1;
        Roster last;
        string releasedWith;

        // A Welcome is held: connected, waiting in the lobby (or for the host to say whether it runs one).
        public bool Held { get { return held; } }
        // The host sent no Roster in time: it runs no lobby, so the join goes the M4 way.
        public bool Legacy { get { return legacy; } }
        public Roster Last { get { return last; } }
        public int Me { get { return me; } }
        // The agency M4's join started with, once let in; null before.
        public string ReleasedWith { get { return releasedWith; } }

        public void Welcomed(int player, long nowMs)
        {
            held = true; legacy = false; claimSent = false; readySent = false;
            me = player; welcomedMs = nowMs; last = null; releasedWith = null;
        }

        // The link is gone, or the join was let in: nothing is held any more.
        public void Clear() { held = false; }

        // A Roster from the host; false when it is older than the one kept (the newest wins).
        public bool OnRoster(Roster r)
        {
            if (r == null || (last != null && r.Seq <= last.Seq)) return false;
            last = r;
            return true;
        }

        public RosterPlayer Mine
        {
            get
            {
                if (last == null) return null;
                foreach (RosterPlayer p in last.Players) if (p != null && p.Id == me) return p;
                return null;
            }
        }

        public RosterAgency Agency(string id)
        {
            if (last == null || id == null) return null;
            foreach (RosterAgency a in last.Agencies) if (a != null && a.Id == id) return a;
            return null;
        }

        // The agency to start M4's join with now, or null (once: the hold ends with it). preset: an agency picked
        // before connecting (the join code's picker, a test profile's dev trigger), used only for a host with no lobby.
        public string Due(long nowMs, string preset)
        {
            if (!held) return null;
            if (last == null)
            {
                if (!legacy && nowMs - welcomedMs >= LegacyAfterMs) legacy = true;
                return legacy && JoinCheck.IsAgencyId(preset) ? Let(preset) : null;
            }
            // let in: the start set or a late joiner (Joining), or a player whose seat the host kept and who came
            // back on a new lobby link (the host already treats that link as its M4 rejoin): either way the join
            // starts now with the roster's agency, never waiting for a Ready or a Start that will not come
            RosterPlayer mine = Mine;
            if (mine != null && mine.State > PlayerRow.Lobby && JoinCheck.IsAgencyId(mine.Agency))
                return Let(mine.Agency);
            return null;
        }

        // A host with no lobby: the agency picked after the wait starts the join.
        public string PickedWithoutLobby(string agency)
        {
            return held && legacy && JoinCheck.IsAgencyId(agency) ? Let(agency) : null;
        }

        string Let(string agency)
        {
            held = false;
            releasedWith = agency;
            return agency;
        }

        // The next lobby message an agency picked before connecting calls for, or null: its claim, once, while this
        // player holds none and the agency is free; then (ready) Ready, once, while the lobby waits for it.
        public Message Auto(string preset, bool ready)
        {
            RosterPlayer mine = Mine;
            if (!held || mine == null || last.Phase == SessionPhase.Ending) return null;
            if (!JoinCheck.IsAgencyId(preset)) return null;
            if (!claimSent && string.IsNullOrEmpty(mine.Agency))
            {
                RosterAgency a = Agency(preset);
                if (a == null || !a.Claimable) return null;
                claimSent = true;
                return new LobbyClaim { Agency = preset };
            }
            if (ready && !readySent && mine.Agency == preset && !mine.Ready && last.Phase == SessionPhase.Lobby)
            {
                readySent = true;
                return new LobbyReady { Ready = true };
            }
            return null;
        }
    }

    // The screens' view of a lobby from a Roster: the host's own (from its room) and a joiner's (from the last one it
    // heard) are built the same way, so both show the same lobby.
    public static class RosterView
    {
        public static void Fill(SessionView v, Roster r, int me)
        {
            if (v == null || r == null) return;
            var rows = new List<PlayerRow>();
            foreach (RosterPlayer p in r.Players)
            {
                if (p == null) continue;
                rows.Add(new PlayerRow
                {
                    Id = p.Id, Name = LobbyLimits.Name(p.Name, PlayerRow.Unknown(p.Id).Name), Agency = p.Agency ?? "",
                    Host = p.Id == r.HostId, Me = p.Id == me, Ready = p.Ready, State = p.State, Check = p.Check,
                    JoinPct = p.JoinPct, JoinSecsLeft = p.JoinSecsLeft, PingMs = p.PingMs,
                });
            }
            v.Players = SessionView.GroupByAgency(rows);
            v.Agencies = new List<AgencyRow>();
            foreach (RosterAgency a in r.Agencies)
            {
                if (a == null) continue;
                var row = new AgencyRow
                {
                    Id = a.Id, State = (AgencyStanding)a.State, Claimable = a.Claimable, Playable = true,
                    EpochAllows = true, LastHolder = a.LastHolder,
                };
                if (a.Holders != null) row.Holders.AddRange(a.Holders);
                v.Agencies.Add(row);
            }
            v.MyId = me;
            v.IsHost = me == r.HostId;
            v.RoomPhase = r.Phase;
            v.NewGame = r.Options != null && r.Options.NewGame;
            v.SaveTitle = r.SaveTitle;
            v.SaveDate = r.SaveDate;
            if (r.Options != null) { v.PlayerLimit = r.Options.MaxPlayers; v.LateJoin = r.Options.LateJoin; }
        }
    }

    // The content check at the lobby's door (DD5): a joiner's summary against the host's own, compared as M4's join
    // compares them, so a mismatch shows before the game starts. The lobby asks for no details, so the report has the
    // summary's lines only; a join that gets that far is checked again in full.
    public static class LobbyDoor
    {
        // 0 = the same content; else the refusal code (as M4's join would give it) and the report's lines.
        public static int Judge(ContentSummary own, ContentSummary theirs, out string[] lines)
        {
            lines = null;
            if (own == null || theirs == null) return JoinCodes.ContentUnavailable;
            ContentSummary host = Clipped(own);
            ContentDiff d = ContentCompare.Compare(host, theirs);
            if (d == null) return JoinCodes.ContentUnavailable;
            if (d.Same) return 0;
            lines = ContentReport.Lines(d, host, theirs, null, null);
            return JoinService.CodeOf(d);
        }

        // The summary as a joiner sends it: its game version cut to the message's cap, so both sides compare the same
        // text.
        public static ContentSummary Clipped(ContentSummary w)
        {
            if (w == null) return null;
            return new ContentSummary
            {
                GameBuild = w.GameBuild, GameVersion = ModInfo.ClipGameVersion(w.GameVersion),
                ModVersion = w.ModVersion,
                Loader = w.Loader, ManagedHash = w.ManagedHash, ManagedCount = w.ManagedCount, Plugins = w.Plugins,
                Assemblies = w.Assemblies, Categories = w.Categories, Overall = w.Overall,
            };
        }
    }
}
