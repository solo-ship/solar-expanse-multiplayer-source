using System;
using System.Collections.Generic;
using System.IO;
using Data.ScriptableObject;
using ScriptableObjectScripts;
using ScriptableObjectScripts.StartGameConfiguration;

namespace SolarExpanseMultiplayer
{
    // The Multiplayer saves folder (DD24): Saves\Multiplayer\ inside the game's own Saves (the game's listings are not
    // recursive, so its Load list, Continue and autosave rotation never see it), with the mod's own index.json beside the
    // files. The folder is made by the mod before its first write. Every file operation here is inside the folder
    // (PathRules.IsInside) and only for names the index lists. Owned by MenuSessions; main thread only.
    public sealed class MpSavesFolder : IMpSaves
    {
        string dir;

        public string Dir
        {
            get
            {
                if (dir == null) dir = Path.Combine(Manager.LoadSaveManager.FullSaveDirectory(), SaveRewrite.Folder);
                return dir;
            }
        }

        public string IndexPath { get { return Path.Combine(Dir, CopyInstaller.IndexName); } }

        // null = made (or already there), else a fixed reason. The folder lies inside Saves by construction; checked anyway.
        public string Ensure()
        {
            try
            {
                string saves = Manager.LoadSaveManager.FullSaveDirectory();
                if (!PathRules.IsInside(saves, Dir)) return "the Multiplayer saves folder is not inside Saves";
                Directory.CreateDirectory(Dir);
                return null;
            }
            catch (Exception e) { return "the Multiplayer saves folder could not be made (" + e.GetType().Name + ")"; }
        }

        public MpSaveIndex ReadIndex()
        {
            string problem;
            MpSaveIndex index = MpSaveIndex.Read(IndexPath, out problem);
            if (problem != null) Log.Warn("saves: " + problem);
            return index;
        }

        public string WriteIndex(MpSaveIndex index) { return index.Write(IndexPath); }

        // The SHA-256 of a file in the folder (a name with its extension), or null when it is not there or cannot be read.
        public string FileSha(string fileName)
        {
            try
            {
                string p = Path.Combine(Dir, fileName);
                if (!PathRules.IsInside(Dir, p) || !File.Exists(p)) return null;
                return Sha256Hex.Of(File.ReadAllBytes(p));
            }
            catch (Exception) { return null; }
        }

        // ---- the list the menu shows (IMpSaves) ----

        public IList<MpSaveRow> MpSaves
        {
            get
            {
                var rows = new List<MpSaveRow>();
                if (!Directory.Exists(Dir)) return rows;
                foreach (MpSaveEntry e in ReadIndex().Entries)
                {
                    if (!File.Exists(Path.Combine(Dir, e.Name + ".json.gz"))) continue;   // the file went: not listed
                    rows.Add(Row(e));
                }
                rows.Sort((a, b) => b.LastPlayedUtcTicks.CompareTo(a.LastPlayedUtcTicks));
                return rows;
            }
        }

        // The title is the file name without its "MP " prefix and number; the agencies come from the saved start
        // configuration and the index's last holders, with the system's idle companies from this game's content.
        static MpSaveRow Row(MpSaveEntry e)
        {
            StartConfigData c = e.Info.StartGameConfiguration;
            var row = new MpSaveRow
            {
                Name = e.Name, Title = e.Name.Substring(SaveNames.Prefix.Length), HostName = e.Host,
                GameDate = e.Info.GameDate.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                Difficulty = Enum.IsDefined(typeof(Manager.GameManager.EGameDifficulty), c.GameDifficulty)
                    ? ((Manager.GameManager.EGameDifficulty)c.GameDifficulty).ToString() : null,
                LastPlayedUtcTicks = e.TimeUtc.Ticks
            };
            row.Agencies = SoloAgencies.For(e, IdleOf(c), Playable);
            return row;
        }

        static List<string> IdleOf(StartConfigData c)
        {
            var idle = new List<string>();
            try
            {
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                if (all == null) return idle;
                foreach (MyIDScriptableObject o in all.AllMyIDScriptableObjects)
                {
                    var d = o as PlanetarySystemDescriptor;
                    if (d == null || d.ID != c.PlanetarySystemDescriptor || d.idleCompanies == null) continue;
                    foreach (CompanyDefinition cd in d.idleCompanies) if (cd != null && cd.ID != null) idle.Add(cd.ID);
                }
            }
            catch (Exception) { }
            return idle;
        }

        // An agency this game has and that is not the World Government (DD24 step 1).
        static bool Playable(string id)
        {
            CompanyDefinition d = SaveInfoMap.Company(id);
            return d != null && !d.IsWorldGovernment;
        }

        // ---- delete ----

        // null = deleted; else a UiText key. Both files go only when they still have the recorded hashes; a mismatch leaves
        // the files and drops the entry (DD24 "Delete").
        public string DeleteMpSave(string name)
        {
            try
            {
                MpSaveIndex index = ReadIndex();
                MpSaveEntry e = index.ForName(name);
                if (e == null) return "menu.saves.gone";
                bool removed = SaveLedger.DeleteListed(Dir, e, FileSha);
                index.Remove(e.Name);
                string why = WriteIndex(index);
                if (why != null) { Log.Warn("saves: the index could not be written after a delete: " + why); return "menu.saves.deleteFailed"; }
                Log.Info("saves: deleted " + (removed ? "the pair" : "the entry only (the files had changed)"));
                return null;
            }
            catch (Exception ex) { Log.Warn("saves: delete failed: " + ex.GetType().Name); return "menu.saves.deleteFailed"; }
        }
    }
}
