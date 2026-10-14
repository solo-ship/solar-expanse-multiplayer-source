using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The decisions behind M6 Task 7's flight rules (src/Game/Flights/FlightRules.cs), kept free of the game so they
    // have real tests. The game side reads its objects into plain values and asks.

    // DD-F8: the orbital supply containers of one agency. Vanilla hands out any agency's first free container; in a
    // session each agency has its own pool, in list order, which is the same on every game while the same entries
    // create containers in the same order.
    public static class ContainerPool
    {
        // The index of the first container that is free and owned by the agency, or -1 (then the caller makes one).
        public static int Pick(int count, Func<int, bool> isFree, Func<int, bool> isOwned)
        {
            if (isFree == null) throw new ArgumentNullException("isFree");
            if (isOwned == null) throw new ArgumentNullException("isOwned");
            for (int i = 0; i < count; i++)
                if (isFree(i) && isOwned(i)) return i;
            return -1;
        }
    }

    // DD-F5 / S6: the departure pad on every planner that is not the game's own load. The game computes the earliest
    // departure as `addToStart` physics seconds from now; a session moves it to at least a game day and an hour past
    // the planning game's boundary.
    public static class DeparturePad
    {
        // The new addToStart (physics seconds), or the same one when it needs no change. gameSecondsPerPhysSecond is
        // GravityScaler.game_sec_per_phys_sec; speed (game seconds per real second) matters to code plans only, whose
        // pad also covers the real time they take.
        public static double AddToStart(double addToStart, double gameSecondsPerPhysSecond, long nowTicks,
            long nextDayTicks, bool codePlan, double speed)
        {
            if (!(gameSecondsPerPhysSecond > 0) || double.IsInfinity(gameSecondsPerPhysSecond)) return addToStart;
            if (double.IsNaN(addToStart) || double.IsInfinity(addToStart)) return addToStart;
            double offsetTicks = addToStart * gameSecondsPerPhysSecond * TimeSpan.TicksPerSecond;
            // a departure beyond the representable range is the game's own business
            if (offsetTicks > long.MaxValue / 4 || offsetTicks < -(long.MaxValue / 4)) return addToStart;
            long earliest = nowTicks + (long)offsetTicks;
            long padded = codePlan ? LaunchTiming.PadDeparture(earliest, nextDayTicks, speed)
                : LaunchTiming.PadDeparture(earliest, nextDayTicks);
            if (padded <= earliest) return addToStart;
            return (padded - nowTicks) / (double)TimeSpan.TicksPerSecond / gameSecondsPerPhysSecond;
        }
    }

    // MissionCancelled's reasons (Value, 1..5; the log names them).
    public static class CancelReasons
    {
        public const int Power = 1, Catapult = 2, Stalled = 3, Cyclical = 4, TargetGone = 5;

        public static string Name(int reason)
        {
            switch (reason)
            {
                case Power: return "power";
                case Catapult: return "catapult";
                case Stalled: return "stalled";
                case Cyclical: return "cyclical";
                case TargetGone: return "target-gone";
                default: return "?";
            }
        }
    }

    // DD-F6: a checked launch is reported by its outcome, not by the check. The host's OnLaunch ran as vanilla; what
    // goes out is what really happened, and every client reproduces that, whatever its own facility says.
    public static class LaunchOutcome
    {
        // 0 = nothing to report (an ordinary rocket, or the launch has not decided), else the entry's kind:
        // MissionLaunched when the ship launched, MissionCancelled when the mission really cancelled. A launch that
        // neither cancelled nor launched (a late ship still unbuilt) decides nothing yet.
        public static int Kind(bool checkedLaunch, bool cancelled, bool launched)
        {
            if (!checkedLaunch) return 0;
            if (cancelled) return ActionKind.MissionCancelled;
            return launched ? ActionKind.MissionLaunched : 0;
        }

        // Why a checked launch cancelled: a facility launch vehicle without power or switched off (1), a catapult with
        // too few units (2).
        public static int Reason(bool catapult) { return catapult ? CancelReasons.Catapult : CancelReasons.Power; }
    }

    // What a client's launch step does with the host's outcome for that mission (FlightClientGlue.OutcomeFor: 1
    // launched, 0 cancelled, -1 none): the host's launch lets the ship go at the game's cancel call whatever the
    // client's own facility says; the host's cancel is made as the host's was, even when the client's check passed;
    // with no outcome the game decides as it always has.
    public static class LaunchVerdictRule
    {
        public const int None = -1, Cancelled = 0, Launched = 1;

        public static bool SkipsCancel(int outcome) { return outcome == Launched; }
        public static bool ForcesCancel(int outcome) { return outcome == Cancelled; }
    }

    // One mission as DD-F12's set reads it. All dates are the missions' own, the same on every game.
    public sealed class GoneRow
    {
        public long MissionId;
        public string Agency;
        public int Start, Target;
        public long DepartTicks, ArriveTicks;
        public int Carried;            // the whole asteroid it carries (an ObjectInfo id), or 0
        public bool StartDestroyed;    // the start body is gone
        public bool Cancelled;
    }

    public sealed class TargetCancel
    {
        public long MissionId;
        public string Agency;
        public bool Lost;              // nowhere to return to: no refund, no crew credit
        public bool Launched;
        public int RestoresCarried;    // another asteroid it carries, to put back on its orbit; 0 = none
    }

    // DD-F12: the missions an asteroid's destruction cancels, decided by date. The same function serves the impact and
    // M7's nuke (atTicks = the apply moment), raises nothing and returns the missions by id.
    public static class TargetGone
    {
        public static List<TargetCancel> Cancels(IList<GoneRow> rows, int asteroid, long atTicks)
        {
            var result = new List<TargetCancel>();
            if (rows == null) return result;
            var sorted = new List<GoneRow>(rows.Count);
            foreach (GoneRow r in rows) if (r != null && !r.Cancelled) sorted.Add(r);
            sorted.Sort((a, b) => a.MissionId.CompareTo(b.MissionId));
            foreach (GoneRow r in sorted)
            {
                bool launched = r.DepartTicks <= atTicks;
                bool to = r.Target == asteroid && r.ArriveTicks > atTicks;
                // flying on from a destroyed start to a surviving target carries on; waiting there is lost with it
                bool from = r.Start == asteroid && !launched;
                bool carrying = r.Carried == asteroid && r.ArriveTicks > atTicks;
                if (!to && !from && !carrying) continue;
                result.Add(new TargetCancel
                {
                    MissionId = r.MissionId, Agency = r.Agency, Launched = launched,
                    Lost = r.StartDestroyed || r.Start == asteroid,
                    RestoresCarried = r.Carried != 0 && r.Carried != asteroid ? r.Carried : 0
                });
            }
            return result;
        }
    }

    // DD-F12, the cap scoped (W1): flights are limited only where an impact can happen, so the reason-5 group stays
    // within MaxFlightsPerTarget. Ordinary flights and cyclical legs to a busy asteroid nobody is crashing never are.
    public static class TargetFull
    {
        // carriesToImpact: the flight carries asteroid X to a body (not an orbit). impactPending: an X impactor is in
        // flight. live: the missions to or from X now.
        public static bool Refuses(bool carriesToImpact, bool impactPending, int live, int cap)
        {
            if (carriesToImpact && live > cap) return true;
            return impactPending && live >= cap;
        }
    }

    // A4 (revision 15c): the end-game flight is recognised by ONE predicate, registration or vanilla's own shape.
    public static class EndGame
    {
        public const string PullerType = "spacecraft_asteroid_puller_ForAsteroidImpact";
        public const int Asteroid = 533;

        public static bool Is(bool registered, int creator, bool wholeAsteroidCargo, string shipType)
        {
            if (registered) return true;
            return creator == MissionCreators.Other && wholeAsteroidCargo && shipType == PullerType;
        }
    }

    // What the host does with a plan its own game made (DD-F4). Raise = send it as FlightPlanned.
    public enum PlanFate
    {
        Raise,
        DropLog,         // a plan of no known creator: logged, never a fault
        DropAi,          // a rival's plan: refused, its task fails on a null mission and tries again
        DropCyclical,    // a route's leg: dropped and the controller reset, or it would wedge (X2)
        DropEndGameGone, // the end-game flight while asteroid 533 is destroyed: no flight, no fault
        HostFault        // the end-game flight cannot be dropped silently
    }

    public static class GamePlanRules
    {
        // creator: MissionCreators of the plan. rival: the plan is a computer rival's (the AI's task). tooClose:
        // LaunchTiming.TooClose for the day the entry would be stamped with. endGame: EndGame.Is. gone533: asteroid
        // 533 is destroyed.
        public static PlanFate Decide(int creator, bool rival, bool endGame, bool tooClose, bool gone533,
            bool targetFull)
        {
            if (endGame && gone533) return PlanFate.DropEndGameGone;
            if (!tooClose && !targetFull) return PlanFate.Raise;
            if (endGame) return PlanFate.HostFault;
            if (creator == MissionCreators.Cyclical) return PlanFate.DropCyclical;
            if (rival) return PlanFate.DropAi;
            return PlanFate.DropLog;
        }
    }

    // The three buy-fuel popups of the schedule step share one message ("PopUp.NoMony" with the price); inside a host
    // acceptance only they are answered at once, and nothing else is.
    public static class PopupMatch
    {
        // template holds "{0}" once; text is the template formatted. A template without it matches only itself.
        public static bool Matches(string template, string text)
        {
            if (template == null || text == null) return false;
            int at = template.IndexOf("{0}", StringComparison.Ordinal);
            if (at < 0) return template == text;
            string head = template.Substring(0, at), tail = template.Substring(at + 3);
            return text.Length >= head.Length + tail.Length && text.StartsWith(head, StringComparison.Ordinal)
                && text.EndsWith(tail, StringComparison.Ordinal);
        }
    }
}
