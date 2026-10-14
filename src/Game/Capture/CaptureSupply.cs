using HarmonyLib;
using MissionInfo = Game.Info.MissionInfo;
using GameManager = Manager.GameManager;
using PMMissionParameter = Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter;

namespace SolarExpanseMultiplayer
{
    // F10, the second half (plan DD-F2): the row's click (CaptureFlights.SupplyRow) lets the game build and queue the
    // supply plan on the clicking game, as it always does; here the plan is bound to that click as it is queued, and
    // when the game asks PlanFlyCode to schedule it (silently, with its own callback that would set the row's link),
    // the plan is read into a SupplyFlight request instead and nothing is planned here. The row's link comes back with
    // the flight, on every game (FlightSupply.Link). Both patches ask the session first and are no-ops outside one.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureSupply
    {
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetPMParameterForCodeJobSystem))]
        [HarmonyPrefix]
        static void Bind(PMMissionParameter _pmMissionParameter)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null || _pmMissionParameter == null) return;
            if (_pmMissionParameter.MissionCreator != MissionInfo.EMissionCreator.ResourcesToOrbitToBuildSC)
                return;
            seam.Memory.Supply.Attach(_pmMissionParameter, UnityEngine.Time.frameCount);
        }

        // First, so a client's backstop (which refuses any plan it does not know) never sees the click's plan.
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlanFlyCode))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static bool Capture(PMMissionParameter missionParameter, ref GameManager.PlanFlyCodeResult __result)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            SupplyClick click = seam.Memory.Supply.Take(missionParameter);
            if (click == null) return true;
            try
            {
                string problem;
                int other = missionParameter.Start.id != click.Site ? missionParameter.Start.id
                    : missionParameter.Target.id;
                ActionRequest a = FlightRequests.Supply(missionParameter, click.Site, other, click.Item, click.Index,
                    click.Down, out problem);
                if (a == null)
                {
                    Log.Info("flight: the supply plan was not sent: " + TextSanitiser.OneLine(problem));
                    WindowCapture.CannotSend(seam);
                }
                else WindowCapture.Send(seam, a, null);
            }
            catch (System.Exception e)
            {
                Log.Error("flight: the supply capture failed: " + e.GetType().Name + ": "
                    + TextSanitiser.OneLine(e.Message));
                WindowCapture.CannotSend(seam);
            }
            finally
            {
                // the planner window the click's plan was computed in is let go, as the game's own step would
                try { FlightApply.WindowFor(missionParameter).ClearVariableForCode(); }
                catch (System.Exception) { }
            }
            // never finished: the game's callback (the row's link) is not run; the entry sets it on every game
            __result = new GameManager.PlanFlyCodeResult { endCalculation = true };
            return false;
        }
    }
}
