using System;
using System.IO;

namespace SolarExpanseMultiplayer
{
    // "At most one line every interval seconds; the ones held back are counted and reported on the next line." Times are
    // plain seconds from any monotonic source (the caller's), so it is tested without a clock.
    public sealed class LineThrottle
    {
        readonly double interval;
        double last = double.NegativeInfinity;
        int held;

        public LineThrottle(double intervalSeconds) { interval = intervalSeconds; }

        // True when a line may be written now; heldBack is how many were refused since the last line that was written.
        public bool Allow(double now, out int heldBack)
        {
            if (now - last >= interval)
            {
                last = now;
                heldBack = held;
                held = 0;
                return true;
            }
            held++;
            heldBack = 0;
            return false;
        }
    }

    public enum RetryStep { None, Retry, GiveUp }

    // A bounded retry schedule: after Arm, Poll says Retry every `every` seconds until `total` seconds have passed, then
    // GiveUp once and the schedule is over (Arm starts a fresh one).
    public sealed class RetryWindow
    {
        readonly double every, total;
        bool armed;
        double start, next;

        public RetryWindow(double everySeconds, double totalSeconds) { every = everySeconds; total = totalSeconds; }

        public bool Armed { get { return armed; } }
        public void Arm(double now) { armed = true; start = now; next = now + every; }
        public void Disarm() { armed = false; }

        public RetryStep Poll(double now)
        {
            if (!armed) return RetryStep.None;
            if (now - start >= total) { armed = false; return RetryStep.GiveUp; }
            if (now < next) return RetryStep.None;
            next = now + every;
            return RetryStep.Retry;
        }
    }

    public enum PublishStep
    {
        None,           // nothing due
        StillFailing,   // a retry ran and failed again
        Written,        // a retry worked: the file now holds the newest text
        GaveUp          // the retry window ran out; the text stays pending, and the next Write starts again
    }

    // A small file that must end up holding the newest text (the host's join-code.txt). It is written through a temp
    // file and swapped in, so there is never a moment without a file. If the swap fails because a reader has the file
    // open (or any other IO error), the text stays pending and the owner's per-frame Poll retries on a bounded schedule:
    // a failed write must never leave the new invite only in the temp file with nothing to try again.
    public sealed class PublishedFile
    {
        readonly string path;
        readonly RetryWindow retry;
        string pending;

        public PublishedFile(string path, double retryEverySeconds, double retryForSeconds)
        {
            this.path = path;
            retry = new RetryWindow(retryEverySeconds, retryForSeconds);
        }

        public bool Pending { get { return pending != null; } }
        public string LastError { get; private set; }   // the most recent IO error (a Windows message, no secrets)

        // The newest text replaces any still waiting. True: the file holds it now. False: an IO error (see LastError);
        // Poll retries.
        public bool Write(string text, double now)
        {
            pending = text;
            if (TryWrite()) { Done(); return true; }
            retry.Arm(now);
            return false;
        }

        public PublishStep Poll(double now)
        {
            RetryStep s = retry.Poll(now);
            if (s == RetryStep.None) return PublishStep.None;
            if (s == RetryStep.GiveUp) return PublishStep.GaveUp;
            if (TryWrite()) { Done(); return PublishStep.Written; }
            return PublishStep.StillFailing;
        }

        // The owner is stopping or deleting the file: nothing pending may be written afterwards.
        public void Cancel() { Done(); }

        void Done() { pending = null; retry.Disarm(); }

        bool TryWrite()
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, pending);
                if (File.Exists(path)) File.Replace(tmp, path, null);   // never a moment without a file
                else File.Move(tmp, path);
                return true;
            }
            catch (IOException e) { LastError = e.Message; return false; }
            catch (UnauthorizedAccessException e) { LastError = e.Message; return false; }
        }
    }
}
