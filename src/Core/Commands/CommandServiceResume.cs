using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host's side of a resume after a drop (M7 DD-W12, Task 6): the player comes back among the recipients at its
    // next seq with no AgencyState, the buffer's missed entries are replayed at the pace the client reports it applies
    // them, and it is released only once its world matches the host's. The decision (ResumeRules), the pause and the
    // wiring are Task 14's; this is the driver's half.
    public sealed partial class CommandService
    {
        sealed class ResumeState
        {
            public int Player;
            public uint FromSeq;
            public long ClientDay, HostDay, StartMs;
            public long ClientDayNow;                     // the latest day the client reported
            public uint LastSent, LastApplied;            // entries moved to its queue / the client says it applied
            public readonly Queue<Item> Pending = new Queue<Item>();   // the replay, then what is raised meanwhile
            public bool CheckSent;
            public uint CheckSeq;
            public Fingerprint HostPrint;
        }

        readonly ReplayBuffer replay = new ReplayBuffer(ActionLimits.ReplayDays, ActionLimits.ReplayBytes);
        readonly List<ResumeState> resumes = new List<ResumeState>();
        bool strayProgressLogged;

        // Ended: true = released (its world matched), false = failed or ended by a second drop. Task 14 lifts the
        // CatchingUp pause on it.
        public event Action<int, bool> ResumeFinished;
        // A replay begins for this player: the host pauses everyone with PauseWhy.CatchingUp until ResumeFinished.
        public event Action<int> ResumeStarted;

        // false: -semp-no-resume, or a session with no world side (every drop is M4/M5's rule)
        public bool ResumeEnabled = true;

        // DD-W12's decision, from the host's facts. No side effects. clientNextTicks is the client's nextDay.
        public bool CanResume(int player, uint lastResult, long clientNextTicks, int declined, out uint nextSeq,
            out long pausedTicks, out string why)
        {
            nextSeq = lastResult + 1;
            pausedTicks = 0;
            var f = new ResumeFacts { Declined = declined, Enabled = ResumeEnabled && WorldSide != null,
                BatchRunning = held };
            Player p;
            f.SameStream = byId.TryGetValue(player, out p) && p.DropEpoch == epoch && !p.Recipient;
            f.Covered = replay.Covers(lastResult);
            long hostDay, clientDay = 0;
            f.DaysKnown = StampDay(out hostDay) && DayKey.TryCurrent(clientNextTicks, world.OriginTicks, out clientDay);
            if (f.DaysKnown) f.GapDays = hostDay - clientDay;
            f.ReplayBytes = replay.BytesFrom(lastResult);
            try { pausedTicks = world.GameTicks; }
            catch (Exception e) { Log.Warn("resume: the game's ticks threw: " + e.GetType().Name); f.DaysKnown = false; }
            return ResumeRules.Decide(f, out why);
        }

        // After the Resume answer went out: the replay from the client's last applied entry. The client's last closed
        // day is the one before the day it is in.
        public void StartResume(int player, uint lastResult, long clientNextTicks)
        {
            long day;
            if (!DayKey.TryCurrent(clientNextTicks, world.OriginTicks, out day)) day = 1;
            Player p = PlayerOf(player);
            p.DropEpoch = -1;
            Resumed(player, lastResult, day - 1);
        }

        public ReplayBuffer Replay { get { return replay; } }

        public bool Resuming(int player)
        {
            WorldPlayer w;
            return worldPlayers.TryGetValue(player, out w) && w.Resume != null;
        }

        static long ItemDay(Item it)
        {
            var r = it.M as ActionResult;
            if (r != null) return r.Day;
            var c = it.M as DayClosed;
            return c != null ? c.Day : long.MinValue;
        }

        void KeepForReplay(Item it)
        {
            if (it.M is ActionResult || it.M is DayClosed) replay.Add(it.M, it.Bytes, ItemDay(it));
        }

        // A player in a resume gets what is sent after the replay, in order, behind it.
        bool QueueForResume(Player p, Item it)
        {
            WorldPlayer w;
            if (!worldPlayers.TryGetValue(p.Id, out w) || w.Resume == null) return false;
            w.Resume.Pending.Enqueue(it);
            if (w.Resume.Pending.Count > MaxOutQueue)
                EndResume(w.Resume, false, "more than " + MaxOutQueue + " messages wait for it", true);
            return true;
        }

        public void Resumed(int player, uint fromSeq) { Resumed(player, fromSeq, 0); }

        // After LinkReplaced: the player is a recipient again from fromSeq + 1. clientDay is the day of its last closed
        // day (the client's own ResultOrder.TrimToApplied decides which closes it needs again).
        public void Resumed(int player, uint fromSeq, long clientDay)
        {
            Player p = PlayerOf(player);
            WorldPlayer w = WorldOf(player);
            if (w.Resume != null) EndResume(w.Resume, false, "a second resume", false);
            Action<int> started = ResumeStarted;
            if (started != null) started(player);
            long hostDay;
            if (!replay.Covers(fromSeq) || !StampDay(out hostDay))
            {
                Log.Info("resume: player " + player + " failed (the buffer does not hold #" + fromSeq + " on)");
                FailAlone(player);
                return;
            }
            var s = new ResumeState { Player = player, FromSeq = fromSeq, ClientDay = clientDay, HostDay = hostDay,
                StartMs = nowMs(), ClientDayNow = clientDay, LastSent = fromSeq, LastApplied = fromSeq };
            // Every close after entry fromSeq goes again, one the client already crossed too: ResumeFrom took its record of
            // it back with the queued entries, so the stream would otherwise wait for a close it never gets.
            foreach (Message m in replay.From(fromSeq))
            {
                string why;
                Item it = Encode(m, out why);
                if (it == null)
                {
                    Log.Info("resume: player " + player + " failed (the buffer holds an unsendable message: " + why + ")");
                    FailAlone(player);
                    return;
                }
                var r = m as ActionResult;
                if (r != null) it.Seq = r.Seq;
                s.Pending.Enqueue(it);
            }
            Remove(p);
            p.Recipient = true;
            w.Resume = s;
            resumes.Add(s);
            Log.Info("resume: player " + player + " replaying " + s.Pending.Count + " messages from #" + fromSeq);
            FeedResume(s);
            Pump(nowMs());
        }

        void FailAlone(int player)
        {
            SendSmall(player, new StreamReset { Code = StreamCodes.HostFault });
            Action<int, bool> h = ResumeFinished;
            if (h != null) h(player, false);
        }

        // The replay moves to the player's queue while it has applied what it was sent (under ResumeAheadEntries
        // entries in flight) and the next message is no more than two days past the day it reports.
        void FeedResume(ResumeState s)
        {
            Player p;
            if (!byId.TryGetValue(s.Player, out p)) return;
            while (s.Pending.Count > 0)
            {
                Item it = s.Pending.Peek();
                bool entry = it.M is ActionResult;
                if (entry && s.LastSent - s.LastApplied >= ActionLimits.ResumeAheadEntries) break;
                if (ItemDay(it) > s.ClientDayNow + 2) break;
                s.Pending.Dequeue();
                p.Out.Enqueue(it);
                if (entry) s.LastSent = it.Seq;
            }
        }

        // The client is at the host's paused date and has applied everything it was sent: the host takes its own
        // fingerprint now, at its last seq, and asks for the client's at that seq.
        void TryCheck(ResumeState s)
        {
            if (s.CheckSent || s.Pending.Count > 0 || s.LastApplied != s.LastSent || s.ClientDayNow < s.HostDay) return;
            Player p;
            if (!byId.TryGetValue(s.Player, out p)) return;
            Fingerprint print = null;
            try { print = WorldSide != null ? WorldSide.Print() : null; }
            catch (Exception e) { Log.Warn("resume: the host's fingerprint threw: " + e.GetType().Name); }
            if (print == null || seq == 0) { EndResume(s, false, "no fingerprint to check against", true); return; }
            string why;
            Item it = Encode(new ResumeCheck { Seq = seq, PausedTicks = world.GameTicks }, out why);
            if (it == null) { EndResume(s, false, "the check is not sendable: " + why, true); return; }
            s.HostPrint = print;
            s.CheckSeq = seq;
            s.CheckSent = true;
            p.Out.Enqueue(it);
            Pump(nowMs());
        }

        // The client reports its day and what it applied (and, answering the check, its fingerprint). Outside the
        // sender's own resume it is ignored. A report that cannot be true fails the resume, not the link.
        public void ResumeProgressFrom(int player, ResumeProgress m)
        {
            WorldPlayer w;
            if (!worldPlayers.TryGetValue(player, out w) || w.Resume == null)
            {
                if (!strayProgressLogged)
                {
                    Log.Info("resume: progress from player " + player + " outside a resume ignored");
                    strayProgressLogged = true;
                }
                return;
            }
            ResumeState s = w.Resume;
            string bad = m == null ? "empty progress" : m.Validate();
            if (bad == null && m.LastApplied > s.LastSent)
                bad = "it applied #" + m.LastApplied + " but was sent #" + s.LastSent;
            if (bad == null && (m.Day < s.ClientDay || m.Day > s.HostDay + 1))
                bad = "day " + m.Day + " is outside " + s.ClientDay + ".." + s.HostDay;
            if (bad != null) { EndResume(s, false, bad, true); return; }
            if (m.LastApplied > s.LastApplied) s.LastApplied = m.LastApplied;
            if (m.Day > s.ClientDayNow) s.ClientDayNow = m.Day;
            if (m.Done)
            {
                if (!s.CheckSent || m.LastApplied < s.CheckSeq)
                {
                    EndResume(s, false, "it answered a check before it applied through it", true);
                    return;
                }
                FingerprintDiff d = FingerprintRules.Compare(s.HostPrint, m.Print, false);
                if (d.Match) EndResume(s, true, null, false);
                else EndResume(s, false, "its world differs (" + d.Level + ")", true);
                return;
            }
            FeedResume(s);
            TryCheck(s);
            Pump(nowMs());
        }

        // The resume is over for this player. Released: it stays a recipient. Failed: it leaves the stream and, when
        // its link is still the one that resumed, is told to rejoin fully.
        public void ResumeEnded(int player, bool released)
        {
            WorldPlayer w;
            if (worldPlayers.TryGetValue(player, out w) && w.Resume != null)
                EndResume(w.Resume, released, released ? null : "ended by the caller", !released);
        }

        void EndResume(ResumeState s, bool released, string why, bool resetPlayer)
        {
            WorldPlayer w;
            if (worldPlayers.TryGetValue(s.Player, out w) && ReferenceEquals(w.Resume, s)) w.Resume = null;
            resumes.Remove(s);
            Player p;
            byId.TryGetValue(s.Player, out p);
            if (released)
            {
                Log.Info("resume: player " + s.Player + " released after " + (nowMs() - s.StartMs) + " ms");
                if (p != null) while (s.Pending.Count > 0) p.Out.Enqueue(s.Pending.Dequeue());
            }
            else
            {
                Log.Info("resume: player " + s.Player + " failed (" + why + ")");
                if (p != null) Remove(p);
                if (resetPlayer) SendSmall(s.Player, new StreamReset { Code = StreamCodes.HostFault });
            }
            Action<int, bool> h = ResumeFinished;
            if (h != null) h(s.Player, released);
        }
    }
}
