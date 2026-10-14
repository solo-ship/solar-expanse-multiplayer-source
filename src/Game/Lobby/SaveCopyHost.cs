using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's send of each written save to the members (DD8 "When"): after a manual save, a yearly autosave and the
    // session's final save. The coalescing and the pacing are SaveCopyPump's; this class gathers the members from the
    // session view, makes the header and pumps a few chunks per frame. The session's link is Peers (the lobby service sets
    // it; until then nothing is sent and the log says so once). Main thread only.
    public sealed class SaveCopyHost
    {
        readonly SaveGlue glue;
        readonly SaveCopyPump pump = new SaveCopyPump();
        readonly SaveCopy sizer = new SaveCopy();
        bool noPeersLogged;
        long lastBytes;

        public SaveCopyHost(SaveGlue glue) { this.glue = glue; }

        // Set by the lobby service when a session starts, cleared when it ends.
        public ISaveCopyPeers Peers { get; set; }

        public bool Idle { get { return pump.Idle; } }

        // A save was written and indexed: queue it for every member that is in the game.
        public void Offer(string saveId, string name, byte[] gz, SaveInfoData info, IDictionary<string, string> holders)
        {
            if (gz.Length < 1 || gz.Length > JoinLimits.MaxCompressed)
            {
                Log.Warn("saves: the save is " + gz.Length + " bytes, outside the copy cap: it is not sent");
                return;
            }
            List<int> to = Members();
            if (to.Count == 0) return;
            string title = name.Substring(SaveNames.Prefix.Length);
            SaveCopyHeader h = SaveCopyHeaders.For(saveId, title, M4SessionAdapter.OwnName(), gz, info, holders);
            if (h == null || h.Validate() != null)
            {
                Log.Warn("saves: the save's header was not valid: " + (h == null ? "no header" : h.Validate()));
                return;
            }
            lastBytes = gz.Length;
            pump.Offer(to, h, gz, NetClock.NowMs);
            Log.Info("saves: queued a copy for " + to.Count + " player(s) (" + gz.Length + " bytes)");
        }

        List<int> Members()
        {
            var list = new List<int>();
            SessionView v = glue.Owner.Actions.View;
            if (v != null)
                foreach (PlayerRow p in v.Players)
                    if (p != null && !p.Host && !p.Me && p.State == PlayerRow.InGame) list.Add(p.Id);
            return list;
        }

        // The deadline for the session's last copy: the join's scaling for the bytes still to send, at most 120 s.
        public long EndDeadlineMs { get { return sizer.EndDeadlineMs(lastBytes); } }

        // A member's ack of its copy (the lobby service calls it with the next chunk the member expects).
        public bool OnAck(int player, int nextReceived) { return pump.Ack(player, nextReceived, NetClock.NowMs); }

        public void OnLeft(int player) { pump.Drop(player); }

        public void Update()
        {
            if (pump.Idle) return;
            ISaveCopyPeers peers = Peers;
            if (peers == null)
            {
                if (!noPeersLogged) { noPeersLogged = true; Log.Warn("saves: no link to send the copy on yet"); }
                return;
            }
            try { pump.Pump(peers, NetClock.NowMs); }
            catch (Exception e) { Log.Error("saves: the copy send failed: " + e.Message); }
        }

        // The session's final save: the game's own exit save, which the rewrite sends to the Multiplayer saves folder
        // and offers to the members (DD21). Once only per call; failures are the save's own (logged by the hook).
        public void FinalSave()
        {
            var lsm = SerializedMonoBehaviourSingleton<Manager.LoadSaveManager>.Instance;
            if (lsm == null) { Log.Warn("saves: no LoadSaveManager for the final save"); return; }
            Log.Info("saves: the final save of the session");
            lsm.CreateAutoSave(true);
        }
    }
}
