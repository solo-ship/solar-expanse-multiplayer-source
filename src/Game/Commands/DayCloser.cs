using System;
using HarmonyLib;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // DD4: the host closes day N in its fixed step, before the day event. TimeController.FixedUpdate runs TimeStep and
    // then DoThePeriodicEvents, whose day event fires when CurrentTime has reached nextDay (D 88875, 88886-88905): this
    // prefix sees that moment first and tells the command driver (DayClosing: the day's prints and DayClosed), and the
    // finalizer tells it the event is over (DayEventDone), even when a day handler throws, so the driver never stays
    // inside a day event (every later entry would be stamped a day late). A handler that throws leaves nextDay as it
    // was and the game runs the day again on the next step; the driver closes each day at most once.
    // A no-op outside a host's command session. The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx)
    // silently skips a patch class without one.
    [HarmonyPatch(typeof(TimeController), "DoThePeriodicEvents")]
    public static class DayCloser
    {
        static readonly AccessTools.FieldRef<TimeController, DateTime> NextDay =
            AccessTools.FieldRefAccess<TimeController, DateTime>("nextDay");

        [HarmonyPrefix]
        static void Prefix(TimeController __instance, ref CommandSession __state)
        {
            CommandSession s = Session.Commands;
            if (s == null || !s.IsHost) return;
            if (s.BeforeDayEvent(__instance.CurrentTime.Ticks, NextDay(__instance).Ticks)) __state = s;
        }

        // A finalizer, not a postfix: it runs when a day handler throws too, and passes the exception on unchanged
        // (the game goes on as it would without us).
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception, CommandSession __state)
        {
            if (__state != null) __state.AfterDayEvent();
            return __exception;
        }
    }
}
