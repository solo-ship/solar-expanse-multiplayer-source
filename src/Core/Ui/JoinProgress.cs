using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The stage a join waits on (JoinerFlow.Step), for the progress view. Waiting: connected or reconnecting, before
    // any of the join's own stages.
    public enum JoinStep { None, Waiting, Queued, Checking, Receiving, Loading }

    // Why the progress view is up. The host's start: its world loading, then the start set joining. A joiner's start
    // (or a late join): the host's world loading, then its own join. An automatic rejoin: its game fell out of step
    // with the host's, its link dropped, or the host's game moved on and its world is reloaded.
    public enum ProgressCause { None, HostLoading, PlayersJoining, WaitingForHost, Joining, OutOfStep, Dropped, Reload }

    // What the progress view shows (SessionView.Progress); the dialog is JoinProgress.Dialog.
    public sealed class JoinProgressFacts
    {
        public ProgressCause Cause;
        public JoinStep Step;
        public int Pct = -1, Ahead, Done, Total, ReloadCode;

        public bool Rejoin
        {
            get
            {
                return Cause == ProgressCause.OutOfStep || Cause == ProgressCause.Dropped ||
                       Cause == ProgressCause.Reload;
            }
        }
    }

    // A joiner's state as the progress view needs it (filled from SessionClient, its lobby and JoinerFlow).
    public sealed class JoinerProgressState
    {
        public bool Holding;                          // waiting in the host's lobby, not let in yet
        public SessionPhase Room = SessionPhase.Idle; // the host's lobby phase from its roster; Idle with none
        public int MyState = -1;                      // this player's roster state (PlayerRow); -1 unknown
        public bool LinkUp, Playing, Dropped, OutOfStep;
        public JoinStep Step;
        public int Pct = -1, Ahead, ReloadCode;
    }

    // The host's side of a start: from its Start until every member of the start set is in the game. A late joiner
    // later in the game shows in the notices and the players' rows, not here.
    public sealed class HostStartProgress
    {
        bool on;

        public JoinProgressFacts Next(SessionPhase room, IList<PlayerRow> rows)
        {
            if (room == SessionPhase.Starting || room == SessionPhase.Loading)
            {
                on = true;
                return new JoinProgressFacts { Cause = ProgressCause.HostLoading };
            }
            if (!on || room != SessionPhase.InGame) { on = false; return null; }
            int done = 0, joining = 0;
            if (rows != null)
                foreach (PlayerRow r in rows)
                {
                    if (r == null || r.Host) continue;
                    if (r.State == PlayerRow.Joining) joining++;
                    else if (r.State == PlayerRow.InGame) done++;
                }
            if (joining == 0) { on = false; return null; }
            return new JoinProgressFacts { Cause = ProgressCause.PlayersJoining, Done = done, Total = done + joining };
        }
    }

    public static class JoinProgress
    {
        // The joiner's progress, or null while there is nothing to wait for (the lobby before Start, the game itself).
        public static JoinProgressFacts ForJoiner(JoinerProgressState s)
        {
            if (s == null || s.Playing) return null;
            ProgressCause rejoin = s.OutOfStep ? ProgressCause.OutOfStep
                : s.ReloadCode > 0 ? ProgressCause.Reload
                : s.Dropped ? ProgressCause.Dropped : ProgressCause.None;
            if (rejoin != ProgressCause.None)
                return new JoinProgressFacts
                {
                    Cause = rejoin, Step = s.LinkUp && s.Step != JoinStep.None ? s.Step : JoinStep.Waiting,
                    Pct = s.Pct, Ahead = s.Ahead, ReloadCode = s.ReloadCode,
                };
            if (s.Holding)
            {
                if (s.Room == SessionPhase.Starting || s.Room == SessionPhase.Loading)
                    return new JoinProgressFacts { Cause = ProgressCause.WaitingForHost };
                // the host's world is up and this player is in the start set: let in any moment now
                if (s.Room == SessionPhase.InGame && s.MyState == PlayerRow.Joining)
                    return new JoinProgressFacts { Cause = ProgressCause.Joining, Step = JoinStep.Waiting };
                return null;
            }
            if (!s.LinkUp && s.Step == JoinStep.None) return null;
            return new JoinProgressFacts
            {
                Cause = ProgressCause.Joining, Step = s.Step == JoinStep.None ? JoinStep.Waiting : s.Step,
                Pct = s.Pct, Ahead = s.Ahead,
            };
        }

        // The one progress view, as a dialog that closes by itself once its cause is gone. Its Id is the start's or
        // the rejoin's, so a stage that moves on refreshes it in place and a player who hid it is not shown it again
        // for the same start or rejoin. The host may hide it; a joiner may leave the session from it.
        public static DialogSpec Dialog(JoinProgressFacts f, bool isHost, Capability caps)
        {
            if (f == null || f.Cause == ProgressCause.None) return null;
            var d = new DialogSpec
            {
                Kind = DialogKind.Progress, Id = f.Rejoin ? "progress rejoin" : "progress start", Overlay = true,
                TitleKey = f.Rejoin ? "dialog.progress.title.rejoin"
                    : f.Cause == ProgressCause.Joining ? "dialog.progress.title.join" : "dialog.progress.title.start",
            };
            switch (f.Cause)
            {
                case ProgressCause.HostLoading: d.Lines.Add(DialogLine.Of("dialog.progress.hostLoading")); break;
                case ProgressCause.PlayersJoining:
                    d.Lines.Add(DialogLine.Of("dialog.progress.playersJoining", f.Done, f.Total));
                    break;
                case ProgressCause.WaitingForHost: d.Lines.Add(DialogLine.Of("dialog.progress.waitingForHost")); break;
                case ProgressCause.Joining: d.Lines.Add(StepLine(f)); break;
                case ProgressCause.OutOfStep:
                    // the reason in the words the commands' out-of-step line already uses
                    d.Lines.Add(new DialogLine { Key = "cmd.outOfStep", Warning = true });
                    d.Lines.Add(StepLine(f));
                    break;
                case ProgressCause.Dropped:
                    d.Lines.Add(new DialogLine { Key = "dialog.progress.dropped", Warning = true });
                    d.Lines.Add(StepLine(f));
                    break;
                case ProgressCause.Reload:
                    string key = "join.rejoin.reload." + f.ReloadCode;
                    d.Lines.Add(DialogLine.Of(UiText.Has(key) ? key : "join.rejoin.reload.default"));
                    d.Lines.Add(StepLine(f));
                    break;
            }
            if (isHost)
            {
                d.OkKey = "dialog.progress.hide";
                d.OkAction = DialogAction.Close;
            }
            else if (CapabilityRules.Has(caps, Capability.LeaveNoPause))
            {
                d.OkKey = "dialog.leave.button";
                d.OkAction = DialogAction.Leave;
            }
            return d;
        }

        public static DialogLine StepLine(JoinProgressFacts f)
        {
            switch (f.Step)
            {
                case JoinStep.Queued:
                    return DialogLine.Of(f.Ahead == 1 ? "join.waiting.one" : "join.waiting.many", f.Ahead);
                case JoinStep.Checking: return DialogLine.Of("join.status.checking");
                case JoinStep.Receiving:
                    return f.Pct >= 0 ? DialogLine.Of("dialog.progress.receiving", f.Pct)
                                      : DialogLine.Of("dialog.progress.receivingNoPct");
                case JoinStep.Loading: return DialogLine.Of("join.status.loading");
                default: return DialogLine.Of("dialog.progress.waiting");
            }
        }
    }
}
