using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    public sealed class PickerCard
    {
        public string Id;
        public LobbyLine Name, Status, Last;
        public bool Mine, Pickable, Dim;
        public int Refused = -1;            // the host refused it in this join: the refusal's code
        public LobbyLine WhyNot;            // why a click does not claim it (null when it can be picked)
    }

    // What the agency picker shows: one card per agency of the view, whether it can be claimed and why not, and the
    // line under the cards. Made only from the view and the picks the host refused in this join (PickerMemory), so an
    // agency the host refused is never offered again.
    public sealed class PickerBoard
    {
        public List<PickerCard> Cards = new List<PickerCard>();
        public LobbyLine Message;
        public bool Sharing;

        public static PickerBoard Build(SessionView v, IDictionary<string, int> refusedHere)
        {
            var b = new PickerBoard();
            if (v == null) v = new SessionView();
            PlayerRow me = LobbyText.Me(v);
            string mine = me != null && !string.IsNullOrEmpty(me.Agency) ? me.Agency : null;
            bool anyPickable = false;
            foreach (AgencyRow a in v.Agencies)
            {
                if (a == null || a.Id == null) continue;
                PickerCard c = Card(v, a, mine, refusedHere);
                if (c.Pickable) anyPickable = true;
                if (c.Status.Key == "lobby.pick.share") b.Sharing = true;
                b.Cards.Add(c);
            }
            LobbyLine refusal = LobbyText.RefusalCode(v.Status) >= 0 ? LobbyText.Status(v.Status) : null;
            if (refusal != null) b.Message = refusal;
            else if (b.Cards.Count == 0) b.Message = new LobbyLine("lobby.pick.none");
            else if (!anyPickable && mine == null) b.Message = new LobbyLine("join.refused.14", null, false, true);
            else b.Message = new LobbyLine(b.Sharing ? "lobby.pick.hintShare" : "lobby.pick.hint");
            return b;
        }

        static PickerCard Card(SessionView v, AgencyRow a, string mine, IDictionary<string, int> refusedHere)
        {
            var c = new PickerCard { Id = a.Id, Name = LobbyText.Agency(a.Id), Mine = a.Id == mine };
            List<string> others = LobbyText.HolderNames(v, a, false, true);
            int code;
            if (refusedHere != null && refusedHere.TryGetValue(a.Id, out code))
            {
                c.Refused = code;
                c.Status = new LobbyLine(code == JoinCodes.AgencyTaken ? "lobby.pick.taken"
                    : code == JoinCodes.AgencyUnknown ? "lobby.pick.notInHost"
                    : code == JoinCodes.AgencyNotPlayable ? "lobby.pick.unplayable" : "lobby.pick.refused");
                c.WhyNot = RefusalLine(code);
            }
            else if (c.Mine)
            {
                c.Status = others.Count > 0
                    ? new LobbyLine("lobby.pick.yoursWith", new object[] { LobbyText.Names(others) }, true)
                    : new LobbyLine("lobby.pick.yours");
                c.WhyNot = new LobbyLine("lobby.pick.alreadyYours");
            }
            else if (others.Count > 0)
            {
                object[] names = { LobbyText.Names(others) };
                if (a.Claimable && a.Playable && a.EpochAllows)
                    c.Status = new LobbyLine("lobby.pick.share", names, true);
                else
                {
                    c.Status = new LobbyLine("lobby.pick.takenBy", names, true);
                    c.WhyNot = RefusalLine(JoinCodes.AgencyTaken);
                }
            }
            else if (!a.Playable)
            {
                c.Status = new LobbyLine("lobby.pick.unplayable");
                c.WhyNot = RefusalLine(JoinCodes.AgencyNotPlayable);
            }
            else if (!a.EpochAllows)
            {
                c.Status = new LobbyLine("lobby.pick.epoch");
                c.WhyNot = new LobbyLine("lobby.pick.epochWhy", null, false, true);
            }
            else if (!a.Claimable)
            {
                bool out_ = a.State == AgencyStanding.NotInGame;
                c.Status = new LobbyLine(out_ ? "lobby.pick.notInGame" : "lobby.pick.unavailable");
                c.WhyNot = new LobbyLine(out_ ? "lobby.pick.notInGameWhy" : "join.badAgency", null, false, true);
            }
            else
                c.Status = new LobbyLine(a.State == AgencyStanding.AiRival ? "lobby.pick.freeAi"
                    : a.State == AgencyStanding.Idle ? "lobby.pick.freeIdle"
                    : a.State == AgencyStanding.NotInGame ? "lobby.pick.freeOut" : "lobby.pick.free");
            c.Pickable = c.WhyNot == null;
            c.Dim = !c.Pickable && !c.Mine;
            if (others.Count == 0 && !c.Mine)
            {
                string last = TextSanitiser.Clean(a.LastHolder, PlayerNames.MaxChars, "");
                if (last.Length > 0) c.Last = new LobbyLine("lobby.card.last", new object[] { last }, true);
            }
            return c;
        }

        // The table's line for a refusal code (join.refused.<code>), or the general one with the code.
        public static LobbyLine RefusalLine(int code)
        {
            string key = "join.refused." + code;
            return UiText.Has(key) ? new LobbyLine(key, null, false, true)
                                   : new LobbyLine("join.refused.unknown", new object[] { code }, false, true);
        }

        public string Shape()
        {
            var sb = new System.Text.StringBuilder();
            foreach (PickerCard c in Cards) sb.Append(c.Id).Append(c.Last != null ? "l" : "").Append(',');
            return sb.ToString();
        }
    }

    // The picker's memory of one join (Track U on M4: the host answers a claim with a refusal and the joiner picks
    // again, never the same agency, within M4's claim rules): which agency was asked for last, and every agency the
    // host refused. A new join forgets it all.
    public sealed class PickerMemory
    {
        readonly Dictionary<string, int> refused = new Dictionary<string, int>(StringComparer.Ordinal);
        string asked;

        public IDictionary<string, int> Refused { get { return refused; } }
        public string Asked { get { return asked; } }

        public void Asking(string agencyId) { asked = agencyId; }

        public void Forget() { refused.Clear(); asked = null; }

        // The host refused the agency asked for last: the view reports a claim refusal AND lists that agency as no
        // longer claimable (a refusal still showing for an earlier pick, while the new one waits for its answer,
        // leaves the new agency claimable, so it is never marked by mistake). Remembered, and true once (the
        // screens mark it; the refusal's own dialog is another screen's). Only the claim refusals a new pick can
        // answer count; the others end the join.
        public bool Observe(SessionView v)
        {
            if (v == null || asked == null) return false;
            int code = LobbyText.RefusalCode(v.Status);
            if (!CanPickAgain(code)) return false;
            AgencyRow a = v.Agency(asked);
            if (a == null || a.Claimable) return false;
            refused[asked] = code;
            asked = null;
            return true;
        }

        public static bool CanPickAgain(int code)
        {
            return code == JoinCodes.AgencyTaken || code == JoinCodes.AgencyUnknown ||
                   code == JoinCodes.AgencyNotPlayable;
        }
    }

    // When the session's view lists this game's own agencies to pick from (M4SessionAdapter): a claim the host
    // refused, or the host's own pick before a new game's pages, at the menu in a process that has not been in a
    // session (M4 hosts or joins once per process). A joiner never picks from its own list before connecting: it
    // connects first and picks from the host's roster, where taken agencies show as taken.
    public static class PickerMode
    {
        public static bool Picking(bool claimRefused, bool atMenu, bool hosting, bool joined, bool started)
        {
            return claimRefused || atMenu && !hosting && !joined && !started;
        }
    }
}
