using System;
using MissionInfo = Game.Info.MissionInfo;
using ObjectInfo = Game.Info.ObjectInfo;
using TimeController = Manager.TimeController;

namespace SolarExpanseMultiplayer
{
    // A client's side of the host-decided flight entries (plan DD-F4, DD-F6, DD-F7, DD-F12): the host's game already
    // made the change, and the client makes the same one with the game's own code, for exactly what the entry names:
    //   * FlightPlanned (70): the mission is copied through the load path over frames, as a player's flight is
    //     (FlightApply.StartClient; the stream waits for it);
    //   * MissionCancelled (71): reasons 1 and 2 inside the client's launch step, where the game's own OnLaunch runs
    //     under the host's outcome (the entry's branch); reason 5 inside the impact's group, the game-side cancel the
    //     host made at the impact; reasons 3 and 4 the game's own cancel of a craft not yet built or a planned leg;
    //   * AsteroidPulled (72): inside the client's landing step, the landing handler run under the host's draws and
    //     orbit id (the entry's branch);
    //   * LandingCraft (73): the ship this game's own landing held is made now, under the host's counters;
    //   * MissionLaunched (74): inside the client's launch step, OnLaunch run under the host's outcome.
    // Anything that does not end where the host's did is out of step (a code; the caller logs why).
    public static class FlightClientEntries
    {
        public static bool Handles(int kind)
        {
            return kind >= ActionKind.FlightPlanned && kind <= ActionKind.MissionLaunched;
        }

        public static ApplyOutcome Apply(ActionRequest a, ApplyNumbers given, CommandSession session)
        {
            if (!HumanAgencies.Active) return OutOfStep(a, "the rules are off, so no host-decided flight can apply");
            if (a.Kind == ActionKind.FlightPlanned) return session.FlightApply.StartClient(a, given);
            string why;
            try
            {
                switch (a.Kind)
                {
                    case ActionKind.MissionCancelled: why = Cancelled(a, given); break;
                    case ActionKind.AsteroidPulled: why = Pulled(a, given); break;
                    case ActionKind.LandingCraft: why = Craft(a); break;
                    case ActionKind.MissionLaunched: why = Launched(a, given); break;
                    default: why = "a host-decided flight kind no client applies"; break;
                }
            }
            catch (Exception e)
            {
                Log.Error("flight: the game threw in a host flight entry: " + ExceptionText.Chain(e));
                why = "the game threw " + e.GetType().Name;
            }
            if (why != null) return OutOfStep(a, why);
            session.FlightWatches.Dirty();
            session.Steps.Watch.Dirty = true;
            return new ApplyOutcome { Code = 0, Applied = a };
        }

        static ApplyOutcome OutOfStep(ActionRequest a, string why)
        {
            Log.Warn("command: " + ActionKind.Name(a.Kind) + " " + a.Agency + " mission " + a.ItemId + ": " + why);
            return new ApplyOutcome { Code = RefuseCodes.NotAllowedHere };
        }

        // ---- 71 ----

        static string Cancelled(ActionRequest a, ApplyNumbers given)
        {
            if (FlightEvents.IsLaunch(a))
            {
                // the host's launch-moment cancel: the game's OnLaunch, run in this client's launch step, cancels it
                // under the host's outcome (FlightLaunch reads FlightClientGlue.OutcomeFor)
                if (given.RunBranch == null) return "a launch's cancel outside its launch step";
                string said = given.RunBranch();
                if (said != null) return said;
                MissionInfo launched = FlightChecks.Mission(a.ItemId);
                return launched != null && launched.cancel ? null
                    : "the host's cancel of mission " + a.ItemId + " did not happen here";
            }
            MissionInfo mi = FlightChecks.Mission(a.ItemId);
            if (mi == null) return "no mission " + a.ItemId + " to cancel";
            TargetCancel cancel;
            if (a.Value == CancelReasons.TargetGone)
            {
                // inside the impact's group: the asteroid went in the landing just applied
                cancel = FlightCancels.ForMission(a.ItemId, NowTicks());
                if (cancel == null) return "mission " + a.ItemId + " is not one the impact cancels here";
            }
            else cancel = new TargetCancel { MissionId = a.ItemId, Agency = a.Agency };
            string why = FlightSources.CancelTargetGone(cancel);
            if (why != null) return why;
            return mi.cancel ? null : "mission " + a.ItemId + " is still live";
        }

        static long NowTicks()
        {
            TimeController tc = MonoBehaviourSingleton<TimeController>.Instance;
            return tc != null ? tc.CurrentTime.Ticks : 0;
        }

        // ---- 72 ----

        static string Pulled(ActionRequest a, ApplyNumbers given)
        {
            if (given.RunBranch == null) return "a pull's entry outside its landing";
            if (given.Pull == null) return "a pull with no draws";
            string said = null;
            FlightSources.BeginPull(given.Pull, a.ItemId);
            string run;
            try { run = AsteroidSpawnRule.RunImpact(a.ItemId, () => { said = given.RunBranch(); }); }
            finally { FlightSources.EndPull(); }
            if (run != null || said != null) return run ?? said;
            ObjectInfo orbit = GameAgencies.Body(a.ToBody);
            return orbit != null ? null : "the pull made no orbit " + a.ToBody;
        }

        // ---- 73 ----

        static string Craft(ActionRequest a)
        {
            return FlightSources.ReleaseLandingCraft(a.Agency, a.Body, a.Item, a.ItemId) ? null
                : "no ship of " + a.Item + " was held here for mission " + a.ItemId;
        }

        // ---- 74 ----

        static string Launched(ActionRequest a, ApplyNumbers given)
        {
            if (given.RunBranch == null) return "a launch outcome outside its launch step";
            string said = given.RunBranch();
            if (said != null) return said;
            MissionInfo mi = FlightChecks.Mission(a.ItemId);
            return mi != null && !mi.cancel ? null
                : "the host's launch of mission " + a.ItemId + " did not happen here";
        }
    }
}
