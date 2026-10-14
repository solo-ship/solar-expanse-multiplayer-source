using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // Who may join the host's Steam lobby. Steam enforces it when someone asks to enter; the door only reads
    // membership, so this is also what the host publishes for joiners to see.
    public enum LobbyAccess { FriendsOnly, InviteOnly, CodeOnly }

    // What the Steam glue (SteamLobby) gives the Core rules: the host's current lobby, nothing about any other.
    // Every value read through GetData may be a stranger's; callers go through LobbyData.TryRead.
    public interface ISteamLobbyView
    {
        bool HasLobby { get; }
        ulong Owner { get; }
        bool IsMember(ulong steamId);
        string GetData(string key);                    // null when absent
        bool SetData(string key, string value);        // host only; false when Steam refuses
    }

    // The ids the host has banned: a Steam account stays banned across sessions (the glue persists Ids, the format
    // is its own). Only individual accounts can be added, so a junk id never takes a slot. Each id may carry the name
    // the player had when banned, for the host's unban list only (cleaned and cut like any player name).
    public sealed class SteamBanList
    {
        public const int MaxEntries = 1000;
        readonly HashSet<ulong> ids = new HashSet<ulong>();
        readonly Dictionary<ulong, string> names = new Dictionary<ulong, string>();

        public int Count { get { return ids.Count; } }

        public bool Add(ulong steamId) { return Add(steamId, null); }

        // A ban that is already there keeps its first name unless it had none.
        public bool Add(ulong steamId, string name)
        {
            if (!SteamCode.IsIndividual(steamId)) return false;
            if (ids.Count >= MaxEntries && !ids.Contains(steamId)) return false;
            ids.Add(steamId);
            string clean = CleanName(name);
            if (clean != null && !names.ContainsKey(steamId)) names[steamId] = clean;
            return true;
        }

        public bool Remove(ulong steamId) { names.Remove(steamId); return ids.Remove(steamId); }
        public bool Contains(ulong steamId) { return ids.Contains(steamId); }

        // The name kept with a ban; null when none was known.
        public string NameOf(ulong steamId) { string n; return names.TryGetValue(steamId, out n) ? n : null; }

        public static string CleanName(string raw)
        {
            string n = TextSanitiser.Clean(raw, LobbyLimits.MaxNameChars, null);
            return string.IsNullOrEmpty(n) ? null : TextBudget.FitBytes(n, LobbyLimits.MaxNameBytes);
        }

        public ulong[] Ids()
        {
            var a = new ulong[ids.Count];
            ids.CopyTo(a);
            Array.Sort(a);
            return a;
        }

        // The persisted form: one decimal id per line, then optionally a space and the name. A file is the host's own,
        // but it may have been edited by hand, so a line that does not start with a player's account is skipped rather
        // than trusted, and a name is cleaned again; at most MaxTextChars are read.
        public const int MaxTextChars = 64 * 1024;

        public string ToText()
        {
            var b = new StringBuilder();
            foreach (ulong id in Ids())
            {
                b.Append(id.ToString(CultureInfo.InvariantCulture));
                string n = NameOf(id);
                if (n != null) b.Append(' ').Append(n);
                b.Append("\r\n");
            }
            return b.ToString();
        }

        public static SteamBanList FromText(string text, out int skipped)
        {
            var list = new SteamBanList();
            skipped = 0;
            if (string.IsNullOrEmpty(text)) return list;
            if (text.Length > MaxTextChars) text = text.Substring(0, MaxTextChars);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int cut = line.IndexOfAny(new[] { ' ', '\t' });
                string digitsPart = cut < 0 ? line : line.Substring(0, cut);
                string name = cut < 0 ? null : line.Substring(cut + 1);
                ulong id;
                bool digits = digitsPart.Length <= 20;
                foreach (char c in digitsPart) if (c < '0' || c > '9') digits = false;
                if (!digits || !ulong.TryParse(digitsPart, NumberStyles.None, CultureInfo.InvariantCulture, out id)
                    || !list.Add(id, name)) skipped++;
            }
            return list;
        }
    }

    // A Steam peer that connects right after entering the host's lobby can reach the host before Steam has told the
    // host about the new member. Rather than turn a friend away, the door holds such a connection (Steam keeps it in
    // Connecting) and asks again for a few seconds. Only "not invited" waits, and only while the host has a lobby:
    // a ban, a closed session or a busy door is final at once.
    public static class DoorGrace
    {
        public const double Seconds = 3;

        public static bool Wait(DoorDecision d, bool hostHasLobby, double waitedSeconds)
        {
            return d != null && d.Verdict == DoorVerdict.NotInvited && hostHasLobby && waitedSeconds < Seconds;
        }
    }

    // The verdict the transport acts on, with the text key the refusal shows.
    public sealed class DoorDecision
    {
        public DoorVerdict Verdict;
        public DoorReason Reason;      // set only for Admit
        public string TextKey;         // null for Admit
        public bool Admitted { get { return Verdict == DoorVerdict.Admit; } }
    }

    public static class SteamLobbyDoor
    {
        // Order: SteamDoor's (closed, not an individual, banned, too many pending), then the host's lobby members,
        // known players, and the open join code. A ban beats lobby membership. lobby and bans may be null.
        public static DoorDecision Decide(DoorFacts f, ISteamLobbyView lobby, SteamBanList bans)
        {
            if (f == null) f = new DoorFacts();
            f.InLobby = lobby != null && lobby.HasLobby && f.RemoteId != 0 && lobby.IsMember(f.RemoteId);
            f.Banned = bans != null && bans.Contains(f.RemoteId);
            DoorReason why;
            DoorVerdict v = SteamDoor.Decide(f, out why);
            return new DoorDecision { Verdict = v, Reason = why, TextKey = v == DoorVerdict.Admit ? null : Key(v) };
        }

        public static string Key(DoorVerdict v)
        {
            switch (v)
            {
                case DoorVerdict.NotIndividual: return "steam.door.not_individual";
                case DoorVerdict.NotSecure: return "steam.door.not_secure";
                case DoorVerdict.Banned: return "steam.door.banned";
                case DoorVerdict.TooManyPending:
                case DoorVerdict.AccountBusy:
                case DoorVerdict.CoolingDown: return "steam.door.busy";
                case DoorVerdict.Closed: return "steam.door.closed";
                case DoorVerdict.NotInvited: return "steam.door.not_invited";
                default: return null;
            }
        }
    }
}
