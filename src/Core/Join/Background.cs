using System;
using System.Collections.Generic;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // Where the join's heavy pure work runs (N-3): the host checks its own snapshot (gunzip and scan) and the joiner checks the
    // received one (hash, gunzip, scan) off the main thread, so a frame never stalls on them. The drivers (JoinService, JoinerFlow)
    // take this interface; the game gives them a ThreadBackground, the tests and the fake client an InlineBackground.
    //
    // The contract for work: it touches no game object and no driver state except through its own result slot, which the driver
    // polls from Update (nothing is called back). It reports failure by setting that slot, not by throwing; whatever it throws
    // anyway is caught and logged by the thread runner so one bad item cannot take the game down.
    public interface IBackground { void Run(Action work); }

    // Runs the work at once, on the caller's thread, and lets an exception through: for unit tests and the fake client, where a
    // work item that fails should fail the test.
    public sealed class InlineBackground : IBackground
    {
        public void Run(Action work)
        {
            if (work == null) throw new ArgumentNullException("work");
            work();
        }
    }

    // Runs the work on one background worker thread, in the order given. The thread starts when there is work and ends when the
    // queue is empty, so an idle game has no join thread at all. A join is a one-off (Global Constraints, Allocation), so this may
    // allocate a thread and a queue entry per item; nothing here runs per frame. Pending is for the drivers' idle checks and the
    // tests.
    public sealed class ThreadBackground : IBackground
    {
        // Far more than any driver queues (the host's self-check, a rejoin print, the joiner's walls: a handful at most). The bound
        // exists so a driver bug that queues without limit fails loudly instead of eating memory.
        public const int MaxQueued = 64;

        readonly object gate = new object();
        readonly Queue<Action> queue = new Queue<Action>();
        readonly Action<Exception> onError;
        bool running;      // a worker thread exists
        bool executing;    // the worker is inside an item

        // onError receives an exception a work item let escape (called on the worker thread); null logs it with Log.Error. A
        // reporter that throws is ignored.
        public ThreadBackground() : this(null) { }

        public ThreadBackground(Action<Exception> onError) { this.onError = onError; }

        // Work queued or running.
        public int Pending
        {
            get { lock (gate) return queue.Count + (executing ? 1 : 0); }
        }

        // Queues the work. Throws ArgumentNullException for null work and InvalidOperationException when MaxQueued items already
        // wait (or the thread cannot start); in either case nothing was queued.
        public void Run(Action work)
        {
            if (work == null) throw new ArgumentNullException("work");
            lock (gate)
            {
                if (queue.Count >= MaxQueued) throw new InvalidOperationException("the background queue is full (" + MaxQueued + ")");
                queue.Enqueue(work);
                if (running) return;
                running = true;
                try
                {
                    var t = new Thread(Work) { IsBackground = true, Name = "semp-join-worker", Priority = ThreadPriority.BelowNormal };
                    t.Start();
                }
                catch (Exception)
                {
                    running = false;
                    queue.Clear();
                    throw;
                }
            }
        }

        void Work()
        {
            for (;;)
            {
                Action item;
                lock (gate)
                {
                    if (queue.Count == 0) { running = false; executing = false; return; }
                    item = queue.Dequeue();
                    executing = true;
                }
                try { item(); }
                catch (Exception e) { Report(e); }
            }
        }

        void Report(Exception e)
        {
            try
            {
                if (onError != null) onError(e);
                else Log.Error("background work failed: " + TextSanitiser.OneLine(e.GetType().Name + ": " + e.Message));
            }
            catch (Exception) { }
        }
    }
}
