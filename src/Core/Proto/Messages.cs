namespace SolarExpanseMultiplayer
{
    // Every message is a small sealed class of plain fields with a fixed schema. Validate() returns null when every
    // value is acceptable, else what is wrong; Envelope.Decode refuses a message that fails it.
    public abstract class Message
    {
        public abstract string Validate();

        internal static string Text(string value, string field, int max, bool required)
        {
            if (value == null) return required ? field + " missing" : null;
            return value.Length > max ? field + " longer than " + max : null;
        }

        internal static string Hex(string value, string field, int length, bool required)
        {
            if (value == null) return required ? field + " missing" : null;
            if (value.Length != length) return field + " is not " + length + " hex digits";
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return field + " is not lower-case hex";
            return null;
        }

        // NaN fails every comparison, so it is checked first; infinities fail the range.
        internal static string Finite(double v, string field, double min, double max)
        {
            return double.IsNaN(v) || v < min || v > max ? field + " outside " + min + ".." + max : null;
        }
    }

    // Client -> host, first message. At most one of Invite (first join) and Rejoin (reconnect) is set; with neither, the
    // gate admits the Hello only when the transport says the sender is a member of the Steam lobby (protocol 7: the Steam
    // account Steam authenticated plus lobby membership is the credential, so a lobby needs no code). GameVersion
    // (protocol 2, Application.version) is what a version Reject shows both sides; it is optional on the wire so a
    // protocol-1 Hello still decodes and is refused by number, with the host's versions, once its credential has passed.
    public sealed class Hello : Message
    {
        public int Protocol;
        public string ModVersion;
        public string GameBuild;
        public string GameVersion;
        public string Name;
        public string Invite;   // 32 hex digits: the one-time secret from the join code
        public string Rejoin;   // 64 hex digits: the key from this session's last Welcome

        public override string Validate()
        {
            return Text(ModVersion, "ModVersion", 32, true) ?? Text(GameBuild, "GameBuild", 96, true) ?? Text(GameVersion, "GameVersion", 32, false)
                ?? Text(Name, "Name", 64, false)
                ?? Hex(Invite, "Invite", 32, false) ?? Hex(Rejoin, "Rejoin", 64, false)
                ?? (Invite != null && Rejoin != null ? "at most one of Invite and Rejoin" : null);
        }
    }

    // Host -> client: accepted. Rejoin is a fresh key for the next reconnect (each key works once).
    public sealed class Welcome : Message
    {
        public int PlayerId;
        public string Rejoin;
        public string HostName;

        public override string Validate()
        {
            string e = (PlayerId < 1 || PlayerId > 64 ? "PlayerId outside 1..64" : null) ?? Hex(Rejoin, "Rejoin", 64, true)
                ?? Text(HostName, "HostName", 64, false);
            if (e != null) return e;
            if (HostName != null) HostName = TextSanitiser.Clean(HostName, 64, "host");
            return null;
        }
    }

    public static class RejectCode
    {
        public const int Protocol = 1, ModVersion = 2, GameBuild = 3, Invite = 4, Full = 5, Rejoin = 6;
    }

    // Host -> client: refused; the connection closes after it. HostGameVersion and HostModVersion are filled only for
    // Protocol, ModVersion and GameBuild, which the host answers only after the credential has passed (I-7), so a stranger
    // learns nothing about the host. HostGameBuild only for GameBuild, under the same rule: with the same version on
    // both sides the builds are all the player has to compare.
    public sealed class Reject : Message
    {
        public int Code;
        public string Reason;
        public string HostGameVersion;
        public string HostModVersion;
        public string HostGameBuild;
        public override string Validate()
        {
            string e = Text(Reason, "Reason", 200, false) ?? Text(HostGameVersion, "HostGameVersion", 32, false)
                ?? Text(HostModVersion, "HostModVersion", 32, false) ?? Text(HostGameBuild, "HostGameBuild", 32, false);
            if (e != null) return e;
            if (HostGameBuild != null) HostGameBuild = TextSanitiser.Clean(HostGameBuild, 32, "");
            if (Reason != null) Reason = TextSanitiser.Clean(Reason, 200, "");
            if (HostGameVersion != null) HostGameVersion = TextSanitiser.Clean(HostGameVersion, 32, "");
            if (HostModVersion != null) HostModVersion = TextSanitiser.Clean(HostModVersion, 32, "");
            return null;
        }

        // The plain-words line for a version refusal, or null when this Reject is not one (or the host sent no versions).
        // The words live in JoinText.
        public string VersionSentence(string yourGameVersion, string yourModVersion, string yourGameBuild)
        {
            return JoinText.VersionSentence(Code, HostGameVersion, HostModVersion, HostGameBuild, yourGameVersion,
                yourModVersion, yourGameBuild);
        }
    }

    // Keep-alive. The receiver answers a Ping with a Pong carrying the same T (the sender's clock, ms).
    public sealed class Ping : Message
    {
        public long T;
        public override string Validate() { return null; }
    }

    public sealed class Pong : Message
    {
        public long T;
        public override string Validate() { return null; }
    }

    public sealed class Vote
    {
        public int Player;   // 0 = the host
        public float Scale;
        public int Seq;      // the last of this player's SpeedRequest.Seq the host acted on: its answer to that request
    }

    // Why the game is paused for everyone, besides plain pause requests: a small fixed set (never text from the network).
    public static class PauseWhy
    {
        public const int Planning = 1, Dropped = 2, Joining = 3;
        // M7: a player's game catching up after a short drop (4 is M8's Window)
        public const int CatchingUp = 5;

        // "Paused: Player 2 is planning a mission" (player 0 is the host). Empty when nobody has a reason.
        public static string Describe(Pause[] pauses)
        {
            if (pauses == null || pauses.Length == 0) return "";
            var parts = new System.Collections.Generic.List<string>();
            foreach (Pause p in pauses) if (p != null) parts.Add(Part(p));
            return Paused(parts);
        }

        // "Paused: " and the parts, joined (PlanningStatus builds its own planner parts).
        public static string Paused(System.Collections.Generic.List<string> parts)
        {
            return UiText.Get("cmd.pause.prefix", string.Join("; ", parts.ToArray()));
        }

        public static string Who(int player)
        {
            return player == 0 ? UiText.Get("cmd.pause.host") : UiText.Get("cmd.pause.player", player);
        }

        // One pause on its own: "Player 2 lost connection".
        public static string Part(Pause p)
        {
            string key = p.Reason == Planning ? "cmd.pause.planning" : p.Reason == Dropped ? "cmd.pause.dropped"
                : p.Reason == Joining ? "cmd.pause.joining" : p.Reason == CatchingUp ? "cmd.pause.catchingUp"
                : "cmd.pause.other";
            return UiText.Get(key, Who(p.Player));
        }
    }

    public sealed class Pause
    {
        public int Player;   // 0 = the host
        public int Reason;   // PauseWhy.Planning, PauseWhy.Dropped or PauseWhy.Joining
    }

    // Host -> clients, 4 times a second and on every speed change: the host's date and speed.
    public sealed class Clock : Message
    {
        public const float MaxScale = 200000f;
        public const int MaxPauses = 16;   // five planners, four joiners and a drop fit (M4)
        public double Game;        // host's game date in seconds (DateTime ticks / 1e7)
        public double Start;       // host's campaign start date in seconds: a different value means a different save
        public float Scale;        // host's speed now, 0 = paused
        public double RatePer1x;   // game seconds per real second at speed 1000, measured by the host; 0 = not known yet
        public long HostMs;        // host's monotonic clock when sent (for logs)
        public bool Locked;        // the host's clock can't change (end-game impact)
        public Vote[] Votes;       // what each player asked for
        public Pause[] Pauses;     // who is holding the game paused beyond a plain pause request, and why

        public override string Validate()
        {
            string e = Finite(Game, "Game", 0, 1e12) ?? Finite(Start, "Start", 0, 1e12) ?? Finite(Scale, "Scale", 0, MaxScale)
                ?? Finite(RatePer1x, "RatePer1x", 0, 1e9);
            if (e != null) return e;
            if (Votes != null)
            {
                if (Votes.Length > 8) return "more than 8 votes";
                foreach (Vote v in Votes)
                {
                    if (v == null) return "an empty vote";
                    if (v.Player < 0 || v.Player > HostGate.MaxPlayerIds) return "Vote.Player out of range";
                    e = Finite(v.Scale, "Vote.Scale", 0, MaxScale);
                    if (e != null) return e;
                    if (v.Seq < 0) return "Vote.Seq is negative";
                }
            }
            if (Pauses != null)
            {
                if (Pauses.Length > MaxPauses) return "more than " + MaxPauses + " pause reasons";
                foreach (Pause p in Pauses)
                {
                    if (p == null) return "an empty pause reason";
                    if (p.Player < 0 || p.Player > HostGate.MaxPlayerIds) return "Pause.Player out of range";
                    if (p.Reason != PauseWhy.Planning && p.Reason != PauseWhy.Dropped && p.Reason != PauseWhy.Joining && p.Reason != PauseWhy.CatchingUp) return "Pause.Reason unknown";
                }
            }
            return null;
        }
    }

    // Client -> host: the speed this player asks for (0 = pause, MaxScale = no limit). The slowest request wins.
    // Planning: this player has the mission planner open, which pauses everyone until it closes (the speed is kept).
    // Scale is always one of the game's listed speeds. Choice counts the client's counted local requests (it rises by 1
    // for every one, even with the same value), so the host can tell a new click on the same speed from a repeat; it
    // may restart at 0 in a new game process (the first message after a join sets the host's baseline). Player: the
    // latest request came from the player (a click or hotkey), not from a call the game made itself. Seq rises by 1 for
    // every message sent; the host echoes the last one it acted on in the player's Vote, so the client knows which
    // Clock answers which request (a vote's value alone can be an older one, or a newer request's).
    public sealed class SpeedRequest : Message
    {
        public float Scale;
        public bool Planning;
        public int Choice;
        public bool Player;
        public int Seq;
        public override string Validate()
        {
            string e = Finite(Scale, "Scale", 0, Clock.MaxScale);
            if (e != null) return e;
            if (!ClockRules.IsListed(Scale)) return "Scale is not a listed speed";
            if (Choice < 0) return "Choice is negative";
            if (Seq < 0) return "Seq is negative";
            return null;
        }
    }

    // Either way: closing on purpose (or why the other side was disconnected).
    public sealed class Bye : Message
    {
        public string Reason;
        // Why the host ended the link, as a JoinCodes value (0: not said, as from a peer older than the field): the
        // joiner's text depends on it (kicked, its agency taken, the session closed). The reason text is for the log.
        public int Code;
        public override string Validate()
        {
            string e = Text(Reason, "Reason", 200, false) ?? JoinCheck.Code(Code, "Code");
            if (e != null) return e;
            if (Reason != null) Reason = TextSanitiser.Clean(Reason, 200, "");
            return null;
        }
    }
}
