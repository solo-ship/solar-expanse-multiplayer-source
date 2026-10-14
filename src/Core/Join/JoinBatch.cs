using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The host's join driver, part 3: the batch, from its start to its end (see JoinService.cs).
    public sealed partial class JoinService
    {
        void StartBatch(long now)
        {
            bool over;
            int overCode = JoinCodes.HostGameOver;
            try { over = world.GameOver; }
            catch (Exception e) { Warn(KindGameOver, "GameOver", e); over = true; overCode = JoinCodes.HostSnapshotFailed; }   // unknown: no batch
            if (over)
            {
                int[] refused = batcher.Start(now);
                batcher.End();
                Log.Info("join: no batch while the host's game is over (or cannot say); " + refused.Length + " refused");
                foreach (int id in refused) { Joiner j = players[id]; if (j != null) Leave(j, overCode, "the host's game is over", false, true, null, true); }
                return;
            }
            bool sound = RefreshPlayable();   // may remove a waiting joiner that holds the host's own agency
            int[] ids = batcher.Start(now);
            if (ids.Length == 0) return;
            if (!sound)
            {
                batcher.End();
                foreach (int id in ids) { Joiner j = players[id]; if (j != null) Leave(j, JoinCodes.HostSnapshotFailed, "the host's agency is held by another player", false, true, null, true); }
                return;
            }
            members = ids;
            batchNo++;
            batchStartMs = now;
            joinedCount = failedCount = 0;
            readyWaitMs = 0;
            endCode = JoinCodes.HostSnapshotFailed; endReason = null;
            foreach (int id in ids)
            {
                Joiner j = players[id];
                if (j == null) { batcher.Fail(id); continue; }
                j.Phase = Phase.Batch; j.Header = null; j.HeaderProblem = null; j.Sender = null; j.Stage = 0;
                // the flag only ever goes ON here: a pause still owed a lift stays on (and owed) when this call throws, so it is lifted
                // at last when an off succeeds
                if (SetJoiningSafe(id, true)) joining[id] = true;
                link.Broadcast(Note(NoticeKind.Joining, id, 0));
                // its batch has started: the joiner keeps its deadlines suspended until its header,
                // which follows the host's own preparation (NoProgressMs at most, inside the joiner's JoinerBatchHeaderMs)
                link.Send(id, new JoinWait { Ahead = 0 });
            }
            Log.Info("join: batch " + batchNo + " start: players " + List(ids) + "; paused");
            prep = Prep.WaitPause;
        }

        // The game is paused: the stream claims its seats and holds (once, in this frame), then the snapshot waits for the
        // world to be ready, at most SnapshotReadyMs. No stream, nothing to wait for.
        void BeginWait(long now)
        {
            string problem;
            if (!BeginStream(out problem)) { EndBatch(JoinCodes.HostSnapshotFailed, problem, now); return; }
            prep = Prep.WaitReady;
            readyStartMs = now;
            startWaits = 0;
            WaitReady(now);
        }

        // A stream or a world that says no is asked again every frame; one that throws counts as no. Past the
        // deadline the batch ends as the host's fault, like a game that will not pause. The wait is not counted
        // against the self-check (Poll).
        void WaitReady(long now)
        {
            bool started = IsStarted();
            if (!started) startWaits++;
            if (!started || !StreamReady())
            {
                if (now - readyStartMs >= JoinLimits.SnapshotReadyMs)
                {
                    string what = started ? "the world was not ready" : "the game's start set-up had not run";
                    EndBatch(JoinCodes.HostSnapshotFailed,
                        what + " to snapshot within " + JoinLimits.SnapshotReadyMs / 1000 + " s", now);
                }
                return;
            }
            readyWaitMs = now - readyStartMs;
            if (startWaits > 0)
                Log.Info("join: the snapshot waited " + startWaits + " frame(s) for the game's start set-up");
            TakeSnapshot(now);
        }

        void TakeSnapshot(long now)
        {
            gcBefore = GC.CollectionCount(0);
            snapMs = now;
            SnapshotData s;
            try { s = world.TakeSnapshot(); }
            catch (Exception e) { s = null; Log.Warn("join: taking the snapshot threw " + e.GetType().Name + ": " + TextSanitiser.OneLine(e.Message)); }
            // in the same frame: the stream's position is the snapshot's
            if (s != null && !SnapshotTakenOnStream()) { EndBatch(JoinCodes.HostSnapshotFailed, "the command stream failed at the snapshot", now); return; }
            string bad = SnapshotProblem(s);
            if (bad != null) { EndBatch(JoinCodes.HostSnapshotFailed, bad, now); return; }
            snap = s;
            var c = new SelfCheck(s.Compressed, s.Uncompressed);
            check = c;
            prep = Prep.Checking;
            try { bg.Run(c.Run); }
            catch (Exception e)   // ThreadBackground's queue full (InvalidOperationException), or a thread that would not start
            {
                EndBatch(JoinCodes.HostSnapshotFailed, "the self-check could not be started (" + e.GetType().Name + ": " + TextSanitiser.OneLine(e.Message) + ")", now);
            }
        }

        // null = the snapshot may be checked. The boundaries are the host's own: if they break the rules, every joiner would refuse them.
        static string SnapshotProblem(SnapshotData s)
        {
            if (s == null) return "the game gave no snapshot";
            if (s.Compressed == null || s.Compressed.Length < 1 || s.Compressed.Length > JoinLimits.MaxCompressed)
                return "the compressed snapshot is outside 1.." + JoinLimits.MaxCompressed + " bytes";
            if (s.Uncompressed < 1 || s.Uncompressed > JoinLimits.MaxUncompressed) return "the uncompressed size is outside 1.." + JoinLimits.MaxUncompressed;
            if (s.Header == null) return "the snapshot has no header";
            string b = BoundaryRules.Check(s.Header.DateTicks, s.Header.OriginTicks, BoundaryRules.Of(s.Header));
            return b == null ? null : "the host's own day boundaries break the rules: " + b;
        }

        void Poll(long now)
        {
            SelfCheck c = check;
            if (!c.Done)
            {
                if (now - batchStartMs - readyWaitMs >= JoinLimits.NoProgressMs) EndBatch(JoinCodes.HostSnapshotFailed, "the self-check took longer than 30 s", now);
                return;
            }
            int gc = GC.CollectionCount(0) - (int)gcBefore;
            string size = snap.Compressed.Length + " bytes (" + snap.Uncompressed + "), took " + (now - snapMs) + " ms, self-check ";
            if (c.Error != null)
            {
                Log.Warn("join: batch " + batchNo + " snapshot " + size + "REFUSED " + TextSanitiser.OneLine(c.RefusedType ?? c.Error) + ", gc +" + gc);
                EndBatch(JoinCodes.HostSnapshotFailed, "the host's own check refused its snapshot: " + TextSanitiser.OneLine(c.Error), now);
                return;
            }
            Log.Info("join: batch " + batchNo + " snapshot " + size + "ok, gc +" + gc);
            JoinHeader b = snap.Header;
            Log.Info(string.Format(CultureInfo.InvariantCulture, "join: boundaries date {0} origin {1} fraction {2} lastDay {3} nextDay {4} lastMonth {5} nextMonth {6} lastYear {7} nextYear {8}",
                b.DateTicks, b.OriginTicks, BoundaryRules.FractionText(BoundaryRules.DayFraction(b.DateTicks, b.OriginTicks)), b.LastDay, b.NextDay, b.LastMonth, b.NextMonth, b.LastYear, b.NextYear));
            SendHeaders(now);
            prep = Prep.Sending;
        }

        void SendHeaders(long now)
        {
            JoinHeader src = snap.Header;
            string rawVersion = src.SaveVersion;
            string version = TextSanitiser.Clean(ModInfo.ClipGameVersion(rawVersion), 32, "?");   // as the joiner's HeaderRules will
            StartConfig host;
            try { host = world.HostConfig; } catch (Exception) { host = null; }
            // Pass 1: every member's header. Each joiner's Seats and Config are taken together, and nothing is released before every
            // header is built, so no header names a seat another one lacks.
            foreach (int id in members)
            {
                Joiner j = players[id];
                if (j == null || j.Phase != Phase.Batch) continue;
                string bad = null;
                AgencySeat[] seats = null;
                StartConfig cfg = null;
                try { seats = ledger.Seats(nameOf); cfg = host == null ? null : ledger.ConfigFor(id, host); }
                catch (Exception e) { bad = "the start configuration failed (" + e.GetType().Name + ")"; }
                if (bad == null && cfg == null) bad = "there is no start configuration for this player (the host's config, its agency or the seat is unknown)";
                JoinHeader h = null;
                if (bad == null)
                {
                    h = new JoinHeader
                    {
                        Format = 1, SaveVersion = version, DateTicks = src.DateTicks, OriginTicks = src.OriginTicks,
                        LastDay = src.LastDay, NextDay = src.NextDay, LastMonth = src.LastMonth, NextMonth = src.NextMonth, LastYear = src.LastYear, NextYear = src.NextYear,
                        YourPlayer = id, Seats = seats, Config = cfg, HumanAgencies = stream == null ? NoHumans : humanAtSnap,
                        CompressedSize = snap.Compressed.Length, UncompressedSize = snap.Uncompressed, ChunkCount = ChunkMath.Count(snap.Compressed.Length),
                        Sha256 = check.Sha, PostLoad = src.PostLoad, SaveId = src.SaveId,
                    };
                    // the joiner's own rules and the wire's: a header either refuses is the host's fault, never the joiner's
                    bad = HeaderRules.Check(h, rawVersion, id) ?? h.Validate();
                }
                j.Header = bad == null ? h : null;
                j.HeaderProblem = bad;
            }
            // Pass 2: send, or end that joiner's join as the host's fault.
            foreach (int id in members)
            {
                Joiner j = players[id];
                if (j == null || j.Phase != Phase.Batch) continue;
                if (j.Header == null) { Leave(j, JoinCodes.HostSnapshotFailed, "its header failed the host's own rules: " + j.HeaderProblem, false, true, null, true); continue; }
                if (!link.Send(id, j.Header)) { Leave(j, JoinCodes.Aborted, "its link would not take its header (gone)", true, false, null, true); continue; }
                j.Sender = new SnapshotSender(snap.Compressed, now);
                batcher.Progress(id, now);   // its deadlines run from the moment it has something to do
                // the total the joiner allows itself, from this header on: both sides measure the same span
                batcher.HeaderSent(id, TotalOf(j), now);
            }
        }

        // Up to the window ahead of each joiner's last ack. A chunk the link will not take means the link is going: its Left, or the
        // joiner's deadline, ends that join.
        void SendChunks()
        {
            foreach (int id in members)
            {
                Joiner j = players[id];
                if (j == null || j.Phase != Phase.Batch || j.Sender == null) continue;
                SnapshotSender s = j.Sender;
                while (s.CanSend(JoinLimits.Window))
                {
                    int index = s.Next;
                    ArraySegment<byte> seg = s.Take();
                    var data = new byte[seg.Count];
                    Buffer.BlockCopy(seg.Array, seg.Offset, data, 0, seg.Count);
                    if (!link.Send(id, new SnapshotChunk { Index = index, Data = data })) break;
                }
            }
        }

        void Expire(long now)
        {
            int[] gone = batcher.Expired(now);
            foreach (int id in gone)
            {
                Joiner j = players[id];
                if (j != null && j.Phase == Phase.Batch)
                    Leave(j, JoinCodes.Deadline, "no progress for " + JoinLimits.NoProgressMs / 1000 + " s, or "
                        + TotalOf(j) / 1000 + " s in all", true, true, null, true);
            }
        }

        // A member's total deadline: JoinLimits.TotalMsFor its header's compressed size, as the joiner's own.
        static int TotalOf(Joiner j)
        {
            return j.Header != null ? JoinLimits.TotalMsFor(j.Header.CompressedSize) : JoinLimits.JoinerTotalMs;
        }

        void OnAck(Joiner j, SnapshotAck a, long now)
        {
            if (j.Phase != Phase.Batch || j.Sender == null) { Violation(j, "an ack out of turn"); return; }
            if (j.Sender.Ack(a.Next, now) != 0) { Violation(j, "an ack that goes backwards, does not advance, passes what was sent or is one too many"); return; }
            batcher.Progress(j.Id, now);
        }

        void OnStage(Joiner j, JoinStage st, long now)
        {
            if (st.Stage == JoinStageKind.Failed)
            {
                if (j.Phase == Phase.InGame || j.Phase == Phase.Dropped) { Violation(j, "a failed stage from a player in game"); return; }
                // QueueWait from a batch member counts, header or not (the re-check's ruling: else a rejoin key loops snapshots and
                // pauses for ever), except once per player per session: the honest race, its queue cap firing while JoinWait{Ahead = 0}
                // and its header are on their way. Outside a batch it never counts (Leave counts only members).
                bool counted = true;
                if (st.Code == JoinCodes.QueueWait && j.Phase == Phase.Batch && !queueWaitForgiven[j.Id]) { queueWaitForgiven[j.Id] = true; counted = false; }
                Leave(j, st.Code, "its own check failed", counted, false, null, true);
                return;
            }
            if (j.Phase != Phase.Batch || j.Sender == null)
            {
                if (st.Stage == JoinStageKind.Loaded) Violation(j, "Loaded from a player that was sent no snapshot");
                return;   // a stage before the header is no progress, and no harm
            }
            if (st.Stage == JoinStageKind.Loaded)
            {
                if (!j.Sender.Done) { Violation(j, "Loaded before every chunk was acked"); return; }
                LoadedAt(j, st.DateTicks);
                return;
            }
            batcher.Progress(j.Id, now);
            if (st.Stage != j.Stage)
            {
                j.Stage = st.Stage;
                Log.Info("join: player " + j.Id + " stage " + (st.Stage == JoinStageKind.Checking ? "Checking" : "Loading"));
            }
        }

        void LoadedAt(Joiner j, long dateTicks)
        {
            long diff = dateTicks - j.Header.DateTicks;
            if (diff > JoinLimits.DateToleranceTicks || diff < -JoinLimits.DateToleranceTicks)
            {
                Leave(j, JoinCodes.DateMismatch, "it loaded at " + dateTicks + ", the header says " + j.Header.DateTicks, true, true, null, true);
                return;
            }
            batcher.Loaded(j.Id);
            ledger.InGame(j.Id);
            everLoaded[j.Id] = true;
            loadedInBatch.Add(j.Id);
            j.Phase = Phase.InGame; j.Sender = null; j.Stage = 0;
            joinedCount++;
            Log.Info(string.Format(CultureInfo.InvariantCulture, "join: player {0} loaded at {1} (header {2}, diff {3:0.####} ms)", j.Id, dateTicks, j.Header.DateTicks, diff / 10000.0));
            // with Loaded, not at the batch's end: InGame turns true here, so the clock must mark the player fresh at the same moment
            // (its first SpeedRequest is then a restore, never a choice)
            PlayerInGameSafe(j.Id, ledger.AgencyOf(j.Id));
            link.Broadcast(Note(NoticeKind.Joined, j.Id, 0));
        }

        void EndBatch(int code, string reason, long now)
        {
            endCode = code; endReason = reason;
            Log.Warn("join: batch " + batchNo + " ended early: " + reason);
            FinishBatch(now);
        }

        // The pause lifts. Anyone the batch still held (a batch ended early) leaves with the end's code, as the host's fault.
        void FinishBatch(long now)
        {
            int[] dropped = batcher.End();
            int[] ended = members;
            prep = Prep.Idle;
            members = None;
            snap = null;
            check = null;
            // the stream's batch end (its steps 1-4) comes before any pause is lifted
            EndStream();
            foreach (int id in dropped)
            {
                Joiner j = players[id];
                if (j != null && j.Phase == Phase.Batch) Leave(j, endCode, endReason ?? "the batch ended", false, true, null, true);
            }
            foreach (int id in ended) LiftJoining(id);
            Log.Info("join: batch " + batchNo + " done: " + joinedCount + " joined, " + failedCount + " failed; pause lifted after " + (now - batchStartMs) + " ms");
        }

        // The flag goes off only once the game has taken the lift; a lift that threw is owed and tried again every update.
        void LiftJoining(int id)
        {
            if (!joining[id]) return;
            if (SetJoiningSafe(id, false)) joining[id] = false;
            else liftOwed = true;
        }

        // A pause still on for a player the running batch no longer holds (its lift threw): try again. A member of the running batch
        // that is still joining or has loaded keeps its pause until the batch ends.
        void RetryLifts()
        {
            liftOwed = false;
            for (int id = 1; id < joining.Length; id++)
            {
                if (!joining[id]) continue;
                Joiner j = players[id];
                bool held = prep != Prep.Idle && Array.IndexOf(members, id) >= 0 && j != null && (j.Phase == Phase.Batch || j.Phase == Phase.InGame);
                if (!held) LiftJoining(id);
            }
        }
    }
}
