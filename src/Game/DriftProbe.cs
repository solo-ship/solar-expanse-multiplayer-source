using System;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    // M3 Task 8: the day probe. One patched method, TimeController.DoThePeriodicEvents (private, once per fixed step, about 9 times
    // a game day at 200000 and 66 at 25000), with a prefix, a postfix and a finalizer, all in this class.
    //   * The prefix notes nextDay (the key of the boundary the step may cross) and lastDay.
    //   * The postfix sees nextDay move: a day event ran, and every handler of that day (and the month and year events of the same
    //     step) has finished. It hands the day to OnDay, inside FixedUpdate.
    //   * The finalizer runs even when a handler throws. A day handler that throws leaves nextDay unchanged, so the game re-runs the
    //     day's earlier handlers on the next step (a game behaviour, docs/research/state-accessors.md "Time and day events") and
    //     the postfix does not run; the finalizer tells OnDayThrew and passes the exception on unchanged.
    // Nothing here allocates: the field refs are cached, the delegates are static fields set by DriftRecorder, and DayTick is a struct.
    // Everything is inside try/catch: the probe must never change what the game does. Main thread only (FixedUpdate).
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch(typeof(Manager.TimeController), "DoThePeriodicEvents")]
    public static class DriftProbe
    {
        const double TicksPerDay = (double)TimeSpan.TicksPerDay;

        // Both set by DriftRecorder when it starts and cleared when it stops. A probe with no listener does nothing but note two fields.
        public static Action<DayTick> OnDay;
        public static Action<Exception> OnDayThrew;

        // nextDay as the prefix of the call in progress (or the last call) saw it: the key of the day that call may record. For OnDayThrew.
        public static long PrevNextTicks { get; private set; }

        static StateField<Manager.TimeController, DateTime> nextRef, lastRef;
        static long prevLast;

        [HarmonyPrefix]
        static void Prefix(Manager.TimeController __instance)
        {
            try
            {
                if (nextRef == null) { lastRef = new StateField<Manager.TimeController, DateTime>("lastDay"); nextRef = new StateField<Manager.TimeController, DateTime>("nextDay"); }
                PrevNextTicks = nextRef.Get(__instance).Ticks;
                prevLast = lastRef.Get(__instance).Ticks;
            }
            catch (Exception) { }
        }

        [HarmonyPostfix]
        static void Postfix(Manager.TimeController __instance)
        {
            try
            {
                Action<DayTick> listener = OnDay;
                if (listener == null || nextRef == null) return;
                if (nextRef.Get(__instance).Ticks == PrevNextTicks) return;       // no day event ran in this step
                DayTick t;
                t.KeyTicks = PrevNextTicks;
                t.AtTicks = __instance.CurrentTime.Ticks;
                t.ElapsedDays = (lastRef.Get(__instance).Ticks - prevLast) / TicksPerDay;     // the argument the game's own day handlers saw
                listener(t);
            }
            catch (Exception) { }
        }

        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                try { Action<Exception> listener = OnDayThrew; if (listener != null) listener(__exception); }
                catch (Exception) { }
            }
            return __exception;     // passed on unchanged: the game behaves as without us
        }
    }
}
