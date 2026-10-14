using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's join driver, part 5: its calls into the command stream (M5; see JoinService.cs). Every call is
    // guarded: the stream's faults are its business, and a throw here must not leave a pause on or a batch half ended.
    public sealed partial class JoinService
    {
        readonly List<int> claimedBy = new List<int>();   // the member behind each entry of claimedNew

        // In the paused snapshot frame, before TakeSnapshot: the stream claims the seats of the members whose agency is
        // not human yet (a full rejoin's own agency, or one that survived a host load, is) and holds. False = the
        // stream failed, and the snapshot must not be taken (it could disagree with the stream).
        bool BeginStream(out string why)
        {
            why = null;
            humanAtSnap = null;
            claimedNew.Clear(); claimedBy.Clear(); loadedInBatch.Clear();
            streamBegan = false;
            if (stream == null) return true;
            try
            {
                string[] before = stream.HumanAgencies;
                foreach (int id in members)
                {
                    Joiner j = players[id];
                    if (j == null || j.Phase != Phase.Batch) continue;
                    string agency = ledger.AgencyOf(id);
                    if (agency == null || Array.IndexOf(before, agency) >= 0 || claimedNew.Contains(agency)) continue;
                    claimedNew.Add(agency); claimedBy.Add(id);
                }
                streamBegan = true;   // set first: a call that throws part way is still ended
                stream.BatchStarting(claimedNew);
                humanAtSnap = stream.HumanAgencies;
                return true;
            }
            catch (Exception e)
            {
                why = "the command stream failed starting the batch (" + e.GetType().Name + ")";
                return false;
            }
        }

        // The batch is over, whatever ended it: the members that loaded and are still in game join the stream, and a
        // seat claimed for a member that never loaded is released. The caller lifts the pause after this.
        void EndStream()
        {
            if (!streamBegan) return;
            streamBegan = false;
            var loaded = new List<int>();
            foreach (int id in loadedInBatch)
            {
                Joiner j = players[id];
                if (j != null && j.Phase == Phase.InGame) loaded.Add(id);
            }
            var released = new List<string>();
            for (int i = 0; i < claimedNew.Count; i++)
                if (!everLoaded[claimedBy[i]]) released.Add(claimedNew[i]);
            humanAtSnap = null;
            claimedNew.Clear(); claimedBy.Clear(); loadedInBatch.Clear();
            StreamCall("BatchEnded", () => stream.BatchEnded(loaded, released));
        }

        // Only a client exactly up to date may rejoin quickly (LastSentTo is the stream's current seq). A stream that
        // cannot say is a stream that may have been missed: full.
        bool ResultsMissed(int player, RejoinOffer o)
        {
            if (stream == null) return false;
            try { return o.LastResult != stream.LastSentTo(player); }
            catch (Exception e)
            {
                Log.Warn("join: the command stream threw in LastSentTo: " + e.GetType().Name);
                return true;
            }
        }

        bool StreamReady()
        {
            if (stream == null) return true;
            try { return stream.ReadyToSnapshot(); }
            catch (Exception e) { Warn(KindReady, "ReadyToSnapshot", e); return false; }
        }

        // False = the stream failed taking its position, so the snapshot cannot be trusted to match it.
        bool SnapshotTakenOnStream()
        {
            if (stream == null) return true;
            try { stream.SnapshotTaken(); return true; }
            catch (Exception e) { Warn(KindReady, "SnapshotTaken", e); return false; }
        }

        void StreamLinkReplaced(int player)
        {
            StreamCall("LinkReplaced", () => stream.LinkReplaced(player));
        }

        void StreamCall(string what, Action call)
        {
            if (stream == null) return;
            try { call(); }
            catch (Exception e) { Log.Warn("join: the command stream threw in " + what + ": " + e.GetType().Name); }
        }
    }
}
