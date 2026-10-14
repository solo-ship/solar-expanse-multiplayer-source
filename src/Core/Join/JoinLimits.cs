namespace SolarExpanseMultiplayer
{
    // Every cap and deadline of M4's join (plan "Interfaces, Limits"). Pure constants, so the host, the joiner, the tests
    // and the fake client all build to the same numbers.
    public static class JoinLimits
    {
        public const long MaxCompressed = 16L * 1024 * 1024;    // spec "16 MB"
        public const long MaxUncompressed = 64L * 1024 * 1024;  // spec "64 MB"
        public const int ChunkSize = 32 * 1024;                 // every chunk but the last is exactly this
        public const int Window = 16;                           // chunks in flight per joiner
        public const int AckEvery = Window / 2;                 // the sink acks every 8 chunks and on the last (I-5)
        public const int MaxPlayers = 5;                        // the host plus four (HostGate seats 4 clients)
        public const int NoProgressMs = 30000;                  // per joiner, host side (spec)
        // After the game has paused, the stream's own wait for the world to be ready to snapshot (M6 settles things first). It
        // has its own allowance, so it never eats the self-check's NoProgressMs; the joiner's header wait adds it.
        public const int SnapshotReadyMs = 10000;
        public const int JoinerNoProgressMs = 30000;            // joiner side, waiting on the host
        public const int JoinerTotalMs = 300000;                // joiner side: the whole join, Welcome to Loaded; restarts at its header
        // From the header on, both sides allow TotalMsFor(the header's CompressedSize): a late-game snapshot on a slow
        // home upload needs more than 300 s, and a joiner that stalls is still caught by the 30 s no-progress deadline.
        public const int TotalBytesPerSecond = 32 * 1024;       // the slowest upload allowed for (about 0.26 Mbit/s)
        public const int TotalMarginMs = 60000;                 // the walls, the summary and the load after it
        // Reconnects with the rejoin key in one join attempt, in all: neither a Welcome nor reaching the game (a load
        // or a quick rejoin) resets the count, so a host that welcomes or resumes the joiner and then drops it cannot
        // keep it reconnecting for ever. Only StableInGameMs in the game, connected and with no drop, does.
        public const int MaxReconnects = 5;
        public const int StableInGameMs = 60000;
        // While a joiner waits for a batch (a JoinWait, before its header) its silence and total deadlines are suspended:
        // waiting behind other players is not its fault. Instead it gives up after this long in all, long enough for several
        // full batches; the host does not count that against it.
        public const int JoinerQueueWaitMs = 15 * 60 * 1000;
        // JoinWait{Ahead = 0} says its batch has started: the header follows the host's own preparation (NoProgressMs at most),
        // so the joiner allows that and a margin.
        public const int JoinerBatchHeaderMs = NoProgressMs + SnapshotReadyMs + 15000;
        public const int SummaryDeadlineMs = 10000;             // Welcome -> ContentSummary
        public const int ClaimDeadlineMs = 30000;               // "content same" -> AgencyClaim
        public const int MaxClaims = 5;                         // claims per link
        public const int MaxFailedJoins = 3;                    // per player per session; then keys revoked
        public const int SummaryMaxBytes = 60 * 1024;           // spec "capped at 64 KiB"
        public const int MaxPlugins = 256, MaxAssemblies = 256, MaxCategories = 16;
        public const int MaxDetailItemsPerPart = 1024, MaxDetailParts = 8;
        public const int MaxDetailBytesTotal = 128 * 1024;      // every ContentDetail of one check, in all (I-5)
        public const int MaxReportLines = 40, MaxReportLineChars = 160;
        public const int ReportMaxBytes = 7 * 1024;             // JoinRefused's lines in UTF-8, under its 8 KiB cap (I-13)
        public const int SummaryShowMs = 5000;                  // Design decision 14
        public const int QuickRejoinMaxDropMs = 120000;         // Design decision 16 (c): under 2 minutes
        public const int MaxFingerprintBlocks = 256;            // RejoinOffer; M3's areas give 10 per agency + 4
        public const long DateToleranceTicks = 10000;           // 1 ms
        public const double PauseDayFraction = 0.4;             // -semp-pause-at-day pauses at N + 0.4 day (I-11)

        // The total deadline from the header for a snapshot of this many compressed bytes: max(JoinerTotalMs, the
        // transfer at TotalBytesPerSecond + TotalMarginMs). The host (per batch member) and the joiner both call this
        // with the header's CompressedSize and count it from the header (the host from sending it, the joiner from
        // receiving it), so they agree for every size. A size outside 0..MaxCompressed counts as the
        // nearest end: a lying header cannot buy more time than the largest snapshot allowed.
        public static int TotalMsFor(long compressedBytes)
        {
            long bytes = compressedBytes < 0 ? 0 : compressedBytes > MaxCompressed ? MaxCompressed : compressedBytes;
            long ms = bytes * 1000 / TotalBytesPerSecond + TotalMarginMs;
            return ms > JoinerTotalMs ? (int)ms : JoinerTotalMs;
        }
    }
}
