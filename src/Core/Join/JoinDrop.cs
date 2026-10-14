using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The host's join driver, part 4: drops, rejoins and leaving (see JoinService.cs).
    public sealed partial class JoinService
    {
        void LeftFor(Joiner j, long now)
        {
            if (j.Phase == Phase.InGame) { RecordDrop(j, now); return; }
            if (j.Phase == Phase.Dropped) return;
            if (j.HaveDrop) { j.Phase = Phase.Dropped; return; }   // the rejoin link closed before its offer: the first drop's record stands
            Leave(j, JoinCodes.Aborted, "its link closed", true, false, null, false);
        }

        void RecordDrop(Joiner j, long now)
        {
            batcher.Fail(j.Id);   // a loaded member of a batch still running leaves it, so a full rejoin can queue again
            j.Phase = Phase.Dropped;
            j.HaveDrop = true; j.Watching = true;
            j.Moved = j.Reloaded = j.SettledTried = false;
            j.Settled = null;
            j.DropMs = now;
            if (!Ticks(out j.DropTicks)) j.Moved = true;
            if (!Loads(out j.DropLoads)) j.Reloaded = true;
            Log.Info("join: player " + j.Id + " dropped at " + j.DropTicks + " (in game)");
            if (IsPaused()) TakeSettled(j);
        }

        // Every frame while a drop is recorded, until the quick window has passed (the answer is full after it whatever this sees):
        // game time that moved, a host load, and the settled print on the first paused frame.
        void Watch(Joiner j, long now)
        {
            long t; int n;
            if (!j.Moved && (!Ticks(out t) || t != j.DropTicks)) { j.Moved = true; Log.Info("join: drop of player " + j.Id + ": game time moved"); }
            if (!j.Reloaded && (!Loads(out n) || n != j.DropLoads)) { j.Reloaded = true; Log.Info("join: drop of player " + j.Id + ": host save loaded"); }
            if (!j.SettledTried && IsPaused()) TakeSettled(j);
            if (now - j.DropMs >= JoinLimits.QuickRejoinMaxDropMs) j.Watching = false;
        }

        void TakeSettled(Joiner j)
        {
            j.SettledTried = true;
            j.Settled = Print();
            Log.Info("join: drop of player " + j.Id + ": settled print " + (j.Settled != null ? "taken" : "FAILED (a rejoin will be full)"));
        }

        Fingerprint Print()
        {
            try { return world.TakeFingerprint(); }
            catch (Exception e) { Log.Warn("join: the fingerprint threw " + e.GetType().Name); return null; }
        }

        static void ClearDrop(Joiner j)
        {
            j.HaveDrop = j.Watching = false;
            j.Settled = null;
        }

        void OnOffer(Joiner j, RejoinOffer o, long now)
        {
            if (j.Phase != Phase.Summary || !j.MayOffer) { Violation(j, "a rejoin offer out of turn"); return; }
            // WasInGame from the session's own record, so a loaded player whose drop record is gone answers NoDropRecord
            var f = new RejoinFacts { WasInGame = everLoaded[j.Id], HaveDrop = j.HaveDrop, DropMs = j.DropMs, NowMs = j.ConfirmedMs };
            Fingerprint hostNow = null;
            if (j.HaveDrop)
            {
                long t; int n;
                if (!Ticks(out t) || t != j.DropTicks) j.Moved = true;
                if (!Loads(out n) || n != j.DropLoads) j.Reloaded = true;
                if (IsPaused()) hostNow = Print();
            }
            f.TimeMoved = j.Moved;
            f.HostReloaded = j.Reloaded;
            string changed = null;
            f.HostSelfCheckHeld = j.Settled != null && hostNow != null && FingerprintRules.Same(j.Settled, hostNow, out changed);
            f.ClientInSession = o.InSession;
            f.ClientSelfCheckHeld = o.SelfCheckHeld;
            f.HavePrints = FingerprintRules.WellFormed(j.Settled) && FingerprintRules.WellFormed(hostNow) && FingerprintRules.WellFormed(o.Print);
            f.HostNextDay = NextDaySafe();   // 0 when unknown: BoundaryDiffers, the safe answer
            f.ClientNextDay = o.NextDay;
            f.Print = f.HavePrints ? FingerprintRules.Compare(hostNow, o.Print, false) : null;
            f.ResultsMissed = ResultsMissed(j.Id, o);
            int code = RejoinRules.Decide(f);
            ClearDrop(j);
            j.MayOffer = false;
            j.HeldClaim = null;
            if (code != RejoinCodes.Quick && TryResume(j, o, code, now)) return;
            if (code == RejoinCodes.Quick)
            {
                link.Send(j.Id, new RejoinResult { Quick = true, Code = RejoinCodes.Quick });
                j.Phase = Phase.InGame;
                long hostTicks;
                long gap = Ticks(out hostTicks) ? o.DateTicks - hostTicks : 0;
                Log.Info("join: player " + j.Id + " rejoin quick (drop " + (f.NowMs - f.DropMs) + " ms, date gap " + gap + ", blocks " + (o.Print.Blocks.Length - f.Print.DiscreteDiffers)
                    + " same, rounded differs " + f.Print.RoundedDiffers + ", exact differs " + f.Print.ExactDiffers + ", self-checks held)");
                PlayerInGameSafe(j.Id, ledger.AgencyOf(j.Id));   // fresh again: its first speed message is a restore, so the drop hold stays
                link.Broadcast(Note(NoticeKind.Rejoined, j.Id, 0));
                // after the in-game mark, so its AgencyState shows the agency present
                StreamCall("QuickRejoined", () => stream.QuickRejoined(j.Id));
                return;
            }
            link.Send(j.Id, new RejoinResult { Quick = false, Code = code });
            Log.Info("join: player " + j.Id + " rejoin full (" + RejoinName(code) + ": " + Why(code, f, changed) + ")");
            j.Phase = Phase.Summary; j.PhaseMs = now; j.AfterFull = true;
        }

        // M7 (DD-W12): where M4/M5's rules would answer a full rejoin, the stream may replay what the player missed. The
        // answer is Resume (the stream continues at ResumeNextSeq, the host's clock is paused at ResumePausedTicks); the
        // player is in game again at once, and the stream's replay and catch-up pause start after the answer.
        bool TryResume(Joiner j, RejoinOffer o, int declined, long now)
        {
            if (stream == null || !ResumeRules.Eligible(declined)) return false;
            uint next; long paused; string why;
            try
            {
                if (!stream.CanResume(j.Id, o.LastResult, o.NextDay, declined, out next, out paused, out why))
                {
                    Log.Info("join: player " + j.Id + " cannot resume (" + why + ")");
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Warn("join: the command stream threw in CanResume: " + e.GetType().Name);
                return false;
            }
            if (!link.Send(j.Id, new RejoinResult { Quick = false, Code = RejoinCodes.Resume, ResumeNextSeq = next,
                ResumePausedTicks = paused }))
            {
                Log.Warn("join: player " + j.Id + ": the resume answer could not be sent");
                return false;
            }
            j.Phase = Phase.InGame;
            Log.Info("join: player " + j.Id + " rejoin resumes from #" + next + " (" + RejoinName(declined)
                + " would have been a full rejoin)");
            PlayerInGameSafe(j.Id, ledger.AgencyOf(j.Id));
            link.Broadcast(Note(NoticeKind.Rejoined, j.Id, 0));
            uint last = o.LastResult; long clientNext = o.NextDay;
            StreamCall("StartResume", () => stream.StartResume(j.Id, last, clientNext));
            return true;
        }

        static string RejoinName(int code)
        {
            switch (code)
            {
                case RejoinCodes.TimeMoved: return "TimeMoved";
                case RejoinCodes.NotInSession: return "NotInSession";
                case RejoinCodes.DropTooLong: return "DropTooLong";
                case RejoinCodes.BoundaryDiffers: return "BoundaryDiffers";
                case RejoinCodes.FingerprintDiffers: return "FingerprintDiffers";
                case RejoinCodes.NotInGameBefore: return "NotInGameBefore";
                case RejoinCodes.NoDropRecord: return "NoDropRecord";
                case RejoinCodes.BadOffer: return "BadOffer";
                case RejoinCodes.HostReloaded: return "HostReloaded";
                case RejoinCodes.ClientChangedDuringDrop: return "ClientChangedDuringDrop";
                case RejoinCodes.HostChangedDuringDrop: return "HostChangedDuringDrop";
                case RejoinCodes.NoPrint: return "NoPrint";
                case RejoinCodes.Resume: return "Resume";
                case RejoinCodes.CommandsMissed: return "CommandsMissed";
                default: return "code " + code;
            }
        }

        static string Why(int code, RejoinFacts f, string changed)
        {
            switch (code)
            {
                case RejoinCodes.DropTooLong: return "drop " + (f.NowMs - f.DropMs) + " ms";
                case RejoinCodes.HostChangedDuringDrop: return changed ?? "the host's print changed";
                case RejoinCodes.BoundaryDiffers: return "next day host " + f.HostNextDay + ", client " + f.ClientNextDay;
                case RejoinCodes.CommandsMissed: return "the client has not applied every command result sent to it";
                case RejoinCodes.FingerprintDiffers: return f.Print == null ? "no cross-check" : f.Print.Level + " in area " + f.Print.FirstArea + ", agency " + f.Print.FirstAgency;
                default: return "see the drop lines above";
            }
        }

        // ---- leaving ----

        // A protocol breach: a join message out of turn or out of order. The joiner's fault.
        void Violation(Joiner j, string what) { Leave(j, JoinCodes.Protocol, what, true, true, null, true); }

        // The player leaves the join path (or the game, for a player in game removed by the host). Its seat is released unless it has
        // loaded (the ledger keeps a loaded seat for the session); its joining pause is lifted; a batch member is reported to everyone
        // (Notice(CouldNotJoin)) and, when the fault is its own, counted. refuse sends the JoinRefused (cut to fit, never dropped
        // silently); kick closes the link (not when it is already closed).
        void Leave(Joiner j, int code, string reason, bool joinerFault, bool refuse, string[] lines, bool kick)
        {
            int id = j.Id;
            bool member = j.Phase == Phase.Batch;
            bool inStream = j.Phase == Phase.InGame || j.Phase == Phase.Dropped;   // it may have been a stream recipient
            players[id] = null;
            batcher.Fail(id);
            ledger.Release(id);
            LiftJoining(id);
            // the link's own Left finds no record any more, so the stream hears of it here
            if (inStream) StreamCall("Left", () => stream.Left(id));
            if (member)
            {
                failedCount++;
                link.Broadcast(Note(NoticeKind.CouldNotJoin, id, code));
                if (joinerFault && ++failures[id] >= JoinLimits.MaxFailedJoins)
                {
                    reason = reason + "; that is " + failures[id] + " failed joins this session, so its keys are revoked";
                    code = JoinCodes.TooManyFailures;
                    refuse = kick = true;
                    lines = null;
                }
            }
            Log.Info("join: player " + id + " could not join: " + code + " " + TextSanitiser.OneLine(reason));
            if (refuse) SendRefusal(id, code, lines);
            if (kick) KickSafe(id, code);
        }

        // The refusal with its report (or the code's own line). Text from the other side is already sanitised; the lines are cut to the
        // JSON byte budget (TextBudget, escaping included). If the link still will not take it, it is cut down further, then sent with
        // the code alone; only a link that takes nothing at all loses it, and that is logged.
        void SendRefusal(int player, int code, string[] lines)
        {
            string[] all = lines != null && lines.Length > 0 ? lines : new[] { JoinText.Refused(code) };
            int budget = JoinLimits.ReportMaxBytes;
            for (;;)
            {
                var m = new JoinRefused { Code = code, Lines = TextBudget.FitLines(all, JoinLimits.MaxReportLines, JoinLimits.MaxReportLineChars, budget) };
                if (link.Send(player, m))
                {
                    if (budget < JoinLimits.ReportMaxBytes) Log.Warn("join: player " + player + ": the report was cut to " + m.Lines.Length + " lines to fit the link");
                    return;
                }
                if (budget == 0) break;
                budget = budget / 2 >= MinReportBudget ? budget / 2 : 0;   // the last try has no lines: the code alone
            }
            Log.Warn("join: player " + player + ": the refusal (" + code + ") could not be sent");
        }
    }
}
