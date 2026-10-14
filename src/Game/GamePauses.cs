using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BaseScreen = Game.UI.Screens.BaseScreen;
using NotificationManager = Manager.NotificationManager;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using TimeController = Manager.TimeController;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // Every place the game pauses (or resumes) by itself for its own windows, menus, tutorial steps and warnings. In a
    // session such a pause became this player's vote and stopped every player: a contract's tutorial step on completion,
    // the tutorial's hints on the construction windows, the Esc menu. While one of these methods runs, ClockSync drops
    // its speed calls (ClockHooks.DropsCall), so the game keeps the session's speed. Outside a session nothing changes.
    //  - TimeController.PauseTutorial / PlayTutorial: every tutorial pause (a step starting or finishing, which every
    //    contract objective's completion does, and the hints shown on windows);
    //  - BaseScreen.Visible: the Esc pause menu and the game-over screen pause while shown, restore when hidden;
    //  - UIManager.ShowTriviaWindow; NotificationManager.ShowNotification (the "pause on notification" toggle);
    //    ObjectInfoData.UpdateLifeSupport (the crew warnings);
    //  - TimeController.PauseGame3 / UnPauseGame3: the error reporter's pause, its only caller.
    // Not here, so unchanged: the speed buttons and hotkeys (the player's own), the planner (ClockSync's "planning"
    // pause), the countdown (forced), the autosave (silent), the end-game impact, and the imminent-bankruptcy warning
    // (worth stopping everyone for: its pause stays that player's vote). The confirm popups already pass
    // pauseTime false in a session (PopupNoPause).
    // Patched only in a test session (HookPlan.GamePauses), like ClockSync; main thread only.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a class without one.
    [HarmonyPatch]
    public static class GamePauses
    {
        // The source whose call was dropped last, for the clock log line.
        public static string Source = "";

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.NoArgs(typeof(TimeController), "PauseTutorial");
            yield return HookTargets.NoArgs(typeof(TimeController), "PlayTutorial");
            yield return HookTargets.NoArgs(typeof(TimeController), "PauseGame3");
            yield return HookTargets.NoArgs(typeof(TimeController), "UnPauseGame3");
            MethodBase visible = AccessTools.PropertySetter(typeof(BaseScreen), "Visible");
            if (visible == null) throw new InvalidOperationException("BaseScreen.Visible's setter was not found");
            yield return visible;
            yield return HookTargets.Method(typeof(UIManager), "ShowTriviaWindow");
            yield return HookTargets.Method(typeof(NotificationManager), "ShowNotification");
            yield return HookTargets.Method(typeof(ObjectInfoData), "UpdateLifeSupport");
        }

        [HarmonyPrefix]
        static void Prefix(MethodBase __originalMethod)
        {
            Source = __originalMethod.Name;
            ClockSync.Hooks.GamePauseBegin();
        }

        // A finalizer runs even when the method throws, so the scope never stays open.
        [HarmonyFinalizer]
        static void Finalizer() { ClockSync.Hooks.GamePauseEnd(); }
    }
}
