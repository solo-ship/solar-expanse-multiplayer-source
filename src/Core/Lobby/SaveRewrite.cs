using System;
using System.Collections.Generic;
using System.IO;

namespace SolarExpanseMultiplayer
{
    public enum SaveRewriteKind { Vanilla = 0, Refuse, Rewrite }

    // What the SaveToFile prefix does with one save name (DD8).
    public sealed class SaveRewritePlan
    {
        public SaveRewriteKind Kind;
        public string Name;       // Rewrite: "Multiplayer/<name>", the name the game is given
        public string BaseName;   // Rewrite: the file name without the folder and the two extensions
        public int EntryKind;     // Rewrite: MpSaveKind.HostSave or HostAutosave
        public string Why;        // Refuse: a fixed reason for the log
    }

    // DD8's name rule, in one pure place: outside a session nothing changes; a client never saves; on the host the F8
    // reporter's "bugSave" stays vanilla and every other name becomes a Multiplayer saves folder name that is either new
    // or a pair the index lists and that is still exactly as written. A file the index does not list is never a target.
    public static class SaveRewrite
    {
        public const string Folder = "Multiplayer", BugSave = "bugSave";

        // fileSha: the SHA-256 of a file in the folder, or null when it is not there. Never throws.
        public static SaveRewritePlan Plan(string name, bool inSession, bool isHost, string saveId, string hostName, DateTime now,
                                           MpSaveIndex index, Func<string, string> fileSha)
        {
            if (!inSession) return new SaveRewritePlan { Kind = SaveRewriteKind.Vanilla };
            if (!isHost) return Refuse("a client never saves");
            if (string.Equals(name, BugSave, StringComparison.Ordinal)) return new SaveRewritePlan { Kind = SaveRewriteKind.Vanilla };
            if (string.IsNullOrEmpty(name)) return Refuse("the save has no name");
            if (!SaveNames.IsSaveId(saveId)) return Refuse("the game's id is not 16 lower-case hex digits");
            if (index == null || fileSha == null) return Refuse("the saves folder is not ready");

            bool auto = name.StartsWith("autosave", StringComparison.OrdinalIgnoreCase);
            string baseName = SaveNames.Clean(name, hostName, now, auto);
            int kind = auto ? MpSaveKind.HostAutosave : MpSaveKind.HostSave;
            for (int n = 1; n < 1000; n++)
            {
                string cand = SaveNames.Numbered(baseName, n);
                if (!SaveNames.IsLegal(cand)) continue;
                MpSaveEntry e = index.ForName(cand);
                string json = fileSha(cand + ".json.gz"), info = fileSha(cand + ".info.gz");
                bool free = e == null && json == null && info == null;
                bool ours = e != null && e.Kind == kind && string.Equals(e.SaveId, saveId, StringComparison.Ordinal) &&
                            index.MayOverwrite(e, json, info);
                if (free || ours)
                    return new SaveRewritePlan { Kind = SaveRewriteKind.Rewrite, BaseName = cand, Name = Folder + "/" + cand, EntryKind = kind };
            }
            return Refuse("no free name was left for the save");
        }

        static SaveRewritePlan Refuse(string why) { return new SaveRewritePlan { Kind = SaveRewriteKind.Refuse, Why = why }; }
    }

    // The index after a save that SaveToFile said it wrote (DD8 "Only a written save counts"), and the clean-up after one it
    // said it did not. Pure over the index and the folder; the glue reads and writes index.json.
    public static class SaveLedger
    {
        // Records the pair in the index and rotates the host's autosaves. Returns the entries dropped by the rotation (their
        // files are deleted by DeleteListed, if still as written).
        public static List<MpSaveEntry> Commit(MpSaveIndex index, SaveRewritePlan plan, string saveId, string host, DateTime utc,
                                               SaveInfoData info, IDictionary<string, string> holders, string jsonSha, string infoSha)
        {
            var e = new MpSaveEntry
            {
                Name = plan.BaseName, Kind = plan.EntryKind, SaveId = saveId, Host = host, TimeUtc = utc, JsonSha = jsonSha, InfoSha = infoSha,
                Info = info, Holders = holders == null ? new Dictionary<string, string>() : new Dictionary<string, string>(holders)
            };
            index.Add(e);
            var dropped = new List<MpSaveEntry>();
            if (plan.EntryKind == MpSaveKind.HostAutosave)
                foreach (MpSaveEntry d in index.AutosavesToDrop(saveId)) { dropped.Add(d); }
            foreach (MpSaveEntry d in dropped) index.Remove(d.Name);
            return dropped;
        }

        // A save that failed: a pair whose two files still have the index's recorded hashes is left as it was (the failure came
        // before any write); otherwise both files of that name are deleted and the entry dropped. Only ever called for a name
        // the rewrite chose, so it never touches a file the mod did not write. True when something was removed.
        public static bool Failed(MpSaveIndex index, string folder, string baseName, Func<string, string> fileSha)
        {
            MpSaveEntry e = index.ForName(baseName);
            string json = fileSha(baseName + ".json.gz"), info = fileSha(baseName + ".info.gz");
            if (e != null && index.MayOverwrite(e, json, info)) return false;
            TryDelete(Path.Combine(folder, baseName + ".json.gz"));
            TryDelete(Path.Combine(folder, baseName + ".info.gz"));
            index.Remove(baseName);
            return true;
        }

        // Deletes an entry's pair, only when both files still have the recorded hashes; a mismatch leaves the files and drops
        // the entry (DD24 "Delete"). The caller has removed or removes the entry from the index.
        public static bool DeleteListed(string folder, MpSaveEntry e, Func<string, string> fileSha)
        {
            string json = fileSha(e.Name + ".json.gz"), info = fileSha(e.Name + ".info.gz");
            bool match = json != null && info != null && PlayerKeys.Same(e.JsonSha, json) && PlayerKeys.Same(e.InfoSha, info);
            if (!match) return false;
            TryDelete(Path.Combine(folder, e.Name + ".json.gz"));
            TryDelete(Path.Combine(folder, e.Name + ".info.gz"));
            return true;
        }

        static void TryDelete(string f) { try { File.Delete(f); } catch (Exception) { } }
    }
}
