using System;
using SaveGameData = Manager.SaveGameData;
using LoadSaveManager = Manager.LoadSaveManager;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // The host's snapshot (Design decision 1; S1): what LoadSaveManager.SaveToFile does without the file. A new
    // SaveGameData, InjectAllIntoSaveData, then the game's own compressed serialise, all in
    // memory. The header gets what only the game knows: the date, the origin, the six boundaries, the save version and
    // the content as it is at the snapshot; JoinService fills the rest from the bytes and the seats. Only ever at a
    // paused moment (the paused state is the one the joiner loads). Stateless. Main thread.
    public static class SnapshotSource
    {
        // null when the game is not paused or the game's own inject failed (it logs why).
        public static SnapshotData Take(HostTime time, Func<CategoryHash[]> postLoad)
        {
            if (!JoinHost.GamePaused)
            {
                Log.Warn("join: snapshot refused: the game is not paused");
                return null;
            }
            LoadSaveManager lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            if (lsm == null || tc == null) { Log.Error("join: snapshot refused: no game loaded"); return null; }
            var header = new JoinHeader { SaveVersion = LoadSaveManager.SaveVersion, DateTicks = tc.CurrentTime.Ticks,
                OriginTicks = time.OriginTicks, SaveId = Drift.HostSaveId() };
            if (!time.Fill(header))
            {
                Log.Error("join: snapshot refused: a TimeController boundary field is missing");
                return null;
            }
            header.PostLoad = postLoad();
            if (header.PostLoad == null)
            {
                Log.Error("join: snapshot refused: the host's content could not be scanned");
                return null;
            }
            int records = FlightRecords.MatchMissions();
            if (records != 0) Log.Info("join: " + records + " flight save records set to their missions' figures");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var data = new SaveGameData { saveVersion = LoadSaveManager.SaveVersion, aiSaveVersion = 0 };
            if (!lsm.InjectAllIntoSaveData(data))
            {
                Log.Error("join: snapshot refused: the game's InjectAllIntoSaveData failed");
                return null;
            }
            long injectMs = watch.ElapsedMilliseconds;
            byte[] gz = data.Serialize(true);
            long size = GzipFacts.DeclaredSize(gz);
            Log.Info("join: snapshot taken at " + header.DateTicks + ": inject " + injectMs + " ms, serialise "
                + (watch.ElapsedMilliseconds - injectMs) +
                     " ms, " + (gz != null ? gz.Length : 0) + " bytes (" + size + ")");
            Log.Info("join: market " + SnapshotLoad.MarketText());
            return new SnapshotData { Compressed = gz, Uncompressed = size, Header = header };
        }
    }
}
