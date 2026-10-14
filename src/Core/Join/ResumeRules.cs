namespace SolarExpanseMultiplayer
{
    // What the host knows when a dropped player comes back and M4/M5's rules would answer a full rejoin (M7 DD-W12).
    public struct ResumeFacts
    {
        public int Declined;          // the code RejoinRules.Decide gave
        public bool Enabled;          // the shared world is on, no -semp-no-resume, a world side to check against
        public bool BatchRunning;     // a join batch holds the stream
        public bool SameStream;       // no reset to every recipient since the player dropped (a drop is not one)
        public bool Covered;          // the replay buffer still holds everything after the client's last applied entry
        public bool DaysKnown;        // both days could be keyed
        public long GapDays;          // the host's day minus the client's
        public long ReplayBytes;      // what the replay would send
    }

    // Resume or the full rejoin. Pure: the facts come from CommandService.
    public static class ResumeRules
    {
        // The reasons M4/M5 give for a rejoin that is no good because the world moved on. The others (never in the
        // game, no drop record, the host reloaded, the client is not in a session, a bad offer) cannot be replayed.
        public static bool Eligible(int code)
        {
            switch (code)
            {
                case RejoinCodes.TimeMoved: case RejoinCodes.DropTooLong: case RejoinCodes.BoundaryDiffers:
                case RejoinCodes.FingerprintDiffers: case RejoinCodes.HostChangedDuringDrop:
                case RejoinCodes.ClientChangedDuringDrop: case RejoinCodes.NoPrint: case RejoinCodes.CommandsMissed:
                    return true;
                default: return false;
            }
        }

        // The estimated time: the days simulated at CatchUpSpeed plus the replay through the host's output budget.
        public static long EstimateMs(long gapDays, long replayBytes)
        {
            long sim = gapDays * 1000L / ActionLimits.CatchUpDaysPerSecond;
            long send = replayBytes * 1000L / ActionLimits.HostOutBytesPerSecond;
            return sim + send;
        }

        // True: answer RejoinCodes.Resume. False: `why` says why the full rejoin stands.
        public static bool Decide(ResumeFacts f, out string why)
        {
            why = null;
            if (!Eligible(f.Declined)) why = "the rejoin's reason (" + f.Declined + ") cannot be replayed";
            else if (!f.Enabled) why = "resume is off";
            else if (f.BatchRunning) why = "a join batch is running";
            else if (!f.SameStream) why = "the stream was reset since the drop";
            else if (!f.Covered) why = "the replay buffer no longer holds what it missed";
            else if (!f.DaysKnown) why = "a day has no key";
            else if (f.GapDays < 0) why = "the client is ahead of the host";
            else if (f.GapDays > ActionLimits.ResumeMaxDays) why = "the gap is " + f.GapDays + " days";
            else
            {
                long ms = EstimateMs(f.GapDays, f.ReplayBytes);
                if (ms >= ActionLimits.ResumeDeadlineMs / 2L)
                    why = "the catch-up would take about " + ms + " ms of the " + ActionLimits.ResumeDeadlineMs
                        + " ms allowed";
            }
            return why == null;
        }
    }
}
