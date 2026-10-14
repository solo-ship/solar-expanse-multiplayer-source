using System;
using PlanMissionWindow = Game.UI.Windows.Windows.PlanMissionWindow;

namespace SolarExpanseMultiplayer
{
    // The visible planner while its Schedule request waits for the host (plan Task 11): it stays open (everybody is
    // paused while a planner is, rule 2) with the session's "Waiting for the host..." line, closes when the entry for
    // its flight arrives on a client (the host accepted it) or is applied on the host, and stays open when the host
    // refused it (the refusal is the status line's; the player may change the plan and press Schedule again).
    // One planner at a time: it is the local player's.
    static class PlannerWait
    {
        static PlanMissionWindow window;

        public static bool IsWaiting(PlanMissionWindow w) { return window != null && ReferenceEquals(window, w); }

        public static void Begin(PlanMissionWindow w) { window = w; }

        // The flight was accepted (a client: its entry arrived) or its copy made (FlightApply.Finished): a schedule,
        // route or supply flight of the local agency closes the planner that asked for it.
        public static void Applied(ActionRequest entry)
        {
            if (window == null || entry == null) return;
            bool route = entry.Kind == ActionKind.CyclicalSet;
            if (entry.Kind != ActionKind.FlightSchedule && entry.Kind != ActionKind.SupplyFlight && !route) return;
            if (entry.Agency != WindowCapture.Local()) return;
            PlanMissionWindow w = window;
            window = null;
            try
            {
                if (w != null && w.Open) w.PublicHide(false);
                // a route's window hands over to the missions list, as the game's own Schedule does
                if (route) SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance.Open(Game.UI.EWindowType.Mission);
            }
            catch (Exception e) { Log.Warn("flight: could not close the planner: " + e.GetType().Name); }
        }

        // A flight request of this player's was refused: the planner stays as it is.
        public static void Refused(ActionRequest request)
        {
            if (request != null && ActionKind.IsFlight(request.Kind)) window = null;
        }

        // Each frame: a planner the player closed meanwhile is no longer waited for.
        public static void Update()
        {
            if (window == null) return;
            try { if (!window.Open) window = null; }
            catch (Exception) { window = null; }
        }
    }
}
