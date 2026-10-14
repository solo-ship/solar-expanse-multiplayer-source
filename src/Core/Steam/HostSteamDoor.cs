using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The host's lobby as the link threads may read it: membership and Steam names as last read on the main thread.
    public interface ISteamMembers
    {
        bool IsMemberNow(ulong steamId);
        string MemberNameNow(ulong steamId);   // null when unknown
    }

    // One row of the host's unban list. SteamId stays on the host's own screen: it is never logged or sent.
    public sealed class BannedPlayer
    {
        public ulong SteamId;
        public string Name;   // the name the player had when banned; null when none was known
    }

    // The host's Steam door beyond the session's own state: the bans kept in <data>\steam-bans.txt across sessions,
    // the accounts the door let through just now (for their Hello) and their Steam names. Owned by SessionHost.
    // Steam gives a lobby owner no way to remove a member, so a banned player can stay in the Steam lobby; the door is
    // where they are turned away. Judge, Refresh, Ban and Unban run on the main thread; the Hello check asks IsMember
    // and NameOf from a link thread.
    // A kick is not a door matter: it takes the player's rejoin keys and frees the seat, and the player may come
    // straight back in (the user's decision); only a ban, kept here, keeps an account out.
    public sealed class HostSteamDoor
    {
        // A member the door admitted counts as one for its Hello for this long, so a member list read a moment before
        // the entry (the lobby's snapshot is half a second old at most) does not refuse the Hello the door let in.
        // The Steam name read at the door is kept as long, for a player who is not a lobby member (a join code, a
        // rejoin key).
        public const long MemberProofMs = 30000;
        public const int MaxRemembered = 64;

        readonly ISteamMembers lobby;
        readonly string banFile;
        readonly Func<long> nowMs;
        readonly SteamBanList bans;
        readonly object gate = new object();
        readonly Dictionary<ulong, Seen> seen = new Dictionary<ulong, Seen>();   // under gate
        readonly DoorAccounts accounts = new DoorAccounts();                     // under gate

        sealed class Seen { public long At; public bool Member; public string Name; }

        public HostSteamDoor(ISteamMembers lobby, string banFile, Func<long> nowMs)
        {
            this.lobby = lobby;
            this.banFile = banFile;
            this.nowMs = nowMs ?? (() => Environment.TickCount);
            bans = Load(banFile);
        }

        // What SteamLobbyDoor.Decide reads: the saved bans. Main thread.
        public SteamBanList Refused { get { return bans; } }

        // The door, main thread, before Steam accepts the connection: the Steam rules with the ban list, the short
        // wait for a member Steam has not told this host about yet (askAgain), and, for an admitted account, its
        // Steam name as Steam knows it now (steamName may give null; Refresh asks again).
        public DoorDecision Judge(DoorFacts facts, ISteamLobbyView view, double waitedSeconds,
                                  Func<ulong, string> steamName, out bool askAgain)
        {
            DoorDecision d;
            ulong id = facts != null ? facts.RemoteId : 0;
            long now = nowMs();
            // A connection asked again (waited > 0) is the one the door already counts for its account.
            bool fresh = waitedSeconds <= 0;
            lock (gate)
            {
                d = SteamLobbyDoor.Decide(facts, view, bans);
                if (d.Admitted || d.Verdict == DoorVerdict.NotInvited)
                {
                    if (accounts.Cooling(id, now)) d = Refuse(DoorVerdict.CoolingDown);
                    else if (fresh && accounts.Busy(id, now)) d = Refuse(DoorVerdict.AccountBusy);
                }
            }
            askAgain = DoorGrace.Wait(d, view != null && view.HasLobby, waitedSeconds);
            if (askAgain) { lock (gate) accounts.Holding(id, now); return d; }
            if (!d.Admitted)
            {
                if (d.Verdict == DoorVerdict.NotInvited) lock (gate) accounts.Ended(id, true, now);
                return d;
            }
            string name = Ask(steamName, id);
            lock (gate)
            {
                accounts.Admitted(id, now);
                Prune();
                seen[id] = new Seen { At = now, Member = d.Reason == DoorReason.LobbyMember, Name = name };
            }
            return d;
        }

        static DoorDecision Refuse(DoorVerdict v)
        {
            return new DoorDecision { Verdict = v, TextKey = SteamLobbyDoor.Key(v) };
        }

        // Any thread (the link's, LinkHost.SteamHandshakeEnded): a Steam connection's handshake is over, joined or
        // not. failed: it closed before the Welcome (a deadline, a refused Hello), which counts towards a cooldown.
        public void HandshakeEnded(ulong steamId, bool failed)
        {
            lock (gate) accounts.Ended(steamId, failed, nowMs());
        }

        // Whether the account has a connection on its way in.
        public bool Busy(ulong steamId)
        {
            lock (gate) return accounts.Busy(steamId, nowMs());
        }

        // Main thread, each frame: a name Steam did not know at the door (it is fetched on request) is asked again
        // while the account's Hello may still come.
        public void Refresh(Func<ulong, string> steamName)
        {
            List<ulong> ask = null;
            lock (gate)
            {
                foreach (KeyValuePair<ulong, Seen> kv in seen)
                    if (kv.Value.Name == null && nowMs() - kv.Value.At < MemberProofMs)
                        (ask ?? (ask = new List<ulong>())).Add(kv.Key);
            }
            if (ask == null) return;
            foreach (ulong id in ask)
            {
                string name = Ask(steamName, id);
                if (name == null) continue;
                lock (gate) { Seen s; if (seen.TryGetValue(id, out s)) s.Name = name; }
            }
        }

        static string Ask(Func<ulong, string> steamName, ulong id)
        {
            try { return steamName != null ? SteamBanList.CleanName(steamName(id)) : null; }
            catch (Exception) { return null; }
        }

        void Prune()
        {
            long now = nowMs();
            var old = new List<ulong>();
            foreach (KeyValuePair<ulong, Seen> kv in seen) if (now - kv.Value.At >= MemberProofMs) old.Add(kv.Key);
            foreach (ulong id in old) seen.Remove(id);
            while (seen.Count >= MaxRemembered)
            {
                ulong oldest = 0;
                long at = long.MaxValue;
                foreach (KeyValuePair<ulong, Seen> kv in seen)
                    if (kv.Value.At < at) { at = kv.Value.At; oldest = kv.Key; }
                seen.Remove(oldest);
            }
        }

        // Any thread, the Hello check: a member of the host's lobby now, or one the door admitted as a member just
        // now.
        public bool IsMember(ulong steamId)
        {
            if (lobby != null && lobby.IsMemberNow(steamId)) return true;
            lock (gate)
            {
                Seen s;
                return seen.TryGetValue(steamId, out s) && s.Member && nowMs() - s.At < MemberProofMs;
            }
        }

        // Any thread: the account's Steam name (the lobby's, else the one read at the door); null when unknown.
        public string NameOf(ulong steamId)
        {
            string n = lobby != null ? SteamBanList.CleanName(lobby.MemberNameNow(steamId)) : null;
            if (n != null) return n;
            lock (gate)
            {
                Seen s;
                return seen.TryGetValue(steamId, out s) && nowMs() - s.At < MemberProofMs ? s.Name : null;
            }
        }

        // The host banned the player on this account: refused at the door now and in every later session, lobby
        // member or not. False when the account cannot be banned or the file could not be written (the ban then
        // holds for this session only).
        public bool Ban(ulong steamId, string name)
        {
            lock (gate)
            {
                seen.Remove(steamId);
                accounts.Forget(steamId);
                if (!bans.Add(steamId, name)) return false;
            }
            return Save();
        }

        // The host's unban: the account passes the door again (as any lobby member or code holder would). False when
        // it was not banned or the file could not be written.
        public bool Unban(ulong steamId)
        {
            lock (gate) if (!bans.Remove(steamId)) return false;
            return Save();
        }

        // The unban list, by name (unnamed last).
        public List<BannedPlayer> Bans()
        {
            var list = new List<BannedPlayer>();
            lock (gate)
                foreach (ulong id in bans.Ids()) list.Add(new BannedPlayer { SteamId = id, Name = bans.NameOf(id) });
            list.Sort((a, b) =>
            {
                if (a.Name == null || b.Name == null) return a.Name == b.Name ? a.SteamId.CompareTo(b.SteamId)
                                                                               : (a.Name == null ? 1 : -1);
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.SteamId.CompareTo(b.SteamId);
            });
            return list;
        }

        bool Save()
        {
            string text;
            lock (gate) text = bans.ToText();
            string err = banFile == null ? "no ban file"
                                         : AtomicFile.Write(banFile, new UTF8Encoding(false).GetBytes(text));
            if (err == null) return true;
            Log.Warn("host: the Steam ban list could not be saved (" + err + "); the change holds for this session "
                + "only");
            return false;
        }

        static SteamBanList Load(string file)
        {
            try
            {
                if (file == null || !File.Exists(file)) return new SteamBanList();
                if (new FileInfo(file).Length > SteamBanList.MaxTextChars)
                    Log.Warn("host: the Steam ban list is over " + SteamBanList.MaxTextChars + " bytes; only its "
                        + "start is read");
                int skipped;
                SteamBanList list;
                using (var r = new StreamReader(file, Encoding.UTF8))
                {
                    var buf = new char[SteamBanList.MaxTextChars];
                    int n = r.ReadBlock(buf, 0, buf.Length);
                    list = SteamBanList.FromText(new string(buf, 0, n), out skipped);
                }
                Log.Info("host: " + list.Count + " banned Steam account(s) read"
                    + (skipped > 0 ? " (" + skipped + " line(s) that are not a player's account skipped)" : ""));
                return list;
            }
            catch (Exception e)
            {
                Log.Warn("host: the Steam ban list could not be read (" + e.GetType().Name + "); starting with none");
                return new SteamBanList();
            }
        }
    }
}
