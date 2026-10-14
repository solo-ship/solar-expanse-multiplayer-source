namespace SolarExpanseMultiplayer
{
    // The object a "forget" names, as the host reads it (ObjectInfo.CanPlayerForgotObject, D 124643-124713, and the
    // trash popup's OK, D 163699-163716). Every field is about the object or about every agency, never the local
    // player's.
    public sealed class ForgetFacts
    {
        public bool Exists;                 // a world object with this id that is not destroyed already
        // a construction site (a space station's, or an interstellar one not yet finished): the game lets its
        // builder throw it away, scrapping the facility that made it
        public bool StationSite, InterstellarSite;
        public string SiteOwner;            // the wire id of the site's builder (CreateByFacility's agency), or null
        public bool TransferPossible;       // a station site: the builder has data on the parent's low orbit and here
        // an asteroid's own reasons (the game's checks in its order)
        public bool Asteroid, ElevatorConnected, UsedInElevator, Pushable, PushOnlyToOrbit, ImpactLabel,
            PushTargetSet, NeverForget, OnShip;
        public bool CarriedByMission;       // an impact or pull mission is taking it (the game's own mission test)
        public bool MissionUnderWay;        // any mission of any agency on its way to or from it
        public bool OthersUse;              // another active agency has facilities, stock, builds or a queue there
    }

    // ForgetObject (45, DD15): vanilla's CanPlayerForgotObject repeated as the ACTING agency. Called as it is, the
    // game's check skips the local player's data and counts the actor's own as another agency's; here "another
    // agency" is every active agency but the actor, and any live mission to or from the object or another agency's
    // unfinished construction there keeps it (PROVISIONAL for the user: "no other agency uses it").
    public static class ForgetCommand
    {
        // One agency's data at the object keeps it when the agency is another active one with facilities, stock,
        // unfinished builds or a queue there (the game's loop skips a company that is switched off).
        public static bool OtherUses(bool actor, bool active, bool facilities, bool stock, bool builds, bool queue)
        {
            if (actor || !active) return false;
            return facilities || stock || builds || queue;
        }

        public static int Decide(string actor, ForgetFacts f)
        {
            if (f == null || !f.Exists) return RefuseCodes.BadTarget;
            if (f.StationSite || f.InterstellarSite)
            {
                // a construction site is its builder's: anyone else naming it names another agency's site
                if (f.SiteOwner == null || f.SiteOwner != actor) return RefuseCodes.BadTarget;
                if (f.StationSite && !f.TransferPossible) return RefuseCodes.NotAllowedHere;
            }
            else
            {
                if (!f.Asteroid) return RefuseCodes.NotAllowedHere;
                if (f.ElevatorConnected || f.UsedInElevator) return RefuseCodes.InUse;
                if (!f.Pushable && !f.PushOnlyToOrbit) return RefuseCodes.NotAllowedHere;
                if (f.ImpactLabel || f.NeverForget) return RefuseCodes.NotAllowedHere;
                if (f.PushTargetSet || f.OnShip) return RefuseCodes.InUse;
            }
            if (f.CarriedByMission || f.MissionUnderWay) return RefuseCodes.InUse;
            return f.OthersUse ? RefuseCodes.InUse : 0;
        }
    }
}
