using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace SolarExpanseMultiplayer
{
    public static class MpSaveKind
    {
        public const int HostSave = 0, HostAutosave = 1, Copy = 2;
    }

    public sealed class MpSaveEntry
    {
        public string Name;                    // "MP <title>": the file name without its two extensions
        public int Kind;                       // MpSaveKind
        public string SaveId;
        public string Host;
        public DateTime TimeUtc;
        public string JsonSha, InfoSha;        // SHA-256 of both files as written
        public SaveInfoData Info;
        public Dictionary<string, string> Holders = new Dictionary<string, string>();   // agency id -> last holder's name

        public string Validate()
        {
            if (!SaveNames.IsLegal(Name)) return "an entry's name is not legal";
            if (Kind < MpSaveKind.HostSave || Kind > MpSaveKind.Copy) return "an entry's kind is not valid";
            if (!SaveNames.IsSaveId(SaveId)) return "an entry's save id is not valid";
            if (Host == null || Host.Length > PlayerNames.MaxChars) return "an entry's host is not valid";
            if (!PlayerKeys.IsHex64(JsonSha) || !PlayerKeys.IsHex64(InfoSha)) return "an entry's hashes are not valid";
            if (Info == null || Info.Validate() != null) return "an entry's info is not valid";
            if (Holders == null || Holders.Count > SaveCopyHeader.MaxHolders) return "an entry's holders are not valid";
            foreach (KeyValuePair<string, string> kv in Holders)
                if (!LobbyIds.IsContentId(kv.Key) || kv.Value == null || kv.Value.Length > PlayerNames.MaxChars) return "an entry's holder is not valid";
            return null;
        }
    }

    // <Multiplayer Saves>\index.json: the mod's own list of its saves (it never lists the folder, and never touches a file the
    // index does not name). Plain JSON, read with a size cap; a damaged index is moved aside and the list starts empty.
    public sealed class MpSaveIndex
    {
        public const int MaxBytes = 4 * 1024 * 1024, MaxEntries = 1000, AutosavesKept = 6;

        sealed class Doc { public int Version = 1; public List<MpSaveEntry> Entries = new List<MpSaveEntry>(); }

        public readonly List<MpSaveEntry> Entries = new List<MpSaveEntry>();

        // A missing file is an empty index (problem null). A file over the cap, unparseable, or holding ANY invalid entry or a
        // repeated name is moved aside as index.json.bad-<time> and the result is empty with the reason: half-trusting it would
        // let a planted entry name a file the mod then deletes.
        public static MpSaveIndex Read(string path, out string problem)
        {
            problem = null;
            var idx = new MpSaveIndex();
            if (!File.Exists(path)) return idx;
            string why = null;
            try
            {
                if (new FileInfo(path).Length > MaxBytes) why = "the index is over its size cap";
                else
                {
                    Doc d = JsonConvert.DeserializeObject<Doc>(File.ReadAllText(path, Encoding.UTF8));
                    if (d == null || d.Entries == null || d.Entries.Count > MaxEntries) why = "the index is not valid";
                    else
                    {
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (MpSaveEntry e in d.Entries)
                        {
                            string w = e == null ? "an entry is empty" : e.Validate();
                            if (w == null && !names.Add(e.Name)) w = "an entry's name is repeated";
                            if (w != null) { why = w; break; }
                        }
                        if (why == null) idx.Entries.AddRange(d.Entries);
                    }
                }
            }
            catch (Exception e) { why = "the index could not be read (" + e.GetType().Name + ")"; }
            if (why == null) return idx;
            idx.Entries.Clear();
            string aside = AtomicFile.MoveAside(path);
            problem = why + (aside != null ? "; moved aside as " + Path.GetFileName(aside) : "; it could not be moved aside");
            return idx;
        }

        // The newest entry for that save id and kind, or null. (One copy per save id and kind; host autosaves are several.)
        public MpSaveEntry ForSaveId(string saveId, int kind)
        {
            MpSaveEntry best = null;
            foreach (MpSaveEntry e in Entries)
                if (e.Kind == kind && string.Equals(e.SaveId, saveId, StringComparison.Ordinal) && (best == null || e.TimeUtc > best.TimeUtc)) best = e;
            return best;
        }

        public MpSaveEntry ForName(string saveName)
        {
            if (saveName == null) return null;
            foreach (MpSaveEntry e in Entries) if (string.Equals(e.Name, saveName, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // Only if both files are on disk exactly as written (the two hashes the caller just measured equal the recorded ones).
        public bool MayOverwrite(MpSaveEntry e, string jsonSha, string infoSha)
        {
            return e != null && jsonSha != null && infoSha != null && PlayerKeys.Same(e.JsonSha, jsonSha) && PlayerKeys.Same(e.InfoSha, infoSha);
        }

        // The host autosaves of that save id beyond the newest AutosavesKept.
        public List<MpSaveEntry> AutosavesToDrop(string saveId)
        {
            return Entries.Where(e => e.Kind == MpSaveKind.HostAutosave && e.SaveId == saveId)
                          .OrderByDescending(e => e.TimeUtc).Skip(AutosavesKept).ToList();
        }

        public void Add(MpSaveEntry e)
        {
            Entries.RemoveAll(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            Entries.Add(e);
        }

        public void Remove(string name)
        {
            Entries.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public string Write(string path)
        {
            string bad = null;
            foreach (MpSaveEntry e in Entries) { bad = e.Validate(); if (bad != null) break; }
            if (bad != null) return bad;
            var d = new Doc { Entries = Entries };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(d, Formatting.Indented));
            if (bytes.Length > MaxBytes) return "the index would be over its size cap";
            return AtomicFile.Write(path, bytes);
        }
    }
}
