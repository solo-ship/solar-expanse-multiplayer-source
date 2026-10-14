using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SolarExpanseMultiplayer
{
    // The host's door: checks a Hello and hands out player ids. Thread-safe (network threads call Check).
    // - One invite is open at a time (the one in the current join code); it works once. NewInvite replaces it.
    // - A rejoin key (sent in Welcome) lets the same player back in once; the next Welcome brings a new key. If the host
    //   still thinks that player is connected (their old link died silently), the new connection replaces it: the key
    //   proves it is the same player, and Verdict.Replaces tells the caller to close the old link.
    // - The credential is checked before the versions, so a stranger learns nothing about the host; a version
    //   mismatch does not spend the invite (the player can update and try again). Once the credential has passed, a
    //   protocol, mod-version or build refusal carries the host's versions (I-7).
    // - Each accepted connection gets a per-connection token (a generation counter) to distinguish old links from new.
    //   Left(id, token) only un-marks the player if the token matches the current one.
    // - N-1 with I-6: Check spends NOTHING. It RESERVES the invite or the presented key and issues the next key; the host
    //   commits with Confirm on the first frame that arrives on the new link (proof that the Welcome arrived). If the link
    //   closes first, Undo runs: the invite is open again and the key used and the key issued both stay valid until
    //   either is used, and then the other is revoked. A player holds at most MaxKeysPerPlayer keys at once; when a Check
    //   would leave more, the oldest key that is neither the one just presented nor the new one goes (the key a client
    //   holds after a lost Welcome is never the one dropped).
    // - An invite that was given back keeps the player id it had been given (and the key issued with it), so a loop of lost
    //   Welcomes burns no ids. That key and the invite are a pair like K1 and K2: using one revokes the other.
    // - A second Hello with a RESERVED invite is refused (the invite race). A second Hello with a key whose connection is
    //   still reserved takes over: the key proves it is the same player, as with a link the host still thinks is open.
    // - A Steam account that still holds a seat (a valid key) and comes back with any join code gets its own id back
    //   and leaves the invite alone: Steam proves the account, so the spent code it joined with is no barrier.
    public sealed class HostGate
    {
        public const int MaxPlayerIds = 64;
        public const int MaxKeysPerPlayer = 2;

        public sealed class Verdict
        {
            public int PlayerId;      // > 0 when accepted
            public string Rejoin;     // the new rejoin key for the Welcome
            public string Name;       // sanitised
            public bool Replaces;     // a rejoin of a player still marked connected: close their old link
            public long Token;        // per-connection generation; Left/Confirm/Undo only act if it matches
            public int RejectCode;
            public string Reason;
            public string HostGameVersion, HostModVersion;   // only for Protocol, ModVersion and GameBuild refusals (I-7)
            public string HostGameBuild;   // only for GameBuild refusals (I-7)
            public bool Ok { get { return PlayerId > 0; } }
        }

        sealed class KeyInfo { public int Id; public long Seq; public long ReservedBy; }

        // One connection that has passed Check and has not yet been Confirmed or Undone.
        sealed class Pending
        {
            public long Token;
            public bool ViaInvite;
            public long InviteGen;
            public string UsedKey;   // the key presented (null for an invite)
            public string NewKey;    // the key issued with the Welcome
            public bool HoldsInvite; // a key connection of the player an undone invite is tied to: it holds the invite too, so nobody else takes it
        }

        readonly object gate = new object();
        readonly int protocol, maxPlayers;
        readonly string modVersion, gameBuild, gameVersion;
        readonly Func<int, byte[]> random;
        byte[] openInvite;
        long inviteGen;                   // bumped by NewInvite: a connection that reserved an older invite never touches the new one
        long inviteReservedBy;            // the token holding the invite now (0 = nobody)
        int inviteId;                     // the player id a given-back invite keeps (0 = none)
        readonly Dictionary<string, KeyInfo> keys = new Dictionary<string, KeyInfo>();
        readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();   // by player id: the current connection's, if unconfirmed
        readonly HashSet<int> connected = new HashSet<int>();
        readonly Dictionary<int, long> tokens = new Dictionary<int, long>();   // id -> current connection token
        // id -> who took the id (TLS, or the Steam account): a rejoin key works only for the same peer
        readonly Dictionary<int, PeerIdentity> peers = new Dictionary<int, PeerIdentity>();
        // Steam accounts whose player broke the protocol: lobby membership alone no longer lets them in this session (a loop of
        // Hellos with no key would otherwise burn a player id each time)
        readonly HashSet<ulong> barredSteam = new HashSet<ulong>();
        int nextId = 1;
        long nextToken = 1, nextKeySeq = 1;

        public HostGate(int protocol, string modVersion, string gameBuild, string gameVersion, int maxPlayers, Func<int, byte[]> random)
        {
            this.protocol = protocol; this.modVersion = modVersion; this.gameBuild = gameBuild; this.gameVersion = gameVersion;
            this.maxPlayers = maxPlayers; this.random = random;
        }

        public static byte[] SecureRandom(int n)
        {
            var b = new byte[n];
            using (var r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            return b;
        }

        public byte[] NewInvite()
        {
            lock (gate)
            {
                openInvite = random(16);
                inviteGen++;
                inviteReservedBy = 0;
                inviteId = 0;
                return (byte[])openInvite.Clone();
            }
        }

        // False while the invite is reserved by a connection that has not been confirmed or undone, and when it is spent.
        public bool HasOpenInvite { get { lock (gate) return openInvite != null && inviteReservedBy == 0; } }

        // True when there is no invite left to offer: none made yet, or the last one was spent (Confirm, or the Revoke of a
        // connection that came in on it). NOT true while a joiner merely holds it: that invite may come back (Undo), so the
        // host must not replace the code in its file (I-B). The host publishes a new code exactly when this is true.
        public bool InviteSpent { get { lock (gate) return openInvite == null; } }

        // Is this the latest connection the gate accepted for the player? The link asks after it has published its ids: two
        // Hellos with one key race, and the one the other overtook must not be accepted (I-C).
        public bool IsCurrent(int playerId, long token) { lock (gate) { long current; return tokens.TryGetValue(playerId, out current) && current == token; } }
        public int ConnectedCount { get { lock (gate) return connected.Count; } }

        // How many rejoin keys of this player are valid now (tests and diagnostics).
        internal int ValidKeys(int playerId)
        {
            lock (gate)
            {
                int n = 0;
                foreach (KeyValuePair<string, KeyInfo> kv in keys) if (kv.Value.Id == playerId) n++;
                return n;
            }
        }

        // A Steam account that holds a player id here and still has a valid key: the door lets it reach the Hello
        // even with no code open (it still needs its key there).
        public bool KnowsSteam(ulong steamId)
        {
            lock (gate)
            {
                if (steamId == 0) return false;
                foreach (KeyValuePair<int, PeerIdentity> kv in peers)
                    if (kv.Value.Kind == PeerKind.Steam && kv.Value.SteamId == steamId)
                        foreach (KeyInfo k in keys.Values) if (k.Id == kv.Key) return true;
                return false;
            }
        }

        // The Steam account that took this player id; 0 for a TLS player or an unknown id. The host bars it at its Steam
        // door after a kick or a ban (the gate only knows the session's own players).
        public ulong SteamIdOf(int playerId)
        {
            lock (gate)
            {
                PeerIdentity p;
                return peers.TryGetValue(playerId, out p) && p.Kind == PeerKind.Steam ? p.SteamId : 0;
            }
        }

        public Verdict Check(Hello h) { return Check(h, PeerIdentity.Tls); }

        public Verdict Check(Hello h, PeerIdentity peer) { return Check(h, peer, false, null); }

        // Under the lock. The player id of a Steam account that holds a seat here: an id it took and a valid key for
        // it (a removed or barred player's keys are gone); 0 for anyone else.
        int SeatOfSteam(PeerIdentity peer)
        {
            if (peer == null || peer.Kind != PeerKind.Steam || barredSteam.Contains(peer.SteamId)) return 0;
            int id = IdOfSteam(peer);
            if (id == 0) return 0;
            foreach (KeyInfo k in keys.Values) if (k.Id == id) return id;
            return 0;
        }

        // A Steam account that holds a player id here (the same account coming back without a key).
        int IdOfSteam(PeerIdentity peer)
        {
            foreach (KeyValuePair<int, PeerIdentity> kv in peers)
                if (kv.Value.Same(peer)) return kv.Key;
            return 0;
        }

        // The same credential rules on every transport: a Steam connection needs the invite or a rejoin key exactly as
        // a TLS one does (the account Steam authenticated is the pin, not a credential). A rejoin key also has to come
        // from the peer that took the player id: a key copied to another Steam account, or carried from Steam to the
        // home network or back, is refused like an unknown key.
        // A Hello with neither an invite nor a key is admitted only for a Steam peer the transport reports as a member of
        // the host's Steam lobby (lobbyMember): the credential is Steam's verified identity plus lobby membership, which
        // the door checked before accepting the connection. steamName is that account's persona name as Steam gave it; the
        // roster shows it (cleaned and cut) instead of the Hello's own, which a peer could set to anything. On a TLS link
        // lobbyMember means nothing, and a Hello with no credential is refused as before.
        public Verdict Check(Hello h, PeerIdentity peer, bool lobbyMember, string steamName)
        {
            lock (gate)
            {
                if (h == null) return No(RejectCode.Invite, "hello missing");
                if (peer == null || (peer.Kind == PeerKind.Steam && !SteamCode.IsIndividual(peer.SteamId)))
                    return No(RejectCode.Invite, "no verified identity for this connection");
                string bad = h.Validate();
                if (bad != null) return No(RejectCode.Invite, "hello invalid: " + bad);

                KeyInfo used = null;
                int id;
                bool viaLobby = h.Invite == null && h.Rejoin == null;
                // A Steam account that still holds a seat here and comes back with a join code (the one it joined
                // with, now spent, or the host's new one): Steam has proved the account, as for a lobby member, so it
                // gets its own id back and the code's invite stays as it was for whoever else holds the code.
                int seat = h.Invite != null ? SeatOfSteam(peer) : 0;
                if (viaLobby)
                {
                    if (peer.Kind != PeerKind.Steam || !lobbyMember)
                        return No(RejectCode.Invite, "no invite or key, and not a member of the Steam lobby");
                    if (barredSteam.Contains(peer.SteamId)) return No(RejectCode.Invite, "this account was removed from the session");
                    id = IdOfSteam(peer);   // the same account coming back keeps its id: a loop of Hellos burns none
                }
                else if (seat != 0) id = seat;
                else if (h.Invite != null)
                {
                    byte[] inv;
                    try { inv = FromHex(h.Invite); }
                    catch { return No(RejectCode.Invite, "invite not valid (already used, replaced or never issued)"); }
                    if (openInvite == null || inviteReservedBy != 0 || !SameBytes(inv, openInvite))
                        return No(RejectCode.Invite, "invite not valid (already used, replaced or never issued)");
                    id = inviteId;   // a given-back invite keeps its id: a lost Welcome burns none
                }
                else
                {
                    PeerIdentity took;
                    if (!FindKey(h.Rejoin, out used) || (peers.TryGetValue(used.Id, out took) && !took.Same(peer)))
                        return No(RejectCode.Rejoin, "rejoin key not known");
                    id = used.Id;
                }
                // the credential has passed: from here a refusal about versions shows the host's
                if (h.Protocol != protocol) return NoVersion(RejectCode.Protocol, "protocol " + h.Protocol + ", the host uses " + protocol);
                if (h.ModVersion != modVersion) return NoVersion(RejectCode.ModVersion, "mod version differs from the host's (" + modVersion + ")");
                if (h.GameBuild != gameBuild || h.GameVersion != gameVersion) return NoVersion(RejectCode.GameBuild, "game build differs from the host's");
                if (id == 0 && nextId > MaxPlayerIds) return No(RejectCode.Full, "no player ids left this session");
                bool replaces = (used != null || viaLobby || seat != 0) && id != 0 && connected.Contains(id);
                if (!replaces && connected.Count >= maxPlayers) return No(RejectCode.Full, "the game is full");

                // accepted. A reserved connection of the same player (an unconfirmed link) is given up first.
                if (id != 0) UndoPending(id);
                if (id == 0) id = nextId++;
                long token = nextToken++;
                bool viaInvite = h.Invite != null && seat == 0;
                var p = new Pending { Token = token, ViaInvite = viaInvite, InviteGen = inviteGen };
                if (viaInvite) inviteReservedBy = token;
                else if (used != null)
                {
                    used.ReservedBy = token; p.UsedKey = h.Rejoin;
                    if (inviteId == id && openInvite != null && inviteReservedBy == 0) { inviteReservedBy = token; p.HoldsInvite = true; }
                }
                string key = Hex(random(32));
                keys[key] = new KeyInfo { Id = id, Seq = nextKeySeq++ };
                p.NewKey = key;
                Trim(id);
                pending[id] = p;
                tokens[id] = token;
                peers[id] = peer;   // an invite (given back or new) binds the id to whoever took it
                connected.Add(id);
                // a Steam player is named by Steam only: until Steam has told the host the name, "Player <id>"
                return new Verdict { PlayerId = id, Rejoin = key, Replaces = replaces, Token = token, Name = LobbyLimits.Name(
                    peer.Kind == PeerKind.Steam ? steamName : h.Name, "Player " + id) };
            }
        }

        // The first frame arrived on the new link: the invite or key is spent and the player's other keys are revoked (the
        // new key stays). A stale token, or a connection already confirmed or undone, changes nothing. True only when this
        // call committed (the caller announces Confirmed only then).
        public bool Confirm(int playerId, long token)
        {
            lock (gate)
            {
                Pending p;
                long current;
                if (!tokens.TryGetValue(playerId, out current) || current != token) return false;
                if (!pending.TryGetValue(playerId, out p) || p.Token != token) return false;
                pending.Remove(playerId);
                if (p.ViaInvite)
                {
                    if (p.InviteGen == inviteGen) SpendInvite();
                }
                else if (inviteId == playerId) SpendInvite();   // the player came in on the key that paired with an undone invite
                var dead = new List<string>();
                foreach (KeyValuePair<string, KeyInfo> kv in keys) if (kv.Value.Id == playerId && kv.Key != p.NewKey) dead.Add(kv.Key);
                foreach (string k in dead) keys.Remove(k);
                return true;
            }
        }

        // The link closed before its first frame: the Welcome may never have arrived. The invite is open again and the key
        // used and the key issued both stay valid (until either is used). A stale token, or a connection already confirmed,
        // changes nothing.
        public void Undo(int playerId, long token)
        {
            lock (gate)
            {
                long current;
                Pending p;
                if (!tokens.TryGetValue(playerId, out current) || current != token) return;
                if (!pending.TryGetValue(playerId, out p) || p.Token != token) return;
                UndoPending(playerId);
                connected.Remove(playerId);
            }
        }

        // Under the lock. Gives a reservation back: the invite is open again (tied to this id), or the key is free again.
        void UndoPending(int id)
        {
            Pending p;
            if (!pending.TryGetValue(id, out p)) return;
            pending.Remove(id);
            if (p.ViaInvite)
            {
                if (p.InviteGen == inviteGen && inviteReservedBy == p.Token) { inviteReservedBy = 0; inviteId = id; }
            }
            else
            {
                if (p.HoldsInvite && p.InviteGen == inviteGen && inviteReservedBy == p.Token) inviteReservedBy = 0;   // the invite stays tied to the id
                KeyInfo ki;
                if (p.UsedKey != null && keys.TryGetValue(p.UsedKey, out ki) && ki.ReservedBy == p.Token) ki.ReservedBy = 0;
            }
            Trim(id);
        }

        void SpendInvite() { openInvite = null; inviteReservedBy = 0; inviteId = 0; }

        // Under the lock. While a player has more than MaxKeysPerPlayer keys, the oldest one that is not reserved goes. The
        // key just presented is reserved by its connection, and the key just issued is the newest, so neither can be it: the
        // key a client holds after a lost Welcome is never the one dropped.
        void Trim(int id)
        {
            while (true)
            {
                int count = 0;
                string oldest = null;
                long oldestSeq = long.MaxValue;
                foreach (KeyValuePair<string, KeyInfo> kv in keys)
                {
                    if (kv.Value.Id != id) continue;
                    count++;
                    if (kv.Value.ReservedBy != 0) continue;
                    if (kv.Value.Seq < oldestSeq) { oldestSeq = kv.Value.Seq; oldest = kv.Key; }
                }
                if (count <= MaxKeysPerPlayer || oldest == null) return;
                keys.Remove(oldest);
            }
        }

        // The player's connection closed: they may come back with their rejoin key. Token must match the current connection.
        public void Left(int playerId, long token) { lock (gate) { long current; if (tokens.TryGetValue(playerId, out current) && current == token) connected.Remove(playerId); } }

        // Kicked for breaking the protocol: their rejoin keys stop working. Token must be the current connection's: a
        // stale link that broke the rules after the player came back on a new one must not take the new one away.
        public void Revoke(int playerId, long token) { Revoke(playerId, token, true); }

        // barAccount false: the host removed the player (HostKick.ByTheHost), so its Steam account's lobby Hello is
        // still admitted; its keys go all the same.
        public void Revoke(int playerId, long token, bool barAccount)
        {
            lock (gate)
            {
                long current;
                if (!tokens.TryGetValue(playerId, out current) || current != token) return;
                // A rule-breaker that came in on an invite (or on the key paired with one) loses it: given back it would let one
                // invite loop Hello, Welcome, violation and take a new player id every time (I-A). The host publishes a new code.
                Pending p;
                bool spend = pending.TryGetValue(playerId, out p) && p.InviteGen == inviteGen && inviteReservedBy == p.Token && (p.ViaInvite || p.HoldsInvite);
                UndoPending(playerId);   // any other reservation still held is given back, not left stuck
                PeerIdentity who;
                if (barAccount && peers.TryGetValue(playerId, out who) && who.Kind == PeerKind.Steam)
                    barredSteam.Add(who.SteamId);
                if (spend) SpendInvite();
                connected.Remove(playerId);
                tokens.Remove(playerId);
                var dead = new List<string>();
                foreach (var kv in keys) if (kv.Value.Id == playerId) dead.Add(kv.Key);
                foreach (string k in dead) keys.Remove(k);
                if (inviteId == playerId) inviteId = 0;
            }
        }

        static Verdict No(int code, string reason) { return new Verdict { RejectCode = code, Reason = reason }; }

        Verdict NoVersion(int code, string reason)
        {
            return new Verdict
            {
                RejectCode = code, Reason = reason, HostGameVersion = gameVersion, HostModVersion = modVersion,
                HostGameBuild = code == RejectCode.GameBuild ? ClipBuild(gameBuild) : null,
            };
        }

        // Reject.HostGameBuild's cap: a build id is "acs-" and 16 hex digits, so this only guards a Reject that would
        // otherwise fail its own check and never be sent.
        static string ClipBuild(string build)
        {
            return build == null || build.Length <= 32 ? build : build.Substring(0, 32);
        }

        // Under the gate. Every valid key is compared, in constant time, as invites and pins are: a hash lookup, or a
        // comparison that stops at the first difference, would let a guesser time how close it came. At most
        // MaxPlayerIds * MaxKeysPerPlayer keys, so looking at all of them costs nothing.
        bool FindKey(string presented, out KeyInfo found)
        {
            found = null;
            if (presented == null) return false;
            foreach (KeyValuePair<string, KeyInfo> kv in keys)
                if (SameText(presented, kv.Key)) found = kv.Value;
            return found != null;
        }

        static bool SameText(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];   // same time whatever matches
            return diff == 0;
        }

        public static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        // Input already passed Hello.Validate (lower-case hex of the right length).
        public static byte[] FromHex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
