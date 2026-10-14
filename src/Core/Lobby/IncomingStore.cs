using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SolarExpanseMultiplayer
{
    // One save copy held at <mod>\data\player\incoming\, waiting for the next main menu (DD8, C1). Nothing here is the game's: the
    // bytes are not read by anything until the walls have run.
    public sealed class HeldCopy
    {
        public string SaveId;
        public byte[] Gz;
        public string Sha256;
        public SaveInfoData Info;
        public Dictionary<string, string> Holders = new Dictionary<string, string>();
        public string Title, HostName;   // the host's save title and name (cleaned peer text), for the copy's file name and index entry
    }

    // The held copies: <save-id>.json.gz plus <save-id>.meta.json (hash, info, last holders), one pair per save id, a newer copy
    // replaces an older one. Size-capped at the join's caps; the hash is checked on the way in and again on the way out.
    public sealed class IncomingStore
    {
        public const int MetaMaxBytes = 64 * 1024;
        const string GzExt = ".json.gz", MetaExt = ".meta.json";

        sealed class Meta
        {
            public string Sha256;
            public SaveInfoData Info;
            public Dictionary<string, string> Holders;
            public string Title, HostName;
        }

        readonly string dir;
        public readonly List<string> Problems = new List<string>();   // fixed text, one per copy dropped by TakeAll

        public IncomingStore(string folder) { dir = folder; }

        // null = held, else a fixed reason (nothing held).
        public string Put(string saveId, byte[] gz, string sha256, SaveInfoData info, IDictionary<string, string> holders,
                          string title = null, string hostName = null)
        {
            if (!SaveNames.IsSaveId(saveId)) return "the copy's save id is not valid";
            if (gz == null || gz.Length < 1 || gz.Length > JoinLimits.MaxCompressed) return "the copy's size is outside the cap";
            if (!PlayerKeys.IsHex64(sha256)) return "the copy's hash is not valid";
            if (!PlayerKeys.Same(Sha256Hex.Of(gz), sha256)) return "the copy does not match its hash";
            if (info == null) return "the copy has no save info";
            string why = info.Validate();
            if (why != null) return why;
            var h = new Dictionary<string, string>();
            if (holders != null)
            {
                if (holders.Count > SaveCopyHeader.MaxHolders) return "the copy's last-holder list is too long";
                foreach (KeyValuePair<string, string> kv in holders)
                {
                    if (!LobbyIds.IsContentId(kv.Key)) return "a last holder is not valid";
                    h[kv.Key] = TextSanitiser.Clean(kv.Value, PlayerNames.MaxChars, "");
                }
            }
            byte[] meta = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Meta { Sha256 = sha256, Info = info, Holders = h,
                Title = TextSanitiser.Clean(title, SaveCopyHeader.MaxTitleChars, ""), HostName = TextSanitiser.Clean(hostName, SaveCopyHeader.MaxHostNameChars, "") }));
            // The world file first, the meta last: a pair without its meta is never read.
            string e = AtomicFile.Write(Path.Combine(dir, saveId + GzExt), gz);
            if (e != null) return e;
            e = AtomicFile.Write(Path.Combine(dir, saveId + MetaExt), meta);
            if (e != null) { Remove(saveId); return e; }
            return null;
        }

        // Every held copy that is whole and still matches its hash. Anything else in the folder that is ours (a lone half, a copy that
        // changed on disk, a stale temp file) is deleted and noted in Problems. The caller deletes each copy with Remove once it
        // has been written or refused.
        public List<HeldCopy> TakeAll()
        {
            var list = new List<HeldCopy>();
            Problems.Clear();
            if (!Directory.Exists(dir)) return list;
            string[] files;
            try { files = Directory.GetFiles(dir); } catch (Exception) { return list; }
            var ids = new HashSet<string>();
            foreach (string f in files)
            {
                string n = Path.GetFileName(f);
                if (n.EndsWith(".tmp", StringComparison.Ordinal)) { TryDelete(f); continue; }
                string id = IdOf(n);
                if (id != null) ids.Add(id);
            }
            foreach (string id in ids)
            {
                HeldCopy c = Load(id);
                if (c != null) list.Add(c); else Remove(id);
            }
            return list;
        }

        static string IdOf(string fileName)
        {
            string stem = null;
            if (fileName.EndsWith(GzExt, StringComparison.Ordinal)) stem = fileName.Substring(0, fileName.Length - GzExt.Length);
            else if (fileName.EndsWith(MetaExt, StringComparison.Ordinal)) stem = fileName.Substring(0, fileName.Length - MetaExt.Length);
            return SaveNames.IsSaveId(stem) ? stem : null;
        }

        HeldCopy Load(string id)
        {
            try
            {
                string gzPath = Path.Combine(dir, id + GzExt), metaPath = Path.Combine(dir, id + MetaExt);
                if (!File.Exists(gzPath) || !File.Exists(metaPath)) { Problems.Add("a held copy was incomplete"); return null; }
                if (new FileInfo(gzPath).Length > JoinLimits.MaxCompressed || new FileInfo(metaPath).Length > MetaMaxBytes) { Problems.Add("a held copy was over the cap"); return null; }
                Meta m = JsonConvert.DeserializeObject<Meta>(File.ReadAllText(metaPath, Encoding.UTF8));
                byte[] gz = File.ReadAllBytes(gzPath);
                if (m == null || m.Info == null || m.Info.Validate() != null || !PlayerKeys.IsHex64(m.Sha256)) { Problems.Add("a held copy's record was not valid"); return null; }
                if (!PlayerKeys.Same(Sha256Hex.Of(gz), m.Sha256)) { Problems.Add("a held copy no longer matched its hash"); return null; }
                var c = new HeldCopy { SaveId = id, Gz = gz, Sha256 = m.Sha256, Info = m.Info,
                    Title = TextSanitiser.Clean(m.Title, SaveCopyHeader.MaxTitleChars, ""), HostName = TextSanitiser.Clean(m.HostName, SaveCopyHeader.MaxHostNameChars, "") };
                if (m.Holders != null)
                {
                    if (m.Holders.Count > SaveCopyHeader.MaxHolders) { Problems.Add("a held copy's holders were too many"); return null; }
                    foreach (KeyValuePair<string, string> kv in m.Holders)
                        if (LobbyIds.IsContentId(kv.Key)) c.Holders[kv.Key] = TextSanitiser.Clean(kv.Value, PlayerNames.MaxChars, "");
                }
                return c;
            }
            catch (Exception e) { Problems.Add("a held copy could not be read (" + e.GetType().Name + ")"); return null; }
        }

        public void Remove(string saveId)
        {
            if (!SaveNames.IsSaveId(saveId)) return;
            TryDelete(Path.Combine(dir, saveId + GzExt));
            TryDelete(Path.Combine(dir, saveId + MetaExt));
        }

        static void TryDelete(string f) { try { File.Delete(f); } catch (Exception) { } }

        public int Count
        {
            get
            {
                try { return Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0; } catch (Exception) { return 0; }
            }
        }
    }
}
