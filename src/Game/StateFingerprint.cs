using System;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // The quick-rejoin print (Design decision 16 (d)), on both sides: one StateReader.Visit into M3's AreaHasher keyed
    // from the host's origin, then FingerprintBuild.From (Core). The reader is prepared once per load and the hasher
    // and its DaySnapshot are kept until the origin or the agency count changes, so a print is one visit (M3: about 4
    // ms) with no garbage per item. It never touches the M3 recorder and writes no file. Refused (null) unless the game
    // is paused: a print of a running game proves nothing. Owned by Session; OnSaveLoaded is its reset per load. Main
    // thread.
    public sealed class StateFingerprint
    {
        int preparesAtLoad = -1, agencies = -1;   // StateReader.Prepares when this load began; -1 = no load seen
        long hasherOrigin;
        AreaHasher hasher;
        DaySnapshot snapshot;

        // Every load (Session, before Drift): a Prepare after this point is this load's, Drift's included.
        public void OnSaveLoaded()
        {
            preparesAtLoad = StateReader.Prepares;
            hasher = null;
        }

        public Fingerprint Take(long originTicks)
        {
            if (!ClockSync.Ready || ClockSync.Scale != 0f)
            {
                Log.Info("join: print refused: the game is not paused");
                return null;
            }
            if (originTicks < 1) { Log.Warn("join: print refused: no origin"); return null; }
            // once per load: a Prepare by Drift (or by an earlier print) since this load began is reused, so a print
            // never resets a running drift record's reader
            if (PrintCheck.NeedsPrepare(preparesAtLoad, StateReader.Prepares, StateReader.IsPrepared))
            {
                StateReader.Prepare();
                if (!StateReader.IsPrepared)
                {
                    Log.Error("join: print failed: the state reader could not prepare");
                    return null;
                }
                hasher = null;
            }
            int n = StateReader.Agencies.Length;
            if (hasher == null || hasherOrigin != originTicks || agencies != n)
            {
                hasher = new AreaHasher(originTicks, null, n);
                snapshot = new DaySnapshot(DriftAreas.Blocks(n), DriftAreas.FieldSums(n), DriftFields.MaxScalars(n));
                hasherOrigin = originTicks;
                agencies = n;
            }
            int gc0 = GC.CollectionCount(0), failures0 = StateReader.AreaFailures;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            hasher.BeginDay(snapshot, null);
            StateReader.Visit(hasher);
            hasher.EndDay();
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int gc1 = GC.CollectionCount(0);
            Fingerprint f = PrintCheck.From(snapshot, originTicks, failures0, StateReader.AreaFailures,
                hasher.SnapshotOverflow);
            Log.Info("join: print " + (f != null ? f.Blocks.Length + " blocks"
                : "FAILED (area failures or an overflow)") +
                     " in " + ms.ToString("F2", CultureInfo.InvariantCulture) + " ms, gc " + gc0 + " -> " + gc1 + ", "
                         + n +
                     " agencies");
            return f;
        }
    }
}
