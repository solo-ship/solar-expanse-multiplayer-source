using System;
using System.Collections.Generic;
using System.IO;
using LoadSaveManager = Manager.LoadSaveManager;
using GameManager = Manager.GameManager;

namespace SolarExpanseMultiplayer
{
    // The save side of a session (M8 Task 15), one instance owned by MenuSessions: the Multiplayer saves folder and its
    // index, the host's name rewrite and bookkeeping (SaveNameRewrite calls in), the send of each written save to the
    // members, the client's held copies and their install at the menu, and the pause screen's exits. Main thread only.
    public sealed class SaveGlue
    {
        readonly MenuSessions owner;
        public readonly MpSavesFolder Folder = new MpSavesFolder();
        public readonly SaveCopyHost Host;
        public readonly SaveCopyClient Client;
        public readonly ClientSaveBlock Block = new ClientSaveBlock();
        public readonly PauseExitGlue Exits;
        public readonly SaveButtons Buttons = new SaveButtons();
        public readonly SoloLoad Solo;
        bool patched;

        public SaveGlue(MenuSessions owner)
        {
            this.owner = owner;
            Host = new SaveCopyHost(this);
            Client = new SaveCopyClient(this);
            Exits = new PauseExitGlue(owner, this);
            Solo = new SoloLoad(this);
        }

        public MenuSessions Owner { get { return owner; } }
        public NoticeQueue Notices { get { return owner.Actions.Notices; } }
        public bool IsHost { get { return Session.Host != null; } }

        // At each world loaded: in a session the rewrite hook goes in now, after the menu's content scan.
        public void OnSaveLoaded()
        {
            if (!Session.Live || patched) return;
            patched = true;
            if (!GameHooks.EnsureDeferred(HookPlan.SaveNameRewrite, false))
                Log.Error("saves: the save rewrite hook could not be installed; session saves are NOT redirected");
        }

        // ---- the host's side of SaveToFile ----

        public SaveRewritePlan PlanSave(string name)
        {
            bool host = IsHost;
            if (!host) return SaveRewrite.Plan(name, true, false, null, null, DateTime.Now, null, null);
            string why = Folder.Ensure();
            if (why != null) return new SaveRewritePlan { Kind = SaveRewriteKind.Refuse, Why = why };
            string saveId = SaveId();
            return SaveRewrite.Plan(name, true, true, saveId, M4SessionAdapter.OwnName(), DateTime.Now, Folder.ReadIndex(), Folder.FileSha);
        }


        static string SaveId()
        {
            var gm = MonoBehaviourSingleton<GameManager>.Instance;
            return gm != null && gm.StartGameConfig != null ? gm.StartGameConfig.gameplayID : null;
        }

        public void Refused(string why)
        {
            if (!IsHost) { Block.Refused(why, Notices, NetClock.NowMs); return; }
            Log.Warn("saves: the session save was refused: " + why);
            Notices.Push("notice.saveRefused", null, NoticeQueue.Warning, NetClock.NowMs);
        }

        // After SaveToFile said whether it wrote the pair. A written one is hashed, indexed, rotated and offered to the
        // players; a failed one is cleaned away if the mod's own half-written files are all that is left of it.
        public void Saved(LoadSaveManager lsm, SaveRewritePlan plan, bool ok)
        {
            MpSaveIndex index = Folder.ReadIndex();
            if (!ok)
            {
                bool removed = SaveLedger.Failed(index, Folder.Dir, plan.BaseName, Folder.FileSha);
                if (removed) { string w = Folder.WriteIndex(index); if (w != null) Log.Warn("saves: " + w); }
                Log.Warn("saves: a session save failed" + (removed ? "; its files were removed" : "; the old pair is as it was"));
                Notices.Push("notice.saveFailed", null, NoticeQueue.Warning, NetClock.NowMs);
                return;
            }
            string jsonPath = Path.Combine(Folder.Dir, plan.BaseName + ".json.gz");
            byte[] gz = File.ReadAllBytes(jsonPath);
            string jsonSha = Sha256Hex.Of(gz), infoSha = Folder.FileSha(plan.BaseName + ".info.gz");
            if (infoSha == null) { Log.Warn("saves: the info file is missing after a save"); return; }
            SaveInfoData info = SaveInfoMap.FromLive(lsm);
            string bad = info.Validate();
            if (bad != null) { Log.Warn("saves: the save's info was not valid (" + bad + "); not indexed"); return; }
            string saveId = info.StartGameConfiguration.GameplayId;
            var holders = HoldersNow(index, saveId);
            List<MpSaveEntry> dropped = SaveLedger.Commit(index, plan, saveId, M4SessionAdapter.OwnName(), DateTime.UtcNow, info,
                                                          holders, jsonSha, infoSha);
            foreach (MpSaveEntry d in dropped) SaveLedger.DeleteListed(Folder.Dir, d, Folder.FileSha);
            string why = Folder.WriteIndex(index);
            if (why != null) { Log.Warn("saves: the index could not be written: " + why); return; }
            Log.Info("saves: saved " + plan.BaseName + " (" + gz.Length + " bytes), " + dropped.Count + " old autosave(s) dropped");
            Host.Offer(saveId, plan.BaseName, gz, info, holders);
        }

        // The last holder of each agency: what the previous save of this id recorded, over who holds it now.
        Dictionary<string, string> HoldersNow(MpSaveIndex index, string saveId)
        {
            var h = new Dictionary<string, string>();
            MpSaveEntry prev = index.ForSaveId(saveId, MpSaveKind.HostSave);
            if (prev != null) foreach (KeyValuePair<string, string> kv in prev.Holders) h[kv.Key] = kv.Value;
            SessionView v = owner.Actions.View;
            if (v != null)
                foreach (PlayerRow p in v.Players)
                    if (p != null && !string.IsNullOrEmpty(p.Agency) && LobbyIds.IsContentId(p.Agency) && h.Count < SaveCopyHeader.MaxHolders)
                        h[p.Agency] = TextSanitiser.Clean(p.Name, PlayerNames.MaxChars, "");
            return h;
        }

        // Every frame (MenuSessions.Update).
        public void Update()
        {
            Host.Update();
            Exits.Update();
        }

        // Every main menu ready: held copies go through the walls and into the folder.
        public void OnMenuReady()
        {
            Client.InstallAtMenu();
        }
    }
}
