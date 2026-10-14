using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // What the game side must answer for a held copy at the menu (Task 15 implements it over M4's route A and the client's own types).
    public interface ICopyWorld
    {
        bool AtMenu { get; }
        // Walls 5-7 on the unpacked, scanned world: every reference resolves in this game's content, the strict deserialise, and the
        // local sanity (no claim, no host header). null = ok, else a fixed reason.
        string DeepWalls(byte[] json, SaveInfoData info);
        // The .info.gz in the game's own format, built with this game's types from the checked fields. null + problem when a content
        // id does not resolve (the copy is refused).
        byte[] BuildInfoGz(SaveInfoData info, out string problem);
    }

    public sealed class InstallOutcome
    {
        public string SaveId;
        public string Name;     // the Multiplayer save written, or null when refused
        public string Why;      // fixed text when refused
    }

    // At the main menu: each held copy goes through ALL the walls, in order, and only then is written into the Multiplayer saves
    // folder (world file first, info file last, then the index). A copy that fails any wall writes nothing. Every held copy is
    // removed from incoming\ either way. Not at the menu: nothing is touched (wall 6 refuses a deserialise anywhere else).
    public sealed class CopyInstaller
    {
        public const string IndexName = "index.json";
        readonly string folder;
        readonly IncomingStore incoming;
        readonly ICopyWorld world;

        public CopyInstaller(string multiplayerSavesFolder, IncomingStore incoming, ICopyWorld world)
        {
            folder = multiplayerSavesFolder; this.incoming = incoming; this.world = world;
        }

        public List<InstallOutcome> RunAtMenu()
        {
            var results = new List<InstallOutcome>();
            bool atMenu;
            try { atMenu = world.AtMenu; } catch (Exception) { atMenu = false; }
            if (!atMenu) return results;
            List<HeldCopy> held = incoming.TakeAll();
            foreach (string p in incoming.Problems) results.Add(new InstallOutcome { Why = p });
            if (held.Count == 0) return results;
            string problem;
            string indexPath = Path.Combine(folder, IndexName);
            MpSaveIndex index = MpSaveIndex.Read(indexPath, out problem);
            if (problem != null) Log.Warn("save copy: " + problem);
            foreach (HeldCopy c in held)
            {
                var r = new InstallOutcome { SaveId = c.SaveId };
                try { r.Why = Install(c, index, indexPath, out r.Name); }
                catch (Exception e) { r.Name = null; r.Why = "the copy could not be installed (" + e.GetType().Name + ")"; }
                if (r.Why != null) r.Name = null;
                incoming.Remove(c.SaveId);
                results.Add(r);
            }
            return results;
        }

        string Install(HeldCopy c, MpSaveIndex index, string indexPath, out string installedName)
        {
            installedName = null;
            // Wall 2: the bytes are the ones whose hash was checked on arrival (TakeAll checked them again).
            if (!PlayerKeys.Same(Sha256Hex.Of(c.Gz), c.Sha256)) return "the copy does not match its hash";
            // Wall 3: the bounded gunzip. The declared size is the gzip trailer's own (ISIZE); the gunzip checks it is exact.
            if (c.Gz.Length < 18) return "the copy is not a gzip file";
            long declared = (uint)(c.Gz[c.Gz.Length - 4] | (c.Gz[c.Gz.Length - 3] << 8) | (c.Gz[c.Gz.Length - 2] << 16) | (c.Gz[c.Gz.Length - 1] << 24));
            int code; string error;
            byte[] json = BoundedGunzip.Decompress(c.Gz, declared, out code, out error);
            if (json == null) return "the copy could not be unpacked: " + error;
            // Wall 4: the strict scan (types, depth, size) before Odin or the game sees a byte.
            ScanResult scan = OdinScan.Scan(json, ScanLimits.FromCorpus());
            if (!scan.Ok) return "the copy was refused by the scan (" + (scan.Code == ScanCode.TypeNotAllowed ? "a type that is not allowed" : "malformed") + ")";
            // Walls 5-7, at the menu: references, the strict deserialise, the local sanity.
            string deep = world.DeepWalls(json, c.Info);
            if (deep != null) return deep;
            json = null;
            string infoProblem;
            byte[] infoGz = world.BuildInfoGz(c.Info, out infoProblem);
            if (infoGz == null || infoGz.Length == 0) return "the copy's info file could not be made: " + (infoProblem ?? "no result");
            if (infoGz.Length > JoinLimits.MaxCompressed) return "the copy's info file is over the cap";
            string infoSha = Sha256Hex.Of(infoGz);

            // The name: replace this save id's copy only if its files are exactly as written; else the next free number.
            MpSaveEntry existing = index.ForSaveId(c.SaveId, MpSaveKind.Copy);
            string name = null;
            bool overwrite = false;
            if (existing != null && SaveNames.IsLegal(existing.Name) && FilesAsWritten(index, existing)) { name = existing.Name; overwrite = true; }
            if (name == null)
            {
                string baseName = SaveNames.Clean(c.Title, c.HostName, c.Info.GameDate, false);
                for (int n = 1; n < 1000 && name == null; n++)
                {
                    string tryName = SaveNames.Numbered(baseName, n);
                    if (SaveNames.IsLegal(tryName) && index.ForName(tryName) == null && !File.Exists(JsonPath(tryName)) && !File.Exists(InfoPath(tryName))) name = tryName;
                }
                if (name == null) return "no free name was left for the copy";
            }
            string jsonPath = JsonPath(name), infoPath = InfoPath(name);
            if (!PathRules.IsInside(folder, jsonPath) || !PathRules.IsInside(folder, infoPath)) return "the copy's name lies outside the folder";

            string e = AtomicFile.Write(jsonPath, c.Gz);
            if (e == null) e = AtomicFile.Write(infoPath, infoGz);   // the info file last
            if (e != null) { Undo(index, name, overwrite); return e; }
            var entry = new MpSaveEntry
            {
                Name = name, Kind = MpSaveKind.Copy, SaveId = c.SaveId, Host = c.HostName.Length > 0 ? c.HostName : "host", TimeUtc = DateTime.UtcNow,
                JsonSha = c.Sha256, InfoSha = infoSha, Info = c.Info, Holders = new Dictionary<string, string>(c.Holders)
            };
            index.Add(entry);
            e = index.Write(indexPath);
            if (e != null) { Undo(index, name, overwrite); return e; }
            installedName = name;
            return null;
        }

        string JsonPath(string name) { return Path.Combine(folder, name + ".json.gz"); }
        string InfoPath(string name) { return Path.Combine(folder, name + ".info.gz"); }

        bool FilesAsWritten(MpSaveIndex index, MpSaveEntry e)
        {
            try
            {
                if (!File.Exists(JsonPath(e.Name)) || !File.Exists(InfoPath(e.Name))) return false;
                return index.MayOverwrite(e, Sha256Hex.Of(File.ReadAllBytes(JsonPath(e.Name))), Sha256Hex.Of(File.ReadAllBytes(InfoPath(e.Name))));
            }
            catch (Exception) { return false; }
        }

        // A failed install leaves no half pair: both files gone and the entry dropped. (A new name had nothing before it; an
        // overwritten copy's old pair is already replaced, so it goes too rather than stay mismatched.)
        void Undo(MpSaveIndex index, string name, bool overwrote)
        {
            try { File.Delete(JsonPath(name)); } catch (Exception) { }
            try { File.Delete(InfoPath(name)); } catch (Exception) { }
            if (overwrote) { index.Remove(name); try { index.Write(Path.Combine(folder, IndexName)); } catch (Exception) { } }
            else index.Remove(name);
        }
    }
}
