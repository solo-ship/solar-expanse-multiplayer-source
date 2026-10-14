using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SaveGameData = Manager.SaveGameData;

namespace SolarExpanseMultiplayer
{
    // The game side of CopyInstaller (DD8): at the main menu a held copy runs walls 5 to 7 on the unpacked, scanned world
    // with this game's own types, and the info file is rebuilt from the checked fields. Nothing here reads an .info.gz.
    public sealed class CopyWorld : ICopyWorld
    {
        readonly SnapshotLoad deserialiser = new SnapshotLoad(false);

        public bool AtMenu { get { return deserialiser.AtMenu; } }

        public string DeepWalls(byte[] json, SaveInfoData info)
        {
            return SaveWalls.Check(deserialiser, json, info, null);
        }

        public byte[] BuildInfoGz(SaveInfoData info, out string problem)
        {
            return SaveInfoMap.BuildInfoGz(info, out problem);
        }
    }

    // Walls 5 to 7 for a Multiplayer save at the menu, shared by a copy's install and a solo load: the strict deserialise
    // (the refusing binder, wall 6), then the local sanity against the save's own info and this game's content.
    public static class SaveWalls
    {
        // null = ok, else a fixed reason. chosenAgency: the agency a solo load will play, or null.
        public static string Check(SnapshotLoad deserialiser, byte[] json, SaveInfoData info, string chosenAgency)
        {
            object obj;
            return Check(deserialiser, json, info, chosenAgency, out obj);
        }

        public static string Check(SnapshotLoad deserialiser, byte[] json, SaveInfoData info, string chosenAgency, out object saveData)
        {
            string error;
            int code = deserialiser.Deserialise(json, out saveData, out error);
            if (code != 0) { saveData = null; return "the save was refused (" + TextSanitiser.Clean(error, 160, "?") + ")"; }
            var obj = (SaveGameData)saveData;
            string why = LocalSanity.Check(Facts(obj), info, Application.version, chosenAgency, WorldGovernmentIds());
            if (why != null) saveData = null;
            return why;
        }

        static LocalFacts Facts(SaveGameData obj)
        {
            var f = new LocalFacts { StartGameSave = obj.startGameSave, SaveVersion = obj.saveVersion };
            try { f.DateTicks = ((DateTime)obj.currentTime).Ticks; }
            catch (Exception) { f.DateReadable = false; }
            if (obj.ObjectInfoSaves != null)
                foreach (SaveGameData.ObjectInfoSave o in obj.ObjectInfoSaves)
                    if (o != null && o.IDObjectInfo != null && o.IDObjectInfo.ID == 533 && o.isInGameDestroy && o.idImpactFrom == 66)
                        f.EndGame = true;
            var agencies = new HashSet<string>(StringComparer.Ordinal);
            if (obj.companyDataSave != null)
                foreach (var cd in obj.companyDataSave)
                    if (cd != null && cd.companyID != null && cd.companyID.id != null) agencies.Add(cd.companyID.id);
            f.AgenciesInSave = agencies;
            f.InContent = id => SaveInfoMap.Company(id) != null;
            return f;
        }

        static List<string> WorldGovernmentIds()
        {
            var ids = new List<string>();
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
            if (all == null) return ids;
            foreach (var o in all.AllMyIDScriptableObjects)
            {
                var d = o as ScriptableObjectScripts.CompanyDefinition;
                if (d != null && d.IsWorldGovernment && d.ID != null) ids.Add(d.ID);
            }
            return ids;
        }
    }

    // The client's save copies (DD8): the link hands each header and chunk in; they are held under incoming\ and installed
    // at the next main menu. The link side is Task 6's; this class is what it calls.
    public sealed class SaveCopyClient
    {
        readonly SaveGlue glue;
        IncomingStore store;
        SaveCopyReceiver receiver;
        CopyInstaller installer;

        public SaveCopyClient(SaveGlue glue) { this.glue = glue; }

        // <mod>\data\player\incoming for a player; a test profile has its own data folder, so it never reaches theirs.
        string IncomingDir { get { return Boot.DataDir == null ? null : Path.Combine(Boot.DataDir, "incoming"); } }

        IncomingStore Store
        {
            get
            {
                if (store == null && IncomingDir != null) store = new IncomingStore(IncomingDir);
                return store;
            }
        }

        // rosterSaveId: the session's save id (a header for another one is refused).
        public string OnHeader(SaveCopyHeader h, Func<string> rosterSaveId)
        {
            IncomingStore s = Store;
            if (s == null) return "no folder to hold the copy in";
            receiver = new SaveCopyReceiver(s, rosterSaveId);
            string why = receiver.OnHeader(h);
            if (why != null) Log.Warn("saves: a copy header was refused: " + why);
            return why;
        }

        // null = fine (more may follow, or the copy is now held); else the reason (the receiver reset).
        public string OnChunk(int index, byte[] data, int offset, int count, out bool complete)
        {
            complete = false;
            if (receiver == null) return "no copy is expected";
            string why = receiver.OnChunk(index, data, offset, count, out complete);
            if (why != null) Log.Warn("saves: a copy chunk was refused: " + why);
            else if (complete) Log.Info("saves: a copy is held; it is installed at the next main menu");
            return why;
        }

        public int Received { get { return receiver != null ? receiver.Received : 0; } }

        // Every main menu ready: the held copies go through the walls and into the Multiplayer saves folder.
        public void InstallAtMenu()
        {
            IncomingStore s = Store;
            if (s == null || s.Count == 0) return;
            string why = glue.Folder.Ensure();
            if (why != null) { Log.Warn("saves: held copies wait: " + why); return; }
            if (installer == null) installer = new CopyInstaller(glue.Folder.Dir, s, new CopyWorld());
            foreach (InstallOutcome o in installer.RunAtMenu())
            {
                if (o.Name != null)
                {
                    Log.Info("saves: a copy was added to Multiplayer saves");
                    glue.Notices.Push("notice.copyInstalled", new object[] { o.Name.Substring(SaveNames.Prefix.Length) }, NoticeQueue.Routine,
                                      NetClock.NowMs);
                }
                else
                {
                    Log.Warn("saves: a copy was refused: " + o.Why);
                    glue.Notices.Push("notice.copyRefused", null, NoticeQueue.Warning, NetClock.NowMs);
                }
            }
        }
    }
}
