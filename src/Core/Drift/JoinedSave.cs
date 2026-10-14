namespace SolarExpanseMultiplayer
{
    // A joiner loads no save file, so its drift record cannot hash one. It records the host's save hash from the join
    // header instead, marked "joined from", so the comparer can still prove both records came from the same save.
    public static class JoinedSave
    {
        public const string Prefix = "joined from ";

        // The record header's save hash for a joiner. hostId is the host's hash from the join header (null = none).
        public static string Mark(string hostId) { return Prefix + (hostId ?? "unknown"); }

        // The save hash a joiner's (joined = true) or any other record's header carries; ownHash reads this game's own
        // save file and is not called for a joiner.
        public static string RecordHash(bool joined, string hostId, System.Func<string> ownHash)
        {
            return joined ? Mark(hostId) : ownHash();
        }

        // What the host puts in the join header: its own save hash, only in a drift run (a normal host reads no
        // save file for this), and null when the hash is not known.
        public static string HostId(bool driftWanted, System.Func<string> ownHash)
        {
            if (!driftWanted) return null;
            string hash = ownHash();
            return hash == "unknown" ? null : hash;
        }

        public static bool IsJoined(string recordHash)
        {
            return recordHash != null && recordHash.StartsWith(Prefix, System.StringComparison.Ordinal);
        }

        // The save hash a record names: a joiner's marker is taken off, so it compares with the host's own hash.
        public static string Plain(string recordHash)
        {
            return IsJoined(recordHash) ? recordHash.Substring(Prefix.Length) : recordHash;
        }
    }
}
