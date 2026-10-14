using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One observatory as the exploration window sees it, for the ACTING agency.
    public sealed class ObservatoryFacts
    {
        public bool Found;          // an observatory of the agency with this descriptor and item id at the body
        public bool Probe;          // named by a ship: a probe observatory takes no player action (it examines its own
                                    // body only)
        public bool Finished;       // the window lists only finished observatories that are switched on (D 134206)
        public bool SwitchedOn;
        public int Mode;            // ObsMode
        public bool SurveyUnlocked; // the agency has ExoplanetSurveyResearchTab (D 134265)
    }

    // A body the examine search could offer, as the acting agency knows it (D 134170-134193; the search's own filter,
    // D 145247: no body clicked through another, no ship).
    public sealed class ExamineTarget
    {
        public bool Listed = true;      // the search lists it at all
        public bool CanMine;
        public bool OrbitOrSystem;      // an orbit, a star system or a solar orbit
        public bool Hidden;             // the body's own IsHideToDiscover
        public bool HasData;            // the agency has data at it
        public bool Discovered;         // the agency's IsDiscoveredOrNotAsteroid
        public bool DataHidden;         // the agency's data's IsHideToDiscover
        public bool KnownUnexplored;    // a deposit the agency has observed and not explored
    }

    // An object the discover search could narrow to, as the acting agency knows it (D 134086-134092, 110100-110107).
    public sealed class DiscoverTarget
    {
        public bool Asteroid;
        public bool Hidden;             // the body's own IsHideToDiscover
        public bool HasData;
        public bool Discovered;         // the agency's IsDiscoveredOrNotAsteroid
        public bool DataHidden;
        public bool BeingFound;         // another observatory is looking for it now
    }

    // O1-O5 (DD14): the exploration window's checks, repeated for the acting agency on the host. The window reads the
    // local player's map (IsDiscoveredForPlayerCache, GetObjectInfoData(Player)); these read the agency's own. A check
    // also refuses what the window would allow but the game's order would then fail at, after the window's "stop
    // first" had already stopped the observatory: a refusal changes nothing, a failed order would.
    public static class ObservatoryCommand
    {
        public static bool Handles(int kind)
        {
            return kind >= ActionKind.ObservatoryExamine && kind <= ActionKind.ObservatoryNotify;
        }

        // The observatory itself, for every kind. The exploration window lists only observatories with units on
        // (D 134206); the facility window's Stop is there for any finished, busy one, whatever its units on
        // (D 134854-134859), so a stop does not ask for them.
        public static int CheckObservatory(ObservatoryFacts o, bool needsUnitsOn = true)
        {
            if (o == null || !o.Found) return RefuseCodes.BadTarget;
            if (o.Probe) return RefuseCodes.NotAllowedHere;
            if (!o.Finished || (needsUnitsOn && !o.SwitchedOn)) return RefuseCodes.NotAllowedHere;
            return 0;
        }

        // The examine search's filter for one body (AdditionalFilter): the window shows a body the agency has no data
        // at, but the order needs the data (GetTargetObjectInfoDataForCompany), so a body target needs it too.
        public static bool InExamineSearch(ExamineTarget t)
        {
            if (t == null || !t.Listed || !t.CanMine || t.OrbitOrSystem || t.Hidden) return false;
            return !t.HasData || (t.Discovered && t.KnownUnexplored);
        }

        public static bool ExaminableBody(ExamineTarget t) { return InExamineSearch(t) && t.HasData; }

        // FindNextObjectForExplorationFromObjectInfoGroups' candidates: the body of a group an order would pick from.
        public static bool GroupCandidate(ExamineTarget t)
        {
            return t != null && t.HasData && t.Discovered && !t.DataHidden && t.KnownUnexplored;
        }

        // O1. target: the body (null when a group is examined); group: the group's members (null when a body is), or
        // an empty list for a group index the game does not have.
        public static int CheckExamine(ObservatoryFacts o, bool bodyNamed, ExamineTarget target, bool groupNamed,
            IList<ExamineTarget> group)
        {
            int code = CheckObservatory(o);
            if (code != 0) return code;
            // the window's search holds a body or a group; a request with both is not one it made
            if (bodyNamed == groupNamed) return RefuseCodes.BadTarget;
            if (bodyNamed)
            {
                if (target == null) return RefuseCodes.BadTarget;
                return ExaminableBody(target) ? 0 : RefuseCodes.NotAllowedHere;
            }
            if (group == null) return RefuseCodes.BadTarget;
            bool shown = false, candidate = false;
            foreach (ExamineTarget t in group)
            {
                shown |= InExamineSearch(t);
                candidate |= GroupCandidate(t);
            }
            return shown && candidate ? 0 : RefuseCodes.NotAllowedHere;
        }

        // The discover search's filter for one object (AdditionalFilterForNarrowingDiscoveryGroup, with the agency's
        // map in place of the local player's).
        public static bool InDiscoverSearch(DiscoverTarget t)
        {
            return t != null && t.Asteroid && !t.Hidden && !t.Discovered;
        }

        // RandomizeNextObjectToDiscover's candidates.
        public static bool DiscoverCandidate(DiscoverTarget t)
        {
            return t != null && t.Asteroid && t.HasData && !t.Discovered && !t.DataHidden && !t.BeingFound;
        }

        // O2. groupNamed: the window narrowed to a group (then members are its objects, null for an index the game
        // does not have); else members are every object in the system.
        public static int CheckDiscover(ObservatoryFacts o, bool groupNamed, IList<DiscoverTarget> members)
        {
            int code = CheckObservatory(o);
            if (code != 0) return code;
            if (members == null) return RefuseCodes.BadTarget;
            bool shown = !groupNamed, candidate = false;
            foreach (DiscoverTarget t in members)
            {
                shown |= InDiscoverSearch(t);
                candidate |= DiscoverCandidate(t);
            }
            return shown && candidate ? 0 : RefuseCodes.NotAllowedHere;
        }

        // O3: only once the agency has the survey, and not for an observatory already on it (D 133939-133951).
        public static int CheckSurvey(ObservatoryFacts o)
        {
            int code = CheckObservatory(o);
            if (code != 0) return code;
            if (!o.SurveyUnlocked) return RefuseCodes.Locked;
            return o.Mode == ObsMode.ExoplanetSurvey ? RefuseCodes.AlreadyDone : 0;
        }

        // O4: the stop buttons are on only for a working observatory (ValidateInputs, D 134320-134322).
        public static int CheckStop(ObservatoryFacts o)
        {
            int code = CheckObservatory(o, false);
            if (code != 0) return code;
            return o.Mode == ObsMode.Idle ? RefuseCodes.AlreadyDone : 0;
        }

        // O5: the toggle is on for any listed observatory; the value's form is the shape's.
        public static int CheckNotify(ObservatoryFacts o) { return CheckObservatory(o); }

        // O1-O3 on a busy observatory: the window's "stop it?" popup stops it before the order (D 134336-134350).
        public static bool StopsFirst(int kind, int mode)
        {
            bool order = kind == ActionKind.ObservatoryExamine || kind == ActionKind.ObservatoryDiscover
                || kind == ActionKind.ObservatorySurvey;
            return order && mode != ObsMode.Idle;
        }

        // The request as the game names its agency (Company.ID): the stream carries the wire id, and the entry's own
        // lookup (ObservatoryWorld.Find) compares the game's. Null when the game id is not this request's agency, so a
        // conversion that went wrong names no observatory rather than another agency's.
        public static ActionRequest WithGameAgency(ActionRequest a, string gameId)
        {
            if (a == null || gameId == null || AgencyWire.From(gameId) != a.Agency) return null;
            ActionRequest named = ActionCopy.Of(a);
            named.Agency = gameId;
            return named;
        }

        // A client's check after the apply: its observatory must be where the host's ended. null = the same.
        public static string AfterProblem(ObsAfter host, ObsAfter mine)
        {
            if (host == null) return "the entry carries no observatory state";
            if (mine == null) return "this game's observatory state could not be read";
            if (!ObservatoryRules.Changed(host, mine)) return null;
            return "the observatory is mode " + mine.Mode + " target " + mine.Target + " group " + mine.Group + " days "
                + mine.Days + ", the host's mode " + host.Mode + " target " + host.Target + " group " + host.Group
                + " days " + host.Days;
        }
    }
}
