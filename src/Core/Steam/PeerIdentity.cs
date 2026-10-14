namespace SolarExpanseMultiplayer
{
    // Who the transport says is at the other end of a link: a TLS peer (anyone with the join code's invite or a key),
    // or a Steam account Steam has authenticated (the connection's verified identity, never a value from a message).
    // The gate ties a player's rejoin keys to it, so a key copied to another account or onto the other transport is
    // refused.
    public enum PeerKind { Tls, Steam }

    public sealed class PeerIdentity
    {
        public readonly PeerKind Kind;
        public readonly ulong SteamId;   // 0 for Tls

        PeerIdentity(PeerKind kind, ulong steamId) { Kind = kind; SteamId = steamId; }

        public static readonly PeerIdentity Tls = new PeerIdentity(PeerKind.Tls, 0);

        public static PeerIdentity Steam(ulong steamId) { return new PeerIdentity(PeerKind.Steam, steamId); }

        public bool Same(PeerIdentity other)
        {
            return other != null && other.Kind == Kind && other.SteamId == SteamId;
        }
    }

    // The door's reason to admit a Steam connection (lobbies, which add LobbyMember, come later).
    public enum DoorReason { None, LobbyMember, KnownPlayer, CodeOpen }
    // AccountBusy and CoolingDown are the door book's own (HostSteamDoor, DoorAccounts), after Decide.
    public enum DoorVerdict { Admit, NotIndividual, NotSecure, Banned, NotInvited, TooManyPending, Closed, AccountBusy,
                              CoolingDown }

    // What the host knows about an incoming Steam connection before accepting it: Steam's verified identity and the
    // session's own state, nothing the peer sent.
    public sealed class DoorFacts
    {
        public ulong RemoteId;
        public bool IndividualAccount;   // CSteamID.BIndividualAccount()
        public int InfoFlags;            // SteamNetConnectionInfo_t.m_nFlags
        public bool SessionOpen;
        public bool InLobby, KnownPlayer, CodeOpen, Banned;
        public int Pending, MaxPending;
    }

    // The host's door for Steam connections, decided before AcceptConnection so a stranger never reaches the
    // handshake. Admitting only lets a connection reach the Hello: the gate still wants the invite or a rejoin key.
    public static class SteamDoor
    {
        public const int FlagUnauthenticated = 1, FlagUnencrypted = 2;

        // Order: Closed, NotIndividual, NotSecure, Banned, TooManyPending, then LobbyMember > KnownPlayer > CodeOpen,
        // else NotInvited. reason is set only for Admit.
        public static DoorVerdict Decide(DoorFacts f, out DoorReason reason)
        {
            reason = DoorReason.None;
            if (f == null || !f.SessionOpen) return DoorVerdict.Closed;
            if (!f.IndividualAccount || !SteamCode.IsIndividual(f.RemoteId)) return DoorVerdict.NotIndividual;
            // No flag test here: while a connection waits in Connecting, Steam has not finished its certificate
            // exchange and still reports it unauthenticated (seen on the first two-PC run). The flags are judged at
            // Connected by SteamPipe.CheckUp, before any message is read or the Hello is answered.
            if (f.Banned) return DoorVerdict.Banned;
            if (f.Pending >= f.MaxPending) return DoorVerdict.TooManyPending;
            if (f.InLobby) reason = DoorReason.LobbyMember;
            else if (f.KnownPlayer) reason = DoorReason.KnownPlayer;
            else if (f.CodeOpen) reason = DoorReason.CodeOpen;
            else return DoorVerdict.NotInvited;
            return DoorVerdict.Admit;
        }

        // For the host's log (never the peer's words).
        public static string Words(DoorVerdict v)
        {
            switch (v)
            {
                case DoorVerdict.Admit: return "admitted";
                case DoorVerdict.NotIndividual: return "not a player's Steam account";
                case DoorVerdict.NotSecure: return "the connection is not authenticated and encrypted";
                case DoorVerdict.Banned: return "banned";
                case DoorVerdict.TooManyPending: return "too many connections in their handshake";
                case DoorVerdict.Closed: return "the session is not open";
                case DoorVerdict.AccountBusy: return "this account already has a connection on its way in";
                case DoorVerdict.CoolingDown: return "this account's handshakes kept failing; it waits a few minutes";
                default: return "no Steam join code is open and it is not a player of this session";
            }
        }
    }
}
