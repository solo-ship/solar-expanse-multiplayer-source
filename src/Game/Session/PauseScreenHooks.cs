using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using PauseScreen = Game.UI.Screens.PauseScreen;

namespace SolarExpanseMultiplayer
{
    // DD21: the pause screen's two exits in a session. A prefix on each handler asks with the mod's own dialog (never the
    // game's pausing "SureExit" pop-up) and skips the original; a client's exits become Leave session and the host's End
    // session, each only where the session offers it (PauseExits). Where it does not (no session, or M4's capabilities)
    // the game's own exit runs as it is. The order after a yes is PauseExitFlow's. The hold and the PLAYERS button stay
    // in PauseScreenEarly (Task 12a), which this file sits beside.
    [HarmonyPatch]
    public static class PauseExitHooks
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            // a method the game no longer has is left out: GameHooks then sees fewer methods than HookPlan expects
            MethodBase w = AccessTools.Method(typeof(PauseScreen), "OnExitToWindowsClick");
            MethodBase m = AccessTools.Method(typeof(PauseScreen), "OnExitToMenuClick");
            if (w != null) yield return w;
            if (m != null) yield return m;
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod)
        {
            SaveGlue glue = Session.Early != null ? Session.Early.Saves : null;
            if (glue == null) return true;
            try { return !glue.Exits.Intercept(__originalMethod.Name == "OnExitToWindowsClick"); }
            catch (Exception e)
            {
                Log.Error("pause exit: the check failed, the game's own exit runs: " + e.Message);
                return true;
            }
        }
    }

    // The exit in progress (one at a time) and its steps each frame. The yes is the dialog's performed leave or end
    // (DialogScreens.ExitsAnswered). The final save of the host is the game's own exit save, written into the
    // Multiplayer saves folder by the name rewrite and offered to the players; the host's session is then ended here,
    // and the game's scene load or quit runs, without a second autosave.
    public sealed class PauseExitGlue
    {
        readonly MenuSessions owner;
        readonly SaveGlue saves;
        readonly PauseExitFlow flow = new PauseExitFlow();
        int answered;

        public PauseExitGlue(MenuSessions owner, SaveGlue saves) { this.owner = owner; this.saves = saves; }

        public bool Active { get { return flow.Active; } }

        // True: the mod took the click (its question is up, or the exit is under way); the game's handler is skipped.
        // A question that went away unanswered is asked again rather than the click swallowed.
        public bool Intercept(bool toWindows)
        {
            if (!Session.Live) return false;
            if (flow.Busy(owner.Dialogs.AskingExit)) return true;
            if (!owner.Dialogs.AskPauseExit()) return false;
            flow.Begin(toWindows);
            answered = owner.Dialogs.ExitsAnswered;
            Log.Info("pause exit: asked (" + (toWindows ? "to Windows" : "to the menu") + ")");
            return true;
        }

        public void Update()
        {
            if (!flow.Active) return;
            if (!flow.Confirmed && owner.Dialogs.ExitsAnswered != answered)
            {
                flow.Confirm();
                Log.Info("pause exit: confirmed");
            }
            SessionView v = owner.Actions.View;
            if (v == null) return;
            switch (flow.Step(v.Phase, v.IsHost, owner.Dialogs.AskingExit, saves.Host.Idle, NetClock.NowMs,
                              saves.Host.EndDeadlineMs))
            {
                case ExitStep.FinalSave:
                    try { saves.Host.FinalSave(); }
                    catch (Exception e) { Log.Error("pause exit: the final save failed: " + e.Message); }
                    break;
                case ExitStep.Run:
                    Run(flow.ToWindows);
                    break;
                case ExitStep.Cancelled:
                    Log.Info("pause exit: cancelled");
                    break;
            }
        }

        // The host's session ends here (its final save is written and sent), so the menu's own end of it finds no
        // session and saves nothing again; then the game's own scene load or quit, minus its autosave. A client's
        // leave already took it to the menu.
        static void Run(bool toWindows)
        {
            Log.Info("pause exit: " + (toWindows ? "quitting" : "back to the menu"));
            if (Session.Host != null) Session.EndHostSession("the host left by the pause screen's exit");
            if (toWindows)
            {
                var analytics = MonoBehaviourSingleton<AnalyticsManager>.Instance;
                if (analytics != null) analytics.SetOnGracefulExit();
                Application.Quit();
                return;
            }
            if (SceneManager.GetActiveScene().name != "Menu") SceneManager.LoadScene("Menu");
        }
    }
}
